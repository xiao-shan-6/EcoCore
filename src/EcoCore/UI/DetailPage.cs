using System.Drawing.Drawing2D;
using EcoCore.Core;

namespace EcoCore.UI;

/// <summary>
/// 「精细调节」二级界面。不是独立窗口，而是盖在主界面之上的一个页面：
/// 主界面点「打开…」把它显示出来（同一窗口内切换），点「返回」回到主页面。
/// 参数改动只有在点「应用并生效」时才通过 <see cref="Applied"/> 交回主界面。
/// </summary>
internal sealed class DetailPage : UserControl
{
    private readonly Coordinator _co;
    private readonly int _pCores;

    private EcoProfile _work = EcoProfile.Balanced();
    private bool _editable;

    private Panel _root = null!;
    private Panel _footer = null!;
    private Label _title = null!;
    private Label _hint = null!;
    private EcoButton _btnCustom = null!;
    private EcoButton _btnApply = null!;
    private EcoButton _btnBack = null!;

    private readonly Dictionary<string, EcoToggle> _toggles = new();
    private readonly Dictionary<string, EcoSlider> _sliders = new();
    private readonly Dictionary<string, Label> _tips = new();
    private bool _built;

    private int _cw = 800;
    private int Px(int v) => Theme.Px(this, v);

    /// <summary>点了「应用并生效」，把新的方案交回主界面。</summary>
    public event Action<EcoProfile>? Applied;

    /// <summary>点了「返回」，请求主界面切回主页面。</summary>
    public event Action? Back;

    public DetailPage(Coordinator co)
    {
        _co = co;
        _pCores = Math.Max(1, co.Topology.PerformanceCores.Count);

        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.BodyFont;
        DoubleBuffered = true;
    }

    /// <summary>进入这个页面：按当前方案刷新全部控件（首次进会先建 UI）。</summary>
    public void OpenProfile(EcoProfile profile)
    {
        _work = profile.Clone();
        _editable = profile.IsCustom;

        if (!_built)
        {
            _built = true;
            _cw = Math.Max(Px(560), Width - Px(16) * 2 - Px(24));
            BuildUi();
        }

        LayoutChrome();
        LoadIntoUi();
    }

    /// <summary>内容＋底栏一共需要多高（主界面据此把窗口撑开）。</summary>
    public int DesiredHeight => (_built ? _root.AutoScrollMinSize.Height : Px(600)) + Px(70);

    private void BuildUi()
    {
        _root = new Panel
        {
            BackColor = Theme.Bg,
            AutoScroll = true,
            Padding = new Padding(0)
        };
        Controls.Add(_root);

        _footer = new Panel { BackColor = Theme.Bg };
        _footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
        };
        Controls.Add(_footer);

        int M = Px(16);
        int y = Px(12);

        _title = new Label
        {
            Location = new Point(M, y),
            Size = new Size(_cw, Px(32)),
            ForeColor = Theme.Text,
            Font = Theme.H3Font,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "精细调节"
        };
        _root.Controls.Add(_title);
        y += Px(32) + Px(2);

