using EcoCore.Core;

namespace EcoCore.UI;

/// <summary>进程列表：单个进程级的 E 核锁定 / 解除 / 排除。</summary>
public sealed class ProcessForm : Form
{
    private readonly Coordinator _co;
    private readonly DataGridView _grid;
    private readonly TextBox _filter;
    private readonly Label _status;
    private List<ProcessInfoRow> _rows = new();

    private int Px(int v) => Theme.Px(this, v);

    public ProcessForm(Coordinator co)
    {
        _co = co;

        Text = "进程列表 — 手动隔离到 E 核";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.BodyFont;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;      // 与主窗体一致：手工按 DPI 换算
        Icon = MainForm.BuildIcon();

        var wa = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min(Px(940), wa.Width - Px(60)),
                              Math.Min(Px(660), wa.Height - Px(80)));
        MinimumSize = new Size(Px(680), Px(460));

        // ── 顶部工具条 ──
        var top = new Panel { Dock = DockStyle.Top, Height = Px(60), BackColor = Theme.Bg };
        Controls.Add(top);

        var lbl = new Label
        {
            Text = "筛选：",
            Location = new Point(Px(14), Px(18)),
            Size = new Size(Px(54), Px(26)),
            ForeColor = Theme.TextDim,
            TextAlign = ContentAlignment.MiddleLeft
        };
        top.Controls.Add(lbl);

        _filter = new TextBox
        {
            Location = new Point(Px(70), Px(18)),
            Size = new Size(Px(250), Px(28)),
            BackColor = Theme.Panel2,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle
        };
        _filter.TextChanged += (_, _) => Reload();
        top.Controls.Add(_filter);

        int bx = Px(334), by = Px(16), bh = Px(30);
        var refresh = new EcoButton { Text = "刷新", Location = new Point(bx, by), Size = new Size(Px(86), bh) };
        refresh.Click += (_, _) => Reload();
        top.Controls.Add(refresh);

        bx += Px(96);
        var pin = new EcoButton { Text = "锁定到 E 核", Primary = true, Location = new Point(bx, by), Size = new Size(Px(126), bh) };
        pin.Click += (_, _) => ActOnSelected(pinIt: true);
        top.Controls.Add(pin);

        bx += Px(136);
        var unpin = new EcoButton { Text = "解除限制", Location = new Point(bx, by), Size = new Size(Px(106), bh) };
        unpin.Click += (_, _) => ActOnSelected(pinIt: false);
        top.Controls.Add(unpin);

        bx += Px(116);
        var exclude = new EcoButton { Text = "加入排除", Location = new Point(bx, by), Size = new Size(Px(106), bh) };
        exclude.Click += (_, _) => ExcludeSelected();
        top.Controls.Add(exclude);

        // ── 底部状态栏（先加，Dock 顺序才对）──
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = Px(40), BackColor = Theme.Bg };
        _status = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextDim,
            Font = Theme.SmallFont,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(Px(14), 0, 0, 0)
        };
        bottom.Controls.Add(_status);
        Controls.Add(bottom);

        // ── 表格 ──
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.FromArgb(13, 15, 20),
            BorderStyle = BorderStyle.None,
            GridColor = Theme.Border,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeight = Px(32),
            EnableHeadersVisualStyles = false
        };
        _grid.RowTemplate.Height = Px(28);
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(20, 23, 30);
        _grid.DefaultCellStyle.ForeColor = Theme.Text;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 58, 78);
        _grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Panel2;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextDim;
        _grid.ColumnHeadersDefaultCellStyle.Font = Theme.BodyFont;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "进程名", FillWeight = 34 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "pid", HeaderText = "PID", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "mem", HeaderText = "内存", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "当前状态", FillWeight = 24 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "note", HeaderText = "备注", FillWeight = 20 });

        Controls.Add(_grid);
        _grid.BringToFront();

        Load += (_, _) => Reload();
    }

    private void Reload()
    {
        string? f = string.IsNullOrWhiteSpace(_filter.Text) ? null : _filter.Text.Trim();
        try
        {
            _rows = _co.EnumerateCandidateProcesses(onlyBackground: false).ToList();
        }
        catch (Exception ex)
        {
            _status.Text = "枚举进程失败：" + ex.Message;
            return;
        }

        if (f is not null)
            _rows = _rows.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var r in _rows)
        {
            int i = _grid.Rows.Add(
                r.Name,
                r.Pid,
                FormatBytes(r.WorkingSet),
                r.IsExcluded ? "已排除" : "默认",
                r.Note);
            _grid.Rows[i].Tag = r;
        }
        _grid.ResumeLayout();

        _status.Text = $"共 {_rows.Count} 个进程可操作。锁定后该进程所有线程都会被限制到 " +
                       $"{_co.Topology.EfficiencyCores.Count} 个 E 核上，并启用效率模式。";
    }

    private static string FormatBytes(long b)
    {
        if (b <= 0) return "-";
        double mb = b / 1024.0 / 1024.0;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    private void ActOnSelected(bool pinIt)
    {
        if (_grid.SelectedRows.Count == 0) { _status.Text = "先选中一行。"; return; }

        int ok = 0, fail = 0;
        string lastErr = "";

        foreach (DataGridViewRow gr in _grid.SelectedRows)
        {
            if (gr.Tag is not ProcessInfoRow r) continue;
            bool done;
            if (pinIt)
                done = _co.PinSingle(r.Pid, r.Name, out lastErr);
            else
                done = _co.UnpinSingle(r.Pid, r.Name, out lastErr);

            if (done) { ok++; r.IsPinnedToECore = pinIt; gr.Cells[3].Value = pinIt ? "已锁 E 核" : "默认"; }
            else fail++;
        }

        _status.Text = $"成功 {ok} 个，失败 {fail} 个。" + (fail > 0 ? " 最后一个错误：" + lastErr : "");
    }

    private void ExcludeSelected()
    {
        int n = 0;
        foreach (DataGridViewRow gr in _grid.SelectedRows)
        {
            if (gr.Tag is not ProcessInfoRow r) continue;
            if (!_co.Settings.ExcludedProcesses.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            {
                _co.Settings.ExcludedProcesses.Add(r.Name);
                n++;
            }
            gr.Cells[3].Value = "已排除";
            r.IsExcluded = true;
        }
        StateStore.Save(_co.Settings);
        _status.Text = $"已排除 {n} 个进程，下次「应用方案」不再处理它们。";
    }
}
