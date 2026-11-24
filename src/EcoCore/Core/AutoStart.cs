using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace EcoCore.Core;

internal enum AutoStartMode
{
    /// <summary>没有设置自启动。</summary>
    None,
    /// <summary>计划任务（登录时以最高权限启动，不弹 UAC）—— 首选。</summary>
    ScheduledTask,
    /// <summary>注册表 Run 项（会以普通权限启动，电源层失效）—— 退路。</summary>
    RunKey
}

/// <summary>
/// 开机自启动。
///
/// 为什么优先用「计划任务 + 最高权限」而不是注册表 Run：
/// 本程序的清单写了 requireAdministrator，普通权限启动时电源层（频率上限 / 睿频 / EPP / 电源滑块）
/// 全部改不动，等于只剩一半功能。而 Run 项启动的进程只能用当前用户的普通令牌 ——
/// 除非每次开机都让人点一次 UAC，否则拿不到管理员权限。
/// 计划任务带 <c>/rl highest</c> 就能在登录时直接拿到提权令牌，**不弹任何框**，
/// 这是 Windows 上给提权程序做自启动的标准做法（大量厂商工具都这么干）。
///
/// 拿不到权限（用户以普通身份打开本程序）时，退回注册表 Run 项并如实告知。
/// </summary>
internal static class AutoStart
{
    /// <summary>计划任务名。故意用纯 ASCII，避免 schtasks 在不同代码页下出问题。</summary>
    public const string TaskName = "EcoCore AutoStart";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "EcoCore";

