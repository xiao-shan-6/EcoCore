using System.Runtime.InteropServices;
using EcoCore.Native;

namespace EcoCore.Core;

/// <summary>一次电池/亮度读数。RateMw 为正表示放电，为负表示充电，单位毫瓦。</summary>
public sealed class BatterySample
{
    public bool Present;          // 机器有电池
    public bool RateKnown;        // 能把放电功率读出来
    public int RateMw;
    public int CapacityMw;        // 当前剩余容量
    public int FullCapacityMw;    // 满充容量
    public int Percent;           // 0..100
    public bool Discharging;
    public bool Charging;
    public bool OnAc;

    public string RateText => !Present ? "无电池" : !RateKnown ? "未知" : $"{RateMw} mW";
}

/// <summary>
/// ACPI 电池直读：走 SetupDi 枚举电池设备 → CreateFile → DeviceIoControl。
/// 比 WMI 的 EstimatedChargeRemaining 细一个量级，能直接拿到 mW 级放电功率，
/// 这是唯一能在笔记本上真实测出「开省电 / 不开省电」差值的指标。
/// </summary>
public static class BatteryMeter
{
    // --- SetupAPI ---
    private static readonly Guid GUID_DEVCLASS_BATTERY = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfo, ref Guid interfaceClassGuid,
        int memberIndex, byte[] deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr hDevInfo, byte[] deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, int detailSize, out int requiredSize, IntPtr deviceInfoData);

    // --- kernel32 ---
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint control, byte[]? inBuf, int inSize,
        byte[]? outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // --- dxva2：屏幕亮度 ---
    [StructLayout(LayoutKind.Sequential)]
    private struct PHYSICAL_MONITOR { public IntPtr hPhysicalMonitor; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription; }

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool DestroyPhysicalMonitors(uint count, [Out] PHYSICAL_MONITOR[] monitors);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
    private const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

    private const uint BATTERY_DISCHARGING = 0x00000001;
    private const uint BATTERY_CHARGING = 0x00000002;
    private const uint BATTERY_POWER_ON_LINE = 0x00000008;

    private static IntPtr _handle = IntPtr.Zero;
    private static bool _tried;
    private static uint _tag;

    public static string DevicePath { get; private set; } = "";
    public static string? LastError { get; private set; }
    public static string RawStatus { get; private set; } = "";
    public static int RawReturned { get; private set; }

    /// <summary>打开第一块电池设备；失败返回 false，LastError 里有原因。</summary>
    private static bool EnsureOpen()
    {
        if (_handle != IntPtr.Zero) return true;
        if (_tried) return false;
        _tried = true;

        var guid = GUID_DEVCLASS_BATTERY;
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) { LastError = "SetupDiGetClassDevs 失败"; return false; }

        try
        {
            for (int i = 0; i < 16; i++)
            {
                var did = new byte[32];
                BitConverter.GetBytes(32).CopyTo(did, 0);
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, did))
                {
                    if (i == 0) LastError = "找不到电池设备接口";
                    break;
                }

                SetupDiGetDeviceInterfaceDetail(set, did, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                if (need <= 0) continue;

                IntPtr buf = Marshal.AllocHGlobal(need);
                try
                {
                    // cbSize：x64 上是 8，x86 上是 6
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, did, buf, need, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(buf + 4) ?? "";
                    if (path.Length == 0) continue;

                    IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == IntPtr.Zero || h == new IntPtr(-1))
                    {
                        h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    }
                    if (h == IntPtr.Zero || h == new IntPtr(-1)) { LastError = "CreateFile 电池设备被拒绝"; continue; }

                    if (QueryTag(h, out uint tag))
                    {
                        _handle = h;
                        _tag = tag;
                        DevicePath = path;
                        return true;
                    }
                    CloseHandle(h);
                    LastError = "IOCTL_BATTERY_QUERY_TAG 失败";
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        if (LastError == null) LastError = "未找到可用电池";
        return false;
    }

    private static bool QueryTag(IntPtr h, out uint tag)
    {
        var input = new byte[4];
        var output = new byte[4];
        tag = 0;
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, input, 4, output, 4, out int ret, IntPtr.Zero) || ret < 4) return false;
        tag = BitConverter.ToUInt32(output, 0);
        return tag != 0;
    }

    /// <summary>读一次电池状态。无电池 / 读不到功率时字段会如实标记。</summary>
    public static BatterySample Read()
    {
        var s = new BatterySample();
        if (!EnsureOpen()) return s;
        s.Present = true;

        // 满充容量 + 当前容量
        var q = new byte[12];
        BitConverter.GetBytes(_tag).CopyTo(q, 0);
        BitConverter.GetBytes(0).CopyTo(q, 4);          // BatteryInformation
        var info = new byte[40];
        if (DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_INFORMATION, q, 12, info, 40, out int ir, IntPtr.Zero) && ir >= 36)
        {
            s.FullCapacityMw = (int)BitConverter.ToUInt32(info, 16);
        }

        // 状态
        var w = new byte[20];
        BitConverter.GetBytes(_tag).CopyTo(w, 0);
        var st = new byte[16];
        if (!DeviceIoControl(_handle, IOCTL_BATTERY_QUERY_STATUS, w, 20, st, 16, out int sr, IntPtr.Zero) || sr < 16)
        {
            _handle = IntPtr.Zero;   // 标签过期或设备拔掉，下次重新枚举
            _tried = false;
            return s;
        }

        uint powerState = BitConverter.ToUInt32(st, 0);
        s.CapacityMw = (int)BitConverter.ToUInt32(st, 4);
        uint rate = BitConverter.ToUInt32(st, 12);
        RawStatus = BitConverter.ToString(st);
        RawReturned = sr;
        s.Discharging = (powerState & BATTERY_DISCHARGING) != 0;
        s.Charging = (powerState & BATTERY_CHARGING) != 0;
        s.OnAc = (powerState & BATTERY_POWER_ON_LINE) != 0;

        // ACPI 的 Rate 方向约定：放电时为负（实测 0xFFFFCD5A = -12966，与 WMI DischargeRate 12966 逐位吻合），
        // 充电时为正。这里统一翻成「正 = 放电」，PowerState 位很多机器上是过期的，不采信。
        if (rate != 0x80000000)
        {
            s.RateKnown = true;
            s.RateMw = -(int)rate;
        }

        // 方向与电量百分比以 GetSystemPowerStatus 为准（ACPI 的 PowerState 在部分机型上是陈旧的）
        if (Win32.GetSystemPowerStatus(out var ps) && ps.ACLineStatus != 255)
        {
            s.OnAc = ps.ACLineStatus == 1;
            s.Charging = (ps.BatteryFlag & 0x08) != 0;
            s.Discharging = !s.OnAc && !s.Charging;
            if (ps.BatteryLifePercent != 255) s.Percent = ps.BatteryLifePercent;
        }
        else if (s.FullCapacityMw > 0)
        {
            s.Percent = Math.Clamp((int)Math.Round(s.CapacityMw * 100.0 / s.FullCapacityMw), 0, 100);
        }

        return s;
    }

    /// <summary>屏幕亮度百分比（笔记本内置屏）。先试 dxva2，不行退回 WMI（部分机型 eDP 屏不支持 DDC/CI）。</summary>
    public static int? Brightness()
    {
        var v = BrightnessDxva2();
        if (v != null) return v;

        // WMI 兜底，15 秒缓存，避免采样时反复起进程
        if (_brightCache != null && (DateTime.UtcNow - _brightAt).TotalSeconds < 15) return _brightCache;
        _brightAt = DateTime.UtcNow;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"$b=(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightness -ErrorAction SilentlyContinue | Select-Object -First 1).CurrentBrightness; if($b){$b}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p != null)
            {
                string s = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(4000);
                if (int.TryParse(s, out int wmi)) { _brightCache = wmi; return wmi; }
            }
        }
        catch { }
        return _brightCache;
    }

    private static int? BrightnessDxva2()
    {
        uint count = 0;
        IntPtr mon = MonitorFromWindow(GetDesktopWindow(), 1 /* MONITOR_DEFAULTTOPRIMARY */);
        if (mon == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(mon, out count) || count == 0) return null;

        var arr = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(mon, count, arr)) return null;
        try
        {
            if (!GetMonitorBrightness(arr[0].hPhysicalMonitor, out uint min, out uint cur, out uint max)) return null;
            if (max <= min) return null;
            return (int)Math.Round((cur - min) * 100.0 / (max - min));
        }
        finally { DestroyPhysicalMonitors(count, arr); }
    }

    private static int? _brightCache;
    private static DateTime _brightAt = DateTime.MinValue;

    /// <summary>把读不到功率的原因写清楚，方便排障。</summary>
    public static string Diagnose()
    {
        var s = Read();
        if (!s.Present) return $"电池不可用：{LastError ?? "未知"}";
        if (!s.RateKnown) return s.OnAc ? "当前接着电源（充电/浮充时不报放电功率）" : "电池存在但 ACPI 未上报放电功率";
        return $"放电功率 {s.RateMw} mW，剩余 {s.Percent}%";
    }
}
