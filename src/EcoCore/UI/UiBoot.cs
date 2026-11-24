namespace EcoCore.UI;

/// <summary>
/// WinForms 启动引导。
/// </summary>
/// <remarks>
/// 直接调用 <c>ApplicationConfiguration.Initialize()</c> 有一个隐蔽的致命点：
/// <c>Application.EnableVisualStyles()</c> 会先把一份激活上下文清单写到 <c>%TEMP%</c>，
/// 写不进去就抛 <see cref="UnauthorizedAccessException"/> —— 进程在 Main 里当场死掉，
/// 用户看到的只是系统那句「应用程序启动异常」，什么都没解释。
/// 实测崩溃栈：
/// <code>
/// System.UnauthorizedAccessException: Access to the path 'C:\Users\...\Temp\blwymlzg.1mp' is denied.
///    at System.Windows.Forms.ThemingScope.CreateActivationContext(Stream manifest)
///    at System.Windows.Forms.Application.EnableVisualStyles()
///    at EcoCore.Program.Main(String[] args)
/// </code>
/// 本程序的界面全部是 GDI+ 自绘，视觉样式对它没有任何影响，所以：
/// ① 先把 %TEMP% 换成一个确定可写的目录；② 万一还是失败，就降级到无样式启动，绝不让进程退出。
/// </remarks>
internal static class UiBoot
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "boot.log");

    public static void Initialize()
    {
        string original = Path.GetTempPath();
        string tempNote = EnsureWritableTemp();
        Log($"原 TEMP={original}  {tempNote}  实际 TEMP={Path.GetTempPath()}");

        try
        {
            ApplicationConfiguration.Initialize();
        }
        catch (Exception ex)
        {
            Log("ApplicationConfiguration.Initialize 抛异常，改用降级启动：" + ex.GetType().Name + ": " + ex.Message);
            TrySetHighDpi();
            try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
            try { Application.EnableVisualStyles(); }
            catch (Exception ex2) { Log("EnableVisualStyles 仍然失败（已忽略，界面收自绘）：" + ex2.Message); }
        }
    }

    /// <summary>%TEMP% 不可写时换目录，返回一句说明写进 boot.log。</summary>
    private static string EnsureWritableTemp()
    {
        string current = Path.GetTempPath();
        if (CanWrite(current)) return "可写";

        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EcoCore", "tmp"),
            Path.Combine(AppContext.BaseDirectory, "tmp")
        };
        try { candidates.Add(Path.Combine(Core.StateStore.DataDir, "tmp")); } catch { }

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                if (!CanWrite(dir)) continue;

                Environment.SetEnvironmentVariable("TEMP", dir);
                Environment.SetEnvironmentVariable("TMP", dir);
                if (Path.GetTempPath().TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return $"不可写 → 已切到 {dir}";
            }
            catch { }
        }

        return "不可写，且没找到替代目录（界面可能无视觉样式，功能不受影响）";
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            string probe = Path.Combine(dir, "eco-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                fs.WriteByte(0);
            return true;
        }
        catch { return false; }
    }

    private static void TrySetHighDpi()
    {
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}"); } catch { }
    }
}
