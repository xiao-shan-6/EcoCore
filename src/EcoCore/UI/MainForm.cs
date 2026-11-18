using System.Drawing.Drawing2D;
using EcoCore.Core;

namespace EcoCore.UI;

public sealed class MainForm : Form
{
    private readonly Coordinator _co;
    private readonly AppSettings _st;

    private Panel _root = null!;
    private DetailPage? _detailPage;
    private int _mainPageHeight;
    private TopologyPanel _topo = null!;
    private Panel _header = null!;
    private Label _desc = null!;
    private Label _entryHint = null!;
    private TextBox _log = null!;
    private readonly List<EcoButton> _profileBtns = new();

    private NotifyIcon _tray = null!;
    private System.Windows.Forms.Timer _tick = null!;
    private BatteryState _battery = new();
    private bool _lastOnAc;
    private bool _allowExit;
    private Panel? _footer;

    /// <summary>当前选中的方案参数（主界面唯一的数据来源，精细调节改的就是它）。</summary>
    private EcoProfile _working = EcoProfile.Balanced();

    /// <summary>调试/截图用：强制以某个方案启动（不写盘）。</summary>
    public static EcoProfile? StartupProfileOverride;

    /// <summary>调试/截图用：启动后立刻打开「精细调节」窗口。</summary>
    public static bool StartupOpenDetail;
    private bool _built;

    private int _cw = 840;          // 内容宽度（设备像素）
    private int Px(int v) => Theme.Px(this, v);

    public MainForm()
    {
        _co = new Coordinator();
        _st = _co.Settings;
        _co.Log += AppendLog;

        Text = "节能核心 EcoCore";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.BodyFont;
        StartPosition = FormStartPosition.CenterScreen;

        // 布局全部按 96 DPI 逻辑像素书写，再用手工换算到当前 DPI。
        // 用 AutoScaleMode.None 是为了避免 WinForms 二次缩放导致自绘文字错位。
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Icon = BuildIcon();

        FormClosing += OnFormClosing;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_built) return;
        _built = true;

        ApplyWindowSize();
        BuildUi();
        BuildTray();

        _battery = BatteryState.Read();
        _lastOnAc = _battery.OnAc;

        _tick = new System.Windows.Forms.Timer { Interval = 2000 };
        _tick.Tick += OnTick;
        _tick.Start();

        LoadProfileIntoUi(StartupProfileOverride
            ?? (_st.ActiveProfileName == EcoProfile.CustomName ? _st.CustomProfile : _st.CurrentProfile));
        AppendLog($"DPI={DeviceDpi} 缩放={Theme.Scale(this):0.##}  窗口={ClientSize.Width}×{ClientSize.Height}");
        AppendLog($"CPU：{_co.Topology.CpuName}");
        AppendLog($"拓扑：{_co.Topology.Describe()}");
        if (!_co.Topology.SupportsIsolation)
            AppendLog("⚠ 本机不是大小核混合架构（或没有检测到 E 核）—— 进程绑核不可用，" +
                      "只会应用电源方案层（频率上限 / 睿频 / EPP / 电源滑块）。");
        AppendLog($"当前电源方案：{_co.CurrentSchemeInfo()}");
        if (!Coordinator.IsElevated())
            AppendLog("⚠ 未以管理员身份运行 —— 进程绑核 / 效率模式照常生效，但「频率上限 / 睿频 / EPP / 电源滑块」改不动。" +
                      "要全部功能请右键「以管理员身份运行」。");

