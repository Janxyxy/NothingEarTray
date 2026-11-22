using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NothingEarTray;

/// <summary>Borderless Win11-style popup shown from the tray icon.</summary>
sealed class Flyout : Form
{
    // state pushed in by TrayApp
    public BatteryInfo? Battery;
    public AncMode? Anc;
    public bool IsConnected;
    public bool Autostart;
    public string DeviceName = "Nothing Ear";

    public event Action<AncMode>? ModeClicked;
    public event Action? RefreshClicked;
    public event Action? AutostartClicked;
    public event Action? ExitClicked;

    AncMode _lastAncLevel = AncMode.High;
    DateTime _hiddenAt;
    readonly List<(RectangleF r, Action a)> _hits = new();
    Point _mouse = new(-1, -1);

    static readonly Color Bg = Color.FromArgb(28, 28, 28);
    static readonly Color Card = Color.FromArgb(40, 40, 40);
    static readonly Color Hover = Color.FromArgb(52, 52, 52);
    static readonly Color Text1 = Color.FromArgb(240, 240, 240);
    static readonly Color Text2 = Color.FromArgb(150, 150, 150);
    static readonly Color Track = Color.FromArgb(60, 60, 60);
    static readonly Color Red = Color.FromArgb(230, 30, 40);
    static readonly Color Green = Color.FromArgb(90, 220, 110);
    static readonly Color Yellow = Color.FromArgb(255, 190, 60);

    const int W = 340, H = 328;

