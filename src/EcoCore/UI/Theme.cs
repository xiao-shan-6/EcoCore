using System.Drawing.Drawing2D;

namespace EcoCore.UI;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(16, 18, 24);
    public static readonly Color Panel = Color.FromArgb(24, 27, 35);
    public static readonly Color Panel2 = Color.FromArgb(31, 35, 45);
    public static readonly Color Border = Color.FromArgb(45, 51, 64);
    public static readonly Color Text = Color.FromArgb(226, 232, 240);
    public static readonly Color TextDim = Color.FromArgb(138, 149, 167);
    public static readonly Color Accent = Color.FromArgb(56, 189, 248);   // 青蓝
    public static readonly Color Green = Color.FromArgb(52, 211, 153);
    public static readonly Color Amber = Color.FromArgb(251, 191, 36);
    public static readonly Color Red = Color.FromArgb(248, 113, 113);
    public static readonly Color Eco = Color.FromArgb(74, 222, 128);     // E 核绿
    public static readonly Color Perf = Color.FromArgb(129, 140, 248);   // P 核紫

    public static readonly Font TitleFont = new("Microsoft YaHei UI", 15f, FontStyle.Bold);
    public static readonly Font H3Font = new("Microsoft YaHei UI", 12.5f, FontStyle.Bold);
    public static readonly Font H2Font = new("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
    public static readonly Font BodyFont = new("Microsoft YaHei UI", 9f);
    public static readonly Font SmallFont = new("Microsoft YaHei UI", 8.5f);
    public static readonly Font MonoFont = new("Microsoft YaHei UI", 9f);

    /// <summary>当前控件的 DPI 缩放系数（96 DPI = 1.0）。</summary>
    public static float Scale(Control c) => c.DeviceDpi <= 0 ? 1f : c.DeviceDpi / 96f;

    /// <summary>把 96 DPI 下的逻辑像素换算成当前 DPI 的设备像素。</summary>
    public static int Px(Control c, int v) => (int)Math.Round(v * Scale(c));

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0) { path.AddRectangle(r); return path; }
        int d = radius * 2;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
    {
        using var p = RoundRect(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void StrokeRound(Graphics g, Rectangle r, int radius, Color c, float w = 1f)
    {
        using var p = RoundRect(r, radius);
        using var pen = new Pen(c, w);
        g.DrawPath(pen, p);
    }
}

/// <summary>圆角扁平按钮。</summary>
internal sealed class EcoButton : Control
{
    public bool Primary { get; set; }
    public bool Danger { get; set; }
    private bool _hover, _down;

    public EcoButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = Theme.BodyFont;
        Height = 34;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        Color fill = Primary ? Theme.Accent : Theme.Panel2;
        Color text = Primary ? Color.FromArgb(9, 20, 28) : Theme.Text;
        if (Danger) { fill = Color.FromArgb(70, 30, 35); text = Theme.Red; }

        if (_hover) fill = Primary ? ControlPaint.Light(fill, 0.12f) : ControlPaint.Light(fill, 0.18f);
        if (_down) fill = ControlPaint.Dark(fill, 0.10f);
        if (!Enabled) { fill = Theme.Panel; text = Theme.TextDim; }

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, r, 8, fill);
        if (!Primary && !Danger) Theme.StrokeRound(g, r, 8, Theme.Border);

        TextRenderer.DrawText(g, Text, Font, r, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>自绘开关。</summary>
internal sealed class EcoToggle : Control
{
    private bool _on;
    private bool _locked;
    public event EventHandler? CheckedChanged;

    /// <summary>锁定后不可点、画成灰态 —— 预设方案用，防用户乱改。</summary>
    public bool Locked
    {
        get => _locked;
        set { if (_locked != value) { _locked = value; Cursor = value ? Cursors.Default : Cursors.Hand; Invalidate(); } }
    }

    public bool Checked
    {
        get => _on;
        set { if (_on != value) { _on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }
    }

    public EcoToggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 22;
        Cursor = Cursors.Hand;
        Font = Theme.BodyFont;
    }

    protected override void OnClick(EventArgs e)
    {
        if (_locked) { base.OnClick(e); return; }
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        int sw = Theme.Px(this, 40), sh = Theme.Px(this, 20), sy = (Height - sh) / 2;
        int pad = Math.Max(1, Theme.Px(this, 2));
        var track = new Rectangle(0, sy, sw, sh);
        Color trackCol = _locked
            ? (_on ? Color.FromArgb(50, 84, 76) : Color.FromArgb(42, 46, 58))
            : (_on ? Theme.Green : Color.FromArgb(58, 64, 78));
        Theme.FillRound(g, track, sh / 2, trackCol);

        int kx = _on ? sw - sh + pad : pad;
        var knob = new Rectangle(kx, sy + pad, sh - pad * 2, sh - pad * 2);
        using (var b = new SolidBrush(_locked ? Color.FromArgb(150, 158, 172) : Color.White))
            g.FillEllipse(b, knob);

        int tx = sw + Theme.Px(this, 10);
        var textRect = new Rectangle(tx, 0, Math.Max(0, Width - tx), Height);
        Color textCol = _locked ? Color.FromArgb(108, 118, 136) : (_on ? Theme.Text : Theme.TextDim);
        TextRenderer.DrawText(g, Text, Font, textRect, textCol,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>自绘滑块，带数值显示。</summary>
internal sealed class EcoSlider : Control
{
    private int _value = 50;
    private bool _drag;
    private bool _locked;

    public int Min { get; set; } = 0;
    public int Max { get; set; } = 100;
    public string Unit { get; set; } = "%";
    public event EventHandler? ValueChanged;

    /// <summary>锁定后不可拖、画成灰态。</summary>
    public bool Locked
    {
        get => _locked;
        set { if (_locked != value) { _locked = value; Cursor = value ? Cursors.Default : Cursors.Hand; Invalidate(); } }
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, Min, Max);
            if (_value != v) { _value = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
        }
    }

    public EcoSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 22;
        Cursor = Cursors.Hand;
        Font = Theme.BodyFont;
    }

    private int TrackLeft => 0;
    private int TrackRight => Math.Max(TrackLeft + Theme.Px(this, 20), Width - Theme.Px(this, 62));

    protected override void OnMouseDown(MouseEventArgs e) { if (!_locked) { _drag = true; SetFromX(e.X); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag && !_locked) SetFromX(e.X); base.OnMouseMove(e); }

    private void SetFromX(int x)
    {
        double frac = (double)(x - TrackLeft) / Math.Max(1, TrackRight - TrackLeft);
        Value = Min + (int)Math.Round(Math.Clamp(frac, 0, 1) * (Max - Min));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        int cy = Height / 2;
        int l = TrackLeft, r = TrackRight;
        int trackW = Math.Max(2, Theme.Px(this, 4));
        using (var pen = new Pen(Color.FromArgb(58, 64, 78), trackW) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, l, cy, r, cy);

        double frac = (double)(_value - Min) / Math.Max(1, Max - Min);
        int kx = l + (int)Math.Round(frac * (r - l));

        Color accent = _locked ? Color.FromArgb(78, 96, 112) : Theme.Accent;
        using (var pen = new Pen(accent, trackW) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, l, cy, kx, cy);

        int kr = Theme.Px(this, 6), kir = Theme.Px(this, 3);
        using (var b = new SolidBrush(_locked ? Color.FromArgb(150, 158, 172) : Color.White))
            g.FillEllipse(b, kx - kr, cy - kr, kr * 2, kr * 2);
        using (var b = new SolidBrush(accent)) g.FillEllipse(b, kx - kir, cy - kir, kir * 2, kir * 2);

        int tx = r + Theme.Px(this, 10);
        var tr = new Rectangle(tx, 0, Math.Max(0, Width - tx), Height);
        TextRenderer.DrawText(g, _value + Unit, Font, tr,
            _locked ? Color.FromArgb(108, 118, 136) : Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>大小核拓扑可视化面板。</summary>
internal sealed class TopologyPanel : Control
{
    public Core.CpuTopology? Topology { get; set; }
    public bool IsEcoActive { get; set; }
    /// <summary>留给系统的大核数量，这几个会画成带描边的"保留"样式。</summary>
    public int ReservedCoreCount { get; set; } = 1;

    public TopologyPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 92;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, r, 10, Theme.Panel);
        Theme.StrokeRound(g, r, 10, Theme.Border);

        if (Topology is null || Topology.All.Count == 0)
        {
            TextRenderer.DrawText(g, "未读取到 CPU 拓扑", Theme.BodyFont,
                r, Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int pad = Theme.Px(this, 14);
        int y = pad;

        if (Topology.IsHybrid)
        {
            var p = Topology.PerformanceCores
                    .OrderBy(c => c.CoreIndex).ToList();   // 与拓扑枚举的保留顺序保持一致
            var eco = Topology.EfficiencyCores;

            DrawGroup(g, $"P 核 · 性能核 ×{p.Count}", p.Count, pad, ref y, Theme.Perf, IsEcoActive, ReservedCoreCount);
            y += Theme.Px(this, 6);
            DrawGroup(g, $"E 核 · 能效核 ×{eco.Count}", eco.Count, pad, ref y, Theme.Eco, false, 0);

            // 右上角图例
            int lw = Theme.Px(this, 190);
            var legend = new Rectangle(Width - lw, Theme.Px(this, 10), lw - pad, Theme.Px(this, 18));
            TextRenderer.DrawText(g, "描边 = 留给系统的大核", Theme.SmallFont, legend, Theme.TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
        else
        {
            DrawGroup(g, $"处理器 ×{Topology.All.Count}", Topology.All.Count, pad, ref y, Theme.Accent, false, 0);
        }
    }

    private void DrawGroup(Graphics g, string label, int count, int pad, ref int y,
                           Color active, bool dimmed, int reservedPrefix)
    {
        int lblW = Theme.Px(this, 150);
        int lblH = Theme.Px(this, 18);
        var lblRect = new Rectangle(pad, y, lblW, lblH);
        TextRenderer.DrawText(g, label, Theme.SmallFont, lblRect, Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        int bx = pad + Theme.Px(this, 158);
        int by = y;
        int size = Theme.Px(this, 15), gap = Theme.Px(this, 4);
        int maxX = Width - pad - Theme.Px(this, 10);

        using var fillNormal = new SolidBrush(dimmed ? Color.FromArgb(34, 38, 48) : active);
        using var fillReserved = new SolidBrush(Color.FromArgb(56, 48, 92));
        using var penNormal = new Pen(dimmed ? Color.FromArgb(52, 58, 72) : ControlPaint.Light(active, 0.25f), 1f);
        using var penReserved = new Pen(Color.FromArgb(196, 181, 253), 1.6f);

        int cx = bx;
        for (int i = 0; i < count; i++)
        {
            if (cx + size > maxX) { cx = bx; by += size + gap; }
            var cell = new Rectangle(cx, by, size, size);
            bool reserved = i < reservedPrefix;

            using (var path = Theme.RoundRect(cell, Theme.Px(this, 3)))
            {
                g.FillPath(reserved ? fillReserved : fillNormal, path);
                g.DrawPath(reserved ? penReserved : penNormal, path);
            }
            cx += size + gap;
        }

        y = by + size + Theme.Px(this, 4);

        if (dimmed && reservedPrefix == 0)
        {
            var note = new Rectangle(bx + count * (size + gap) + Theme.Px(this, 6), by, Theme.Px(this, 100), size);
            TextRenderer.DrawText(g, "已降频", Theme.SmallFont, note, Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }
}