        _hint = new Label
        {
            Location = new Point(M, y),
            Size = new Size(_cw, Px(24)),
            ForeColor = Theme.TextDim,
            Font = Theme.SmallFont,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _root.Controls.Add(_hint);
        y += Px(24) + Px(12);

        // ── 参数卡片 ──
        var card = MakeCard(_root, new Point(M, y), new Size(_cw, Px(300)));
        int py = Px(10), px = Px(20);

        AddToggle(card, "chkPin", "后台进程硬绑定到 E 核", px, ref py,
            "把非前台进程的 CPU Set 限制到小核（就是 FlexCore 那套 API，反过来用）");
        AddToggle(card, "chkEco", "后台进程启用效率模式 EcoQoS", px, ref py,
            "告诉调度器这些任务不急，Windows 会优先丢到小核并压低频率");
        AddToggle(card, "chkPri", "后台进程降优先级", px, ref py,
            "进一步让出前台资源，抑制突发睿频");
        AddToggle(card, "chkSlider", "同步 Windows 电源滑块到「最佳能效」", px, ref py,
            "等同于设置里手动拖电源模式滑块");

        AddSlider(card, "sldMax", "最大处理器状态", px, ref py, 1, 100, "%",
            "封顶频率。60% 左右能砍掉大量功耗，但重活会变慢");
        AddSlider(card, "sldMin", "最小处理器状态", px, ref py, 0, 100, "%",
            "保持低值（5）让 CPU 能真正降到低频");
        AddSlider(card, "sldEpp", "能效偏好 EPP", px, ref py, 0, 100, "",
            "100 = 最省电，0 = 最性能。这是 Intel 上最有效的省电旋钮之一");
        AddSlider(card, "sldReserve", "保留大核给系统", px, ref py, 0, _pCores, " 核",
            "≥1 时调度策略不会降到「只用 E 核」，且 DWM/音频被钉在这几个 P 核上。设 0 = 大核全放假（最省电，但交互会顿）");

        card.Height = py + Px(12);
        y += card.Height + Px(14);

        // ── 自动切换卡片（全局行为，不随方案锁定）──
        var card2 = MakeCard(_root, new Point(M, y), new Size(_cw, Px(120)));
        int py2 = Px(10);
        AddToggle(card2, "chkOnAc", "接通电源时自动恢复系统电源方案", px, ref py2,
            "插上适配器就切回你原来的 Windows 方案（并解除全部进程绑定），不再省电");
        AddToggle(card2, "chkBatt", "拔掉电源时自动应用当前方案", px, ref py2,
            "只用电池时自动套用这套设置，插回去再自动还原");
        AddToggle(card2, "chkAuto", "开机自动启动（登录时静默提权，不弹 UAC）", px, ref py2,
            "勾上后写一条 Windows 计划任务：登录即启动、直接拿管理员权限、缩到托盘，" +
            "并把上次生效的方案重新应用一次（进程绑定不跨重启保留，不重放等于没开机自启）。");
        card2.Height = py2 + Px(12);
        y += card2.Height + Px(12);

        _root.AutoScrollMinSize = new Size(0, y);

        // ── 底部按钮 ──
        _btnApply = new EcoButton
        {
            Text = "应用并生效",
            Primary = true,
            Location = new Point(Px(16), Px(12)),
            Size = new Size(Px(170), Px(46)),
            Font = Theme.H2Font
        };
        _btnApply.Click += (_, _) => Apply();
        _footer.Controls.Add(_btnApply);

        _btnCustom = new EcoButton
        {
            Text = "切到「自定义」",
            Location = new Point(Px(16) + Px(182), Px(12)),
            Size = new Size(Px(160), Px(46))
        };
        _btnCustom.Click += (_, _) => SwitchToCustom();
        _footer.Controls.Add(_btnCustom);

        _btnBack = new EcoButton
        {
            Text = "← 返回",
            Location = new Point(Px(16) + Px(354), Px(12)),
            Size = new Size(Px(110), Px(46))
        };
        _btnBack.Click += (_, _) => Back?.Invoke();
        _footer.Controls.Add(_btnBack);

        // 自动切换开关：立刻持久化
        _toggles["chkOnAc"].CheckedChanged += (_, _) =>
        {
            _co.Settings.AutoSwitchOnAc = _toggles["chkOnAc"].Checked;
            StateStore.Save(_co.Settings);
        };
        _toggles["chkBatt"].CheckedChanged += (_, _) =>
        {
            _co.Settings.AutoSwitchOnBattery = _toggles["chkBatt"].Checked;
            StateStore.Save(_co.Settings);
        };

        // 开机自启：真的去建/删计划任务，然后把「实际结果」回填到开关上（失败就弹回去）
        _toggles["chkAuto"].CheckedChanged += (_, _) =>
        {
            bool want = _toggles["chkAuto"].Checked;
            bool ok = want ? AutoStart.Enable(out string msg) : AutoStart.Disable(out msg);

            var mode = AutoStart.Detect(out string state);
            _co.Settings.StartWithWindows = ok && want && mode != AutoStartMode.None;
            StateStore.Save(_co.Settings);

            if (_toggles["chkAuto"].Checked != _co.Settings.StartWithWindows)
                _toggles["chkAuto"].Checked = _co.Settings.StartWithWindows;

            if (_tips.TryGetValue("chkAuto", out var tip))
            {
                tip.Text = msg;
                tip.ForeColor = ok ? Theme.Green : Color.FromArgb(248, 113, 113);
            }
        };

        // 改任何参数 → 变成自定义（仅解锁状态下）
        foreach (var k in new[] { "chkPin", "chkEco", "chkPri", "chkSlider" })
            _toggles[k].CheckedChanged += (_, _) => MarkCustom();
    }