    /// <summary>当前程序的启动命令前缀（已经把「exe 直跑」和「dotnet 跑 dll」两种情况都覆盖）。</summary>
    /// <remarks>
    /// 开发时用 <c>dotnet EcoCore.dll</c> 跑，此时 <see cref="Environment.ProcessPath"/> 是 dotnet.exe，
    /// 直接拿它当命令会变成「开机启动一个空的 dotnet」—— 必须把 dll 路径也带上。
    /// </remarks>
    private static (string Command, string Identity) Invocation()
    {
        var host = Environment.ProcessPath;
        var asm = typeof(AutoStart).Assembly.Location;
        var hostName = string.IsNullOrEmpty(host) ? "" : Path.GetFileNameWithoutExtension(host);

        if (string.Equals(hostName, "dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(asm))
            return ($"\"{host}\" \"{asm}\"", asm);

        if (!string.IsNullOrEmpty(host) && host.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return ($"\"{host}\"", host);

        var fallback = Path.Combine(AppContext.BaseDirectory, "EcoCore.exe");
        return ($"\"{fallback}\"", fallback);
    }

    /// <summary>用来判断「计划任务指向的是不是本程序」的身份路径。</summary>
    public static string Identity => Invocation().Identity;

    /// <summary>自启动时用的命令行：最小化到托盘 + 重新应用上次的方案。</summary>
    /// <remarks>
    /// 为什么要 <c>--resume</c>：进程的 E 核绑定和 EcoQoS 都不跨重启保留，
    /// 开机只把窗口挂到托盘里而不重新应用，等于什么都没做。
    /// </remarks>
    public static string CommandLine => Invocation().Command + " --tray --resume";

    // ───────────────────────── 探测 ─────────────────────────

    public static AutoStartMode Detect(out string detail)
    {
        if (TaskExists(out string taskCmd))
        {
            var xml = TaskXml();
            bool bad = XmlBool(xml, "DisallowStartIfOnBatteries") || XmlBool(xml, "StopIfGoingOnBatteries");
            bool highest = XmlText(xml, "RunLevel").Contains("Highest", StringComparison.OrdinalIgnoreCase);

            detail = highest
                ? "已启用（计划任务 · 登录时以管理员身份启动，不弹 UAC · 电池供电也启动）"
                : "已启用（计划任务 · 普通权限启动，电源层改不动）";
            if (bad) detail += "　⚠ 这条任务带着「电池时不启动」，拔电开机不会启动 —— 关掉再打开开关即可重建";
            if (!string.IsNullOrEmpty(taskCmd) && !taskCmd.Contains(Identity, StringComparison.OrdinalIgnoreCase))
                detail += $"　⚠ 任务指向的是另一个路径：{taskCmd}";
            return AutoStartMode.ScheduledTask;
        }

        var run = ReadRunValue();
        if (!string.IsNullOrEmpty(run))
        {
            detail = "已启用（注册表 Run 项 · 普通权限启动，电源层改不动）";
            if (!run.Contains(Identity, StringComparison.OrdinalIgnoreCase))
                detail += $"  ⚠ 指向的是另一个路径：{run}";
            return AutoStartMode.RunKey;
        }

        detail = "未启用（勾上会写一条 Windows 计划任务：登录即启动、静默提权、缩到托盘，并重放上次生效的方案）";
        return AutoStartMode.None;
    }

    // ───────────────────────── 开关 ─────────────────────────

    /// <summary>打开自启动。优先建计划任务；没权限时退回注册表。</summary>
    public static bool Enable(out string detail)
    {
        if (TaskExists(out _)) DeleteTask(out _);

        if (RegisterTask(out string regMsg))
        {
            DeleteRunValue();   // 计划任务优先，清掉可能残留的 Run 项，避免开机启动两次
            detail = IsRunLevelHighest()
                ? $"已启用（计划任务「{TaskName}」· 登录时以管理员身份启动，不弹 UAC · 电池供电同样启动）"
                : $"已启用（计划任务「{TaskName}」· 普通权限，电池供电同样启动；" +
                  "但电源层改不动 —— 想静默提权请以管理员身份重新打开本程序再勾一次）";
            return true;
        }

        // 退路：注册表 Run 项
        if (WriteRunValue(CommandLine))
        {
            detail = "已启用（注册表 Run 项）。注意：计划任务建不上（多半是没有管理员权限），只能走注册表 —— " +
                     "登录后电源层不会生效，请以管理员身份重新打开本程序再勾一次。";
            return true;
        }

        detail = "启用失败：" + regMsg;
        return false;
    }

    /// <summary>
    /// 用 XML 注册计划任务。
    /// </summary>
    /// <remarks>
    /// **为什么不能用 <c>schtasks /create /sc onlogon</c> 的默认模板**：
    /// 它生成的任务里 <c>DisallowStartIfOnBatteries=true</c>、<c>StopIfGoingOnBatteries=true</c>，
    /// 也就是「只有接着电源才启动」「切到电池就停掉」。对一台笔记本来说，拔了电源开机
    /// 这条任务根本不会跑 —— 表现就是「设置好了开机自启，但开机没有任何反应」。
    /// 实测：任务存在、状态 Ready、`Last Result = 267011`（SCHED_S_TASK_HAS_NOT_RUN），
    /// 电池供电的这次登录被直接跳过。所以必须自己写 XML 把这两个开关关掉。
    /// </remarks>
    private static bool RegisterTask(out string msg)
    {
        var levels = IsElevated()
            ? new[] { "HighestAvailable", "LeastPrivilege" }
            : new[] { "LeastPrivilege" };

        var errs = new List<string>();
        foreach (var level in levels)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "EcoCore-autostart-" + Guid.NewGuid().ToString("N")[..8] + ".xml");
            try
            {
                // XML 声明是 UTF-16，schtasks 会按声明解析，写 UTF-8 会报「XML 格式不正确」
                File.WriteAllText(path, BuildTaskXml(level), Encoding.Unicode);
            }
            catch (Exception ex)
            {
                errs.Add($"[{level}] 写临时文件失败：{ex.Message}");
                continue;
            }

            var r = Run("schtasks", new[] { "/create", "/tn", TaskName, "/xml", path, "/f" });
            try { File.Delete(path); } catch { /* 删不掉就留着，临时目录无所谓 */ }

            if (r.Exit == 0 && TaskExists(out _)) { msg = $"已注册（RunLevel={level}）"; return true; }
            errs.Add($"[{level}] {Trim(r.Out + r.Err)}");
        }

        msg = string.Join("；", errs);
        return false;
    }

    /// <summary>生成计划任务 XML（登录触发、电池也启动、无执行时限、多实例忽略新的）。</summary>
    private static string BuildTaskXml(string runLevel)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var (command, _) = Invocation();
        string desc = "EcoCore 开机自启：登录后在托盘静默启动，并按上次使用的方案重新应用节能设置。";

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{desc}</Description>
    <Author>{Environment.UserDomainName}\{Environment.UserName}</Author>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{sid}</UserId>
      <Delay>PT10S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{sid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>{runLevel}</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command.Replace("\"", "")}</Command>
      <Arguments>--tray --resume</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    /// <summary>关掉自启动（计划任务和注册表两处都清）。</summary>
    public static bool Disable(out string detail)
    {
        bool hadTask = DeleteTask(out string taskMsg);
        bool hadRun = DeleteRunValue();

        if (!hadTask && !hadRun)
        {
            detail = "本来就是关闭状态";
            return true;
        }

        var parts = new List<string>();
        if (hadTask) parts.Add("计划任务已删除");
        if (hadRun) parts.Add("注册表项已清除");
        detail = "已关闭（" + string.Join("，", parts) + "）";
        if (hadTask && !string.IsNullOrEmpty(taskMsg) && taskMsg.Contains("拒绝"))
            detail += $"  ⚠ {taskMsg}";
        return true;
    }

    // ───────────────────────── 计划任务 ─────────────────────────

    private static bool TaskExists(out string command)
    {
        command = "";
        var r = Run("schtasks", new[] { "/query", "/tn", TaskName, "/fo", "list", "/v" });
        if (r.Exit != 0) return false;

        // 从输出里挑「要运行的任务」那一行（中英文系统措辞不同，按 Task To Run 兼容）
        foreach (var line in r.Out.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("要运行的任务", StringComparison.Ordinal) ||
                t.StartsWith("Task To Run", StringComparison.OrdinalIgnoreCase))
            {
                int idx = t.IndexOf(':');
                if (idx < 0) idx = t.IndexOf('：');
                if (idx >= 0 && idx + 1 < t.Length) command = t[(idx + 1)..].Trim();
                break;
            }
        }
        return true;
    }

