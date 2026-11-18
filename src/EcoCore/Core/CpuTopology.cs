using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EcoCore.Core;

/// <summary>一个 CPU Set 的信息（对应 SYSTEM_CPU_SET_INFORMATION.CpuSet）。</summary>
public sealed class CpuSetEntry
{
    public uint Id { get; init; }
    public ushort Group { get; init; }
    public byte LogicalProcessorIndex { get; init; }
    public byte CoreIndex { get; init; }
    public byte LastLevelCacheIndex { get; init; }
    public byte NumaNodeIndex { get; init; }
    /// <summary>能效等级。数值越大 = 性能越强。Intel 上 P 核 > E 核。</summary>
    public byte EfficiencyClass { get; init; }
    public byte Flags { get; init; }
    public uint SchedulingClass { get; init; }

    /// <summary>真正的大核（性能核）。</summary>
    public bool IsPerformanceCore { get; set; }
    /// <summary>真正的小核（能效核）。</summary>
    public bool IsEfficiencyCore { get; set; }

    public string Kind => IsPerformanceCore ? "P" : IsEfficiencyCore ? "E" : "?";

    public override string ToString()
        => $"#{Id} LP{Group}:{LogicalProcessorIndex} core={CoreIndex} eff={EfficiencyClass} {(IsPerformanceCore ? "P" : IsEfficiencyCore ? "E" : "?")}";
}

/// <summary>
/// CPU 拓扑：用 GetSystemCpuSetInformation 按 EfficiencyClass 把物理核心分成大核 / 小核两组。
/// 这是整个软件的地基 —— 后面所有"隔离"都是围绕这两组 Id 做文章。
/// </summary>
public sealed class CpuTopology
{
    public IReadOnlyList<CpuSetEntry> All { get; }
    public IReadOnlyList<CpuSetEntry> PerformanceCores { get; }
    public IReadOnlyList<CpuSetEntry> EfficiencyCores { get; }

    /// <summary>真·大小核混合架构（存在至少两个不同的 EfficiencyClass）。</summary>
    public bool IsHybrid { get; }

    /// <summary>CPU 型号名（从注册表读，取不到就给个兜底串）。</summary>
    public string CpuName { get; private init; } = "未知 CPU";

    /// <summary>能不能用上大小核隔离：必须是混合架构且确实存在 E 核。</summary>
    public bool SupportsIsolation => IsHybrid && EfficiencyCores.Count > 0;

    public byte MaxEfficiencyClass { get; }
    public byte MinEfficiencyClass { get; }

    public uint[] EfficiencyCoreIds => EfficiencyCores.Select(c => c.Id).ToArray();
    public uint[] PerformanceCoreIds => PerformanceCores.Select(c => c.Id).ToArray();
    public uint[] AllIds => All.Select(c => c.Id).ToArray();

    /// <summary>
    /// 取 N 个"留给系统"的大核。按 CoreIndex 从小到大取 —— 也就是 LP0/LP1 那一片，
    /// Windows 本来就把中断、DPC、内核线程主要压在这些核上，保留它们最不打扰调度。
    /// </summary>
    public IReadOnlyList<CpuSetEntry> ReservedPerformanceCores(int n)
    {
        int k = Math.Clamp(n, 0, PerformanceCores.Count);
        return PerformanceCores.OrderBy(c => c.CoreIndex).Take(k).ToList();
    }

    /// <summary>剔除保留核之后，真正"可以被系统之外的任务使用"的大核。</summary>
    public IReadOnlyList<CpuSetEntry> AvailablePerformanceCores(int reserved)
    {
        var keep = ReservedPerformanceCores(reserved).Select(c => c.Id).ToHashSet();
        return PerformanceCores.Where(c => !keep.Contains(c.Id)).ToList();
    }