    private Panel MakeCard(Panel host, Point loc, Size size)
    {
        var card = new Panel { Location = loc, Size = size, BackColor = Theme.Panel };
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(e.Graphics, new Rectangle(0, 0, card.Width - 1, card.Height - 1), Px(10), Theme.Panel);
            Theme.StrokeRound(e.Graphics, new Rectangle(0, 0, card.Width - 1, card.Height - 1), Px(10), Theme.Border);
        };
        host.Controls.Add(card);
        return card;
    }

    private int TipWidth(int x) => Math.Max(Px(200), _cw - x - Px(16));

    private void AddToggle(Panel host, string key, string text, int x, ref int y, string tip)
    {
        var t = new EcoToggle
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(_cw - x - Px(20), Px(26))
        };
        host.Controls.Add(t);
        _toggles[key] = t;

        var tipLbl = new Label
        {
            Text = tip,
            Location = new Point(x + Px(8), y + Px(26)),
            AutoSize = true,
            MaximumSize = new Size(TipWidth(x), 0),
            ForeColor = Theme.TextDim,
            Font = Theme.SmallFont,
            BackColor = Color.Transparent
        };
        host.Controls.Add(tipLbl);
        _tips[key] = tipLbl;

        y += Px(26) + tipLbl.Height + Px(6);
    }

    private void AddSlider(Panel host, string key, string title, int x, ref int y,
        int min, int max, string unit, string tip)
    {
        var lbl = new Label
        {
            Text = title,
            Location = new Point(x, y),
            Size = new Size(Px(150), Px(26)),
            ForeColor = Theme.Text,
            Font = Theme.BodyFont,
            TextAlign = ContentAlignment.MiddleLeft
        };
        host.Controls.Add(lbl);

        int sx = x + Px(152);
        var s = new EcoSlider
        {
            Location = new Point(sx, y),
            Size = new Size(_cw - sx - Px(20), Px(26)),
            Min = min,
            Max = max,
            Unit = unit
        };
        host.Controls.Add(s);
        _sliders[key] = s;

        var tipLbl = new Label
        {
            Text = tip,
            Location = new Point(x + Px(8), y + Px(26)),
            AutoSize = true,
            MaximumSize = new Size(TipWidth(x), 0),
            ForeColor = Theme.TextDim,
            Font = Theme.SmallFont,
            BackColor = Color.Transparent
        };
        host.Controls.Add(tipLbl);

        y += Px(26) + tipLbl.Height + Px(6);
    }

    // ── 值 ↔ 控件 ──

    private void LoadIntoUi()
    {
        _toggles["chkPin"].Checked = _work.PinBackgroundToECores;
        _toggles["chkEco"].Checked = _work.EcoQosBackground;
        _toggles["chkPri"].Checked = _work.LowerBackgroundPriority;
        _toggles["chkSlider"].Checked = _work.UsePowerSlider;

        _toggles["chkOnAc"].Checked = _co.Settings.AutoSwitchOnAc;
        _toggles["chkBatt"].Checked = _co.Settings.AutoSwitchOnBattery;

        // 开机自启：以磁盘上的实际状态为准（可能在别的机器上被改过，也可能是手建的计划任务）
        var autoMode = AutoStart.Detect(out string autoState);
        _toggles["chkAuto"].Checked = autoMode != AutoStartMode.None;
        _co.Settings.StartWithWindows = autoMode != AutoStartMode.None;
        if (_tips.TryGetValue("chkAuto", out var autoTip))
        {
            autoTip.Text = autoState;
            autoTip.ForeColor = autoMode switch
            {
                AutoStartMode.ScheduledTask => Theme.Green,
                AutoStartMode.RunKey => Color.FromArgb(251, 191, 36),
                _ => Theme.TextDim
            };
        }

        _sliders["sldMax"].Value = (int)Math.Clamp(_work.ThrottleMax, 0u, 100u);
        _sliders["sldMin"].Value = (int)Math.Clamp(_work.ThrottleMin, 0u, 100u);
        _sliders["sldEpp"].Value = (int)Math.Clamp(_work.PerfEpp, 0u, 100u);
        _sliders["sldReserve"].Value = Math.Clamp(_work.ReservedPerformanceCores, 0, _pCores);

        ApplyLock();
        UpdateHeader();
    }

    private void ApplyLock()
    {
        foreach (var k in new[] { "chkPin", "chkEco", "chkPri", "chkSlider" })
            if (_toggles.TryGetValue(k, out var t)) { t.Enabled = _editable; t.Locked = !_editable; }
        foreach (var k in new[] { "sldMax", "sldMin", "sldEpp", "sldReserve" })
            if (_sliders.TryGetValue(k, out var s)) { s.Enabled = _editable; s.Locked = !_editable; }

        _btnCustom.Visible = !_editable;
        // 自定义态没有「切到自定义」按钮，返回键左移补位
        _btnBack.Left = Px(16) + (_editable ? Px(182) : Px(354));
        _btnApply.Text = _editable ? "应用并生效" : "应用自动切换设置";
    }

    private void UpdateHeader()
    {
        _title.Text = $"精细调节 — 当前方案「{_work.Name}」";
        _hint.ForeColor = _editable ? Theme.Eco : Theme.TextDim;
        _hint.Text = _editable
            ? "这个方案可以随便调：改完整点「应用并生效」，参数会自动存进「自定义」。"
            : "这个方案的数值是调过的成套参数，已锁定。想自己调请点下面的「切到「自定义」」。";
    }

    /// <summary>只在解锁状态被调用：用户动了参数就把方案标成自定义，免得存回预设里。</summary>
    private void MarkCustom()
    {
        if (!_editable || _suppress) return;
        _work.Name = EcoProfile.CustomName;
        _work.Description = EcoProfile.CustomDescription;
        UpdateHeader();
    }

    private bool _suppress;

    private void SwitchToCustom()
    {
        _suppress = true;
        try
        {
            _work.Name = EcoProfile.CustomName;
            _work.Description = EcoProfile.CustomDescription;
            _editable = true;
            ApplyLock();
            UpdateHeader();
        }
        finally { _suppress = false; }
    }

    private EcoProfile Collect()
    {
        var p = _work.Clone();
        p.PinBackgroundToECores = _toggles["chkPin"].Checked;
        p.EcoQosBackground = _toggles["chkEco"].Checked;
        p.LowerBackgroundPriority = _toggles["chkPri"].Checked;
        p.UsePowerSlider = _toggles["chkSlider"].Checked;

        p.ThrottleMax = (uint)Math.Clamp(_sliders["sldMax"].Value, 1, 100);
        p.ThrottleMin = (uint)Math.Clamp(_sliders["sldMin"].Value, 0, (int)p.ThrottleMax);
        p.PerfEpp = (uint)Math.Clamp(_sliders["sldEpp"].Value, 0, 100);
        p.ReservedPerformanceCores = Math.Clamp(_sliders["sldReserve"].Value, 0, _pCores);
        p.PinSystemToReservedCore = p.ReservedPerformanceCores >= 1;
        p.UseCoreParking = false;

        // 数值反推调度策略：改频率上限的人一般也想让调度策略一致
        if (p.ThrottleMax >= 100) { p.SchedPolicy = 5; p.ShortSchedPolicy = 5; p.PerfBoostMode = p.PerfEpp >= 90 ? 0u : 1u; }
        else if (p.ThrottleMax <= 55) { p.SchedPolicy = 3; p.ShortSchedPolicy = 3; p.PerfBoostMode = 0; }
        else { p.SchedPolicy = 4; p.ShortSchedPolicy = 4; p.PerfBoostMode = 0; }

        p.OnlyBackgroundProcesses = true;
        return p;
    }

    private void Apply()
    {
        var p = Collect();
        if (p.IsCustom)
        {
            _co.Settings.CustomProfile = p.Clone();
            _co.Settings.CurrentProfile = p.Clone();
            _co.Settings.ActiveProfileName = EcoProfile.CustomName;
        }
        else
        {
            _co.Settings.CurrentProfile = p.Clone();
            _co.Settings.ActiveProfileName = p.Name;
        }
        StateStore.Save(_co.Settings);

        _work = p.Clone();
        UpdateHeader();
        Applied?.Invoke(p);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_built) return;
        LayoutChrome();
    }

    private void LayoutChrome()
    {
        if (_root is null || _footer is null) return;
        int fh = Px(70);
        int h = Height;
        _footer.Bounds = new Rectangle(0, Math.Max(0, h - fh), Width, fh);
        _root.Bounds = new Rectangle(0, 0, Width, Math.Max(0, h - fh));
    }
}


