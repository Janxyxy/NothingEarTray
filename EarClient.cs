using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace NothingEarTray;

public record BudState(int? Percent, bool Charging);

public enum AncMode : byte
{
    High = 0x01, Mid = 0x02, Low = 0x03, Adaptive = 0x04, Off = 0x05, Transparency = 0x07,
}

public record BatteryInfo(BudState Left, BudState Right, BudState Case);

/// <summary>
/// Talks to Nothing earbuds over their proprietary "NT LINK" RFCOMM service.
/// Protocol (reverse-engineered, see ear-web): 55 60 01 [cmd lo] [cmd hi] [len] 00 [opId] [payload] [crc16 lo] [crc16 hi]
/// </summary>
public sealed class EarClient : IDisposable
{
    static readonly Guid NtLinkUuid = new("aeac4a03-dff5-498f-843a-34487cf133eb");
    const ushort CmdGetBattery = 0xC007;
    const ushort RespBattery = 0x4007;
    const ushort NotifyBattery = 0xE001;
    const ushort CmdGetAnc = 0xC01E;
    const ushort RespAnc = 0x401E;
    const ushort NotifyAnc = 0xE003;
    const ushort CmdSetAnc = 0xF00F;

    public event Action<AncMode>? AncReceived;

    StreamSocket? _socket;
    DataWriter? _writer;
    CancellationTokenSource? _readCts;
    byte _opId;

    public string? DeviceName { get; private set; }
    public bool Connected => _socket != null;
    public event Action<BatteryInfo>? BatteryReceived;
    public event Action? Disconnected;

    public async Task<bool> ConnectAsync()
    {
        Close();
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector);
        foreach (var info in devices)
        {
            BluetoothDevice? dev = null;
            try
            {
                dev = await BluetoothDevice.FromIdAsync(info.Id);
                if (dev == null || dev.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;

                var res = await dev.GetRfcommServicesForIdAsync(
                    RfcommServiceId.FromUuid(NtLinkUuid), BluetoothCacheMode.Uncached);
                if (res.Services.Count == 0) continue;

                var svc = res.Services[0];
                var sock = new StreamSocket();
                await sock.ConnectAsync(svc.ConnectionHostName, svc.ConnectionServiceName);
                _socket = sock;
                _writer = new DataWriter(sock.OutputStream);
                DeviceName = dev.Name;
                Log.Write($"connected to {dev.Name}");
                _readCts = new CancellationTokenSource();
                _ = ReadLoop(sock, _readCts.Token);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"connect {info.Name} failed: {ex.Message}");
            }
            finally
            {
                dev?.Dispose();
            }
        }
        return false;
    }

    public Task RequestBatteryAsync() => SendAsync(CmdGetBattery, []);
    public Task RequestAncAsync() => SendAsync(CmdGetAnc, []);
    public Task SetAncAsync(AncMode mode) => SendAsync(CmdSetAnc, [0x01, (byte)mode, 0x00]);

    async Task SendAsync(ushort cmd, byte[] payload)
    {
        if (_writer == null) return;
        try
        {
            _writer.WriteBytes(BuildPacket(cmd, payload));
            await _writer.StoreAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"send failed: {ex.Message}");
            HandleDisconnect();
        }
    }

    byte[] BuildPacket(ushort cmd, byte[] payload)
    {
        var p = new List<byte> { 0x55, 0x60, 0x01, (byte)(cmd & 0xFF), (byte)(cmd >> 8), (byte)payload.Length, 0x00, _opId++ };
        p.AddRange(payload);
        ushort crc = Crc16(p);
        p.Add((byte)(crc & 0xFF));
        p.Add((byte)(crc >> 8));
        return p.ToArray();
    }

    static ushort Crc16(IEnumerable<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    async Task ReadLoop(StreamSocket sock, CancellationToken ct)
    {
        var reader = new DataReader(sock.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
        var buf = new List<byte>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                uint n = await reader.LoadAsync(1024);
                if (n == 0) break;
                var chunk = new byte[n];
                reader.ReadBytes(chunk);
                buf.AddRange(chunk);
                ParseFrames(buf);
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Log.Write($"read failed: {ex.Message}");
        }
        if (!ct.IsCancellationRequested) HandleDisconnect();
    }

    void ParseFrames(List<byte> buf)
    {
        while (true)
        {
            int start = buf.IndexOf(0x55);
            if (start < 0) { buf.Clear(); return; }
            if (start > 0) buf.RemoveRange(0, start);
            if (buf.Count < 8) return;
            // header: 55 [flags] 01 ...; flags 0x60 = has CRC, 0x00 = notification without CRC
            if (buf[2] != 0x01 || (buf[1] != 0x60 && buf[1] != 0x00)) { buf.RemoveAt(0); continue; }
            bool hasCrc = buf[1] == 0x60;
            int len = buf[5];
            int total = 8 + len + (hasCrc ? 2 : 0);
            if (buf.Count < total) return;

            var frame = buf.GetRange(0, total).ToArray();
            if (hasCrc && Crc16(frame.Take(total - 2)) != (ushort)(frame[^2] | (frame[^1] << 8)))
            {
                buf.RemoveAt(0); // bad CRC, resync
                continue;
            }
            buf.RemoveRange(0, total);
            ushort cmd = (ushort)(frame[3] | (frame[4] << 8));
            Log.Write($"rx cmd=0x{cmd:X4} {Convert.ToHexString(frame)}");

            if (cmd == RespBattery || cmd == NotifyBattery)
            {
                var info = ParseBattery(frame.AsSpan(8, len));
                if (info != null) BatteryReceived?.Invoke(info);
            }
            else if ((cmd == RespAnc || cmd == NotifyAnc) && len >= 2)
            {
                AncReceived?.Invoke((AncMode)frame[9]);
            }
        }
    }

    static BatteryInfo? ParseBattery(ReadOnlySpan<byte> p)
    {
        if (p.Length < 1) return null;
        int count = p[0];
        BudState l = new(null, false), r = new(null, false), c = new(null, false);
        for (int i = 0; i < count; i++)
        {
            int idx = 1 + i * 2;
            if (idx + 1 >= p.Length) break;
            byte id = p[idx], val = p[idx + 1];
            var s = new BudState(val & 0x7F, (val & 0x80) != 0);
            switch (id)
            {
                case 0x02: l = s; break;
                case 0x03: r = s; break;
                case 0x04: c = s; break;
            }
        }
        return new BatteryInfo(l, r, c);
    }

    void HandleDisconnect()
    {
        Close();
        Disconnected?.Invoke();
    }

    public void Close()
    {
        _readCts?.Cancel();
        _readCts = null;
        try { _writer?.DetachStream(); } catch { }
        _writer?.Dispose();
        _writer = null;
        _socket?.Dispose();
        _socket = null;
    }

    public void Dispose() => Close();
}
