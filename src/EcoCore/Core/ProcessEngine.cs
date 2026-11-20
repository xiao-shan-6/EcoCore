using System.Diagnostics;
using System.Runtime.InteropServices;
using EcoCore.Native;

namespace EcoCore.Core;

public enum PinResult { Ok, AlreadyPinned, AccessDenied, NotRunning, Failed }

public sealed class ProcessInfoRow
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public long WorkingSet { get; init; }
    public bool IsPinnedToECore { get; set; }
    public bool HasEcoQos { get; set; }
    public bool IsExcluded { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// 进程引擎：把"降低大核占用"真正落到进程/线程上。
///
/// 三层手段，由软到硬：
///   1. EcoQoS（PROCESS_POWER_THROTTLING_EXECUTION_SPEED）
///      → 告诉调度器"这活儿不急"，Windows 会优先丢到 E 核并压低频率。最安全，效果最明显。
///   2. CPU Set 绑定（SetProcessDefaultCpuSets / SetThreadSelectedCpuSets）
///      → 硬隔离，进程连看到 P 核的资格都没有。就是 FlexCore 用的那套 API。
///   3. 降优先级（SetPriorityClass / SetThreadPriority）
///      → 让出前台资源，配合上面两条进一步降低突发睿频。
/// </summary>
public sealed class ProcessEngine
{
    private readonly CpuTopology _topo;

    // 绝对不能碰的进程（碰了会崩系统或掉驱动）
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "Secure System",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm",
        "svchost", "fontdrvhost", "dwm", "audiodg", "MsMpEng", "NisSrv",
        "SecurityHealthService", "SgrmBroker", "SystemSettings", "ctfmon",
        "explorer", "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost",
        "TextInputHost", "RuntimeBroker", "dllhost", "sihost", "taskhostw",
        "WUDFHost", "spoolsv", "SearchIndexer", "WmiPrvSE", "MoUsoCoreWorker",
        "TrustedInstaller", "TiWorker", "CompatTelRunner", "WerFault"
    };

    public ProcessEngine(CpuTopology topo) => _topo = topo;

    public bool CanIsolate => _topo.IsHybrid && _topo.EfficiencyCores.Count > 0;

    public static bool IsProtected(string name) => ProtectedNames.Contains(Path.GetFileNameWithoutExtension(name));

    public static uint[] ECoreIdsOf(CpuTopology topo) => topo.EfficiencyCoreIds;

    // ───────────────────── 1. EcoQoS ─────────────────────

    public static bool SetEcoQos(int pid, bool enable, out string error)
    {
        error = "";
        IntPtr h = Win32.OpenProcess(
            Win32.PROCESS_SET_INFORMATION | Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            h = Win32.OpenProcess(
                Win32.PROCESS_SET_LIMITED_INFORMATION | Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            error = $"OpenProcess 失败: {Win32.Describe(Win32.LastError)}";
            return false;
        }
        try
        {
            var state = Win32.PROCESS_POWER_THROTTLING_STATE.Create(enable);
            if (!Win32.SetProcessInformation(h, Win32.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<Win32.PROCESS_POWER_THROTTLING_STATE>()))
            {
                error = $"SetProcessInformation 失败: {Win32.Describe(Win32.LastError)}";
                return false;
            }
            return true;
        }
        finally { Win32.CloseHandle(h); }
    }

    /// <summary>读回进程当前的效率模式状态（EcoQoS 是否打开）。读不到返回 false。</summary>
    public static bool ReadEcoQos(int pid)
    {
        IntPtr h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            // Version 必须先填 1，否则 GetProcessInformation 直接返回 ERROR_INVALID_PARAMETER（实测踩到过）。
            var state = Win32.PROCESS_POWER_THROTTLING_STATE.Create(false);
            if (!Win32.GetProcessInformation(h, Win32.ProcessPowerThrottling, ref state,
                    (uint)Marshal.SizeOf<Win32.PROCESS_POWER_THROTTLING_STATE>()))
                return false;
            return (state.StateMask & Win32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
        }
        finally { Win32.CloseHandle(h); }
    }

    // ───────────────────── 2. CPU Set 绑定 ─────────────────────
    /// <summary>
    /// 打开进程用于 CPU Set 操作。SetProcessDefaultCpuSets 认的是 PROCESS_SET_LIMITED_INFORMATION，
    /// 只给 PROCESS_SET_INFORMATION 会直接被判 AccessDenied（实测：mask=0x0200 → err=5，mask=0x2000 → OK）。
    /// </summary>
    private static IntPtr OpenForCpuSets(int pid)
    {
        IntPtr h = Win32.OpenProcess(
            Win32.PROCESS_SET_LIMITED_INFORMATION | Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            h = Win32.OpenProcess(
                Win32.PROCESS_SET_INFORMATION | Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        return h;
    }

    /// <summary>读回进程当前的默认 CPU Set，用来确认限制真的落地了。</summary>
    public static uint[] ReadProcessCpuSets(int pid)
    {
        IntPtr h = OpenForCpuSets(pid);
        if (h == IntPtr.Zero) return Array.Empty<uint>();
        try
        {
            var buf = new uint[256];
            if (!Win32.GetProcessDefaultCpuSets(h, buf, (uint)buf.Length, out uint need)) return Array.Empty<uint>();
            if (need == 0 || need > buf.Length) return Array.Empty<uint>();
            var r = new uint[need];
            Array.Copy(buf, r, (int)need);
            return r;
        }
        finally { Win32.CloseHandle(h); }
    }

    /// <summary>进程级：把进程默认 CPU Set 限制到指定集合。传 null 表示恢复。</summary>
    public static bool SetProcessCpuSets(int pid, uint[]? ids, out string error)
    {
        error = "";
        IntPtr h = OpenForCpuSets(pid);
        if (h == IntPtr.Zero)
        {
            error = $"OpenProcess 失败: {Win32.Describe(Win32.LastError)}";
            return false;
        }
        try
        {
            bool ok = ids is null || ids.Length == 0
                ? Win32.SetProcessDefaultCpuSets(h, null, 0)
                : Win32.SetProcessDefaultCpuSets(h, ids, (uint)ids.Length);
            if (!ok) { error = $"SetProcessDefaultCpuSets 失败: {Win32.Describe(Win32.LastError)}"; return false; }
            return true;
        }
        finally { Win32.CloseHandle(h); }
    }

    /// <summary>线程级：对进程内所有已存在线程做同样的限制。新建线程继承进程默认值。</summary>
    public static int SetProcessThreadCpuSets(int pid, uint[]? ids, out int denied)
    {
        denied = 0;
        int done = 0;
        try
        {
            using var proc = Process.GetProcessById(pid);
            foreach (ProcessThread t in proc.Threads)
            {
                IntPtr h = Win32.OpenThread(Win32.THREAD_SET_LIMITED_INFORMATION, false, (uint)t.Id);
                if (h == IntPtr.Zero)
                    h = Win32.OpenThread(Win32.THREAD_SET_INFORMATION | Win32.THREAD_SET_LIMITED_INFORMATION, false, (uint)t.Id);
                if (h == IntPtr.Zero) { denied++; continue; }
                try
                {
                    bool ok = ids is null || ids.Length == 0
                        ? Win32.SetThreadSelectedCpuSets(h, null, 0)
                        : Win32.SetThreadSelectedCpuSets(h, ids, (uint)ids.Length);
                    if (ok) done++; else denied++;
                }
                finally { Win32.CloseHandle(h); }
            }
        }
        catch { /* 进程已退出 */ }
        return done;
    }

    // ───────────────────── 3. 优先级 ─────────────────────

    public static bool LowerPriority(int pid, out string error)
    {
        error = "";
        IntPtr h = Win32.OpenProcess(Win32.PROCESS_SET_INFORMATION | Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) { error = Win32.Describe(Win32.LastError); return false; }
        try
        {
            if (!Win32.SetPriorityClass(h, Win32.BELOW_NORMAL_PRIORITY_CLASS))
            {
                error = Win32.Describe(Win32.LastError);
                return false;
            }
            return true;
        }
        finally { Win32.CloseHandle(h); }
    }

    // ───────────────────── 组合操作 ─────────────────────

    /// <summary>把进程完整压到 E 核：CPU Set + 全部已有线程 + EcoQoS。</summary>
    public PinResult PinToEfficiencyCores(int pid, bool alsoEcoQos, out string detail)
    {
        detail = "";
        if (!CanIsolate) { detail = "本机不是大小核架构，无法隔离"; return PinResult.Failed; }

        var ids = ECoreIdsOf(_topo);
        if (!SetProcessCpuSets(pid, ids, out var err))
        {
            detail = err;
            return err.Contains("拒绝访问") ? PinResult.AccessDenied : PinResult.Failed;
        }

        int threads = SetProcessThreadCpuSets(pid, ids, out int denied);

        if (alsoEcoQos) SetEcoQos(pid, true, out _);

        var back = ReadProcessCpuSets(pid);
        bool verified = back.Length == ids.Length && !ids.Except(back).Any();

        detail = $"进程已绑定到 {ids.Length} 个 E 核（读回 {(verified ? "一致" : "不一致")}）；线程命中 {threads} 个" +
                 (denied > 0 ? $"（{denied} 个线程拒绝访问）" : "");
        return PinResult.Ok;
    }

    /// <summary>
    /// 把进程钉到指定的一组核上（用于把系统进程固定在"保留大核"）。
    /// 不做 EcoQoS，保持系统进程的响应速度。
    /// </summary>
    public static bool PinToSpecificCpuSets(int pid, uint[] ids, out string detail)
    {
        detail = "";
        if (ids.Length == 0) { detail = "目标核集合为空"; return false; }
        if (!SetProcessCpuSets(pid, ids, out var err)) { detail = err; return false; }
        SetProcessThreadCpuSets(pid, ids, out _);

        var back = ReadProcessCpuSets(pid);
        bool verified = back.Length == ids.Length && !ids.Except(back).Any();
        detail = verified
            ? $"已固定到 {ids.Length} 个核（读回一致）"
            : $"已提交 {ids.Length} 个核，但读回 {back.Length} 个（可能被系统策略覆盖）";
        return true;
    }

    /// <summary>解除限制。</summary>
    public bool Unpin(int pid, out string detail)
    {
        detail = "";
        if (!SetProcessCpuSets(pid, null, out var err)) { detail = err; return false; }
        SetProcessThreadCpuSets(pid, null, out _);
        SetEcoQos(pid, false, out _);
        detail = "已恢复默认 CPU 分配";
        return true;
    }

    public bool EnableEcoQosOnly(int pid, out string detail)
    {
        bool ok = SetEcoQos(pid, true, out var err);
        detail = ok ? "已启用效率模式" : err;
        return ok;
    }
}
