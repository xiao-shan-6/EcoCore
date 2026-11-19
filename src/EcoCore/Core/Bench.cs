using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace EcoCore.Core;

/// <summary>
/// 实机功耗对比测试：把每个方案真跑一遍，用 ACPI 电池放电功率（mW）当指标，
/// 同时记录 CPU 占用、屏幕亮度、前台/后台应用，最后出 CSV + 汇总。
/// 注意：只有在「拔掉电源、电池放电」时放电功率才有意义，接电源测出来是充电功率。
/// </summary>
public static class Bench
{
    private sealed class PhaseRow
    {
        public string Name = "";
        public string ProfileName = "";
        public List<double> Rates = new();
        public List<double> Cpus = new();
        public List<double> Brights = new();
        public int Percent0 = -1, Percent1 = -1;
        public string Notes = "";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public static int Run(string[] args)
    {
        int seconds = GetInt(args, "--seconds", 120);
        int settle = GetInt(args, "--settle", 30);
        bool waitUnplug = args.Contains("--wait-unplug");
        int waitMinutes = GetInt(args, "--wait-minutes", 15);
        string outDir = GetStr(args, "--out") ?? Path.Combine(AppContext.BaseDirectory, "bench",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        var log = new StringBuilder();
        void W(string s) { Console.WriteLine(s); Console.Out.Flush(); log.AppendLine(s); }

        Directory.CreateDirectory(outDir);

        var co = new Coordinator();
        var topo = co.Topology;
        bool admin = Coordinator.IsElevated();

        W("=== EcoCore 功耗对比测试 ===");
        W($"CPU        : {topo.CpuName}");
        W($"拓扑       : {(topo.IsHybrid ? $"{topo.PerformanceCores.Count} 个 P 核 + {topo.EfficiencyCores.Count} 个 E 核" : "非混合架构")}");
        W($"权限       : {(admin ? "管理员（进程层 + 电源层都会生效）" : "非管理员（只跑进程层，电源层跳过）")}");
        W($"电池       : {BatteryMeter.Diagnose()}");
        W($"屏幕亮度   : {BatteryMeter.Brightness()?.ToString() ?? "读取失败"}%");
        W($"输出目录   : {outDir}");

        // 等拔电源
        if (waitUnplug)
        {
            var until = DateTime.Now.AddMinutes(waitMinutes);
            var b0 = BatteryMeter.Read();
            if (b0.Present && (b0.OnAc || !b0.Discharging))
            {
                W("");
                W($"请拔掉电源适配器：{waitMinutes} 分钟内开始，等待中…");
                while (DateTime.Now < until)
                {
                    var b = BatteryMeter.Read();
                    if (b.Present && !b.OnAc && b.Discharging && b.RateKnown)
                    {
                        W($"已检测到电池放电（{b.RateMw} mW），3 秒后开始。");
                        Thread.Sleep(3000);
                        break;
                    }
                    Console.Write(".");
                    Console.Out.Flush();
                    Thread.Sleep(2000);
                }
                if (DateTime.Now >= until)
                {
                    W("");
                    W("超时仍接着电源，测试中止。拔掉电源后重跑。");
                    File.WriteAllText(Path.Combine(outDir, "bench.log"), log.ToString(), Encoding.UTF8);
                    return 2;
                }
            }
        }

        var start = BatteryMeter.Read();
        if (start.Present && start.OnAc)
            W("⚠ 当前接着电源：放电功率读出来的是充电功率，数值没有省电参考意义。");
        if (start.Present && !start.RateKnown)
            W("⚠ 电池没上报功率，只能靠电量百分比变化做粗测（会很不准）。");

        // 应用使用情况快照
        var procs = SnapshotProcesses(co);
        File.WriteAllLines(Path.Combine(outDir, "processes-before.csv"),
            new[] { "pid,name,workingSetMB,cpuSeconds,hasEcoQos,pinnedToE" }
                .Concat(procs.Select(p => $"{p.Pid},{p.Name},{p.WorkingSet / 1024.0 / 1024:F1},{p.CpuSeconds:F1},{p.EcoQos},{p.Pinned}")),
            Encoding.UTF8);

        var meta = new StringBuilder();
        meta.AppendLine($"time={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        meta.AppendLine($"cpu={topo.CpuName}");
        meta.AppendLine($"hybrid={topo.IsHybrid}");
        meta.AppendLine($"pcore={topo.PerformanceCores.Count}");
        meta.AppendLine($"ecore={topo.EfficiencyCores.Count}");
        meta.AppendLine($"admin={admin}");
        meta.AppendLine($"brightness={BatteryMeter.Brightness()}");
        meta.AppendLine($"batteryOnAc={start.OnAc}");
        meta.AppendLine($"batteryRateMw={(start.RateKnown ? start.RateMw : (int?)null)}");
        meta.AppendLine($"scheme={co.CurrentSchemeInfo()}");
        meta.AppendLine($"seconds={seconds}");
        meta.AppendLine($"settle={settle}");
        meta.AppendLine("---top by memory---");
        foreach (var p in procs.Take(20)) meta.AppendLine($"{p.Name}\t{p.Pid}\t{p.WorkingSet / 1024.0 / 1024:F0}MB\tcpu={p.CpuSeconds:F0}s");
        meta.AppendLine("---top by cpu---");
        foreach (var p in procs.OrderByDescending(x => x.CpuSeconds).Take(15)) meta.AppendLine($"{p.Name}\t{p.Pid}\tcpu={p.CpuSeconds:F0}s");
        File.WriteAllText(Path.Combine(outDir, "meta.txt"), meta.ToString(), Encoding.UTF8);

        // 阶段表：baseline = 什么都不动，直接量
        bool ab = args.Contains("--ab");
        int rounds = Math.Max(1, GetInt(args, "--rounds", 3));
        string wantProfile = GetStr(args, "--profile") ?? "maxsaving";
        var target = EcoProfile.Presets().FirstOrDefault(p => p.Name.Equals(wantProfile, StringComparison.OrdinalIgnoreCase)
            || p.Name == wantProfile) ?? wantProfile switch
        {
            "balanced" => EcoProfile.Balanced(),
            "maxsaving" => EcoProfile.MaxSaving(),
            "light" => EcoProfile.LightSaving(),
            "performance" => EcoProfile.Performance(),
            _ => EcoProfile.MaxSaving(),
        };

        var phases = ab
            ? Enumerable.Range(1, rounds)
                .SelectMany(r => new (string Key, string Title, EcoProfile? Profile)[]
                {
                    ($"base{r}", $"第 {r} 轮 原样（不用 EcoCore）", null),
                    ($"test{r}", $"第 {r} 轮 EcoCore「{target.Name}」", target.Clone()),
                })
                .ToArray()
            : new (string Key, string Title, EcoProfile? Profile)[]
        {
            ("baseline", "原样（当前系统/华硕电源方案，不用 EcoCore）", null),
            ("balanced", "EcoCore「均衡」", EcoProfile.Balanced()),
            ("maxsaving", "EcoCore「极致省电」", EcoProfile.MaxSaving()),
            ("light", "EcoCore「轻度省电」", EcoProfile.LightSaving()),
            ("off", "关闭 EcoCore（恢复默认）", null),
        };

        // 可选：拉起 N 个可控负载进程，模拟"写代码"那种持续但很轻的后台占用
        int load = GetInt(args, "--load", 0);
        var loaders = new List<Process>();
        if (load > 0)
        {
            string host = Environment.ProcessPath ?? "dotnet";
            string entry = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
            bool dotnetHost = host.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) && entry.Length > 0;
            int totalSec = (settle + seconds) * phases.Length + 90;
            for (int i = 0; i < load; i++)
            {
                try
                {
                    var psi = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    if (dotnetHost) psi.ArgumentList.Add(entry);
                    psi.ArgumentList.Add("--busyload");
                    psi.ArgumentList.Add("--seconds");
                    psi.ArgumentList.Add(totalSec.ToString(CultureInfo.InvariantCulture));
                    var lp = Process.Start(psi);
                    if (lp != null) loaders.Add(lp);
                }
                catch (Exception ex) { W($"⚠ 负载进程启动失败：{ex.Message}"); }
            }
            W($"轻负载     : 已拉起 {loaders.Count} 个忙循环进程（模拟写代码时的后台编译/索引占用）");
            Thread.Sleep(3000);
        }

        var rows = new List<PhaseRow>();
        var csv = new StringBuilder();
        csv.AppendLine("time,phase,elapsed_s,rate_mw,percent,on_ac,cpu_pct,brightness,fg_app,fg_title");

        var sw = Stopwatch.StartNew();
        foreach (var (key, title, profile) in phases)
        {
            var row = new PhaseRow { Name = key, ProfileName = title };
            W("");
            W($"── 阶段 {key}：{title} ──");

            // 应用 / 还原
            string msg;
            bool ok = true;
            if (key == "off" || key.StartsWith("base", StringComparison.Ordinal)) ok = co.Restore(out msg);
            else if (profile != null) ok = co.Apply(profile, out msg);
            else msg = "不做改动，保持现状";
            W($"  {(ok ? "✓" : "✗")} {msg}");
            row.Notes = msg;

            W($"  稳定 {settle}s（等调度器/频率收敛）…");
            Thread.Sleep(settle * 1000);

            row.Percent0 = BatteryMeter.Read().Percent;
            var t0 = DateTime.Now;
            var lastCpu = Sample.cpu();
            int n = 0;
            double sum = 0;
            while ((DateTime.Now - t0).TotalSeconds < seconds)
            {
                Thread.Sleep(2000);
                var b = BatteryMeter.Read();
                var cpu = Sample.cpuDelta(lastCpu, out lastCpu);
                var bright = BatteryMeter.Brightness();
                var fg = Foreground();

                csv.AppendLine(string.Join(",",
                    DateTime.Now.ToString("HH:mm:ss"), key,
                    ((DateTime.Now - t0).TotalSeconds).ToString("F0", CultureInfo.InvariantCulture),
                    b.RateKnown ? b.RateMw.ToString() : "",
                    b.Percent.ToString(),
                    b.OnAc ? "1" : "0",
                    cpu.ToString("F1", CultureInfo.InvariantCulture),
                    bright?.ToString() ?? "",
                    Csv(fg.Name), Csv(fg.ReplacedTitle())));

                if (b.RateKnown) { row.Rates.Add(b.RateMw); sum += b.RateMw; }
                row.Cpus.Add(cpu);
                if (bright != null) row.Brights.Add(bright.Value);
                n++;
                Console.Write($"\r  {key}: {n * 2}/{seconds}s  平均 {(row.Rates.Count > 0 ? (sum / row.Rates.Count).ToString("F0") : "?")} mW  CPU {cpu:F0}%   ");
            }
            row.Percent1 = BatteryMeter.Read().Percent;
            W("");
            rows.Add(row);
        }

        co.Restore(out _);   // 收尾一定还原
        foreach (var lp in loaders)
        {
            try { if (!lp.HasExited) lp.Kill(true); } catch { }
            try { lp.Dispose(); } catch { }
        }

        // 汇总
        var summary = new StringBuilder();
        summary.AppendLine("=== EcoCore 功耗对比汇总 ===");
        summary.AppendLine($"机型/CPU : {topo.CpuName}（{topo.PerformanceCores.Count}P + {topo.EfficiencyCores.Count}E）");
        summary.AppendLine($"权限     : {(admin ? "管理员" : "非管理员（电源层未生效）")}");
        summary.AppendLine($"屏幕亮度 : {BatteryMeter.Brightness()?.ToString() ?? "?"}%（测试期间保持不变）");
        summary.AppendLine($"采样     : 每阶段稳定 {settle}s 后连续采 {seconds}s，2s 一个点");
        summary.AppendLine();
        summary.AppendLine("阶段            平均功耗      最低      最高   样本  平均CPU  亮度  电量变化");
        double baseAvg = rows.FirstOrDefault(r => r.Name == "baseline")?.Rates.DefaultIfEmpty(0).Average() ?? 0;

        foreach (var r in rows)
        {
            double avg = r.Rates.Count > 0 ? r.Rates.Average() : double.NaN;
            string delta = (r.Name != "baseline" && baseAvg > 0 && !double.IsNaN(avg))
                ? $"  （相对原样 {(avg - baseAvg) / baseAvg * 100:F1}%）" : "";
            summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-14} {1,10} {2,8} {3,8} {4,5} {5,8} {6,5}  {7}% → {8}%{9}",
                r.Name,
                r.Rates.Count > 0 ? $"{avg:F0} mW" : "无数据",
                r.Rates.Count > 0 ? $"{r.Rates.Min():F0}" : "-",
                r.Rates.Count > 0 ? $"{r.Rates.Max():F0}" : "-",
                r.Rates.Count,
                r.Cpus.Count > 0 ? $"{r.Cpus.Average():F1}%" : "-",
                r.Brights.Count > 0 ? $"{r.Brights.Average():F0}%" : "-",
                r.Percent0, r.Percent1, delta));
        }