    private static bool DeleteTask(out string message)
    {
        message = "";
        if (!TaskExists(out _)) return false;
        var r = Run("schtasks", new[] { "/delete", "/tn", TaskName, "/f" });
        message = Trim(r.Out + r.Err);
        return r.Exit == 0;
    }

    /// <summary>已有计划任务的 XML（没有任务返回空串）。</summary>
    private static string TaskXml()
    {
        var r = Run("schtasks", new[] { "/query", "/tn", TaskName, "/xml" });
        return r.Exit == 0 ? r.Out : "";
    }

    private static bool XmlBool(string xml, string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(xml, $"<{tag}>([^<]+)</{tag}>");
        return m.Success && m.Groups[1].Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string XmlText(string xml, string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(xml, $"<{tag}>([^<]+)</{tag}>");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>任务是不是以最高权限运行（决定电源层能不能生效）。</summary>
    private static bool IsRunLevelHighest() =>
        XmlText(TaskXml(), "RunLevel").Contains("Highest", StringComparison.OrdinalIgnoreCase);

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// 启动时自检：老版本建的任务带着「电池时不启动」，在管理员环境下顺手按新模板重建。
    /// </summary>
    /// <returns>真的重建了返回 true，message 说明结果。</returns>
    public static bool RepairIfNeeded(out string message)
    {
        message = "";
        if (!TaskExists(out _)) return false;

        var xml = TaskXml();
        bool bad = XmlBool(xml, "DisallowStartIfOnBatteries") || XmlBool(xml, "StopIfGoingOnBatteries");
        bool highest = XmlText(xml, "RunLevel").Contains("Highest", StringComparison.OrdinalIgnoreCase);
        if (!bad) return false;

        if (!IsElevated())
        {
            message = "开机自启的任务是旧版建的（带「电池时不启动」），以管理员身份打开本程序会自动修好。";
            return false;
        }

        bool ok = RegisterTask(out string reg);
        message = ok
            ? $"开机自启：已把旧任务重建为「电池供电也启动」{(highest ? "（管理员权限）" : "")}。"
            : $"开机自启：旧任务重建失败 —— {reg}";
        return ok;
    }

    /// <summary>立即跑一次计划任务，用来验证「开机自启」这条路真的通。</summary>
    public static bool RunNow(out string message)
    {
        if (!TaskExists(out _)) { message = "没有计划任务，先打开开关"; return false; }
        var r = Run("schtasks", new[] { "/run", "/tn", TaskName });
        message = r.Exit == 0
            ? "已触发计划任务（几秒后托盘里应出现 EcoCore 图标）"
            : $"触发失败：{Trim(r.Out + r.Err)}";
        return r.Exit == 0;
    }

    /// <summary>
    /// 体检：把计划任务里真正决定「会不会启动」的几个开关打出来。
    /// </summary>
    /// <remarks>
    /// `<c>DisallowStartIfOnBatteries</c>` 是这里最容易被忽略的一条 ——
    /// `schtasks /create` 的默认模板把它设成 true，笔记本一拔电，登录时任务直接被跳过，
    /// 表现就是「设置了开机自启，但开机什么都没发生」。老版本建出来的任务会一直带着这个坑。
    /// </remarks>
    public static string Health()
    {
        var xml = TaskXml();
        if (string.IsNullOrEmpty(xml))
            return "（没有计划任务" + (string.IsNullOrEmpty(ReadRunValue()) ? "" : "，当前靠注册表 Run 项") + "）";

        var lines = new List<string>
        {
            $"  运行级别      : {XmlText(xml, "RunLevel")}" +
                (IsRunLevelHighest() ? "（登录即提权，电源层生效）" : "（普通权限，电源层不生效）"),
            $"  登录后延迟    : {XmlText(xml, "Delay")}",
            $"  电池时禁止启动: {(XmlBool(xml, "DisallowStartIfOnBatteries") ? "是 ← 笔记本上会导致拔电后不启动" : "否（正确）")}",
            $"  切电池时停止  : {(XmlBool(xml, "StopIfGoingOnBatteries") ? "是 ← 拔电后会被杀掉" : "否（正确）")}",
            $"  执行时限      : {XmlText(xml, "ExecutionTimeLimit")}",
            $"  命令          : {XmlText(xml, "Command")} {XmlText(xml, "Arguments")}"
        };

        if (XmlBool(xml, "DisallowStartIfOnBatteries") || XmlBool(xml, "StopIfGoingOnBatteries"))
            lines.Add("  ⚠ 这条任务是老版本建的（带了「电池时不启动」）。请把「开机自动启动」关掉再打开，会按新模板重建。");

        return string.Join(Environment.NewLine, lines);
    }

    // ───────────────────────── 注册表 ─────────────────────────

    private static string? ReadRunValue()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return k?.GetValue(RunValueName) as string;
        }
        catch { return null; }
    }

    private static bool WriteRunValue(string cmd)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (k is null) return false;
            k.SetValue(RunValueName, cmd);
            return true;
        }
        catch { return false; }
    }

    private static bool DeleteRunValue()
    {
        if (string.IsNullOrEmpty(ReadRunValue())) return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            k?.DeleteValue(RunValueName, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }

    // ───────────────────────── 进程调用 ─────────────────────────

    private static (int Exit, string Out, string Err) Run(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "进程没起来");

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, stdout, stderr + "（超时 15 秒）");
            }
            return (p.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private static string Trim(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 240 ? s[..240] + "…" : s;
    }
}

