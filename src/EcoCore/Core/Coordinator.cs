using System.Diagnostics;
using EcoCore.Native;

namespace EcoCore.Core;

public sealed class BatteryState
{
    public bool OnAc { get; init; }
    public int Percent { get; init; }
    public int SecondsRemaining { get; init; }
    public bool Charging { get; init; }

    public string RemainingText
    {
        get
        {
            if (SecondsRemaining <= 0) return "计算中";
            var t = TimeSpan.FromSeconds(SecondsRemaining);
            return $"{(int)t.TotalHours}小时{t.Minutes:D2}分";
        }
    }

    public static BatteryState Read()
    {
        if (!Win32.GetSystemPowerStatus(out var s))
            return new BatteryState { OnAc = true, Percent = -1 };

        return new BatteryState
        {
            OnAc = s.ACLineStatus == 1,
            Percent = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent,
            SecondsRemaining = s.BatteryLifeTime < 0 ? -1 : s.BatteryLifeTime,
            Charging = (s.BatteryFlag & 8) != 0
        };
    }
}

/// <summary>
/// 总协调器：把 拓扑 / 进程 / 电源 三块拼成一套可应用、可回滚的方案。
/// </summary>
public sealed class Coordinator
{
    public CpuTopology Topology { get; private set; }
    public ProcessEngine Engine { get; }
    public AppSettings Settings { get; }
    public bool IsApplied { get; private set; }
    public string LastAppliedSchemeName { get; private set; } = "";

    public event Action<string>? Log;

    private readonly List<(int Pid, string Name)> _pinnedByUs = new();
    private readonly List<(int Pid, string Name)> _systemPinned = new();

    /// <summary>被钉到"保留大核"上的系统进程名。这些进程必须在 E 核之外运行，否则界面会顿、音频会爆。</summary>
    private static readonly string[] SystemCritical = { "dwm", "audiodg" };

    public Coordinator()
    {
        Topology = CpuTopology.Discover();
        Engine = new ProcessEngine(Topology);
        Settings = StateStore.Load();
    }

    private void L(string msg)
    {
        StateStore.Log(msg);
        Log?.Invoke(msg);
    }

    public void RefreshTopology()
    {
        Topology = CpuTopology.Discover();
    }

    // ───────────────────────── 应用方案 ─────────────────────────

    public bool Apply(EcoProfile profile, out string summary)
    {
        var lines = new List<string>();
        summary = "";

        bool admin = IsElevated();

        try
        {
            // ── 1. 电源策略（PowerDuplicateScheme / PowerWrite* 必须管理员）──
            if (admin) ApplyPowerLayer(profile, lines);
            else lines.Add("⚠ 非管理员运行：跳过电源策略层（频率上限 / 睿频 / EPP 不生效），进程层照常生效");

            // ── 2. 系统大核保留 ──
            var reservedIds = Topology.ReservedPerformanceCores(profile.ReservedPerformanceCores)
                                         .Select(c => c.Id).ToArray();
            if (reservedIds.Length > 0)
            {
                lines.Add($"保留大核 CPU Set = [{string.Join(", ", reservedIds)}]");
                if (profile.PinSystemToReservedCore)
                {
                    int n = PinSystemProcesses(reservedIds);
                    lines.Add($"系统关键进程钉到保留大核：{n} 个（DWM / 音频）");
                }
            }

            // ── 3. 进程层 ──
            if (Topology.IsHybrid && (profile.PinBackgroundToECores || profile.EcoQosBackground))
            {
                var (pinned, ecoQos) = ApplyToProcesses(profile);
                lines.Add($"后台进程：CPU Set 绑定 {pinned} 个，效率模式 {ecoQos} 个");
            }
            else if (!Topology.IsHybrid)
            {
                lines.Add("⚠ 本机未检测到大小核架构，跳过进程隔离");
            }

            IsApplied = true;
            Settings.Applied = true;
            Settings.CurrentProfile = profile.Clone();
            Settings.ActiveProfileName = profile.Name;
            StateStore.Save(Settings);

            summary = string.Join("\n", lines);
            L($"应用方案 [{profile.Name}]\n{summary}");
            return true;
        }
        catch (Exception ex)
        {
            summary = "应用失败：" + ex.Message;
            L(summary);
            return false;
        }
    }