        summary.AppendLine();
        if (ab)
        {
            // 配对分析：同一轮里的 base 与 test 相减，抵消机器发热/后台波动造成的整体漂移
            var pairs = new List<(int Round, double Base, double Test)>();
            for (int r = 1; r <= rounds; r++)
            {
                var b = rows.FirstOrDefault(x => x.Name == $"base{r}");
                var t = rows.FirstOrDefault(x => x.Name == $"test{r}");
                if (b?.Rates.Count > 0 && t?.Rates.Count > 0)
                    pairs.Add((r, b.Rates.Average(), t.Rates.Average()));
            }
            summary.AppendLine($"=== 配对对比（每个回合交替「原样 → EcoCore {target.Name}」，抵消漂移）===");
            summary.AppendLine("回合   原样          EcoCore       差值       百分比");
            foreach (var (r, b, t) in pairs)
                summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-5} {1,10:F0} mW {2,10:F0} mW {3,9:F0} mW {4,9:F1}%", r, b, t, t - b, (t - b) / b * 100));
            if (pairs.Count > 0)
            {
                double mb = pairs.Average(x => x.Base), mt = pairs.Average(x => x.Test);
                summary.AppendLine();
                summary.AppendLine($"平均：原样 {mb:F0} mW → EcoCore「{target.Name}」{mt:F0} mW，变化 {(mt - mb) / mb * 100:F1}%（{mt - mb:F0} mW）");
                double wh = 82.0;   // 本机电池 82112 mWh
                summary.AppendLine($"续航估算（{wh:F0} Wh 电池）：原样 {wh / (mb / 1000):F2} 小时 → {(wh / (mt / 1000)):F2} 小时");
            }
            else summary.AppendLine("配对数据不足，无法给出结论。");
        }
        else if (baseAvg > 0)
        {
            var best = rows.Where(r => r.Rates.Count > 0 && r.Name != "baseline").OrderBy(r => r.Rates.Average()).FirstOrDefault();
            if (best != null)
                summary.AppendLine($"最省电：{best.Name}（{best.Rates.Average():F0} mW，比原样低 {(baseAvg - best.Rates.Average()) / baseAvg * 100:F1}%）");
            else
                summary.AppendLine("没有可比数据");
            double hours = baseAvg / 1000.0;
            summary.AppendLine($"按原样 {baseAvg:F0} mW 估算，若电池容量为 X Wh，续航 ≈ X/{hours:F2} 小时；同容量下换成最省电方案会按上面的百分比线性拉长。");
        }
        else summary.AppendLine("没有读到放电功率，无法给出结论（检查是不是接着电源）。");

        File.WriteAllText(Path.Combine(outDir, "samples.csv"), csv.ToString(), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary.ToString(), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outDir, "bench.log"), log.ToString() + summary, Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine(summary.ToString());
        Console.WriteLine($"文件：{outDir}");
        Console.WriteLine($"耗时：{sw.Elapsed.TotalMinutes:F1} 分钟");
        return 0;
    }

    private sealed class ProcRow
    {
        public int Pid; public string Name = ""; public long WorkingSet; public double CpuSeconds; public bool EcoQos; public bool Pinned;
    }

    private static List<ProcRow> SnapshotProcesses(Coordinator co)
    {
        var list = new List<ProcRow>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var row = new ProcRow { Pid = p.Id, Name = p.ProcessName, WorkingSet = p.WorkingSet64 };
                try { row.CpuSeconds = p.TotalProcessorTime.TotalSeconds; } catch { }
                row.EcoQos = ProcessEngine.ReadEcoQos(p.Id);
                var sets = ProcessEngine.ReadProcessCpuSets(p.Id);
                if (sets.Length > 0 && co.Topology.EfficiencyCoreIds.Length > 0)
                    row.Pinned = sets.All(id => co.Topology.EfficiencyCoreIds.Contains(id));
                list.Add(row);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list;
    }

    private readonly record struct Fg(string Name, string Title)
    {
        public string ReplacedTitle() => Title.Replace("\"", "'");
    }

    private static Fg Foreground()
    {
        try
        {
            int pid = Coordinator.ForegroundPid();
            IntPtr h = GetForegroundWindow();
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            string name = "";
            try { name = Process.GetProcessById(pid).ProcessName; } catch { }
            return new Fg(name, sb.ToString());
        }
        catch { return new Fg("", ""); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    private static string Csv(string? s) => s == null ? "" : (s.Contains(',') || s.Contains('"')) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static int GetInt(string[] a, string key, int dflt)
    {
        int i = Array.IndexOf(a, key);
        return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out int v) ? v : dflt;
    }

    private static string? GetStr(string[] a, string key)
    {
        int i = Array.IndexOf(a, key);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}

/// <summary>总 CPU 占用（两次采样之间的增量），不用 WMI。</summary>
internal static class Sample
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public sealed class CpuState { public long Idle, Kernel, User; }

    public static CpuState cpu()
    {
        GetSystemTimes(out long i, out long k, out long u);
        return new CpuState { Idle = i, Kernel = k, User = u };
    }

    public static double cpuDelta(CpuState prev, out CpuState now)
    {
        now = cpu();
        long dIdle = now.Idle - prev.Idle;
        long dKernel = now.Kernel - prev.Kernel;
        long dUser = now.User - prev.User;
        long total = dKernel + dUser;              // kernel 里已经含 idle
        if (total <= 0) return 0;
        double busy = total - dIdle;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }
}
