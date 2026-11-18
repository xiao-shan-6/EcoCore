using System.Text.Json;
using System.Text.Json.Serialization;

namespace EcoCore.Core;

/// <summary>一个"续航方案"的全部参数。</summary>
public sealed class EcoProfile
{
    public string Name { get; set; } = "均衡";
    public string Description { get; set; } = "";

    // ── 电源策略层 ──
    /// <summary>异类线程调度策略：3=只用 E 核，4=首选 E 核，5=自动</summary>
    public uint SchedPolicy { get; set; } = 4;
    public uint ShortSchedPolicy { get; set; } = 4;
    /// <summary>睿频：0=禁用</summary>
    public uint PerfBoostMode { get; set; } = 0;
    /// <summary>最大处理器状态 %</summary>
    public uint ThrottleMax { get; set; } = 70;
    public uint ThrottleMin { get; set; } = 5;
    /// <summary>能效偏好：100 = 最省电</summary>
    public uint PerfEpp { get; set; } = 90;
    /// <summary>核心停放下限 %，100 = 不停放</summary>
    public uint CpMinCores { get; set; } = 100;
    /// <summary>核心停放上限 %，100 = 全不停放</summary>
    public uint CpMaxCores { get; set; } = 100;
    /// <summary>是否写 CPMAXCORES / CPMINCORES</summary>
    public bool UseCoreParking { get; set; } = false;
    /// <summary>是否把 Windows 电源滑块打到"最佳能效"</summary>
    public bool UsePowerSlider { get; set; } = true;

    // ── 系统保留 ──
    /// <summary>
    /// 强制留给系统的大核数量。≥1 时：
    ///   · 异类调度策略最高只能到 4（首选高效核），绝不会落到 3（只用 E 核）
    ///   · 保留的这几个 P 核专门承载 DWM / 音频 / 中断等系统线程
    /// 0 = 不留，允许 P 核全程休眠（更省电，但高负载交互会顿）
    /// </summary>
    public int ReservedPerformanceCores { get; set; } = 1;

    /// <summary>是否把系统关键进程（dwm / audiodg）钉到保留的 P 核上。</summary>
    public bool PinSystemToReservedCore { get; set; } = true;

    /// <summary>真正生效的异类调度策略：保留了大核就不能用"只用 E 核"。</summary>
    public uint EffectiveSchedPolicy =>
        ReservedPerformanceCores >= 1 ? Math.Min(SchedPolicy, 4u) : SchedPolicy;

    public uint EffectiveShortSchedPolicy =>
        ReservedPerformanceCores >= 1 ? Math.Min(ShortSchedPolicy, 4u) : ShortSchedPolicy;

    // ── 进程层 ──
    /// <summary>把后台进程硬绑定到 E 核（CPU Set）</summary>
    public bool PinBackgroundToECores { get; set; } = true;
    /// <summary>给后台进程打 EcoQoS 标记</summary>
    public bool EcoQosBackground { get; set; } = true;
    /// <summary>后台进程降优先级</summary>
    public bool LowerBackgroundPriority { get; set; } = false;
    /// <summary>只处理"非前台"进程（推荐开）</summary>
    public bool OnlyBackgroundProcesses { get; set; } = true;

    public EcoProfile Clone() => (EcoProfile)MemberwiseClone();

    // ── 预设 ──
    public static EcoProfile MaxSaving() => new()
    {
        Name = "极致省电",
        Description = "大核几乎完全停用，后台全压 E 核，睿频关闭，频率封顶 60%。续航最大化，重负载会明显变卡。",
        SchedPolicy = 3,
        ShortSchedPolicy = 3,
        PerfBoostMode = 0,
        ThrottleMax = 60,
        ThrottleMin = 5,
        PerfEpp = 100,
        UseCoreParking = true,
        CpMinCores = 20,
        CpMaxCores = 60,
        UsePowerSlider = true,
        PinBackgroundToECores = true,
        EcoQosBackground = true,
        LowerBackgroundPriority = true,
        OnlyBackgroundProcesses = true,
        ReservedPerformanceCores = 1,
        PinSystemToReservedCore = true
    };

    public static EcoProfile Balanced() => new()
    {
        Name = "均衡",
        Description = "后台任务压 E 核 + 效率模式，睿频关闭但频率不封顶。日常办公、网页、视频都够快，续航明显提升。",
        SchedPolicy = 4,
        ShortSchedPolicy = 4,
        PerfBoostMode = 0,
        ThrottleMax = 85,
        ThrottleMin = 5,
        PerfEpp = 80,
        UseCoreParking = false,
        CpMinCores = 100,
        CpMaxCores = 100,
        UsePowerSlider = true,
        PinBackgroundToECores = true,
        EcoQosBackground = true,
        LowerBackgroundPriority = false,
        OnlyBackgroundProcesses = true,
        ReservedPerformanceCores = 1,
        PinSystemToReservedCore = true
    };