    /// <summary>
    /// 第 1 层：复制一份当前电源方案（不动原方案），在副本里写频率/调度/EPP，再激活它。
    /// 全部 PowerWrite* 都要管理员权限，非提权时由调用方跳过。
    /// </summary>
    private void ApplyPowerLayer(EcoProfile profile, List<string> lines)
    {
        Guid original = PowerTuner.GetActiveScheme();
        if (Settings.OriginalSchemeGuid is null)
            Settings.OriginalSchemeGuid = original.ToString();

        Guid eco = original;
        if (!string.IsNullOrWhiteSpace(Settings.EcoSchemeGuid) &&
            Guid.TryParse(Settings.EcoSchemeGuid, out var existing))
        {
            eco = existing;
        }
        else
        {
            eco = PowerTuner.DuplicateActive(original);
            PowerTuner.RenameScheme(eco, "EcoCore 节能");
            Settings.EcoSchemeGuid = eco.ToString();
            lines.Add($"创建专用电源方案 EcoCore 节能 ({eco})");
        }

        // 只用电池还是交流都写？两个都写，AC/DC 统一，避免插拔跳变。
        void Set(PowerSettingDef def, uint v)
        {
            uint ra = PowerTuner.WriteAc(eco, def, v);
            uint rd = PowerTuner.WriteDc(eco, def, v);
            if (ra == 0 && rd == 0)
                lines.Add($"{def.DisplayName} = {v}（{def.Explain(v)}）");
            else
                lines.Add($"{def.DisplayName} 写入失败 AC={ra} DC={rd}");
        }

        Set(PowerTuner.SchedPolicy, profile.EffectiveSchedPolicy);
        Set(PowerTuner.ShortSchedPolicy, profile.EffectiveShortSchedPolicy);

        if (profile.ReservedPerformanceCores >= 1 && profile.SchedPolicy < 4)
            lines.Add($"⚠ 当前方案想设「只用 E 核」，但已保留 {profile.ReservedPerformanceCores} 个大核给系统，" +
                      $"调度策略被抬到 4（首选高效核）");
        else if (profile.ReservedPerformanceCores >= 1)
            lines.Add($"已为系统保留 {profile.ReservedPerformanceCores} 个大核");
        Set(PowerTuner.PerfBoostMode, profile.PerfBoostMode);
        Set(PowerTuner.ThrottleMax, profile.ThrottleMax);
        Set(PowerTuner.ThrottleMin, profile.ThrottleMin);
        Set(PowerTuner.PerfEpp, profile.PerfEpp);
        if (profile.UseCoreParking)
        {
            Set(PowerTuner.CpMinCores, profile.CpMinCores);
            Set(PowerTuner.CpMaxCores, profile.CpMaxCores);
        }

        if (!PowerTuner.SetActiveScheme(eco))
            lines.Add("⚠ 激活专用方案失败");

        if (profile.UsePowerSlider)
        {
            bool ok = PowerTuner.TrySetOverlay(PowerTuner.OverlayBestEfficiency);
            lines.Add(ok ? "电源滑块 → 最佳能效" : "⚠ 电源滑块设置失败（需要管理员）");
        }
    }

