using System.Text;
using EcoCore.Core;
using EcoCore.UI;

namespace EcoCore;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 自检模式：不开界面，把引擎跑一遍写进日志，用于验证/排障。
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run(args);

        // 冒烟模式：只把主窗体建出来渲染一帧就退出，用来验证界面代码不抛异常。
        if (args.Any(a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Smoke();

        // 截图模式：把主窗体画成 PNG，用来核对排版
        // 可选 --profile 自定义 指定启动方案
        int profIdx = Array.FindIndex(args, a => a.Equals("--profile", StringComparison.OrdinalIgnoreCase));
        if (profIdx >= 0 && profIdx + 1 < args.Length)
        {
            var want = args[profIdx + 1];
            MainForm.StartupProfileOverride = EcoProfile.Presets()
                .FirstOrDefault(p => p.Name == want) ?? EcoProfile.Balanced();
        }

        // 调试：启动时直接打开「精细调节」第二界面（截图/排障用）
        if (args.Any(a => a.Equals("--detail", StringComparison.OrdinalIgnoreCase)))
            MainForm.StartupOpenDetail = true;

        int shotIdx = Array.FindIndex(args, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
        if (shotIdx >= 0)
        {
            string outPath = shotIdx + 1 < args.Length
                ? args[shotIdx + 1]
                : Path.Combine(AppContext.BaseDirectory, "ui.png");
            return SelfTest.Shot(outPath);
        }

        // 端到端功能实测：真跑一遍 Apply → 读回验证 → Restore
        if (args.Any(a => a.Equals("--applytest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.ApplyTest();

        // 实机功耗对比：各方案逐个跑，读 ACPI 电池放电功率，出 CSV + 汇总
        if (args.Any(a => a.Equals("--bench", StringComparison.OrdinalIgnoreCase)))
            return Bench.Run(args);

        // 只读一次电池/亮度，用来确认能不能测
        if (args.Any(a => a.Equals("--battery", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(BatteryMeter.Diagnose());
            Console.WriteLine($"屏幕亮度: {BatteryMeter.Brightness()?.ToString() ?? "读取失败"}%");
            var bs = BatteryMeter.Read();
            Console.WriteLine($"设备: {BatteryMeter.DevicePath}");
            Console.WriteLine($"容量: {bs.CapacityMw} mW / 满充 {bs.FullCapacityMw} mW  ({bs.Percent}%)  放电={bs.Discharging} 充电={bs.Charging} 接电源={bs.OnAc}");
            Console.WriteLine($"原始状态 ({BatteryMeter.RawReturned} 字节): {BatteryMeter.RawStatus}");
            return 0;
        }

        // 诊断：查某个（或几个）进程当前真实的 CPU Set 与 EcoQoS 状态
        // 用途：验证「钉到 E 核」到底有没有落到内核里，而不是只看返回值
        if (args.Any(a => a.Equals("--pidinfo", StringComparison.OrdinalIgnoreCase)))
        {
            int i0 = Array.FindIndex(args, a => a.Equals("--pidinfo", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("pid     进程               EcoQoS  CPU Set");
            for (int j = i0 + 1; j < args.Length && int.TryParse(args[j], out int pid); j++)
            {
                string name;
                try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { name = "?"; }
                var sets = ProcessEngine.ReadProcessCpuSets(pid);
                bool eco = ProcessEngine.ReadEcoQos(pid);
                string ids = sets.Length == 0 ? "（读不到）" : string.Join(",", sets.OrderBy(x => x));
                Console.WriteLine($"{pid,-8} {name,-18} {eco,-6} {ids}");
            }
            return 0;
        }

        // 手工验证用：按「极致省电」钉住所有后台进程并保持（不自动还原，配合 --unpinall）
        if (args.Any(a => a.Equals("--pinall", StringComparison.OrdinalIgnoreCase)))
        {
            var cp = new Coordinator();
            Console.WriteLine(Coordinator.IsElevated() ? "权限: 管理员（电源层也会写）" : "权限: 普通用户（只跑进程层）");
            Console.WriteLine(cp.Apply(EcoProfile.MaxSaving(), out string psum) ? psum : "失败: " + psum);
            return 0;
        }

        if (args.Any(a => a.Equals("--unpinall", StringComparison.OrdinalIgnoreCase)))
        {
            // 绑核不跨进程持久：新进程的 Coordinator 不知道上一轮钉过谁，
            // 所以这里直接把所有进程的 CPU Set 清空（= 允许全部核心）、并关掉 EcoQoS。
            int cleared = 0, ecoOff = 0, denied = 0;
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId || p.Id <= 4) continue;
                    if (ProcessEngine.SetProcessCpuSets(p.Id, null, out string d1)) cleared++;
                    else if (d1.Contains("拒绝") || d1.Contains("Access")) denied++;
                    if (ProcessEngine.SetEcoQos(p.Id, false, out _)) ecoOff++;
                }
            }
            var cp = new Coordinator();
            cp.Restore(out string rsum);
            Console.WriteLine($"清除 CPU Set 绑定: {cleared} 个进程（拒绝 {denied} 个），关闭 EcoQoS: {ecoOff} 个");
            Console.WriteLine(rsum);
            return 0;
        }

        // 可控负载进程：给 --bench 当"后台应用"用，跑固定时长的忙循环
        if (args.Any(a => a.Equals("--busyload", StringComparison.OrdinalIgnoreCase)))
        {
            int sec = 600;
            int si = Array.FindIndex(args, a => a.Equals("--seconds", StringComparison.OrdinalIgnoreCase));
            if (si >= 0 && si + 1 < args.Length && int.TryParse(args[si + 1], out int v)) sec = v;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double acc = 0;
            while (sw.Elapsed.TotalSeconds < sec)
            {
                for (int k = 0; k < 200000; k++) acc += Math.Sqrt(k % 97) * 1.000001;
                if (acc > 1e18) acc = 0;   // 防止被优化掉
            }
            Console.WriteLine(acc);
            return 0;
        }

        // 精细调节第二界面的截图
        int shot3Idx = Array.FindIndex(args, a => a.Equals("--shot3", StringComparison.OrdinalIgnoreCase));
        if (shot3Idx >= 0)
        {
            string outPath3 = shot3Idx + 1 < args.Length
                ? args[shot3Idx + 1]
                : Path.Combine(AppContext.BaseDirectory, "ui-detail.png");
            return SelfTest.Shot3(outPath3);
        }

        // 进程列表截图
        int shot2Idx = Array.FindIndex(args, a => a.Equals("--shot2", StringComparison.OrdinalIgnoreCase));
        if (shot2Idx >= 0)
        {
            string outPath2 = shot2Idx + 1 < args.Length
                ? args[shot2Idx + 1]
                : Path.Combine(AppContext.BaseDirectory, "ui2.png");
            return SelfTest.Shot2(outPath2);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{e.ExceptionObject}");
            }
            catch { }
        };

        using var mutex = new Mutex(true, @"Global\EcoCore.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("节能核心已经在运行了（看任务栏托盘）。", "节能核心",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
        return 0;
    }
}

/// <summary>无界面自检：验证拓扑、权限、电源读写、CPU Set 绑定是否真能用。</summary>
internal static class SelfTest
{
    private static readonly List<string> StageLog = new();

    private static void Stage(string s)
    {
        StageLog.Add($"{DateTime.Now:HH:mm:ss.fff} {s}");
        try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "stage.txt"), StageLog); } catch { }
    }

    public static int Run(string[] args)
    {
        Stage("enter Run");
        try { return RunCore(args); }
        catch (Exception ex)
        {
            Stage("crash: " + ex.GetType().Name + " " + ex.Message);
            Console.WriteLine("自检崩溃: " + ex);
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest-crash.log"), ex.ToString());
            }
            catch { }
            return 2;
        }
    }

    /// <summary>冒烟测试：构建主窗体、渲染一帧、自动关闭。验证 UI 代码路径不抛异常。</summary>
    public static int Smoke()
    {
        Stage("smoke enter");
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Stage("smoke new MainForm");
            using var f = new MainForm();
            f.Opacity = 0.01;          // 基本不可见，不打扰用户
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.Show();
            Stage("smoke shown");
            for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(30); }
            Stage("smoke pumping ok");
            f.Close();
            Stage("smoke closed");
            var log = Path.Combine(AppContext.BaseDirectory, "smoke.log");
            File.WriteAllText(log, "OK\n" + string.Join("\n", StageLog));
            Console.WriteLine("SMOKE OK -> " + log);
            return 0;
        }
        catch (Exception ex)
        {
            Stage("smoke crash: " + ex.GetType().Name + " " + ex.Message);
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-crash.log"), ex.ToString());
            }
            catch { }
            Console.WriteLine("SMOKE FAIL: " + ex);
            return 3;
        }
    }

    /// <summary>把主窗体渲染成 PNG，用于人工核对排版/字体有没有被裁掉。</summary>
    public static int Shot(string path)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var f = new MainForm();
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);   // 屏幕外，不闪
            f.ShowInTaskbar = false;
            f.Show();
            for (int i = 0; i < 60; i++) { Application.DoEvents(); Thread.Sleep(20); }

            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"SHOT OK dpi={f.DeviceDpi} size={f.Width}x{f.Height} -> {path}");
            f.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SHOT FAIL: " + ex);
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shot-crash.log"), ex.ToString()); } catch { }
            return 4;
        }
    }

    /// <summary>把「精细调节」二级界面渲染成 PNG（走真实主界面切换流程），用来自查排版。</summary>
    public static int Shot3(string path)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--custom", StringComparison.OrdinalIgnoreCase)))
                MainForm.StartupProfileOverride = EcoProfile.Custom();
            MainForm.StartupOpenDetail = true;

            using var f = new MainForm();
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.ShowInTaskbar = false;
            f.Show();
            for (int i = 0; i < 160; i++) { Application.DoEvents(); Thread.Sleep(20); }

            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"SHOT3 OK dpi={f.DeviceDpi} size={f.Width}x{f.Height} -> {path}");
            f.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SHOT3 FAIL: " + ex);
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shot3-crash.log"), ex.ToString()); } catch { }
            return 4;
        }
    }


    /// <summary>把进程列表窗体渲染成 PNG。</summary>
    public static int Shot2(string path)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var co = new Coordinator();
            using var f = new ProcessForm(co);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.ShowInTaskbar = false;
            f.Show();
            for (int i = 0; i < 120; i++) { Application.DoEvents(); Thread.Sleep(20); }

            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"SHOT2 OK dpi={f.DeviceDpi} size={f.Width}x{f.Height} -> {path}");
            f.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SHOT2 FAIL: " + ex);
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shot2-crash.log"), ex.ToString()); } catch { }
            return 4;
        }
    }

    /// <summary>
    /// 端到端实测：起一个探针进程 → 真跑 Coordinator.Apply → 读回探针的 CPU Set 确认真的被压到 E 核
    /// → 再 Restore 确认能还原。这一步不依赖界面，是最能说明"软件确实干活了"的证据。
    /// </summary>
    public static int ApplyTest()
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        W("==================== EcoCore 端到端实测 ====================");
        W($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"管理员: {Coordinator.IsElevated()}");

        System.Diagnostics.Process? probe = null;
        int rc = 0;
        try
        {
            var co = new Coordinator();
            W("拓扑: " + co.Topology.Describe());
            W($"E 核: [{string.Join(", ", co.Topology.EfficiencyCoreIds)}]");

            probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                UseShellExecute = true
            });
            if (probe is null) throw new InvalidOperationException("启动 notepad 失败");
            Thread.Sleep(1200);
            probe.Refresh();
            W($"探针: notepad.exe pid={probe.Id}");

            var before = ProcessEngine.ReadProcessCpuSets(probe.Id);
            W($"应用前探针 CPU Set: {(before.Length == 0 ? "全部（未限制）" : string.Join(",", before))}");

            var prof = EcoProfile.Balanced();
            prof.ReservedPerformanceCores = 1;
            bool ok = co.Apply(prof, out string summary);
            W("");
            W($"Apply([{prof.Name}]) → {ok}");
            W(summary);
            W("");

            var after = ProcessEngine.ReadProcessCpuSets(probe.Id);
            bool pinned = after.Length > 0 && after.All(id => co.Topology.EfficiencyCoreIds.Contains(id));
            W($"应用后探针 CPU Set: [{string.Join(", ", after)}]");
            W(pinned ? "[PASS] 探针已被限制在 E 核内" : "[FAIL] 探针没有被限制到 E 核");
            if (!pinned) rc = 1;

            co.Restore(out string restoreSummary);
            var restored = ProcessEngine.ReadProcessCpuSets(probe.Id);
            W("");
            W($"Restore:\n{restoreSummary}");
            W($"恢复后探针 CPU Set: {(restored.Length == 0 ? "全部（已解除）" : "[" + string.Join(", ", restored) + "]")}");
            bool free = restored.Length == 0 || restored.Length >= co.Topology.AllIds.Length;
            W(free ? "[PASS] 已解除绑定" : "[FAIL] 绑定没有完全解除");
            if (!free) rc = 1;
        }
        catch (Exception ex)
        {
            W("实测异常: " + ex);
            rc = 2;
        }
        finally
        {
            try { probe?.Kill(); probe?.Dispose(); } catch { }
        }

        W("==========================================================");
        string path = Path.Combine(AppContext.BaseDirectory, "applytest.log");
        try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch { }
        try { File.WriteAllText(Path.Combine(StateStore.DataDir, "applytest.log"), sb.ToString(), Encoding.UTF8); } catch { }
        return rc;
    }

    private static int RunCore(string[] args)    {
        var sb = new StringBuilder();
        int failures = 0;

        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); }
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            W($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
        }

        W("==================== EcoCore 自检 ====================");
        W($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"管理员: {Coordinator.IsElevated()}");
        W($"OS: {Environment.OSVersion}");
        W($"运行时: {Environment.Version}");
        W("");

        // 1. 拓扑
        W("── 1. CPU 拓扑 ──");
        Stage("discover topology");
        var topo = CpuTopology.Discover();
        W("CPU: " + topo.CpuName);
        W(topo.Describe());
        foreach (var c in topo.All)
            W($"    {c}");
        Check("检测到至少 1 个 CPU Set", topo.All.Count > 0, $"{topo.All.Count} 个");
        Check("识别为大小核混合架构", topo.IsHybrid,
            topo.IsHybrid ? $"P={topo.PerformanceCores.Count} E={topo.EfficiencyCores.Count}" : "非混合（不支持绑核）");

        var reserved = topo.ReservedPerformanceCores(1);
        Check("能取出保留大核", reserved.Count == 1,
            reserved.Count > 0 ? string.Join(",", reserved.Select(r => r.Id)) : "无");
        W("");

        // 2. 电源方案
        W("── 2. 电源方案 ──");
        try
        {
            Stage("power schemes");
        var active = PowerTuner.GetActiveScheme();
            W($"当前激活方案: {active}");
            Check("读取激活电源方案", active != Guid.Empty);

            var snap = PowerTuner.Snapshot(active);
            foreach (var kv in snap)
                W($"    {kv.Key,-18} AC={kv.Value.Ac,-6} DC={kv.Value.Dc}");

            foreach (var def in PowerTuner.All)
            {
                bool canRead = PowerTuner.TryReadAc(active, def, out uint v);
                W($"    {(canRead ? "可读" : "不可读")}  {def.Alias,-18} {(canRead ? v.ToString() : "-")}  ({def.DisplayName})");
            }

            Check("SCHEDPOLICY 可读", PowerTuner.TryReadAc(active, PowerTuner.SchedPolicy, out _));
            Check("PERFBOOSTMODE 可读", PowerTuner.TryReadAc(active, PowerTuner.PerfBoostMode, out _));
            Check("PERFEPP 可读", PowerTuner.TryReadAc(active, PowerTuner.PerfEpp, out _));
        }
        catch (Exception ex)
        {
            Check("电源方案读取", false, ex.Message);
        }
        W("");

        // 3. 电池
        W("── 3. 电池 ──");
        var bat = BatteryState.Read();
        W($"交流供电={bat.OnAc} 电量={bat.Percent}% 剩余={bat.RemainingText} 充电={bat.Charging}");
        W("");

        // 4. 写权限实测：复制方案 → 写值 → 读回 → 删掉
        W("── 4. 电源写入实测（复制方案 → 写 → 读回 → 删除）──");
        try
        {
            Stage("power write test");
            var src = PowerTuner.GetActiveScheme();
            var tmp = PowerTuner.DuplicateActive(src);
            W($"复制得到的临时方案: {tmp}");
            Check("复制电源方案", tmp != Guid.Empty && tmp != src);

            uint rc1 = PowerTuner.WriteAc(tmp, PowerTuner.PerfEpp, 77);
            uint rc2 = PowerTuner.WriteDc(tmp, PowerTuner.PerfEpp, 77);
            W($"写入 PERFEPP=77 → AC rc={rc1}, DC rc={rc2}");
            Check("写入 PERFEPP", rc1 == 0 && rc2 == 0);

            bool readBack = PowerTuner.TryReadAc(tmp, PowerTuner.PerfEpp, out uint back);
            W($"读回 PERFEPP = {(readBack ? back.ToString() : "失败")}");
            Check("写入后可读回且值正确", readBack && back == 77);

            uint rc3 = PowerTuner.WriteAc(tmp, PowerTuner.SchedPolicy, 4);
            bool rb2 = PowerTuner.TryReadAc(tmp, PowerTuner.SchedPolicy, out uint sp);
            W($"写入 SCHEDPOLICY=4 → rc={rc3}, 读回={sp}");
            Check("写入 SCHEDPOLICY", rc3 == 0 && rb2 && sp == 4);

            uint rc4 = PowerTuner.WriteAc(tmp, PowerTuner.PerfBoostMode, 0);
            bool rb3 = PowerTuner.TryReadAc(tmp, PowerTuner.PerfBoostMode, out uint bm);
            W($"写入 PERFBOOSTMODE=0 → rc={rc4}, 读回={bm}");
            Check("写入 PERFBOOSTMODE", rc4 == 0 && rb3 && bm == 0);

            bool deleted = PowerTuner.DeleteScheme(tmp);
            Check("删除临时方案", deleted);
        }
        catch (Exception ex)
        {
            Check("电源写入实测", false, ex.Message);
        }
        W("");

        // 5. 进程 CPU Set 实测：拿一个自己起的记事本试
        W("── 5. 进程 CPU Set 绑定实测 ──");
        System.Diagnostics.Process? probe = null;
        try
        {
            Stage("cpu set probe");
            probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                UseShellExecute = true
            });
            if (probe is null) throw new InvalidOperationException("启动 notepad 失败");
            Thread.Sleep(1200);
            probe.Refresh();
            W($"探针进程 notepad.exe pid={probe.Id}");

            var ecores = topo.EfficiencyCoreIds;
            W($"E 核 CPU Set = [{string.Join(", ", ecores)}]");

            bool ok = ProcessEngine.PinToSpecificCpuSets(probe.Id, ecores, out string d);
            W($"SetProcessDefaultCpuSets → {ok} ({d})");
            Check("把进程 CPU Set 限制到 E 核", ok, d);

            var back = ProcessEngine.ReadProcessCpuSets(probe.Id);
            W($"GetProcessDefaultCpuSets 读回 = [{string.Join(", ", back)}]");
            bool same = back.Length == ecores.Length && !ecores.Except(back).Any();
            Check("读回确认只允许 E 核", same, same ? $"{back.Length} 个 E 核" : $"读回 {back.Length} 个，与目标不符");

            bool okRollback = ProcessEngine.PinToSpecificCpuSets(probe.Id, topo.AllIds, out string d2);
            W($"恢复全部 CPU Set → {okRollback} ({d2})");
            Check("恢复进程全部 CPU Set", okRollback, d2);

            var back2 = ProcessEngine.ReadProcessCpuSets(probe.Id);
            W($"恢复后读回 = {back2.Length} 个核");
            Check("恢复后读回全部核", back2.Length >= topo.AllIds.Length, $"{back2.Length}/{topo.AllIds.Length}");

            bool eco = ProcessEngine.SetEcoQos(probe.Id, true, out string de);
            W($"SetProcessInformation(EcoQoS=on) → {eco} ({de})");
            Check("设置进程 EcoQoS", eco, de);

            bool ecoOff = ProcessEngine.SetEcoQos(probe.Id, false, out string de2);
            Check("清除进程 EcoQoS", ecoOff, de2);
        }
        catch (Exception ex)
        {
            Check("进程 CPU Set 实测", false, ex.Message);
        }
        finally
        {
            try { probe?.Kill(); probe?.Dispose(); } catch { }
        }
        W("");

        // 6. 保留大核实测：把 dwm 钉到 1 个 P 核再放开
        W("── 6. 系统保留核实测（DWM）──");
        try
        {
            Stage("dwm test");
            var dwm = System.Diagnostics.Process.GetProcessesByName("dwm").FirstOrDefault();
            if (dwm is null) Check("找到 dwm.exe", false, "未找到");
            else
            {
                var r1 = topo.ReservedPerformanceCores(1).Select(c => c.Id).ToArray();
                bool ok = ProcessEngine.PinToSpecificCpuSets(dwm.Id, r1, out string d);
                W($"把 dwm(pid={dwm.Id}) 钉到 CPU Set [{string.Join(",", r1)}] → {ok} ({d})");
                Check("把 DWM 钉到保留大核", ok, d);

                bool ok2 = ProcessEngine.PinToSpecificCpuSets(dwm.Id, topo.AllIds, out string d2);
                Check("恢复 DWM 全部 CPU Set", ok2, d2);
                dwm.Dispose();
            }
        }
        catch (Exception ex)
        {
            Check("系统保留核实测", false, ex.Message);
        }
        W("");

        W("==================== 结果 ====================");
        W(failures == 0 ? "全部通过 ✓" : $"失败 {failures} 项 ✗");

        Stage("writing log");
        string path = Path.Combine(StateStore.DataDir, "selftest.log");
        try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch { }

        // 同时尝试写一份到 exe 目录，便于用户直接看
        try
        {
            string near = Path.Combine(AppContext.BaseDirectory, "selftest.log");
            File.WriteAllText(near, sb.ToString(), Encoding.UTF8);
        }
        catch { }

        try
        {
            bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
            if (!quiet)
                MessageBox.Show(sb.ToString(), "EcoCore 自检结果",
                    MessageBoxButtons.OK, failures == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch { }

        return failures == 0 ? 0 : 1;
    }
}
