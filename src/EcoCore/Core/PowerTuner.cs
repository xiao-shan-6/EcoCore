using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace EcoCore.Core;

/// <summary>一个可调电源设置项的元数据。</summary>
public sealed class PowerSettingDef
{
    public required string Alias { get; init; }
    public required string DisplayName { get; init; }
    public required Guid Guid { get; init; }
    public uint Min { get; init; }
    public uint Max { get; init; }
    public IReadOnlyDictionary<uint, string>? ValueNames { get; init; }

    public string Explain(uint v) =>
        ValueNames is not null && ValueNames.TryGetValue(v, out var n) ? n : v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 电源策略层。所有"降低大核占用"的电源旋钮都在这里。
/// 做法：复制一份当前激活方案 → 在副本上写值 → 激活副本。这样用户的原始方案毫发无损。
/// </summary>
public sealed class PowerTuner
{
    public static readonly Guid SUB_PROCESSOR = new("54533251-82be-4824-96c1-47b60b740d00");

    private static readonly Dictionary<uint, string> HeteroPolicyNames = new()
    {
        [0] = "所有处理器",
        [1] = "高性能处理器（只用 P 核）",
        [2] = "首选高性能处理器",
        [3] = "高效处理器（只用 E 核）",
        [4] = "首选高效处理器",
        [5] = "自动"
    };

    private static readonly Dictionary<uint, string> BoostModeNames = new()
    {
        [0] = "已禁用",
        [1] = "已启用",
        [2] = "积极",
        [3] = "高效启用",
        [4] = "高效积极",
        [5] = "保证频率下积极",
        [6] = "保证频率下高效积极"
    };

    // ── 本软件会改动的全部设置项 ──
    public static readonly PowerSettingDef SchedPolicy = new()
    {
        Alias = "SCHEDPOLICY",
        DisplayName = "异类线程调度策略",
        Guid = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d"),
        Min = 0, Max = 5,
        ValueNames = HeteroPolicyNames
    };

    public static readonly PowerSettingDef ShortSchedPolicy = new()
    {
        Alias = "SHORTSCHEDPOLICY",
        DisplayName = "异类短运行线程调度策略",
        Guid = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b"),
        Min = 0, Max = 5,
        ValueNames = HeteroPolicyNames
    };

    public static readonly PowerSettingDef PerfBoostMode = new()
    {
        Alias = "PERFBOOSTMODE",
        DisplayName = "处理器性能提升模式（睿频）",
        Guid = new Guid("be337238-0d82-4146-a960-4f3749d470c7"),
        Min = 0, Max = 6,
        ValueNames = BoostModeNames
    };

    public static readonly PowerSettingDef ThrottleMax = new()
    {
        Alias = "PROCTHROTTLEMAX",
        DisplayName = "最大处理器状态",
        Guid = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"),
        Min = 1, Max = 100
    };

    public static readonly PowerSettingDef ThrottleMin = new()
    {
        Alias = "PROCTHROTTLEMIN",
        DisplayName = "最小处理器状态",
        Guid = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c"),
        Min = 0, Max = 100
    };

    public static readonly PowerSettingDef PerfEpp = new()
    {
        Alias = "PERFEPP",
        DisplayName = "能效偏好 EPP",
        Guid = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"),
        Min = 0, Max = 100
    };

    public static readonly PowerSettingDef CpMinCores = new()
    {
        Alias = "CPMINCORES",
        DisplayName = "核心停放下限（最少不停放的核心百分比）",
        Guid = new Guid("0cc5b647-c1df-4637-891a-dec35c318583"),
        Min = 0, Max = 100
    };

    public static readonly PowerSettingDef CpMaxCores = new()
    {
        Alias = "CPMAXCORES",
        DisplayName = "核心停放上限（最多不停放的核心百分比）",
        Guid = new Guid("0cc5b647-c1df-4637-891a-dec35c318584"),
        Min = 0, Max = 100
    };

    public static readonly PowerSettingDef[] All =
    {
        SchedPolicy, ShortSchedPolicy, PerfBoostMode, ThrottleMax, ThrottleMin, PerfEpp, CpMinCores, CpMaxCores
    };

    // ── 电源滑块 overlay ──
    public static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static readonly Guid OverlayBetterPerformance = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    public static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    // ───────────────────────── 方案操作 ─────────────────────────