    /// <summary>
    /// 把 DWM（桌面合成）和 audiodg（音频引擎）钉到保留的大核上。
    /// 这两个进程一卡，整机手感就废了 —— 省电不能省到它们头上。
    /// </summary>
    private int PinSystemProcesses(uint[] reservedIds)
    {
        int n = 0;
        foreach (var name in SystemCritical)
        {
            System.Diagnostics.Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (ProcessEngine.PinToSpecificCpuSets(p.Id, reservedIds, out _))                    {
                        _systemPinned.Add((p.Id, name));
                        n++;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return n;
    }

    private (int pinned, int ecoQos) ApplyToProcesses(EcoProfile p)
    {
        int pinned = 0, ecoQos = 0;
        uint[]? ecores = Topology.EfficiencyCoreIds;

        foreach (var proc in EnumerateCandidateProcesses(p.OnlyBackgroundProcesses))
        {
            if (proc.IsExcluded) continue;
            try
            {
                bool isPinned = false;

                if (p.PinBackgroundToECores && ecores is { Length: > 0 })
                {
                    if (Engine.PinToEfficiencyCores(proc.Pid, p.EcoQosBackground, out _) == PinResult.Ok)
                    {
                        pinned++;
                        isPinned = true;
                        if (p.EcoQosBackground) ecoQos++;   // 绑定里已经顺带打了 EcoQoS
                        _pinnedByUs.Add((proc.Pid, proc.Name));
                    }
                }

                if (p.EcoQosBackground && !isPinned)
                {
                    if (Engine.EnableEcoQosOnly(proc.Pid, out _)) ecoQos++;
                }

                if (p.LowerBackgroundPriority)
                    ProcessEngine.LowerPriority(proc.Pid, out _);
            }
            catch { }
        }
        return (pinned, ecoQos);
    }

    public IEnumerable<ProcessInfoRow> EnumerateCandidateProcesses(bool onlyBackground)
    {
        var rows = new List<ProcessInfoRow>();
        int foregroundPid = ForegroundPid();

        foreach (var proc in Process.GetProcesses())
        {
            ProcessInfoRow row;
            try
            {
                string name = proc.ProcessName;
                if (ProcessEngine.IsProtected(name)) { proc.Dispose(); continue; }
                if (proc.Id <= 4) { proc.Dispose(); continue; }
                if (proc.Id == Environment.ProcessId) { proc.Dispose(); continue; }  // 不折腾自己
                if (onlyBackground && proc.Id == foregroundPid) { proc.Dispose(); continue; }

                int sessionId = 0;
                try { sessionId = proc.SessionId; } catch { }

                row = new ProcessInfoRow
                {
                    Pid = proc.Id,
                    Name = name,
                    WorkingSet = SafeWorkingSet(proc),
                    IsExcluded = Settings.ExcludedProcesses.Contains(name, StringComparer.OrdinalIgnoreCase),
                    Note = sessionId == 0 ? "系统会话" : ""
                };
            }
            catch { proc.Dispose(); continue; }
            proc.Dispose();
            rows.Add(row);
        }
        return rows.OrderByDescending(r => r.WorkingSet).ToList();
    }

    private static long SafeWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }

    public static int ForegroundPid()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(h, out uint pid);
            return (int)pid;
        }
        catch { return 0; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ───────────────────────── 恢复 ─────────────────────────

    public bool Restore(out string summary)
    {
        var lines = new List<string>();
        summary = "";

        // 1. 解除所有我们绑定过的进程
        foreach (var (pid, name) in _pinnedByUs.ToList())
        {
            try
            {
                if (Engine.Unpin(pid, out _)) lines.Add($"解除 {name}({pid}) 的 E 核绑定");
            }
            catch { }
        }
        _pinnedByUs.Clear();

        foreach (var (pid, name) in _systemPinned.ToList())
        {
            try { if (Engine.Unpin(pid, out _)) lines.Add($"解除 {name}({pid}) 的大核钉选"); }
            catch { }
        }
        _systemPinned.Clear();

        // 2. 电源方案切回原始
        //    只有当"当前激活的确实是我们的副本"时才切回去 —— 如果用户自己在 Windows 设置里
        //    换了方案，就别去覆盖他的选择。
        bool haveOriginal = Guid.TryParse(Settings.OriginalSchemeGuid ?? "", out var orig);
        bool haveEco = Guid.TryParse(Settings.EcoSchemeGuid ?? "", out var eco) && eco != Guid.Empty;
        bool ecoIsActive = haveEco && PowerTuner.GetActiveScheme() == eco;

        if (haveOriginal && (ecoIsActive || !haveEco))
        {
            if (PowerTuner.SetActiveScheme(orig)) lines.Add($"电源方案恢复为系统原有方案 {orig}");
            else if (!IsElevated()) lines.Add("⚠ 非管理员：电源方案没能切回去（需要管理员）");
        }
        else if (ecoIsActive)
        {
            lines.Add("⚠ 找不到原始方案记录，保持了当前方案");
        }

        if (haveEco)
        {
            // 先切走才能删
            if (ecoIsActive && haveOriginal) PowerTuner.SetActiveScheme(orig);
            if (PowerTuner.DeleteScheme(eco))
                lines.Add("删除专用方案 EcoCore 节能");
            Settings.EcoSchemeGuid = null;
        }

        // 3. 电源滑块
        PowerTuner.TrySetOverlay(PowerTuner.OverlayBestPerformance);

        IsApplied = false;
        Settings.Applied = false;
        StateStore.Save(Settings);

        summary = lines.Count > 0 ? string.Join("\n", lines) : "没有任何需要恢复的改动。";
        L($"恢复默认\n{summary}");
        return true;
    }

    /// <summary>仅解除进程绑定，不动电源。</summary>
    public int ReleaseAllProcesses()
    {
        int n = 0;
        foreach (var (pid, _) in _pinnedByUs.ToList())
        {
            try { if (Engine.Unpin(pid, out _)) n++; } catch { }
        }
        _pinnedByUs.Clear();
        return n;
    }

    // ───────────────────────── 单进程操作 ─────────────────────────

    public bool PinSingle(int pid, string name, out string detail)
    {
        if (Topology.EfficiencyCores.Count == 0)
        {
            detail = "本机不是大小核架构";
            return false;
        }
        var r = Engine.PinToEfficiencyCores(pid, true, out detail);
        if (r == PinResult.Ok)
        {
            _pinnedByUs.Add((pid, name));
            if (!Settings.PinnedProcesses.Contains(name, StringComparer.OrdinalIgnoreCase))
                Settings.PinnedProcesses.Add(name);
            StateStore.Save(Settings);
            return true;
        }
        return false;
    }

    public bool UnpinSingle(int pid, string name, out string detail)
    {
        bool ok = Engine.Unpin(pid, out detail);
        _pinnedByUs.RemoveAll(x => x.Pid == pid);
        Settings.PinnedProcesses.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        StateStore.Save(Settings);
        return ok;
    }

    // ───────────────────────── 工具 ─────────────────────────

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public string CurrentSchemeInfo()
    {
        try
        {
            var g = PowerTuner.GetActiveScheme();
            return g.ToString();
        }
        catch { return "未知"; }
    }
}