    public Flyout()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        Deactivate += (_, _) => { Log.Write("flyout deactivate"); Hide(); _hiddenAt = DateTime.Now; };
        MouseMove += (_, e) => { _mouse = e.Location; Invalidate(); };
        MouseLeave += (_, _) => { _mouse = new(-1, -1); Invalidate(); };
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            foreach (var (r, a) in _hits) if (r.Contains(e.Location)) { a(); Invalidate(); break; }
        };
    }

    float Sc => DeviceDpi / 96f;
    float P(float v) => v * Sc;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; /* WS_EX_TOOLWINDOW */ return cp; }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);   // rounded corners
        int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4);     // dark border
    }

    public void Toggle()
    {
        if (Visible) { Hide(); return; }
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 250) return; // click on tray that just closed us
        FitSize();
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int x = Math.Min(Cursor.Position.X - Width / 2, wa.Right - Width - (int)P(12));
        Location = new Point(Math.Max(wa.Left + (int)P(12), x), wa.Bottom - Height - (int)P(12));
        Show();
        Activate();
        Log.Write($"flyout shown at {Location} {Size} visible={Visible}");
    }

    public void FitSize() => Size = new Size((int)P(W), (int)P(H));

    public void Refresh2() { if (Visible) Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        _hits.Clear();
        if (Anc is AncMode.High or AncMode.Mid or AncMode.Low or AncMode.Adaptive) _lastAncLevel = Anc.Value;

        using var fTitle = new Font("Segoe UI Semibold", 12f);
        using var fBody = new Font("Segoe UI", 9.5f);
        using var fSmall = new Font("Segoe UI", 8.5f);
        using var fBold = new Font("Segoe UI Semibold", 9.5f);
        using var fSeg = new Font("Segoe UI", 9f);

        float pad = P(16), y = P(16), w = Width - pad * 2;

        // header
        g.DrawString(DeviceName, fTitle, Brush(Text1), pad, y);
        var tw = g.MeasureString(DeviceName, fTitle).Width;
        g.FillEllipse(Brush(Red), pad + tw + P(2), y + P(6), P(7), P(7));
        string status = !IsConnected ? "Not connected" : "Connected";
        var sw = g.MeasureString(status, fSmall).Width;
        g.DrawString(status, fSmall, Brush(IsConnected ? Text2 : Yellow), Width - pad - sw, y + P(5));
        y += P(40);

        // battery card
        var card = new RectangleF(pad, y, w, P(104));
        FillRound(g, card, P(8), Card);
        float ry = y + P(12);
        Row("Left", Battery?.Left);
        Row("Right", Battery?.Right);
        Row("Case", Battery?.Case);
        y = card.Bottom + P(16);

        void Row(string label, BudState? s)
        {
            g.DrawString(label, fBody, Brush(Text2), card.X + P(12), ry);
            float bx = card.X + P(70), bw = card.Width - P(70) - P(84), by = ry + P(8), bh = P(6);
            FillRound(g, new RectangleF(bx, by, bw, bh), bh / 2, Track);
            string txt = "—";
            Color tc = Text2;
            if (s?.Percent is int p)
            {
                Color c = s.Charging ? Green : p <= 15 ? Red : p <= 35 ? Yellow : Text1;
                if (p > 0) FillRound(g, new RectangleF(bx, by, Math.Max(bh, bw * p / 100f), bh), bh / 2, c);
                txt = s.Charging ? $"{p}% · chg" : $"{p}%";
                tc = s.Charging ? Green : Text1;
            }
            var mw = g.MeasureString(txt, fBold).Width;
            g.DrawString(txt, fBold, Brush(tc), card.Right - P(12) - mw, ry);
            ry += P(28);
        }

        // listening mode
        g.DrawString("Listening mode", fSmall, Brush(Text2), pad, y);
        y += P(22);
        var seg = new RectangleF(pad, y, w, P(36));
        FillRound(g, seg, P(8), Card);
        var modes = new (string label, AncMode mode, bool on)[]
        {
            ("Off", AncMode.Off, Anc == AncMode.Off),
            ("Transparency", AncMode.Transparency, Anc == AncMode.Transparency),
            ("Noise cancel", _lastAncLevel, Anc is AncMode.High or AncMode.Mid or AncMode.Low or AncMode.Adaptive),
        };
        float segW = (seg.Width - P(8)) / modes.Length;
        for (int i = 0; i < modes.Length; i++)
        {
            var (label, mode, on) = modes[i];
            var r = new RectangleF(seg.X + P(4) + i * segW, seg.Y + P(4), segW, seg.Height - P(8));
            Button(r, label, on, () => ModeClicked?.Invoke(mode), fSeg, P(6));
        }
        y = seg.Bottom + P(10);

        // ANC level chips
        bool ancOn = modes[2].on;
        var levels = new (string, AncMode)[] { ("High", AncMode.High), ("Mid", AncMode.Mid), ("Low", AncMode.Low), ("Adaptive", AncMode.Adaptive) };
        float gap = P(6), cw = (w - gap * (levels.Length - 1)) / levels.Length;
        for (int i = 0; i < levels.Length; i++)
        {
            var (label, mode) = levels[i];
            var r = new RectangleF(pad + i * (cw + gap), y, cw, P(28));
            Chip(r, label, ancOn && Anc == mode, ancOn, () => ModeClicked?.Invoke(mode), fSmall);
        }
        y += P(28) + P(16);

        // footer
        using (var pen = new Pen(Track, 1)) g.DrawLine(pen, 0, y, Width, y);
        y += P(8);
        var auto = new RectangleF(pad - P(6), y, P(150), P(30));
        if (auto.Contains(_mouse)) FillRound(g, auto, P(6), Hover);
        var box = new RectangleF(auto.X + P(8), auto.Y + P(8), P(14), P(14));
        if (Autostart) { FillRound(g, box, P(3), Text1); using var pk = new Pen(Bg, P(2)); g.DrawLines(pk, new[] { new PointF(box.X + P(3), box.Y + P(7)), new PointF(box.X + P(6), box.Y + P(10)), new PointF(box.X + P(11), box.Y + P(4)) }); }
        else using (var pb = new Pen(Text2, P(1.2f))) g.DrawPath(pb, RoundPath(box, P(3)));
        g.DrawString("Start with Windows", fSmall, Brush(Text2), box.Right + P(8), auto.Y + P(7));
        _hits.Add((auto, () => AutostartClicked?.Invoke()));

        var exit = new RectangleF(Width - pad - P(50) + P(6), y, P(50), P(30));
        var refr = new RectangleF(exit.X - P(70), y, P(66), P(30));
        TextButton(refr, "Refresh", () => RefreshClicked?.Invoke(), fSmall);
        TextButton(exit, "Exit", () => ExitClicked?.Invoke(), fSmall);

        void Button(RectangleF r, string label, bool on, Action a, Font f, float rad)
        {
            if (on) FillRound(g, r, rad, Text1);
            else if (r.Contains(_mouse)) FillRound(g, r, rad, Hover);
            Center(label, f, on ? Bg : Text1, r);
            _hits.Add((r, a));
        }

        void Chip(RectangleF r, string label, bool on, bool enabled, Action a, Font f)
        {
            if (on) FillRound(g, r, r.Height / 2, Red);
            else if (r.Contains(_mouse)) FillRound(g, r, r.Height / 2, Hover);
            else using (var pen = new Pen(Track, P(1))) g.DrawPath(pen, RoundPath(r, r.Height / 2));
            Center(label, f, on ? Color.White : enabled ? Text1 : Text2, r);
            _hits.Add((r, a));
        }

        void TextButton(RectangleF r, string label, Action a, Font f)
        {
            if (r.Contains(_mouse)) FillRound(g, r, P(6), Hover);
            Center(label, f, Text2, r);
            _hits.Add((r, a));
        }

        void Center(string s, Font f, Color c, RectangleF r)
        {
            using var sf = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.None };
            g.DrawString(s, f, Brush(c), r, sf);
        }
    }

    readonly Dictionary<Color, SolidBrush> _brushes = new();
    SolidBrush Brush(Color c) => _brushes.TryGetValue(c, out var b) ? b : _brushes[c] = new SolidBrush(c);

    void FillRound(Graphics g, RectangleF r, float rad, Color c)
    {
        using var p = RoundPath(r, rad);
        g.FillPath(Brush(c), p);
    }

    static GraphicsPath RoundPath(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var b in _brushes.Values) b.Dispose();
        base.Dispose(disposing);
    }
}