        if (_st.StartMinimized) BeginInvoke(HideToTray);
        if (StartupOpenDetail) BeginInvoke(OpenDetailWindow);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutChrome();
    }

    /// <summary>按当前 DPI 和屏幕可用区域定一个不会超出屏幕的窗口尺寸。</summary>
    private void ApplyWindowSize()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int w = Math.Min(Px(940), wa.Width - Px(24));
        int h = Math.Min(Px(1000), wa.Height - Px(24));
        ClientSize = new Size(w, h);
        MinimumSize = new Size(Px(620), Px(520));
    }

    // ───────────────────────── UI 构建 ─────────────────────────

    private void BuildUi()
    {
        int M = Px(16);                                   // 左右外边距
        int reserve = Px(24);                             // 预留滚动条宽度
        _cw = Math.Max(Px(600), ClientSize.Width - M * 2 - reserve);

        _root = new Panel
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoScroll = true,
            BackColor = Theme.Bg,
            Padding = new Padding(0)
        };

        // 底部按钮条固定在窗口底部，不随内容滚动 —— 屏幕再矮也点得到「应用」
        _footer = new Panel
        {
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Height = Px(46) + Px(16),
            BackColor = Theme.Bg
        };
        Controls.Add(_root);
        Controls.Add(_footer);

        int y = Px(10);

        // ── 头部 ──
        _header = new Panel { Location = new Point(M, y), Size = new Size(_cw, Px(74)), BackColor = Theme.Bg };
        _header.Paint += PaintHeader;
        _root.Controls.Add(_header);
        y += _header.Height + Px(10);

        // ── 拓扑 ──
        _topo = new TopologyPanel
        {
            Location = new Point(M, y),
            Size = new Size(_cw, Px(74)),
            Topology = _co.Topology
        };
        _root.Controls.Add(_topo);
        y += _topo.Height + Px(16);

        // ── 方案按钮 ──
        y = AddSectionLabel("续航方案", y);
        var presets = EcoProfile.Presets();
        int gap = Px(10);
        int bw = (_cw - gap * (presets.Length - 1)) / presets.Length;
        for (int i = 0; i < presets.Length; i++)
        {
            var pr = presets[i];
            var b = new EcoButton
            {
                Text = pr.Name,
                Location = new Point(M + i * (bw + gap), y),
                Size = new Size(bw, Px(44)),
                Font = Theme.H2Font
            };
            b.Click += (_, _) => LoadProfileIntoUi(pr);
            _root.Controls.Add(b);
            _profileBtns.Add(b);
        }
        y += Px(44) + Px(12);

        _desc = new Label
        {
            Location = new Point(M, y),
            AutoSize = true,
            MaximumSize = new Size(_cw, 0),
            ForeColor = Theme.TextDim,
            Font = Theme.SmallFont,
            Text = _st.CurrentProfile?.Description ?? ""
        };
        _root.Controls.Add(_desc);
        y += Px(56);                                   // 固定留两行，预设模式会多一句"数值已锁定"

        // ── 精细调节入口：点开是独立的第二界面 ──
        var entry = new Panel
        {
            Location = new Point(M, y),
            Size = new Size(_cw, Px(76)),
            BackColor = Theme.Panel
        };
        entry.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(e.Graphics, new Rectangle(0, 0, entry.Width - 1, entry.Height - 1), Px(10), Theme.Panel);
            Theme.StrokeRound(e.Graphics, new Rectangle(0, 0, entry.Width - 1, entry.Height - 1), Px(10), Theme.Border);
        };
        _root.Controls.Add(entry);

        var entryTitle = new Label
        {
            Text = "精细调节",
            Location = new Point(Px(20), Px(12)),
            Size = new Size(Px(300), Px(26)),
            ForeColor = Theme.Text,
            Font = Theme.H2Font,
            TextAlign = ContentAlignment.MiddleLeft
        };
        entry.Controls.Add(entryTitle);

        _entryHint = new Label
        {
            Location = new Point(Px(20), Px(40)),
            Size = new Size(_cw - Px(190), Px(24)),
            ForeColor = Theme.TextDim,
            Font = Theme.SmallFont,
            TextAlign = ContentAlignment.MiddleLeft
        };
        entry.Controls.Add(_entryHint);

        var openDetail = new EcoButton
        {
            Text = "打开…",
            Primary = true,
            Location = new Point(_cw - Px(150), Px(16)),
            Size = new Size(Px(130), Px(44)),
            Font = Theme.H2Font
        };
        openDetail.Click += (_, _) => OpenDetailWindow();
        entry.Controls.Add(openDetail);

        y += entry.Height + Px(16);

        // ── 日志 ──
        y = AddSectionLabel("执行日志", y);
        _log = new TextBox
        {
            Location = new Point(M, y),
            Size = new Size(_cw, Px(120)),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(13, 15, 20),
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = Theme.MonoFont,          // 微软雅黑，中文不缺字
            BorderStyle = BorderStyle.FixedSingle
        };
        _root.Controls.Add(_log);
        y += Px(120) + Px(14);

        // ── 底部按钮 ──
        var apply = new EcoButton
        {
            Text = "应用当前方案",
            Primary = true,
            Location = new Point(M, Px(8)),
            Size = new Size(Px(190), Px(46)),
            Font = Theme.H2Font
        };
        apply.Click += (_, _) => DoApply();
        _footer.Controls.Add(apply);

        var restore = new EcoButton
        {
            Text = "全部恢复默认",
            Danger = true,
            Location = new Point(M + Px(202), Px(8)),
            Size = new Size(Px(166), Px(46))
        };
        restore.Click += (_, _) => DoRestore();
        _footer.Controls.Add(restore);

        var procs = new EcoButton
        {
            Text = "进程列表…",
            Location = new Point(M + Px(380), Px(8)),
            Size = new Size(Px(136), Px(46))
        };
        procs.Click += (_, _) => OpenProcessWindow();
        _footer.Controls.Add(procs);

        var tray = new EcoButton
        {
            Text = "最小化到托盘",
            Location = new Point(M + Px(528), Px(8)),
            Size = new Size(Px(150), Px(46))
        };
        tray.Click += (_, _) => HideToTray();
        _footer.Controls.Add(tray);

        // 内容撑多高窗口就开多高，不超过屏幕可用高度；剩余高度全给日志框
        RelayoutTail();
    }

    /// <summary>切到「精细调节」二级界面：同一窗口内换页面，不弹新窗口。</summary>
    private void OpenDetailWindow()
    {
        _detailPage ??= new DetailPage(_co) { Visible = false };
        if (_detailPage.Parent == null)
        {
            Controls.Add(_detailPage);
            _detailPage.Applied += OnDetailApplied;
            _detailPage.Back += CloseDetailPage;
        }

        // 主页面整套藏起来，视觉上就是"进了下一级"
        _mainPageHeight = Height;
        _root.Visible = false;
        if (_footer != null) _footer.Visible = false;

        // 先按当前尺寸建/摆一次，量出内容高度，再把窗口撑到装得下（不超过屏幕可用高度）
        _detailPage.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        _detailPage.OpenProfile(_working);

        var wa = Screen.FromControl(this).WorkingArea;
        int want = _detailPage.DesiredHeight + (Height - ClientSize.Height);
        if (want > Height) Height = Math.Min(want, wa.Height - Px(8));

        _detailPage.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        _detailPage.OpenProfile(_working);      // 尺寸变过，重新摆一次

        _detailPage.Visible = true;
        _detailPage.BringToFront();
        _detailPage.Focus();
        AppendLog("进入精细调节界面");
    }

    /// <summary>从「精细调节」返回主页面。</summary>
    private void CloseDetailPage()
    {
        if (_detailPage != null) _detailPage.Visible = false;
        _root.Visible = true;
        if (_footer != null) _footer.Visible = true;
        if (_mainPageHeight > 0) Height = _mainPageHeight;
        LayoutChrome();
        _root.Focus();
    }

    /// <summary>精细调节点「应用并生效」后的回调：落到设置里，刷新主界面，立刻生效。</summary>
    private void OnDetailApplied(EcoProfile p)
    {
        _working = p.Clone();
        _st.CurrentProfile = p.Clone();
        _st.ActiveProfileName = p.Name;
        if (p.IsCustom) _st.CustomProfile = p.Clone();
        StateStore.Save(_st);

        LoadProfileIntoUi(p);
        AppendLog($"精细调节已更新：[{p.Name}] 频率上限 {p.ThrottleMax}% / EPP {p.PerfEpp} / " +
                  $"保留大核 {p.ReservedPerformanceCores} 个");
        DoApply();
        CloseDetailPage();      // 应用完自动回到主页面
    }

    /// <summary>
    /// 按"日志框底部"重算窗口高度、滚动范围和日志框高度。
    /// 折叠 / 展开、加控件之后都调它，保证一屏填满、装不下才出滚动条。
    /// </summary>
    private void RelayoutTail()
    {
        if (_log is null || _root is null || _footer is null) return;

        int contentH = _log.Bottom + Px(16);
        var wa = Screen.FromControl(this).WorkingArea;
        int chrome = Height - ClientSize.Height;              // 标题栏 + 边框
        int maxH = wa.Height - Px(8);
        int wantH = contentH + chrome + _footer.Height;
        Height = Math.Min(wantH, maxH);
        if (Height < MinimumSize.Height) Height = MinimumSize.Height;

        LayoutChrome();

        int slack = _root.ClientSize.Height - contentH;
        if (slack != 0)
        {
            int oldH = _log.Height;
            int newH = Math.Clamp(oldH + slack, Px(44), Px(420));
            if (newH != oldH)
            {
                _log.Height = newH;
                contentH += newH - oldH;
            }
        }

        _root.AutoScrollMinSize = _root.ClientSize.Height >= contentH
            ? new Size(0, 0)
            : new Size(_cw + Px(16) * 2, contentH);
    }

    /// <summary>手动摆放"滚动内容区 + 固定底部按钮条"，比 Dock 的 z 序规则更可控。</summary>
    private void LayoutChrome()
    {
        if (_root is null || _footer is null) return;
        int fh = _footer.Height > 0 ? _footer.Height : Px(46) + Px(16);
        int h = ClientSize.Height;
        _footer.Bounds = new Rectangle(0, Math.Max(0, h - fh), ClientSize.Width, fh);
        _root.Bounds = new Rectangle(0, 0, ClientSize.Width, Math.Max(0, h - fh));
    }

    private int AddSectionLabel(string text, int y) => AddSectionLabel(text, y, out _);

    private int AddSectionLabel(string text, int y, out Label label)
    {
        label = new Label
        {
            Text = text,
            Location = new Point(Px(16), y),
            Size = new Size(_cw, Px(26)),
            ForeColor = Theme.Text,
            Font = Theme.H2Font,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _root.Controls.Add(label);
        return y + Px(26) + Px(8);
    }

    private int TipWidth(int x) => Math.Max(Px(200), _cw - x - Px(16));

    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var title = new Rectangle(0, Px(2), Px(460), Px(36));
        TextRenderer.DrawText(g, "节能核心 EcoCore", Theme.TitleFont, title, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var sub = new Rectangle(0, Px(38), Px(500), Px(24));
        TextRenderer.DrawText(g, "英特尔大小核 · 大核降载 · 续航优化", Theme.SmallFont, sub, Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // 右侧电池胶囊
        string txt;
        Color col;
        if (_battery.Percent < 0) { txt = "台式机 / 无电池"; col = Theme.TextDim; }
        else
        {
            string src = _battery.OnAc ? (_battery.Charging ? "充电中" : "已接通电源") : "电池供电";
            txt = $"{src}  {_battery.Percent}%";
            col = _battery.OnAc ? Theme.Text : (_battery.Percent <= 20 ? Theme.Red : Theme.Green);
            if (!_battery.OnAc && _battery.SecondsRemaining > 0)
                txt += $"  剩余 {_battery.RemainingText}";
        }

        var size = TextRenderer.MeasureText(txt, Theme.BodyFont);
        int w = size.Width + Px(30);
        var chip = new Rectangle(_header.Width - w, Px(16), w, Px(38));
        Theme.FillRound(g, chip, chip.Height / 2, Theme.Panel2);
        Theme.StrokeRound(g, chip, chip.Height / 2, Theme.Border);
        TextRenderer.DrawText(g, txt, Theme.BodyFont, chip, col,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // 已应用标记
        if (_co.IsApplied)
        {
            var badge = new Rectangle(_header.Width - w - Px(92), Px(24), Px(82), Px(24));
            Theme.FillRound(g, badge, badge.Height / 2, Color.FromArgb(22, 55, 44));
            TextRenderer.DrawText(g, "已生效", Theme.SmallFont, badge, Theme.Green,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // ───────────────────────── 方案 → 界面 ─────────────────────────

    private void LoadProfileIntoUi(EcoProfile p)
    {
        _working = p.Clone();

        _desc.Text = p.IsCustom
            ? p.Description
            : p.Description + Environment.NewLine + "（此方案的数值已锁定，想自己调请切到「自定义」）";
        _st.ActiveProfileName = p.Name;
        _st.CurrentProfile = p.Clone();

        _entryHint.Text = p.IsCustom
            ? "当前方案可自由调节（频率上限 / 能效偏好 / 保留大核 / 自动切换…）"
            : $"当前方案「{p.Name}」参数已锁定，点开可查看；要自己调需切到「自定义」";

        for (int i = 0; i < _profileBtns.Count; i++)
        {
            var preset = EcoProfile.Presets()[i];
            _profileBtns[i].Primary = preset.Name == p.Name;
            _profileBtns[i].Invalidate();
        }

        _topo.IsEcoActive = p.PerfBoostMode == 0 || p.ThrottleMax < 100;
        _topo.ReservedCoreCount = p.ReservedPerformanceCores;
        _topo.Invalidate();
    }

    /// <summary>主界面不再直接编辑参数：参数由「精细调节」窗口改，这里直接取当前工作副本。</summary>
    private EcoProfile CollectProfile() => _working.Clone();

    // ───────────────────────── 动作 ─────────────────────────

    private void DoApply()
    {
        var p = CollectProfile();
        AppendLog($"—— 开始应用：[{p.Name}] ——");
        Cursor = Cursors.WaitCursor;
        try
        {
            if (_co.Apply(p, out string summary)) AppendLog(summary);
            else AppendLog("✗ " + summary);
        }
        finally { Cursor = Cursors.Default; }

        _topo.IsEcoActive = p.ThrottleMax < 100 || p.PerfBoostMode == 0;
        _topo.Invalidate();
        _header.Invalidate();
    }

    private void DoRestore()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            _co.Restore(out string summary);
            AppendLog("—— 恢复默认 ——");
            AppendLog(summary);
        }
        finally { Cursor = Cursors.Default; }

        _topo.IsEcoActive = false;
        _topo.Invalidate();
        _header.Invalidate();
    }

    private void OpenProcessWindow()
    {
        using var f = new ProcessForm(_co);
        f.ShowDialog(this);
    }

    // ───────────────────────── 托盘 ─────────────────────────

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add("应用当前方案", null, (_, _) => DoApply());
        menu.Items.Add("恢复默认", null, (_, _) => DoRestore());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => { _allowExit = true; Close(); });

        _tray = new NotifyIcon
        {
            Icon = Icon,
            Text = "节能核心 EcoCore",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray()
    {
        Hide();
        _tray.ShowBalloonTip(1500, "节能核心", "已最小化到托盘，双击图标恢复。", ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowExit && _st.MinimizeToTrayOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // 退出时若还处于已应用状态，问一句要不要断开隔离
        if (_co.IsApplied && e.CloseReason == CloseReason.UserClosing)
        {
            var r = MessageBox.Show(this,
                "退出后会解除全部进程的 E 核绑定。\n电源方案里写入的值会保留（可在「全部恢复默认」中清除）。\n\n退出前要顺便恢复电源方案吗？",
                "节能核心", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes) _co.Restore(out _);
            else _co.ReleaseAllProcesses();
        }

        StateStore.Save(_st);
        _tick?.Stop();
        if (_tray is not null) _tray.Visible = false;
    }

    // ───────────────────────── 定时器 ─────────────────────────

    private void OnTick(object? sender, EventArgs e)
    {
        _battery = BatteryState.Read();
        _header.Invalidate();

        // 自动切换：只在 AC/电池状态发生变化时触发一次
        if (_battery.OnAc != _lastOnAc)
        {
            _lastOnAc = _battery.OnAc;
            if (_battery.OnAc && _st.AutoSwitchOnAc)
            {
                AppendLog("检测到接通电源 → 恢复系统原有电源方案，并解除全部进程绑定");
                try
                {
                    _co.Restore(out string sum);
                    AppendLog(sum);
                }
                catch (Exception ex) { AppendLog("✗ 恢复失败：" + ex.Message); }
                _topo.IsEcoActive = false;
                _topo.Invalidate();
                _header.Invalidate();
            }
            else if (!_battery.OnAc && _st.AutoSwitchOnBattery)
            {
                AppendLog("检测到断开电源 → 自动切换到「均衡」方案");
                LoadProfileIntoUi(EcoProfile.Balanced());
                DoApply();
            }
        }
    }

    private void AppendLog(string msg)
    {
        if (_log is null) return;
        if (_log.Lines.Length > 400)
        {
            var keep = _log.Lines.Skip(_log.Lines.Length - 200).ToArray();
            _log.Lines = keep;
        }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }

    // ───────────────────────── 图标 ─────────────────────────

    public static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var b = new SolidBrush(Color.FromArgb(16, 18, 24)))
                g.FillEllipse(b, 0, 0, 31, 31);

            // 一片叶子
            using var path = new GraphicsPath();
            path.AddBezier(9, 24, 9, 12, 20, 8, 24, 8);
            path.AddBezier(24, 8, 24, 20, 16, 24, 9, 24);
            using var leafBrush = new LinearGradientBrush(
                new Point(8, 8), new Point(24, 24),
                Color.FromArgb(74, 222, 128), Color.FromArgb(16, 185, 129));
            g.FillPath(leafBrush, path);

            using var stem = new Pen(Color.FromArgb(180, 255, 255, 255), 1.4f);
            g.DrawLine(stem, 10, 24, 22, 11);
        }

        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

