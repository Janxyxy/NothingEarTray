using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NothingEarTray;

static class Log
{
    static readonly string PathFile = Path.Combine(AppContext.BaseDirectory, "NothingEarTray.log");
    public static void Write(string msg)
    {
        try
        {
            var fi = new FileInfo(PathFile);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(PathFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--render-flyout")
        {
            ApplicationConfiguration.Initialize();
            using var f = new Flyout { IsConnected = true, Anc = AncMode.Mid,
                Battery = new BatteryInfo(new(50, false), new(75, false), new(12, true)) };
            f.FitSize();
            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--render-icons")
        {
            // showcase strip: normal, low, critical, charging on a dark taskbar-like background
            var states = new (string l, int? p, bool c)[] { ("75", 75, false), ("30", 30, false), ("12", 12, false), ("50", 50, true) };
            const int px = 96, gap = 32, pad = 28;
            using var strip = new Bitmap(pad * 2 + states.Length * px + (states.Length - 1) * gap, px + pad * 2);
            using (var g = Graphics.FromImage(strip))
            {
                g.Clear(Color.FromArgb(32, 32, 32));
                for (int i = 0; i < states.Length; i++)
                {
                    using var ic = TrayApp.DrawIcon(states[i].l, TrayApp.ColorFor(states[i].p), states[i].c, px);
                    g.DrawImage(ic, pad + i * (px + gap), pad);
                }
            }
            strip.Save(args[1]);
            return;
        }
        if (args.Contains("--test-anc")) { TestAnc().GetAwaiter().GetResult(); return; }
        using var mutex = new Mutex(true, "NothingEarTray_single", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        TrayApp.ShowOnStart = args.Contains("--flyout");
        Application.Run(new TrayApp());
    }

    // Diagnostic: read ANC, switch to the other of Off/Transparency, read back, restore. Results go to the log.
    static async Task TestAnc()
    {
        using var ear = new EarClient();
        AncMode? seen = null;
        ear.AncReceived += m => { seen = m; Log.Write($"TEST anc = {m}"); };
        if (!await ear.ConnectAsync()) { Log.Write("TEST connect failed"); return; }
        await ear.RequestAncAsync(); await Task.Delay(800);
        var original = seen;
        var target = original == AncMode.Transparency ? AncMode.Off : AncMode.Transparency;
        Log.Write($"TEST set {target}");
        await ear.SetAncAsync(target); await Task.Delay(800);
        seen = null; await ear.RequestAncAsync(); await Task.Delay(800);
        Log.Write(seen == target ? "TEST switch OK" : $"TEST switch FAILED (got {seen})");
        if (original != null) { await ear.SetAncAsync(original.Value); await Task.Delay(800); await ear.RequestAncAsync(); await Task.Delay(800); }
    }
}

sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "NothingEarTray";

    readonly NotifyIcon _tray;
    readonly EarClient _ear = new();
    readonly System.Windows.Forms.Timer _timer;
    readonly SynchronizationContext _ui;
    public static bool ShowOnStart;
    readonly Flyout _flyout = new();
    AncMode? _anc;
    Icon? _currentIcon;
    BatteryInfo? _last;
    bool _busy;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _flyout.Autostart = IsAutostart();
        _flyout.ModeClicked += async m => await SetMode(m);
        _flyout.RefreshClicked += async () => await Tick();
        _flyout.AutostartClicked += () => { SetAutostart(!IsAutostart()); _flyout.Autostart = IsAutostart(); _flyout.Refresh2(); };
        _flyout.ExitClicked += () => ExitThread();
        _flyout.CreateControl();

        _tray = new NotifyIcon { Visible = true };
        _tray.MouseUp += async (_, e) =>
        {
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
            _flyout.Toggle();
            if (_flyout.Visible) await Tick();
        };
        ShowDisconnected();

        _ear.BatteryReceived += b => _ui.Post(_ => ShowBattery(b), null);
        _ear.Disconnected += () => _ui.Post(_ => ShowDisconnected(), null);
        _ear.AncReceived += m => _ui.Post(_ => ShowAnc(m), null);

        _timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _timer.Tick += async (_, _) => await Tick();
        _timer.Start();
        _ = Tick();

        if (ShowOnStart)
        {
            var t = new System.Windows.Forms.Timer { Interval = 2500 };
            t.Tick += (_, _) => { t.Stop(); _flyout.Toggle(); };
            t.Start();
        }
    }

    async Task SetMode(AncMode mode)
    {
        if (!_ear.Connected && !await _ear.ConnectAsync()) return;
        await _ear.SetAncAsync(mode);
        ShowAnc(mode);
        await Task.Delay(400);
        await _ear.RequestAncAsync(); // read back actual state
    }

    void ShowAnc(AncMode mode)
    {
        _anc = mode;
        _flyout.Anc = mode;
        _flyout.Refresh2();
        if (_last != null) ShowBattery(_last);
    }

    static string AncName(AncMode? m) => m switch
    {
        AncMode.High => "ANC high", AncMode.Mid => "ANC mid", AncMode.Low => "ANC low",
        AncMode.Adaptive => "ANC adaptive", AncMode.Transparency => "Transparency", AncMode.Off => "ANC off",
        _ => "",
    };

    async Task Tick()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!_ear.Connected && !await _ear.ConnectAsync())
            {
                ShowDisconnected();
                return;
            }
            await _ear.RequestBatteryAsync();
            await _ear.RequestAncAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"tick: {ex.Message}");
        }
        finally { _busy = false; }
    }

    void ShowBattery(BatteryInfo b)
    {
        _last = b;
        _flyout.Battery = b; _flyout.IsConnected = true; _flyout.DeviceName = _ear.DeviceName ?? "Nothing Ear"; _flyout.Refresh2();
        int? low = new[] { b.Left.Percent, b.Right.Percent }.Where(x => x.HasValue).Min();
        bool charging = b.Left.Charging || b.Right.Charging;
        SetIcon(low?.ToString() ?? "?", ColorFor(low), charging);

        static string Fmt(string n, BudState s) =>
            s.Percent is int p ? $"{n} {p}%{(s.Charging ? " charging" : "")}" : $"{n} –";
        string text = $"{_ear.DeviceName ?? "Nothing Ear"}\n{Fmt("L", b.Left)}  {Fmt("R", b.Right)}\n{Fmt("Case", b.Case)}{(_anc != null ? "  · " + AncName(_anc) : "")}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    void ShowDisconnected()
    {
        _last = null;
        _flyout.Battery = null; _flyout.IsConnected = false; _flyout.Anc = null; _flyout.Refresh2();
        SetIcon("–", Color.Gray, false);
        _tray.Text = "Nothing Ear: not connected";
    }

    public static Color ColorFor(int? p) => p switch
    {
        null => Color.Gray,
        <= 15 => Color.FromArgb(255, 80, 80),
        <= 35 => Color.FromArgb(255, 190, 60),
        _ => Color.White,
    };

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    public static Bitmap DrawIcon(string label, Color color, bool charging, int px = 32)
    {
        const int S = 32;
        var bmp = new Bitmap(px, px);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            g.ScaleTransform(px / (float)S, px / (float)S);
            // identifier: rounded outline + red dot (Nothing style) in the top-right corner
            using (var path = RoundedRect(new RectangleF(1.5f, 1.5f, S - 3, S - 3), 7))
            using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 2.5f))
                g.DrawPath(pen, path);

            float size = label.Length >= 3 ? 13f : 18f;
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, brush, new RectangleF(-2, 1, S + 4, S), fmt);

            using (var dot = new SolidBrush(Color.FromArgb(230, 30, 40)))
                g.FillEllipse(dot, S - 11, 0, 11, 11);

            if (charging)
            {
                using var bar = new SolidBrush(Color.FromArgb(90, 220, 110));
                g.FillRectangle(bar, 6, S - 7, S - 12, 3);
            }
        }
        return bmp;
    }

    void SetIcon(string label, Color color, bool charging)
    {
        using var bmp = DrawIcon(label, color, charging);
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        _tray.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    public static GraphicsPath RoundedRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) != null;
    }

    static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
        if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(RunName, false);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _ear.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _flyout.Dispose();
        _currentIcon?.Dispose();
        base.ExitThreadCore();
    }
}