    public static Guid GetActiveScheme()
    {
        uint r = Native.Win32.PowerGetActiveScheme(IntPtr.Zero, out IntPtr p);
        if (r != 0 || p == IntPtr.Zero)
            throw new InvalidOperationException($"PowerGetActiveScheme 失败: {r}");
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { Native.Win32.LocalFree(p); }
    }

    public static bool SetActiveScheme(Guid g)
        => Native.Win32.PowerSetActiveScheme(IntPtr.Zero, ref g) == 0;

    /// <summary>复制当前激活方案，返回新方案 GUID。</summary>
    public static Guid DuplicateActive(Guid src)
    {
        uint r = Native.Win32.PowerDuplicateScheme(IntPtr.Zero, ref src, out IntPtr dst);
        if (r != 0 || dst == IntPtr.Zero)
            throw new InvalidOperationException($"PowerDuplicateScheme 失败: {r}");
        try { return Marshal.PtrToStructure<Guid>(dst); }
        finally { Native.Win32.LocalFree(dst); }
    }

    public static bool DeleteScheme(Guid g)
        => Native.Win32.PowerDeleteScheme(IntPtr.Zero, ref g) == 0;

    public static void RenameScheme(Guid g, string name)
    {
        var psi = new ProcessStartInfo("powercfg", $"-changename {g} \"{name}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(4000);
    }

    // ───────────────────────── 读写 ─────────────────────────

    public static uint ReadAc(Guid scheme, PowerSettingDef def)
    {
        var sub = SUB_PROCESSOR;
        var set = def.Guid;
        uint r = Native.Win32.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out uint v);
        if (r != 0) throw new InvalidOperationException($"读取 {def.Alias}(AC) 失败: 错误码 {r}");
        return v;
    }

    public static uint ReadDc(Guid scheme, PowerSettingDef def)
    {
        var sub = SUB_PROCESSOR;
        var set = def.Guid;
        uint r = Native.Win32.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out uint v);
        if (r != 0) throw new InvalidOperationException($"读取 {def.Alias}(DC) 失败: 错误码 {r}");
        return v;
    }

    public static bool TryReadAc(Guid scheme, PowerSettingDef def, out uint value)
    {
        var sub = SUB_PROCESSOR;
        var set = def.Guid;
        uint r = Native.Win32.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out value);
        return r == 0;
    }

    public static bool TryReadDc(Guid scheme, PowerSettingDef def, out uint value)
    {
        var sub = SUB_PROCESSOR;
        var set = def.Guid;
        uint r = Native.Win32.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out value);
        return r == 0;
    }

    public static uint WriteAc(Guid scheme, PowerSettingDef def, uint value)
    {
        var sub = SUB_PROCESSOR;
        var set = def.Guid;
        return Native.Win32.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, value);
    }

    public static uint WriteDc(Guid scheme, PowerSettingDef def, uint value)
    {
        var sub = SUB_PROCESSOR;
        var set = def.Guid;
        return Native.Win32.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, value);
    }

    public static bool TrySetOverlay(Guid overlay)
        => Native.Win32.PowerSetActiveOverlayScheme(overlay) == 0;

    public static Guid? GetOverlay()
    {
        uint r = Native.Win32.PowerGetEffectiveOverlayScheme(out Guid g);
        return r == 0 ? g : null;
    }

    public static string OverlayName(Guid? g)
    {
        if (g is null) return "未知";
        if (g == OverlayBestPerformance) return "最佳性能";
        if (g == OverlayBetterPerformance) return "更好的性能";
        if (g == OverlayBestEfficiency) return "最佳能效";
        return g.Value.ToString();
    }

    /// <summary>读出某个方案下我们关心的全部设置，用于快照与显示。</summary>
    public static Dictionary<string, (uint Ac, uint Dc)> Snapshot(Guid scheme)
    {
        var d = new Dictionary<string, (uint, uint)>();
        foreach (var def in All)
        {
            uint ac = 0, dc = 0;
            bool okAc = TryReadAc(scheme, def, out ac);
            bool okDc = TryReadDc(scheme, def, out dc);
            if (okAc || okDc) d[def.Alias] = (okAc ? ac : dc, okDc ? dc : ac);
        }
        return d;
    }
}
