using System.Runtime.InteropServices;

namespace EcoCore.Native;

/// <summary>
/// 所有原生 API 集中在这里。全部按 Windows SDK 原始签名声明，不做"美化"。
/// </summary>
internal static class Win32
{
    // ───────────────────────── 常量 ─────────────────────────

    public const uint PROCESS_SET_INFORMATION = 0x0200;
    // SetProcessDefaultCpuSets 要的是 SET_LIMITED_INFORMATION，给成 SET_INFORMATION 会被
    // 内核判 AccessDenied —— 这是微软文档里最容易被忽略的一处，两个都要给。
    public const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SET_QUOTA = 0x0100;

    public const uint THREAD_SET_INFORMATION = 0x0020;
    public const uint THREAD_SET_LIMITED_INFORMATION = 0x0400;
    public const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

    public const int ProcessPowerThrottling = 4;
    public const int ThreadPowerThrottling = 3;

    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    public const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    public const uint CPU_SET_INFORMATION_TYPE_INFORMATION = 0;

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NOT_FOUND = 1168;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_BAD_LENGTH = 24;

    // ───────────────────────── 结构 ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;

        public static PROCESS_POWER_THROTTLING_STATE Create(bool enableEcoQos)
        {
            return new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enableEcoQos ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0u
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    // ───────────────────────── CPU Sets ─────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemCpuSetInformation(
        IntPtr info, uint bufLen, out uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDefaultCpuSets(IntPtr process, uint[]? cpuSetIds, uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint[]? cpuSetIds, uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessDefaultCpuSets(
        IntPtr process, uint[]? cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadGroupAffinity(IntPtr hThread, ref GROUP_AFFINITY groupAffinity, out GROUP_AFFINITY previousGroupAffinity);

    [StructLayout(LayoutKind.Sequential)]
    public struct GROUP_AFFINITY
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    // ───────────────────────── 进程 / 线程 ─────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(
        IntPtr hProcess, int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadInformation(
        IntPtr hThread, int threadInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE threadInformation, uint threadInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll")]
    public static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    public const uint IDLE_PRIORITY_CLASS = 0x00000040;
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;

    public const int THREAD_PRIORITY_LOWEST = -2;
    public const int THREAD_PRIORITY_BELOW_NORMAL = -1;

    // ───────────────────────── 电池 ─────────────────────────

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    // ───────────────────────── 电源方案 (powrprof) ─────────────────────────

    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerDuplicateScheme(IntPtr rootPowerKey, ref Guid srcSchemeGuid, out IntPtr dstSchemeGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerDeleteScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint acValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint dcValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid,
        IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    // ───────────────────────── 工具 ─────────────────────────

    public static Guid ToGuid(IntPtr p) => Marshal.PtrToStructure<Guid>(p);

    public static int LastError => Marshal.GetLastWin32Error();

    public static string Describe(int err) => err switch
    {
        ERROR_SUCCESS => "成功",
        ERROR_ACCESS_DENIED => "拒绝访问（需要管理员权限或目标进程受保护）",
        ERROR_NOT_FOUND => "目标不存在（进程/线程已退出）",
        ERROR_INSUFFICIENT_BUFFER => "缓冲区不足",
        ERROR_BAD_LENGTH => "长度非法",
        _ => $"错误码 {err}"
    };
}
