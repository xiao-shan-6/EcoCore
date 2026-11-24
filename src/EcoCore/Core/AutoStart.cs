using System.Diagnostics;
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
            detail = "已启用（计划任务 · 登录时以管理员身份启动，不弹 UAC）";
            if (!string.IsNullOrEmpty(taskCmd) && !taskCmd.Contains(Identity, StringComparison.OrdinalIgnoreCase))
                detail += $"  ⚠ 任务指向的是另一个路径：{taskCmd}";
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
        // 1) 计划任务（需要管理员）
        if (TaskExists(out _)) DeleteTask(out _);

        var args = new[]
        {
            "/create", "/f",
            "/tn", TaskName,
            "/tr", CommandLine,
            "/sc", "onlogon",
            "/rl", "highest"
        };
        var r = Run("schtasks", args);
        if (r.Exit != 0)
        {
            // 有些系统不接受 onlogon + highest 的组合，再试一次不带 /rl
            var r2 = Run("schtasks", new[]
            {
                "/create", "/f",
                "/tn", TaskName,
                "/tr", CommandLine,
                "/sc", "onlogon"
            });
            if (r2.Exit != 0)
            {
                // 2) 退回注册表 Run 项
                bool ok = WriteRunValue(CommandLine);
                detail = ok
                    ? "已启用（注册表 Run 项）。注意：改计划任务需要管理员权限，当前是普通权限，" +
                      "所以只能走注册表 —— **登录后电源层不会生效**，请以管理员身份重新打开本程序再勾一次。"
                    : $"启用失败：{Trim(r2.Out + r2.Err)}";
                return ok;
            }
        }

        // 建完再确认一遍，别报成功其实没建上
        if (!TaskExists(out string cmd))
        {
            detail = $"计划任务创建后查不到：{Trim(r.Out + r.Err)}";
            return false;
        }

        // 计划任务优先，顺手把可能残留的 Run 项清掉，避免开机启动两次
        DeleteRunValue();
        detail = $"已启用（计划任务「{TaskName}」· 登录时以管理员身份启动，不弹 UAC）" +
                 (cmd.Contains(Identity, StringComparison.OrdinalIgnoreCase) ? "" : $"  ⚠ 实际命令：{cmd}");
        return true;
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