    private CpuTopology(List<CpuSetEntry> all)
    {
        All = all;

        var classes = all.Select(a => a.EfficiencyClass).Distinct().OrderBy(x => x).ToList();
        IsHybrid = classes.Count > 1;
        MaxEfficiencyClass = classes.Count > 0 ? classes[^1] : (byte)0;
        MinEfficiencyClass = classes.Count > 0 ? classes[0] : (byte)0;

        if (IsHybrid)
        {
            foreach (var e in all)
            {
                if (e.EfficiencyClass == MaxEfficiencyClass) e.IsPerformanceCore = true;
                else if (e.EfficiencyClass == MinEfficiencyClass) e.IsEfficiencyCore = true;
                // 中间档（理论上不存在）既不标 P 也不标 E，保守处理
            }
            PerformanceCores = all.Where(a => a.IsPerformanceCore).ToList();
            EfficiencyCores = all.Where(a => a.IsEfficiencyCore).ToList();
        }
        else
        {
            // 非混合架构：全部视为"性能核"，不提供隔离
            foreach (var e in all) e.IsPerformanceCore = true;
            PerformanceCores = all.ToList();
            EfficiencyCores = Array.Empty<CpuSetEntry>();
        }
    }

    public static CpuTopology Discover()
    {
        uint len = 0;
        Native.Win32.GetSystemCpuSetInformation(IntPtr.Zero, 0, out len, IntPtr.Zero, 0);
        if (len == 0) return new CpuTopology(new List<CpuSetEntry>()) { CpuName = ReadCpuName() };

        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!Native.Win32.GetSystemCpuSetInformation(buf, len, out len, IntPtr.Zero, 0))
                throw new InvalidOperationException($"GetSystemCpuSetInformation 失败: {Marshal.GetLastWin32Error()}");

            var list = new List<CpuSetEntry>();
            int off = 0;
            int guard = 0;
            while (off < len && guard++ < 4096)
            {
                uint size = (uint)Marshal.ReadInt32(buf, off);
                if (size < 24 || off + size > len) break;

                int type = Marshal.ReadInt32(buf, off + 4);
                if (type != Native.Win32.CPU_SET_INFORMATION_TYPE_INFORMATION) { off += (int)size; continue; }

                list.Add(new CpuSetEntry
                {
                    Id = (uint)Marshal.ReadInt32(buf, off + 8),
                    Group = (ushort)Marshal.ReadInt16(buf, off + 12),
                    LogicalProcessorIndex = Marshal.ReadByte(buf, off + 14),
                    CoreIndex = Marshal.ReadByte(buf, off + 15),
                    LastLevelCacheIndex = Marshal.ReadByte(buf, off + 16),
                    NumaNodeIndex = Marshal.ReadByte(buf, off + 17),
                    EfficiencyClass = Marshal.ReadByte(buf, off + 18),
                    Flags = Marshal.ReadByte(buf, off + 19),
                    SchedulingClass = (uint)Marshal.ReadInt32(buf, off + 20)
                });

                off += (int)size;
            }

            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new CpuTopology(list) { CpuName = ReadCpuName() };
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>读注册表里的 CPU 型号名，例如 "Intel(R) Core(TM) Ultra 9 290HX Plus"。</summary>
    private static string ReadCpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var s = k?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        catch { /* 读不到就算了，不影响功能 */ }
        try { return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "未知 CPU"; }
        catch { return "未知 CPU"; }
    }

    public string Describe()
    {
        if (All.Count == 0) return $"{CpuName}：未读取到任何 CPU Set";
        if (!IsHybrid)
            return $"非混合架构：{All.Count} 个逻辑处理器，EfficiencyClass 单一（{MaxEfficiencyClass}）";
        return $"混合架构：{PerformanceCores.Count} 个 P 核（EfficiencyClass={MaxEfficiencyClass}） + " +
               $"{EfficiencyCores.Count} 个 E 核（EfficiencyClass={MinEfficiencyClass}），共 {All.Count} 个 CPU Set";
    }
}