    public static EcoProfile LightSaving() => new()
    {
        Name = "轻度省电",
        Description = "只给后台进程打效率模式，不动频率、不动大核。几乎无感，适合边用边省。",
        SchedPolicy = 5,
        ShortSchedPolicy = 5,
        PerfBoostMode = 1,
        ThrottleMax = 100,
        ThrottleMin = 5,
        PerfEpp = 50,
        UseCoreParking = false,
        CpMinCores = 100,
        CpMaxCores = 100,
        UsePowerSlider = false,
        PinBackgroundToECores = false,
        EcoQosBackground = true,
        LowerBackgroundPriority = false,
        OnlyBackgroundProcesses = true,
        ReservedPerformanceCores = 2,
        PinSystemToReservedCore = false
    };

    public static EcoProfile Performance() => new()
    {
        Name = "性能",
        Description = "全部恢复出厂调度：满睿频、全核可用、不隔离。等于关掉本软件的作用。",
        SchedPolicy = 5,
        ShortSchedPolicy = 5,
        PerfBoostMode = 1,
        ThrottleMax = 100,
        ThrottleMin = 5,
        PerfEpp = 0,
        UseCoreParking = false,
        CpMinCores = 100,
        CpMaxCores = 100,
        UsePowerSlider = false,
        PinBackgroundToECores = false,
        EcoQosBackground = false,
        LowerBackgroundPriority = false,
        OnlyBackgroundProcesses = true,
        ReservedPerformanceCores = 0,
        PinSystemToReservedCore = false
    };

    public const string CustomName = "自定义";
    public const string CustomDescription =
        "所有参数由你自己决定：勾选项、频率上限、EPP、保留大核都能调，改完自动记住。";

    /// <summary>自定义方案：初始值取「均衡」，之后完全由用户改。</summary>
    public static EcoProfile Custom()
    {
        var p = Balanced();
        p.Name = CustomName;
        p.Description = CustomDescription;
        return p;
    }

    public static EcoProfile[] Presets() =>
        new[] { MaxSaving(), Balanced(), LightSaving(), Performance(), Custom() };

    /// <summary>预设方案（数值锁定、不许改）还是自定义。</summary>
    public bool IsCustom => string.Equals(Name, CustomName, StringComparison.Ordinal);
}

/// <summary>持久化到 %ProgramData%\EcoCore\settings.json 的全局配置。</summary>
public sealed class AppSettings
{
    public string ActiveProfileName { get; set; } = "均衡";
    public EcoProfile CurrentProfile { get; set; } = EcoProfile.Balanced();
    /// <summary>「自定义」方案的用户取值（独立保存，切走再切回不丢）。</summary>
    public EcoProfile CustomProfile { get; set; } = EcoProfile.Custom();
    public bool AutoSwitchOnBattery { get; set; } = true;
    public bool AutoSwitchOnAc { get; set; } = true;
    public int AutoBatteryPercent { get; set; } = 100;
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    /// <summary>用户手动排除（永不处理）的进程名。</summary>
    public List<string> ExcludedProcesses { get; set; } = new();
    /// <summary>用户手动锁定到 E 核的进程名。</summary>
    public List<string> PinnedProcesses { get; set; } = new();
    /// <summary>我们复制出来的专用电源方案 GUID（空 = 尚未创建）。</summary>
    public string? EcoSchemeGuid { get; set; }
    /// <summary>创建专用方案前的原始激活方案 GUID。</summary>
    public string? OriginalSchemeGuid { get; set; }
    /// <summary>上次运行结束时是否处于"已应用"状态（用于崩溃后提示恢复）。</summary>
    public bool Applied { get; set; }
}

public static class StateStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static string? _dataDir;

    /// <summary>数据目录。优先 ProgramData；无写权限（未提权）时退回 LocalAppData。</summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir is not null) return _dataDir;

            string common = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EcoCore");
            if (TryEnsure(common)) return _dataDir = common;

            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EcoCore");
            if (TryEnsure(local)) return _dataDir = local;

            string tmp = Path.Combine(Path.GetTempPath(), "EcoCore");
            TryEnsure(tmp);
            return _dataDir = tmp;
        }
    }

    private static bool TryEnsure(string dir)
    {
        try { Directory.CreateDirectory(dir); return true; }
        catch { return false; }
    }

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Opts);
                if (s is not null) return s;
            }
        }
        catch { /* 配置文件损坏则重建 */ }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch { /* 忽略写失败 */ }
    }

    public static void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "ecocore.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
