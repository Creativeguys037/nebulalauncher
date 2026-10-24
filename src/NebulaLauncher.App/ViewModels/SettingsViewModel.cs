using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Diagnostics;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Net;
using NebulaLauncher.Core.Paths;
using NebulaLauncher.Services;
using Conv = NebulaLauncher.Converters.Converters;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 设置页。所有输入先停留在页面草稿里，点「保存设置」才写入 <see cref="ISettingsService.Current"/> 并落盘；
/// 动效与背景特效是例外——它们即时生效，同时立刻回写，避免用户关掉动效后刷新又弹回来。
///
/// 依赖全部是可选的：Core 的 DI 还没接上时页面仍能渲染并给出提示，而不是让导航直接抛异常。
/// 这是分模块并行开发阶段的降级路径，集成后每个依赖都会被容器注入。
/// </summary>
public sealed partial class SettingsViewModel : PageViewModelBase
{
    private const int MaxMemoryMinimum = 1024;
    private const int MaxMemoryMaximum = 16384;
    private const int MinMemoryMinimum = 256;
    private const int MinMemoryMaximum = 8192;
    private const int DownloadThreadsMinimum = 8;
    private const int DownloadThreadsMaximum = 256;

    private readonly ISettingsService? _settings;
    private readonly IJavaService? _java;
    private readonly IPlatformService? _platform;
    private readonly IAppLog? _log;
    private readonly ILauncherPaths? _paths;
    private readonly IProfileBundleService? _bundle;
    private readonly IStorageService? _storage;

    private CancellationTokenSource? _javaCts;

    /// <summary>连通性测试的取消源。离开设置页时取消，避免截图/导航后继续占着网络。</summary>
    private CancellationTokenSource? _probeCts;

    /// <summary>存储扫描的取消源。扫描要递归遍历 cache / logs / trash，离开设置页时应当停下。</summary>
    private CancellationTokenSource? _storageCts;

    private bool _suppressDirty;
    private bool _loadAttempted;

    /// <summary>存储分区是否已经扫描过一次。第一次进入该分区才自动扫描，避免每次切分区都重扫。</summary>
    private bool _storageScanned;

    /// <summary>待清理的类别键（确认浮层点「确认清理」时才真正执行）。</summary>
    private IReadOnlyList<string> _pendingStorageKeys = [];

    public SettingsViewModel(
        ISettingsService? settings = null,
        IJavaService? java = null,
        IPlatformService? platform = null,
        IAppLog? log = null,
        ILauncherPaths? paths = null,
        IProfileBundleService? bundle = null,
        IStorageService? storage = null)
    {
        _settings = settings;
        _java = java;
        _platform = platform;
        _log = log;
        _paths = paths;
        _bundle = bundle;
        _storage = storage;

        Sections.Add(new SettingsSection("general", "常规", SettingsIcons.General));
        Sections.Add(new SettingsSection("java", "Java", SettingsIcons.Java));
        Sections.Add(new SettingsSection("game", "游戏", SettingsIcons.Game));
        Sections.Add(new SettingsSection("download", "下载", SettingsIcons.Download));
        Sections.Add(new SettingsSection("content", "在线资源", SettingsIcons.Content));
        Sections.Add(new SettingsSection("appearance", "外观", SettingsIcons.Appearance));
        Sections.Add(new SettingsSection("storage", "存储", SettingsIcons.Storage));
        Sections.Add(new SettingsSection("about", "关于", SettingsIcons.About));
        Sections[0].IsActive = true;

        DetectedJavas.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasJavaResults));
            OnPropertyChanged(nameof(IsJavaEmpty));
        };

        ExcludedJavas.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasExcludedJavas));
            OnPropertyChanged(nameof(ExcludedJavaCount));
            OnPropertyChanged(nameof(ExcludedSummary));
        };

        JavaStatus = _java is null ? "Java 服务尚未接入" : "尚未检测本机 Java";
        SelectedJavaDownload = JavaDownloadOptions[0];

        // 设置页也走同一个状态机；主版本来自下拉而不是 planner，文案口径是「状态行短句 + 红行长句」。
        JavaInstall = new JavaInstallState(java, JavaInstallWording.Settings);

        // 下拉至少要有默认项：设置页可能在导航之前就被构造，空 ItemsSource 会让首帧显示空白下拉。
        // 这里直接写后备字段，避免触发 OnSelectedJvmPresetChanged 把页面标记成「有未保存的更改」。
        JvmPresetChoices.Add(_defaultJvmPresetRow);
        _selectedJvmPreset = _defaultJvmPresetRow;

        // 初始值本身不会触发 OnChanged，先把卡片的选中态摆正，否则首帧三张卡都是未选中。
        SyncIsolationOptions();
    }

    public override string Title => "设置";

    public override string? Subtitle => "游戏目录与版本隔离、内存、Java、下载源、在线资源、外观、存储";

    public override string Key => NavigationKeys.Settings;

    /// <summary>分区导航项。</summary>
    public ObservableCollection<SettingsSection> Sections { get; } = new();

    // ---------------- 服务可用性 ----------------

    /// <summary>核心服务未注入时页面只读，顶部显示提示条。</summary>
    public bool HasServiceWarning => _settings is null;

    public string ServiceWarning => "核心服务尚未接入：当前只能查看界面，设置不会读取或保存。";

    // ---------------- 通用状态 ----------------

    [ObservableProperty]
    private string _selectedSectionKey = "general";

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _hasValidationError;

    [ObservableProperty]
    private string? _validationMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _saveStatus = "尚未修改";

    [ObservableProperty]
    private bool _isSaving;

    public bool IsGeneralSection => SelectedSectionKey == "general";

    public bool IsJavaSection => SelectedSectionKey == "java";

    public bool IsGameSection => SelectedSectionKey == "game";

    public bool IsDownloadSection => SelectedSectionKey == "download";

    public bool IsContentSection => SelectedSectionKey == "content";

    public bool IsAppearanceSection => SelectedSectionKey == "appearance";

    public bool IsStorageSection => SelectedSectionKey == "storage";

    public bool IsAboutSection => SelectedSectionKey == "about";

    partial void OnSelectedSectionKeyChanged(string value)
    {
        foreach (var section in Sections)
        {
            section.IsActive = string.Equals(section.Key, value, StringComparison.Ordinal);
        }

        OnPropertyChanged(nameof(IsGeneralSection));
        OnPropertyChanged(nameof(IsJavaSection));
        OnPropertyChanged(nameof(IsGameSection));
        OnPropertyChanged(nameof(IsDownloadSection));
        OnPropertyChanged(nameof(IsContentSection));
        OnPropertyChanged(nameof(IsAppearanceSection));
        OnPropertyChanged(nameof(IsStorageSection));
        OnPropertyChanged(nameof(IsAboutSection));

        // 存储分区首次进入时自动扫一次：占用数字是这个分区的全部内容，空表等于页面没打开。
        // 之后的刷新由「刷新」按钮和清理后的重扫负责，避免来回切分区就重扫几百个目录。
        if (IsStorageSection && !_storageScanned && _storage is not null)
        {
            _storageScanned = true;
            _ = ScanStorageCoreAsync();
        }
    }

    [RelayCommand]
    private void SelectSection(string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            SelectedSectionKey = key;
        }
    }

    // ---------------- 常规 ----------------

    /// <summary>当前只有简体中文；列表结构保留，后续加语言包时不用改 XAML。</summary>
    public IReadOnlyList<string> Languages { get; } = ["简体中文"];

    [ObservableProperty]
    private string _selectedLanguage = "简体中文";

    [ObservableProperty]
    private bool _closeLauncherAfterLaunch;


    [ObservableProperty]
    private bool _closeToTray;

    partial void OnSelectedLanguageChanged(string value) => MarkDirty();

    partial void OnCloseLauncherAfterLaunchChanged(bool value) => MarkDirty();


    partial void OnCloseToTrayChanged(bool value) => MarkDirty();

    // ---------------- Java ----------------

    [ObservableProperty]
    private string _javaPath = string.Empty;

    /// <summary>
    /// 是否持久化 Java 探测结果（PCL2 <c>Configs.JavaList</c>）。
    /// 打开后第二次启动启动器不再对每个候选跑一次 <c>java -version</c>：
    /// 本机常见 10~20 个候选，每次冷启动要几秒。
    /// </summary>
    [ObservableProperty]
    private bool _enableJavaCache = true;

    public string JavaCacheHint => EnableJavaCache
        ? "已开启：探测结果写入 java-cache.json，文件未变化的 Java 不再重复探测。"
        : "已关闭：每次启动都重新探测全部候选（冷启动会慢几秒）。";

    /// <summary><see cref="IJavaService.DetectAsync"/> 的结果，按主版本降序。</summary>
    public ObservableCollection<JavaOption> DetectedJavas { get; } = new();

    /// <summary>
    /// 被用户移出候选的 Java（PCL2 的 <c>JavaRemovedList</c>）。折叠展示：
    /// 正常用户不需要看见它，只有「我点错了、恢复回来」时才展开。
    /// </summary>
    public ObservableCollection<ExcludedJavaOption> ExcludedJavas { get; } = new();

    /// <summary>主列表的选中项。「移除选中」靠它，避免每行都挂一个破坏性按钮。</summary>
    [ObservableProperty]
    private JavaOption? _selectedJava;

    /// <summary>是否展开「已忽略的 Java」。默认折叠。</summary>
    [ObservableProperty]
    private bool _showExcludedJavas;

    public bool HasSelectedJava => SelectedJava is not null;

    public bool HasExcludedJavas => ExcludedJavas.Count > 0;

    public int ExcludedJavaCount => ExcludedJavas.Count;

    public string ExcludedSummary => ExcludedJavas.Count == 0
        ? "没有被忽略的 Java。移出列表的 Java 会记在 java-excluded.json，恢复后重新参与选择。"
        : $"已忽略 {ExcludedJavas.Count} 个 Java：它们不会出现在探测结果里，也不会被自动选择。";

    partial void OnSelectedJavaChanged(JavaOption? value) => OnPropertyChanged(nameof(HasSelectedJava));

    [ObservableProperty]
    private bool _isJavaLoading;

    [ObservableProperty]
    private string _javaStatus = string.Empty;

    [ObservableProperty]
    private string? _javaError;

    public bool HasJavaResults => DetectedJavas.Count > 0;

    public bool IsJavaEmpty => !IsJavaLoading && DetectedJavas.Count == 0 && _java is not null;

    partial void OnJavaPathChanged(string value) => MarkDirty();

    partial void OnEnableJavaCacheChanged(bool value)
    {
        OnPropertyChanged(nameof(JavaCacheHint));
        MarkDirty();
    }

    partial void OnIsJavaLoadingChanged(bool value) => OnPropertyChanged(nameof(IsJavaEmpty));

    [RelayCommand]
    private Task DetectJavaAsync() => DetectJavaCoreAsync(forceRefresh: true);

    /// <summary>
    /// 重新探测并刷新两个列表。
    ///
    /// <paramref name="forceRefresh"/> 为 <c>false</c> 时采信 <c>java-cache.json</c>：首次进入设置页走这条，
    /// 这是 <see cref="EnableJavaCache"/> 承诺的语义（否则每次进页面都要为每个候选跑一次子进程）。
    /// 「重新扫描」按钮才强制跳过缓存。
    /// </summary>
    private async Task DetectJavaCoreAsync(bool forceRefresh, bool updateStatus = true)
    {
        if (_java is null)
        {
            JavaStatus = "Java 服务尚未接入";
            return;
        }

        if (IsJavaLoading)
        {
            return;
        }

        _javaCts?.Cancel();
        _javaCts = new CancellationTokenSource();
        var token = _javaCts.Token;

        IsJavaLoading = true;
        JavaError = null;
        if (updateStatus)
        {
            JavaStatus = "正在扫描本机 Java…";
        }

        try
        {
            var list = await _java.DetectAsync(forceRefresh, token);
            DetectedJavas.Clear();
            foreach (var info in list)
            {
                DetectedJavas.Add(new JavaOption(info));
            }

            await LoadExcludedJavasAsync(token);

            if (updateStatus)
            {
                JavaStatus = list.Count == 0
                    ? "未检测到 Java，请手动指定路径或安装一个 JRE/JDK"
                    : $"已检测到 {list.Count} 个 Java 运行时";
            }
        }
        catch (OperationCanceledException)
        {
            JavaStatus = "检测已取消";
        }
        catch (Exception ex)
        {
            JavaError = $"Java 检测失败：{ex.Message}";
            JavaStatus = "检测失败";
        }
        finally
        {
            IsJavaLoading = false;
        }
    }

    /// <summary>读「已忽略」列表。取不到只记日志：它不该让整个 Java 分区变成错误态。</summary>
    private async Task LoadExcludedJavasAsync(CancellationToken token)
    {
        if (_java is null)
        {
            return;
        }

        var excluded = await _java.GetExcludedAsync(token);

        ExcludedJavas.Clear();
        foreach (var path in excluded)
        {
            ExcludedJavas.Add(new ExcludedJavaOption(path));
        }
    }

    /// <summary>
    /// 添加一个手动指定的 Java。文件选择在视图层（code-behind）完成，这里只吃路径：
    /// <c>java.exe</c> 与 JDK 根目录都接受，探测失败给提示而不抛。
    /// </summary>
    public async Task AddJavaAsync(string? path)
    {
        if (_java is null)
        {
            JavaError = "Java 服务尚未接入，无法添加。";
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var info = await _java.AddManualAsync(path);

            if (info is null)
            {
                JavaError = $"添加失败：{path} 不是可用的 Java（需要 java.exe 或 JDK 根目录）。";
                JavaStatus = "添加失败";
                return;
            }

            JavaError = null;
            JavaPath = info.Path;

            await DetectJavaCoreAsync(forceRefresh: false, updateStatus: false);
            JavaStatus = $"已添加 Java {info.MajorVersion}：{info.Path}";
        }
        catch (Exception ex)
        {
            JavaError = $"添加 Java 失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private Task RemoveSelectedJavaAsync() => RemoveJavaAsync(SelectedJava);

    /// <summary>把一条 Java 移出候选并写进排除列表。</summary>
    [RelayCommand]
    private async Task RemoveJavaAsync(JavaOption? option)
    {
        if (option is null || _java is null)
        {
            return;
        }

        try
        {
            await _java.RemoveAsync(option.Path);

            JavaError = null;
            await DetectJavaCoreAsync(forceRefresh: false, updateStatus: false);
            JavaStatus = $"已移出列表并忽略：{option.Path}";
        }
        catch (Exception ex)
        {
            JavaError = $"移除 Java 失败：{ex.Message}";
        }
    }

    /// <summary>恢复一条被忽略的 Java（从排除列表撤销，随后重新探测让它回到候选里）。</summary>
    [RelayCommand]
    private async Task RestoreJavaAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || _java is null)
        {
            return;
        }

        try
        {
            var restored = await _java.RestoreAsync(path);

            JavaError = null;
            await DetectJavaCoreAsync(forceRefresh: false, updateStatus: false);
            JavaStatus = restored ? $"已恢复：{path}" : $"已不在忽略列表里：{path}";
        }
        catch (Exception ex)
        {
            JavaError = $"恢复 Java 失败：{ex.Message}";
        }
    }

    /// <summary>清空忽略列表并重新扫描（PCL2 的 <c>ManuallySearchJava</c>：全部重置后重搜）。</summary>
    [RelayCommand]
    private async Task ClearExcludedJavaAsync()
    {
        if (_java is null)
        {
            return;
        }

        try
        {
            await _java.ClearExcludedAsync();

            JavaError = null;
            ExcludedJavas.Clear();
            await DetectJavaCoreAsync(forceRefresh: false, updateStatus: false);
            JavaStatus = "已清空忽略列表并重新扫描";
        }
        catch (Exception ex)
        {
            JavaError = $"清空忽略列表失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void UseJava(JavaOption? option)
    {
        if (option is null)
        {
            return;
        }

        JavaPath = option.Path;
    }

    // ---------------- Java 运行时下载 ----------------

    /// <summary>
    /// 可下载的 Java 主版本。与 <c>JavaRequirementTable</c> 的版本线一一对应，
    /// 让「本机没有 Java 25，26.x 起不来」这类问题在界面内就能解决。
    /// </summary>
    public IReadOnlyList<JavaDownloadOption> JavaDownloadOptions { get; } =
    [
        new(25, "Java 25", "Minecraft 26.x 需要"),
        new(21, "Java 21", "1.20.5～25.x 需要"),
        new(17, "Java 17", "1.18～1.20.4 需要"),
        new(16, "Java 16", "1.17 需要"),
        new(8, "Java 8", "1.16 及更早需要"),
    ];

    [ObservableProperty]
    private JavaDownloadOption? _selectedJavaDownload;

    /// <summary>
    /// Java 下载状态机。与首页 / 版本页共用同一实现；设置页的差异只有两处，都在调用点体现：
    /// 主版本由用户在下拉里选（不由 planner 给），新路径只写回页面草稿（落盘交给「保存设置」）。
    /// </summary>
    public JavaInstallState JavaInstall { get; }

    [RelayCommand]
    private async Task DownloadJavaAsync()
    {
        if (_java is null)
        {
            JavaError = "Java 服务尚未接入，无法下载运行时。";
            return;
        }

        if (JavaInstall.IsInstalling)
        {
            return;
        }

        var option = SelectedJavaDownload ?? JavaDownloadOptions[0];

        JavaError = null;
        var progress = new Progress<DownloadProgress>(JavaInstall.ApplyProgress);

        // 只写回草稿：提前写 settings.json 会让「改了但不保存」变得不彻底。
        var result = await JavaInstall
            .InstallAsync(option.MajorVersion, JavaInstallSink.Draft(path => JavaPath = path), progress)
            .ConfigureAwait(true);

        switch (result.Outcome)
        {
            case JavaInstallOutcome.Succeeded:
                // 装完立刻重新扫描，让「已检测到的 Java」列表与路径输入框自洽。
                // 用 false：状态机已经强制重扫过缓存，这里只把列表读出来，不必再跑一遍子进程。
                await DetectJavaCoreAsync(forceRefresh: false);
                break;

            case JavaInstallOutcome.DownloadFailed:
                // 状态行只写短句（由状态机给），详细原因留在红行里——与原排版一致。
                JavaError = $"{option.Label} 下载失败，请检查网络或代理后重试。";
                break;

            case JavaInstallOutcome.Failed:
                JavaError = $"{option.Label} 安装失败：{result.Error}";
                break;
        }
    }

    // ---------------- 游戏 ----------------

    /// <summary>
    /// 游戏目录草稿。空字符串表示「跟随平台默认目录」，与 <see cref="AppSettings.MinecraftDirectory"/> 的 null 语义一致。
    /// </summary>
    [ObservableProperty]
    private string _gameDirectory = string.Empty;

    /// <summary>目录可写性检查结果；null 表示尚未检查或检查通过。</summary>
    [ObservableProperty]
    private string? _gameDirectoryError;

    [ObservableProperty]
    private string _gameDirectoryStatus = string.Empty;

    /// <summary>平台默认的 .minecraft 位置，用于「恢复默认」与占位提示。</summary>
    public string DefaultGameDirectory =>
        _platform?.DefaultMinecraftDirectory ?? LauncherPaths.ResolveDefaultMinecraft();

    /// <summary>当前实际生效的目录（草稿为空时是默认目录）。</summary>
    public string EffectiveGameDirectory =>
        string.IsNullOrWhiteSpace(GameDirectory) ? DefaultGameDirectory : GameDirectory.Trim();

    /// <summary>
    /// 进程真正在用的游戏目录。可能因为平台默认目录不可写而回退到启动器目录下，
    /// 和草稿里的值不一定一致，所以单独展示一行，避免用户以为设置没生效。
    /// </summary>
    public string ActiveGameDirectoryText =>
        $"当前生效：{_paths?.MinecraftDirectory ?? DefaultGameDirectory}";

    /// <summary>启动器数据目录（设置/账户/实例都写在这里）。发生回退时与平台默认不同。</summary>
    public string ActiveDataDirectoryText =>
        $"数据目录：{_paths?.RootDirectory ?? LauncherPaths.ResolveDefaultRoot()}";

    /// <summary>是否仍在使用平台默认目录。</summary>
    public bool IsDefaultGameDirectory =>
        string.IsNullOrWhiteSpace(GameDirectory) ||
        string.Equals(
            SafeFullPath(GameDirectory),
            SafeFullPath(DefaultGameDirectory),
            StringComparison.OrdinalIgnoreCase);

    public string GameDirectoryHint => IsDefaultGameDirectory
        ? $"默认位置：{DefaultGameDirectory}"
        : "自定义位置：换目录后新安装的版本会落在新目录，旧目录不会被自动搬走。";

    partial void OnGameDirectoryChanged(string value)
    {
        GameDirectoryError = null;
        GameDirectoryStatus = string.Empty;
        OnPropertyChanged(nameof(EffectiveGameDirectory));
        OnPropertyChanged(nameof(IsDefaultGameDirectory));
        OnPropertyChanged(nameof(GameDirectoryHint));
        OnPropertyChanged(nameof(ActiveGameDirectoryText));
        OnPropertyChanged(nameof(ActiveDataDirectoryText));
        MarkDirty();
    }

    /// <summary>选择目录后由视图层调用。只记路径，不做写入探测（保存时只要求目录可创建）。</summary>
    public void SetGameDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        GameDirectory = path.Trim();
    }

    [RelayCommand]
    private void UseDefaultGameDirectory()
    {
        GameDirectory = string.Empty;
        GameDirectoryError = null;
        GameDirectoryStatus = $"已恢复默认目录：{DefaultGameDirectory}";
    }

    /// <summary>
    /// 诊断写入（可选操作，不影响保存）。
    ///
    /// 这一步用「写一个随机临时文件」来试写，会被杀软/EDR/受控目录策略拦掉，
    /// 而同一目录里真正的游戏文件往往能写。所以它只能当参考，不能当保存门槛——
    /// 用它阻断保存等于把可用目录判死。
    /// </summary>
    [RelayCommand]
    private void ProbeGameDirectory()
    {
        var path = EffectiveGameDirectory;

        if (!TryNormalize(path, out var full, out var syntaxError))
        {
            GameDirectoryError = syntaxError;
            GameDirectoryStatus = "路径无效";
            return;
        }

        var reason = PathProbe.TestWritable(full);
        if (reason is not null)
        {
            GameDirectoryError = $"诊断：写入测试失败（不影响保存）：{reason}";
            GameDirectoryStatus = "诊断失败，安装时若写不进去会给出具体文件与原因";
            return;
        }

        var space = PathProbe.GetFreeBytes(full);
        GameDirectoryError = null;
        GameDirectoryStatus = space is null
            ? "诊断：目录可写（无法读取磁盘剩余空间）"
            : $"诊断：目录可写，剩余 {PathProbe.FormatSize(space.Value)}";
    }

    [RelayCommand]
    private async Task OpenGameDirectoryAsync()
    {
        if (_platform is null)
        {
            ActionMessage = "平台服务尚未接入，无法打开目录。";
            return;
        }

        var path = EffectiveGameDirectory;
        if (!TryNormalize(path, out var full, out var syntaxError))
        {
            ActionMessage = syntaxError;
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(full);
            ActionMessage = $"已打开游戏目录：{full}";
        }
        catch (Exception ex)
        {
            ActionMessage = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 保存前的目录检查：只要求路径合法、目录能创建。
    ///
    /// 不做「写临时文件」探测。探测失败不代表游戏文件写不进去，
    /// 而阻断保存会让用户在一个明明可用的目录上被卡住；
    /// 真正的写入失败发生在安装时，由 CmlLib 抛出并转成 InstallTargetException 的提示。
    /// </summary>
    private bool ValidateGameDirectory()
    {
        if (!TryNormalize(EffectiveGameDirectory, out var full, out var syntaxError))
        {
            GameDirectoryError = syntaxError;
            return false;
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex)
        {
            GameDirectoryError = $"无法创建游戏目录：{ex.Message}";
            return false;
        }

        GameDirectoryError = null;
        GameDirectoryStatus = $"将使用 {full}";
        return true;
    }

    /// <summary>
    /// 把底层的 "Access is denied" 翻译成用户能行动的一句话（仅用于诊断按钮）。
    /// </summary>
    private static string DescribeWriteFailure(string reason) =>
        $"无法写入：{reason}。若资源管理器能手动新建文件，说明是安全软件拦截了这个进程；"
        + "把 NebulaLauncher.exe 加入信任后重试。这不影响保存目录，只影响写入诊断。";

    private static bool TryNormalize(string? path, out string full, out string? error)
    {
        full = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "游戏目录不能为空；留空请用「恢复默认」。";
            return false;
        }

        try
        {
            full = Path.GetFullPath(path.Trim());
            return true;
        }
        catch (Exception ex)
        {
            error = $"路径格式非法：{ex.Message}";
            return false;
        }
    }

    private static string SafeFullPath(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path ?? string.Empty;
        }
    }

    // ---------------- 版本隔离策略 ----------------

    /// <summary>
    /// 版本首次启动时是否自动建独立目录。说明文字统一取自
    /// <see cref="IsolationPolicyResolver.Describe"/>，页面不另写一套文案。
    /// </summary>
    [ObservableProperty]
    private IsolationPolicyKind _isolationPolicy = IsolationPolicyKind.Modded;

    /// <summary>三选一卡片。顺序固定为「推荐 → 更激进 → 更保守」，避免列表随枚举顺序变化。</summary>
    public IReadOnlyList<IsolationOption> IsolationOptions { get; } =
    [
        new(IsolationPolicyKind.Modded, "仅加载器版本", isRecommended: true),
        new(IsolationPolicyKind.Always, "每个版本都隔离"),
        new(IsolationPolicyKind.Never, "不隔离"),
    ];

    partial void OnIsolationPolicyChanged(IsolationPolicyKind value)
    {
        SyncIsolationOptions();
        MarkDirty();
    }

    [RelayCommand]
    private void SelectIsolationPolicy(IsolationOption? option)
    {
        if (option is not null)
        {
            IsolationPolicy = option.Kind;
        }
    }

    private void SyncIsolationOptions()
    {
        foreach (var option in IsolationOptions)
        {
            option.IsActive = option.Kind == IsolationPolicy;
        }
    }

    // ---------------- 启动后行为（PCL2 LaunchArgumentVisible / LaunchArgumentPriority 等） ----------------

    /// <summary>
    /// 游戏进程起来之后启动器窗口怎么处理。旧字段 <see cref="AppSettings.CloseLauncherAfterLaunch"/>
    /// 只有「关 / 不关」两态，这里扩成三态，保存时仍写回新字段。
    /// </summary>
    [ObservableProperty]
    private string _launcherVisibility = "keep";

    public IReadOnlyList<ChoiceOption> VisibilityOptions { get; } =
    [
        new("keep", "保持窗口", "启动后启动器停在原地，便于查看控制台输出。"),
        new("minimize", "最小化窗口", "游戏窗口在前台，启动器收进任务栏。"),
        new("close", "关闭启动器", "游戏继续运行；下次打开启动器仍能看到本次运行记录。"),
    ];

    /// <summary>游戏进程优先级（PCL2 <c>LaunchArgumentPriority</c>）。</summary>
    [ObservableProperty]
    private string _gameProcessPriority = "normal";

    public IReadOnlyList<ChoiceOption> PriorityOptions { get; } =
    [
        new("normal", "正常", "系统默认调度，适合绝大多数情况。"),
        new("high", "高", "帧生成更稳；会让启动器与后台程序让出 CPU。"),
        new("low", "低", "给录屏 / 直播留出 CPU，代价是游戏可能掉帧。"),
    ];

    /// <summary>启动前把 <c>options.txt</c> 的语言改成简体中文（PCL2 <c>ToolHelpChinese</c>）。</summary>
    [ObservableProperty]
    private bool _autoChineseLanguage = true;

    public string AutoChineseLanguageHint => AutoChineseLanguage
        ? "启动前把游戏语言设为简体中文（仅当当前是英文或未设置时改写）。"
        : "不干预游戏语言，保持你在游戏内的选择。";

    /// <summary>
    /// 启动前把 Java 注册为「高性能 GPU」（PCL2 <c>LaunchAdvanceGraphicCard</c>）。
    /// 只影响 Windows；非 Windows 平台启动时由平台层静默跳过，这里给一句说明。
    /// </summary>
    [ObservableProperty]
    private bool _registerGpuPreference = true;

    public string RegisterGpuPreferenceHint
    {
        get
        {
            if (_platform is not null && !string.Equals(_platform.PlatformName, "Windows", StringComparison.Ordinal))
            {
                return $"{_platform.PlatformName} 没有这个选项，启动时会自动跳过。";
            }

            return RegisterGpuPreference
                ? "启动前把 javaw.exe 写入显卡首选项（只改当前用户的注册表），双显卡笔记本上游戏会走独显。"
                : "不写注册表；显卡由系统默认策略决定，双显卡机器上游戏可能跑在核显上。";
        }
    }

    partial void OnRegisterGpuPreferenceChanged(bool value)
    {
        OnPropertyChanged(nameof(RegisterGpuPreferenceHint));
        MarkDirty();
    }

    /// <summary>崩溃后把崩溃报告与 logs 打成 zip 归档（PCL2 <c>ModCrash</c> 的报告归档）。</summary>
    [ObservableProperty]
    private bool _archiveCrashReports = true;

    public string ArchiveCrashReportsHint => ArchiveCrashReports
        ? "崩溃后自动打包 crash-report 与 latest.log 到 crash-reports/archive，方便直接发出去。"
        : "崩溃后不打包，报告仍留在 crash-reports 目录。";

    partial void OnLauncherVisibilityChanged(string value)
    {
        SyncChoiceOptions(VisibilityOptions, value);
        MarkDirty();
    }

    partial void OnGameProcessPriorityChanged(string value)
    {
        SyncChoiceOptions(PriorityOptions, value);
        MarkDirty();
    }

    partial void OnAutoChineseLanguageChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoChineseLanguageHint));
        MarkDirty();
    }

    partial void OnArchiveCrashReportsChanged(bool value)
    {
        OnPropertyChanged(nameof(ArchiveCrashReportsHint));
        MarkDirty();
    }

    /// <summary>游戏退出后自动备份世界存档（HMCL 的「退出后备份」，PCL2 没有）。</summary>
    [ObservableProperty]
    private bool _autoBackupWorldsOnExit;

    public string AutoBackupWorldsHint => AutoBackupWorldsOnExit
        ? "每次游戏退出后为当前实例的全部世界各存一份 zip；崩溃退出同样会备份。"
        : "关闭时不自动备份，仍可在「世界」页手动备份。";

    /// <summary>每个世界保留的自动备份份数。0 与负数在 Core 侧被当作「不轮转」。</summary>
    [ObservableProperty]
    private int _worldBackupRetention = 20;

    partial void OnAutoBackupWorldsOnExitChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoBackupWorldsHint));
        MarkDirty();
    }

    partial void OnWorldBackupRetentionChanged(int value) => MarkDirty();

    [RelayCommand]
    private void SelectVisibility(ChoiceOption? option)
    {
        if (option is not null)
        {
            LauncherVisibility = option.Value;
        }
    }

    [RelayCommand]
    private void SelectPriority(ChoiceOption? option)
    {
        if (option is not null)
        {
            GameProcessPriority = option.Value;
        }
    }

    private static void SyncChoiceOptions(IEnumerable<ChoiceOption> options, string value)
    {
        foreach (var option in options)
        {
            option.IsActive = string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [ObservableProperty]
    private int _maxMemoryMb = 4096;

    [ObservableProperty]
    private int _minMemoryMb = 512;

    [ObservableProperty]
    private string _windowWidthText = "854";

    [ObservableProperty]
    private string _windowHeightText = "480";

    [ObservableProperty]
    private bool _fullscreen;

    [ObservableProperty]
    private string _extraJvmArguments = string.Empty;

    // ---------------- 游戏：JVM 参数预设 ----------------

    /// <summary>
    /// 下拉里的固定第一项：「默认」，表示不用任何预设，直接用下方的自定义参数。
    ///
    /// 用一个 Id 为 null 的哨兵行而不是「下拉里没有选中项」来表达默认：
    /// ComboBox 允许选中项为 null，但那样界面会显示成空白，用户看不出当前是什么状态。
    /// </summary>
    private readonly JvmPresetRow _defaultJvmPresetRow = new(null, JvmPresetRow.DefaultDisplayName, string.Empty);

    /// <summary>真实预设（不含默认哨兵行），保存时按此顺序写回 <see cref="AppSettings.JvmPresets"/>。</summary>
    public ObservableCollection<JvmPresetRow> JvmPresets { get; } = new();

    /// <summary>下拉数据源：第一项是默认哨兵，其余与 <see cref="JvmPresets"/> 是同一批行对象。</summary>
    public ObservableCollection<JvmPresetRow> JvmPresetChoices { get; } = new();

    [ObservableProperty]
    private JvmPresetRow? _selectedJvmPreset;

    /// <summary>选中的是一个真实预设（而不是「默认」）。</summary>
    public bool IsPresetSelected => SelectedJvmPreset is { IsDefault: false };

    public string ActiveJvmPresetHint => SelectedJvmPreset is { IsDefault: false } row
        ? $"当前由预设「{row.DisplayName}」提供，下方的自定义参数会被忽略。"
        : "未使用预设：启动时使用下方的自定义参数。";

    /// <summary>
    /// 选中预设的名称。用独立属性转发而不是在 XAML 里绑 <c>SelectedJvmPreset.Name</c>：
    /// 默认模式下选中项其实是哨兵行，直接绑会把「默认（使用下面的自定义参数）」写进名称框。
    /// </summary>
    public string SelectedJvmPresetName
    {
        get => SelectedJvmPreset is { IsDefault: false } row ? row.Name : string.Empty;
        set
        {
            if (SelectedJvmPreset is not { IsDefault: false } row || row.Name == value)
            {
                return;
            }

            // 行的 PropertyChanged 会把通知与标脏一起处理，这里不再重复发。
            row.Name = value ?? string.Empty;
        }
    }

    public string SelectedJvmPresetArguments
    {
        get => SelectedJvmPreset is { IsDefault: false } row ? row.Arguments : string.Empty;
        set
        {
            if (SelectedJvmPreset is not { IsDefault: false } row || row.Arguments == value)
            {
                return;
            }

            row.Arguments = value ?? string.Empty;
        }
    }

    /// <summary>选中真实预设时「删除」才可用；默认项本身删不掉。</summary>
    public bool CanDeleteJvmPreset => SelectedJvmPreset is { IsDefault: false };

    /// <summary>只在选中真实预设时「切回默认」才有意义，否则按钮点了没有任何反馈。</summary>
    public bool CanUseDefaultJvmPreset => IsPresetSelected;

    partial void OnSelectedJvmPresetChanged(JvmPresetRow? value)
    {
        OnPropertyChanged(nameof(IsPresetSelected));
        OnPropertyChanged(nameof(ActiveJvmPresetHint));
        OnPropertyChanged(nameof(SelectedJvmPresetName));
        OnPropertyChanged(nameof(SelectedJvmPresetArguments));
        DeleteJvmPresetCommand.NotifyCanExecuteChanged();
        UseDefaultJvmPresetCommand.NotifyCanExecuteChanged();

        // 切换选中项会改变 ActiveJvmPresetId，属于需要保存的改动。
        MarkDirty();
    }

    [RelayCommand]
    private void AddJvmPreset()
    {
        var row = new JvmPresetRow(
            Guid.NewGuid().ToString("N")[..8],
            NextJvmPresetName(),
            "-XX:+UseG1GC -XX:MaxGCPauseMillis=50");

        AttachJvmPresetRow(row);
        JvmPresets.Add(row);
        JvmPresetChoices.Add(row);
        SelectedJvmPreset = row;
        MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteJvmPreset))]
    private void DeleteJvmPreset()
    {
        if (SelectedJvmPreset is not { IsDefault: false } row)
        {
            return;
        }

        row.PropertyChanged -= OnJvmPresetRowPropertyChanged;
        JvmPresets.Remove(row);
        JvmPresetChoices.Remove(row);
        SelectedJvmPreset = _defaultJvmPresetRow;
        MarkDirty();
    }

    /// <summary>切回默认：保留下方的自定义参数原文，只是不再应用预设。</summary>
    [RelayCommand(CanExecute = nameof(CanUseDefaultJvmPreset))]
    private void UseDefaultJvmPreset() => SelectedJvmPreset = _defaultJvmPresetRow;

    private string NextJvmPresetName()
    {
        // 递增编号，避免新增几条后全是同名，下拉里根本分不清。
        var index = 1;
        while (JvmPresets.Any(row => string.Equals(row.Name, $"预设 {index}", StringComparison.Ordinal)))
        {
            index++;
        }

        return $"预设 {index}";
    }

    private void AttachJvmPresetRow(JvmPresetRow row) => row.PropertyChanged += OnJvmPresetRowPropertyChanged;

    private void OnJvmPresetRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedJvmPreset))
        {
            OnPropertyChanged(nameof(SelectedJvmPresetName));
            OnPropertyChanged(nameof(SelectedJvmPresetArguments));
            OnPropertyChanged(nameof(ActiveJvmPresetHint));
        }

        if (e.PropertyName is nameof(JvmPresetRow.Name) or nameof(JvmPresetRow.Arguments))
        {
            MarkDirty();
        }
    }

    /// <summary>
    /// 把持久化的预设映射成行模型。每次都重建集合：设置可能在别处（另一窗口/另一进程）被改过，
    /// 增量合并会留下指向旧对象的选中项。
    /// </summary>
    private void LoadJvmPresets(AppSettings settings)
    {
        foreach (var row in JvmPresets)
        {
            row.PropertyChanged -= OnJvmPresetRowPropertyChanged;
        }

        JvmPresets.Clear();
        JvmPresetChoices.Clear();
        JvmPresetChoices.Add(_defaultJvmPresetRow);

        foreach (var preset in settings.JvmPresets ?? new List<JvmPreset>())
        {
            if (preset is null)
            {
                continue;
            }

            var row = new JvmPresetRow(preset.Id, preset.Name ?? string.Empty, preset.Arguments ?? string.Empty);
            AttachJvmPresetRow(row);
            JvmPresets.Add(row);
            JvmPresetChoices.Add(row);
        }

        SelectedJvmPreset = JvmPresets.FirstOrDefault(
            row => string.Equals(row.Id, settings.ActiveJvmPresetId, StringComparison.Ordinal))
            ?? _defaultJvmPresetRow;
    }

    // ---------------- 游戏：启动参数与环境（对应 PCL2 的 LaunchArgument* / VersionArgument* 全局部分） ----------------

    /// <summary>全局游戏参数。实例级 <see cref="NebulaLauncher.Core.Models.InstanceInfo.GameArguments"/> 优先。</summary>
    [ObservableProperty]
    private string _extraGameArguments = string.Empty;

    /// <summary>
    /// 游戏窗口标题模板。支持 <c>{version}</c> / <c>{user}</c> / <c>{date}</c> / <c>{time}</c>。
    /// 只在 Windows 生效：换标题需要 EnumWindows 找到游戏窗口。
    /// </summary>
    [ObservableProperty]
    private string _gameWindowTitle = string.Empty;

    /// <summary>窗口出现后自动最大化。</summary>
    [ObservableProperty]
    private bool _maximizeGameWindow;

    /// <summary>
    /// 版本信息（<c>--versionType</c>，PCL2 <c>LaunchArgumentInfo</c>）。
    /// 显示在 Minecraft 主菜单标题与 F3 屏上，与启动器里的实例名、目录名无关。
    ///
    /// 留空 = 不显示：CmlLib 一定会生成 <c>--versionType ""</c>，由 GameLauncher 在
    /// 拼完命令行后把键和值两个 token 一起摘掉。实例级 <see cref="InstanceInfo.VersionType"/> 优先。
    /// </summary>
    [ObservableProperty]
    private string _versionArgumentInfo = string.Empty;

    /// <summary>
    /// 版本信息的长度上限。<c>--versionType</c> 会原样进命令行与主菜单标题，
    /// 没有上限时一次误粘贴就能让标题横穿半个屏幕；32 字符够放「整合包名 + 版本号」。
    /// </summary>
    internal const int VersionArgumentInfoMaxLength = 32;

    /// <summary>
    /// 按可用内存自动分配 -Xmx（PCL2 <c>LaunchArgumentRam</c>）。
    /// 打开后 <see cref="MaxMemoryMb"/> 只作为「关掉自动后的值」保存；
    /// 实例显式设置的内存在自动模式下仍然优先。
    /// </summary>
    [ObservableProperty]
    private bool _autoMaxMemory = true;

    /// <summary>自动内存模式下，滑块不再决定实际堆大小，置灰以免用户误以为能调。</summary>
    public bool IsManualMemoryEnabled => !AutoMaxMemory;

    public string AutoMemoryHint => AutoMaxMemory
        ? "已开启：启动时按本机可用内存与该实例的模组数量自动计算 -Xmx（参考 PCL2 的四段斜坡公式）。"
        : "已关闭：使用下方滑块的内存值。";

    public string WindowTitleHint => "支持占位符 {version} {user} {date} {time}；留空表示不改标题。仅 Windows 生效。";

    public string MaxMemoryDisplay => $"{MaxMemoryMb} MB";

    public string MinMemoryDisplay => $"{MinMemoryMb} MB";

    // ---------------- 游戏：内存三段可视化 ----------------

    /// <summary>
    /// 可分配内存快照。只在进入页面、按 F5、点「重新测量」时采集。
    /// 拖滑杆时不重采：「其它占用」每拖一格就变一个数，用户会以为自己在改别的程序。
    /// </summary>
    private MemorySnapshot _memory = MemorySnapshot.Create(0, 0);

    /// <summary>当前快照。无值时 <see cref="MemorySnapshot.HasValue"/> 为 false，图例显示「未知」。</summary>
    public MemorySnapshot Memory => _memory;

    /// <summary>探测到了可分配上限。false 时条不显示，只剩图例的「未知」。</summary>
    public bool HasMemorySnapshot => _memory.HasValue;

    /// <summary>其它占用比例（不含游戏堆），恒在 0..1。</summary>
    public double MemoryUsedRatio => _memory.UsedRatio;

    /// <summary>
    /// 本实例将分配（-Xmx）的比例，恒在 0..1。
    /// 与 <see cref="MemoryUsedRatio"/> 相加可能超过 1——那是「内存设太大了」的可见信号，这里不夹和。
    /// </summary>
    public double MemoryGameRatio => _memory.GameRatio(MaxMemoryMb);

    /// <summary>
    /// 三段条里「已用 + 本实例」那一段的比例，夹到 0..1 给进度条用。
    /// 夹在这里而不是靠控件：「超过上限」由 <see cref="IsMemoryOverCommit"/> 的文字与颜色表达，
    /// 条本身只负责「看起来满了」。
    /// </summary>
    public double MemoryUsedPlusGameRatio => Math.Clamp(MemoryUsedRatio + MemoryGameRatio, 0d, 1d);

    /// <summary>其它占用 + 本实例分配已经超过可分配上限。</summary>
    public bool IsMemoryOverCommit => IsOverCommit(GameHeapBytes, _memory);

    /// <summary>
    /// 超额提示。这条是整块图的核心价值：<c>-Xmx8G</c> 在 8 GB 可分配的机器上必然起不来，
    /// 而单看「可用内存 8 GB」的文字用户永远发现不了。
    /// </summary>
    public string MemoryOverCommitHint =>
        $"最大内存已超过可分配内存（{MemoryTotalText}），游戏可能无法启动。";

    /// <summary>可分配上限。探测不到时是「未知」，不是「0 GB」。</summary>
    public string MemoryTotalText => _memory.HasValue
        ? MemorySnapshot.FormatGigabytes(_memory.TotalBytes)
        : "未知";

    /// <summary>
    /// 其它程序已占用。等于「上限 减 剩余」，游戏堆不算在里面。
    /// 探到上限但差值恰为 0 时要写「0 MB」而不是「未知」——
    /// 后者是 <see cref="MemorySnapshot.FormatGigabytes"/> 对 0 的默认说法（意思是没探到），
    /// 在这里会把「几乎没占」误读成「测不出来」。
    /// </summary>
    public string MemoryUsedText => !_memory.HasValue
        ? "未知"
        : _memory.UsedBytes > 0
            ? MemorySnapshot.FormatGigabytes(_memory.UsedBytes)
            : "0 MB";

    /// <summary>本实例将分配的堆大小。这个数永远已知（来自滑杆），与快照是否探到无关。</summary>
    public string MemoryGameText => MemorySnapshot.FormatGigabytes(GameHeapBytes);

    private long GameHeapBytes => MaxMemoryMb * 1024L * 1024L;

    /// <summary>
    /// 采一次快照并刷新三段条。页面进入、F5、点「重新测量」都走这里；
    /// 拖滑杆走 <see cref="NotifyMemoryBar"/>，不重采。
    /// </summary>
    internal void ApplyMemorySnapshot(MemorySnapshot snapshot)
    {
        _memory = snapshot ?? MemorySnapshot.Create(0, 0);
        NotifyMemoryBar();
    }

    /// <summary>只把跟条有关的派生属性通知出去，不动快照。</summary>
    private void NotifyMemoryBar()
    {
        OnPropertyChanged(nameof(Memory));
        OnPropertyChanged(nameof(HasMemorySnapshot));
        OnPropertyChanged(nameof(MemoryUsedRatio));
        OnPropertyChanged(nameof(MemoryGameRatio));
        OnPropertyChanged(nameof(MemoryUsedPlusGameRatio));
        OnPropertyChanged(nameof(IsMemoryOverCommit));
        OnPropertyChanged(nameof(MemoryOverCommitHint));
        OnPropertyChanged(nameof(MemoryTotalText));
        OnPropertyChanged(nameof(MemoryUsedText));
        OnPropertyChanged(nameof(MemoryGameText));
    }

    [RelayCommand]
    private void RefreshMemorySnapshot() => ApplyMemorySnapshot(MemorySnapshot.Read());

    /// <summary>
    /// 游戏堆是否已超过「上限 - 其它占用」。
    ///
    /// 用字节比而不是比例和：<see cref="MemorySnapshot.GameRatio"/> 把比例夹在 0..1，
    /// 堆比上限还大时它同样是 1，两者相加恒等于 1，「超额」就永远判不出来（这是实测踩到的坑）。
    /// 比较字节没有这个退化。
    ///
    /// 探测不到上限时返回 false：不知道上限就喊「超过」是瞎猜。
    /// </summary>
    internal static bool IsOverCommit(long gameHeapBytes, MemorySnapshot snapshot) =>
        snapshot.HasValue && gameHeapBytes > snapshot.TotalBytes - snapshot.UsedBytes;

    partial void OnMaxMemoryMbChanged(int value)
    {
        if (MinMemoryMb > value)
        {
            MinMemoryMb = value;
        }

        OnPropertyChanged(nameof(MaxMemoryDisplay));

        // 滑杆动了，第三段与图例跟着动；快照不动（见 _memory 的注释）。
        NotifyMemoryBar();
        MarkDirty();
    }

    partial void OnMinMemoryMbChanged(int value)
    {
        OnPropertyChanged(nameof(MinMemoryDisplay));
        MarkDirty();
    }

    partial void OnWindowWidthTextChanged(string value) => MarkDirty();

    partial void OnWindowHeightTextChanged(string value) => MarkDirty();

    partial void OnFullscreenChanged(bool value) => MarkDirty();

    partial void OnExtraJvmArgumentsChanged(string value) => MarkDirty();

    partial void OnExtraGameArgumentsChanged(string value) => MarkDirty();

    partial void OnGameWindowTitleChanged(string value) => MarkDirty();

    partial void OnMaximizeGameWindowChanged(bool value) => MarkDirty();

    /// <summary>
    /// 超长就地截断而不是弹错误：这个框只影响主菜单标题，拦着不让保存的代价
    /// （用户以为设置坏了）比截掉几个字符大得多。<c>TextBox.MaxLength</c> 挡住手输，
    /// 这里挡住粘贴与「手改 settings.json 之后再进页面」。
    /// </summary>
    partial void OnVersionArgumentInfoChanged(string value)
    {
        var clipped = ClipVersionArgumentInfo(value);

        if (!string.Equals(clipped, value, StringComparison.Ordinal))
        {
            VersionArgumentInfo = clipped;
            return;
        }

        MarkDirty();
    }

    /// <summary>去掉首尾空白并按上限截断；null 归一成空串。</summary>
    internal static string ClipVersionArgumentInfo(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();

        return trimmed.Length <= VersionArgumentInfoMaxLength
            ? trimmed
            : trimmed[..VersionArgumentInfoMaxLength].TrimEnd();
    }

    partial void OnAutoMaxMemoryChanged(bool value)
    {
        OnPropertyChanged(nameof(IsManualMemoryEnabled));
        OnPropertyChanged(nameof(AutoMemoryHint));
        MarkDirty();
    }

    // ---------------- 下载 ----------------

    [ObservableProperty]
    private DownloadSource _downloadSource = DownloadSource.Mirror;

    [ObservableProperty]
    private int _downloadThreads = 64;

    /// <summary>
    /// 全局下载限速 KB/s（PCL2 <c>ToolDownloadSpeed</c>）。0 表示不限速。
    /// 限速只约束下载队列的聚合带宽；B 站/CDN 类单文件限速黑洞靠慢速判定处理，不在这里。
    /// </summary>
    [ObservableProperty]
    private int _downloadSpeedLimitKb;

    /// <summary>
    /// Modrinth 镜像兜底开关（第三方 <c>mod.mcimirror.top</c>）。默认关：镜像会把搜索内容暴露给
    /// 第三方，不能替用户默认打开。打开后也只在官方**连不上**时用，官方 404 不换镜像。
    /// </summary>
    [ObservableProperty]
    private bool _useModrinthMirror;

    public string UseModrinthMirrorHint => UseModrinthMirror
        ? "镜像来自第三方（mod.mcimirror.top），仅在 Modrinth 官方直连失败时使用。"
        : "关闭时不向任何第三方镜像发请求；Modrinth 官方连不上时直接报错。";

    partial void OnUseModrinthMirrorChanged(bool value)
    {
        OnPropertyChanged(nameof(UseModrinthMirrorHint));
        MarkDirty();
    }

    public bool IsUnlimitedSpeed => DownloadSpeedLimitKb <= 0;

    public string DownloadSpeedDisplay => IsUnlimitedSpeed
        ? "不限速"
        : DownloadSpeedLimitKb >= 1024
            ? $"{DownloadSpeedLimitKb / 1024.0:0.#} MB/s"
            : $"{DownloadSpeedLimitKb} KB/s";

    public bool IsAutoSource => DownloadSource == DownloadSource.Auto;

    /// <summary>
    /// 一句说明：下载页的「源测速」只提示、不改设置。
    /// 不写这句，用户点完测速看到「建议用镜像」会以为启动器已经替他切了——
    /// 而测速恰恰刻意不写设置（§6.23：今天的网络结论不该变成永久配置）。
    /// </summary>
    public string SourceProbeHint =>
        "下载页的「源测速」只做一次测量并给建议，不会修改这里的下载源；要不要切换由你自己选。";

    public bool IsOfficialSource => DownloadSource == DownloadSource.Official;

    public bool IsMirrorSource => DownloadSource == DownloadSource.Mirror;

    partial void OnDownloadSourceChanged(DownloadSource value)
    {
        NotifySourceOptions();
        MarkDirty();
    }

    partial void OnDownloadThreadsChanged(int value) => MarkDirty();

    partial void OnDownloadSpeedLimitKbChanged(int value)
    {
        OnPropertyChanged(nameof(IsUnlimitedSpeed));
        OnPropertyChanged(nameof(DownloadSpeedDisplay));
        MarkDirty();
    }

    [RelayCommand]
    private void SelectDownloadSource(string? source)
    {
        DownloadSource = source switch
        {
            "auto" => DownloadSource.Auto,
            "official" => DownloadSource.Official,
            "mirror" or "bmclapi" => DownloadSource.Mirror,
            _ => DownloadSource,
        };
    }

    private void NotifySourceOptions()
    {
        OnPropertyChanged(nameof(IsAutoSource));
        OnPropertyChanged(nameof(IsOfficialSource));
        OnPropertyChanged(nameof(IsMirrorSource));
    }

    // ---------------- 网络代理 ----------------

    /// <summary>代理模式草稿，取值见 <see cref="ProxyModes"/>（<c>none</c> / <c>system</c> / <c>custom</c>）。</summary>
    [ObservableProperty]
    private string _proxyMode = ProxyModes.System;

    /// <summary>自定义代理地址草稿。规范化与回落都在 Core，这里只保留用户原文。</summary>
    [ObservableProperty]
    private string _proxyAddress = string.Empty;

    [ObservableProperty]
    private string _proxyUserName = string.Empty;

    /// <summary>
    /// 密码输入框的草稿。磁盘上存的是密文，这里**永远不回显已保存的密码**：
    /// 空表示「保持不变」，填了才覆盖。少一个「显示密码」勾选，就少一条把口令抄进截图的路径。
    /// </summary>
    [ObservableProperty]
    private string _proxyPassword = string.Empty;

    /// <summary>当前磁盘上那段密码的存储方式说明（已加密 / 未加密 / 未设置）。</summary>
    [ObservableProperty]
    private string _proxyCredentialHint = "未设置";

    [ObservableProperty]
    private bool _isProxyTesting;

    /// <summary>连通性测试的总结行（如「3/3 个端点可达」）。null 表示本次还没测过。</summary>
    [ObservableProperty]
    private string? _proxyTestStatus;

    /// <summary>逐端点的测试结果，按 <see cref="NetworkProbe.Targets"/> 的顺序追加。</summary>
    public ObservableCollection<ProxyProbeRow> ProxyProbeResults { get; } = new();

    public bool IsProxyNone => string.Equals(ProxyMode, ProxyModes.None, StringComparison.Ordinal);

    public bool IsProxySystem => string.Equals(ProxyMode, ProxyModes.System, StringComparison.Ordinal);

    public bool IsProxyCustom => string.Equals(ProxyMode, ProxyModes.Custom, StringComparison.Ordinal);

    /// <summary>
    /// 三张卡片下方的说明。讲清两件容易误会的事：
    /// 「无」是真的绕过系统代理（不只是不设置），以及改动要保存后对后续请求生效。
    /// </summary>
    public string ProxyModeHint => ProxyMode switch
    {
        ProxyModes.None => "所有请求直连，连系统里的代理与环境变量都不用。只在本机能直接访问外网时选。",
        ProxyModes.Custom => "启动器的全部网络请求都走下面这个地址；代理需要认证时填用户名与密码。",
        _ => "沿用 Windows 系统代理与环境变量（HTTPS_PROXY / HTTP_PROXY），这是历史默认行为。",
    };

    partial void OnProxyModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsProxyNone));
        OnPropertyChanged(nameof(IsProxySystem));
        OnPropertyChanged(nameof(IsProxyCustom));
        OnPropertyChanged(nameof(ProxyModeHint));
        MarkDirty();
    }

    partial void OnProxyAddressChanged(string value) => MarkDirty();

    partial void OnProxyUserNameChanged(string value) => MarkDirty();

    partial void OnProxyPasswordChanged(string value) => MarkDirty();

    [RelayCommand]
    private void SelectProxyMode(string? mode)
    {
        ProxyMode = mode switch
        {
            ProxyModes.None => ProxyModes.None,
            ProxyModes.Custom => ProxyModes.Custom,
            ProxyModes.System => ProxyModes.System,
            _ => ProxyMode,
        };
    }

    public bool CanTestProxy => !IsProxyTesting;

    partial void OnIsProxyTestingChanged(bool value) => TestProxyCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// 测试连通性。探测的是界面上的草稿而不是已保存的设置：
    /// 用户通常先改地址再测，「先保存才生效」会让试一个代理变成两步操作。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestProxy))]
    private async Task TestProxyAsync()
    {
        if (_probeCts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _probeCts = cts;
        IsProxyTesting = true;
        ProxyProbeResults.Clear();
        ProxyTestStatus = "正在测试…";

        try
        {
            var draft = new AppSettings
            {
                ProxyMode = ProxyMode,
                ProxyAddress = string.IsNullOrWhiteSpace(ProxyAddress) ? null : ProxyAddress.Trim(),
                ProxyUserName = string.IsNullOrWhiteSpace(ProxyUserName) ? null : ProxyUserName.Trim(),
                ProxyPassword = string.IsNullOrEmpty(ProxyPassword) ? null : ProxyPassword,
            };

            var results = await NetworkProbe.RunAsync(draft, cts.Token);

            foreach (var result in results)
            {
                ProxyProbeResults.Add(new ProxyProbeRow(result));
            }

            var reachable = results.Count(result => result.Ok);
            ProxyTestStatus = reachable == results.Count
                ? $"{reachable}/{results.Count} 个端点可达"
                : $"{reachable}/{results.Count} 个端点可达，失败原因见下方";
        }
        catch (OperationCanceledException)
        {
            ProxyTestStatus = "测试已取消";
        }
        catch (Exception ex)
        {
            ProxyTestStatus = $"测试失败：{ex.Message}";
        }
        finally
        {
            IsProxyTesting = false;
            _probeCts = null;
            cts.Dispose();
        }
    }

    // ---------------- 在线资源 ----------------

    /// <summary>
    /// 在线资源默认数据源。取值与 <see cref="AppSettings.ContentSource"/> 一致（<c>modrinth</c> / <c>curseforge</c>），
    /// 用字符串而不是枚举，settings.json 里的值才能原样往返。
    /// </summary>
    [ObservableProperty]
    private string _contentSource = "modrinth";

    /// <summary>Base URL 覆盖草稿。空表示走官方地址。</summary>
    [ObservableProperty]
    private string _curseForgeBaseUrl = string.Empty;

    public bool IsModrinthSource => string.Equals(ContentSource, "modrinth", StringComparison.OrdinalIgnoreCase);

    public bool IsCurseForgeSource => string.Equals(ContentSource, "curseforge", StringComparison.OrdinalIgnoreCase);

    partial void OnContentSourceChanged(string value)
    {
        NotifyContentSourceOptions();
        MarkDirty();
    }

    partial void OnCurseForgeBaseUrlChanged(string value) => MarkDirty();

    [RelayCommand]
    private void SelectContentSource(string? source)
    {
        ContentSource = source switch
        {
            "curseforge" => "curseforge",
            "modrinth" => "modrinth",
            _ => ContentSource,
        };
    }

    private void NotifyContentSourceOptions()
    {
        OnPropertyChanged(nameof(IsModrinthSource));
        OnPropertyChanged(nameof(IsCurseForgeSource));
    }

    /// <summary>
    /// 与 <c>SettingsService.Normalize</c> 同口径：剥掉首尾空白与末尾斜杠，非法地址退回 null（等于用官方地址）。
    /// 保存路径上不会经过 Normalize，不在这里收口的话，运行期的客户端会先拿到一串非法地址。
    /// </summary>
    private static string? NormalizeCurseForgeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimEnd('/');

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? trimmed
            : null;
    }

    // ---------------- 外观 ----------------

    [ObservableProperty]
    private string _theme = "dark";

    /// <summary>启动器背景图绝对路径；空表示未设置。</summary>
    [ObservableProperty]
    private string _backgroundImagePath = string.Empty;

    /// <summary>背景图上的遮罩强度（0–1）。</summary>
    [ObservableProperty]
    private double _backgroundImageOverlay = 0.55;

    /// <summary>是否设置了背景图（控制设置页的「清除」按钮）。</summary>
    public bool HasBackgroundImage => !string.IsNullOrWhiteSpace(BackgroundImagePath);

    partial void OnBackgroundImagePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasBackgroundImage));

        // 背景图是「立即生效」项：主窗口订阅 ISettingsService.Changed，落盘后当场换图。
        // 载入设置时也会走到这里（_suppressDirty=true），那一次只同步界面（REVIEW-R43-R51 P2-1）。
        if (_suppressDirty)
        {
            return;
        }

        MarkDirty();
        _ = PersistLiveAsync(settings => settings.BackgroundImagePath =
            string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    partial void OnBackgroundImageOverlayChanged(double value)
    {
        if (_suppressDirty)
        {
            return;
        }

        MarkDirty();
        _ = PersistLiveAsync(settings => settings.BackgroundImageOverlay = Math.Clamp(value, 0, 0.95));
    }

    /// <summary>清除背景图。写回设置由保存按钮统一完成（与其它草稿字段一致）。</summary>
    [RelayCommand]
    private void ClearBackgroundImage() => BackgroundImagePath = string.Empty;

    [ObservableProperty]
    private bool _enableAnimations = true;

    [ObservableProperty]
    private bool _enableBackgroundEffects = true;

    [ObservableProperty]
    private string _windowMaterial = "mica";

    /// <summary>记住启动器窗口尺寸与最大化状态。默认开启（关掉后启动 / 关闭两端都不读写）。</summary>
    [ObservableProperty]
    private bool _rememberWindowSize = true;

    public bool IsThemeDark => string.Equals(Theme, "dark", StringComparison.Ordinal);

    public bool IsThemeLight => string.Equals(Theme, "light", StringComparison.Ordinal);

    public bool IsThemeSystem => string.Equals(Theme, "system", StringComparison.Ordinal);

    public bool IsMaterialMica => string.Equals(WindowMaterial, "mica", StringComparison.Ordinal);

    public bool IsMaterialAcrylic => string.Equals(WindowMaterial, "acrylic", StringComparison.Ordinal);

    public bool IsMaterialSolid => string.Equals(WindowMaterial, "solid", StringComparison.Ordinal);

    /// <summary>Mica / 亚克力只在 Windows 11+ 可用，其它平台自动退回纯色。</summary>
    public bool CanUseWindowMaterial => _platform?.SupportsWindowMaterial ?? false;

    public string WindowMaterialHint => CanUseWindowMaterial
        ? "窗口材质由系统 DWM 提供；切换后需要重新进入页面才能看到完整效果。"
        : $"当前平台（{PlatformName}）不支持 Mica / 亚克力，窗口将以纯色背景运行。";

    partial void OnThemeChanged(string value)
    {
        NotifyThemeOptions();

        // 主题是「立即生效」项：先切变体（界面当场重画），再落盘。
        // 载入设置时也会走到这里（_suppressDirty=true），那一次只负责把界面同步到当前值。
        ThemeService.Apply(ThemeService.Effective(value), Application.Current);

        if (_suppressDirty)
        {
            return;
        }

        MarkDirty();
        _ = PersistLiveAsync(settings => settings.Theme = ThemeService.Normalize(Theme));
    }

    partial void OnWindowMaterialChanged(string value)
    {
        NotifyMaterialOptions();
        MarkDirty();
    }

    partial void OnEnableAnimationsChanged(bool value)
    {
        // 动效开关是「立即生效」项：先动全局开关，再落盘。
        Motion.Enabled = value;
        if (_suppressDirty)
        {
            return;
        }

        MarkDirty();
        _ = PersistLiveAsync(settings => settings.EnableAnimations = value);
    }

    partial void OnEnableBackgroundEffectsChanged(bool value)
    {
        if (_suppressDirty)
        {
            return;
        }

        MarkDirty();
        _ = PersistLiveAsync(settings => settings.EnableBackgroundEffects = value);
    }

    /// <summary>
    /// 窗口尺寸记忆是「立即生效」项：只落盘，不改当前窗口。
    /// 关掉后下次启动按默认尺寸打开；本次会话的窗口保持用户当前拖出来的大小。
    /// </summary>
    partial void OnRememberWindowSizeChanged(bool value)
    {
        if (_suppressDirty)
        {
            return;
        }

        MarkDirty();
        _ = PersistLiveAsync(settings => settings.RememberWindowSize = value);
    }

    private void NotifyThemeOptions()
    {
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeSystem));
    }

    [RelayCommand]
    private void SelectTheme(string? theme)
    {
        if (theme is "dark" or "light" or "system")
        {
            Theme = theme;
        }
    }

    private void NotifyMaterialOptions()
    {
        OnPropertyChanged(nameof(IsMaterialMica));
        OnPropertyChanged(nameof(IsMaterialAcrylic));
        OnPropertyChanged(nameof(IsMaterialSolid));
    }

    [RelayCommand]
    private void SelectMaterial(string? material)
    {
        if (material is "mica" or "acrylic" or "solid")
        {
            WindowMaterial = material;
        }
    }

    // ---------------- 存储 ----------------

    /// <summary>
    /// 四个存储类别的扫描结果。勾选用每行复选框而不是 ListBox 多选：
    /// 编译绑定能校验 <c>IsChecked</c>，而 <c>SelectedItems</c> 绑错只会在运行期静默失效（§6.22）。
    /// </summary>
    public ObservableCollection<StorageCategoryRow> StorageCategories { get; } = new();

    [ObservableProperty]
    private bool _isStorageScanning;

    [ObservableProperty]
    private bool _isStorageCleaning;

    [ObservableProperty]
    private string _storageStatus = "尚未统计数据目录";

    [ObservableProperty]
    private string _storageTotalSizeText = "—";

    [ObservableProperty]
    private string _storageTotalFilesText = "共 0 个文件";

    [ObservableProperty]
    private string _storageScannedAtText = "尚未扫描";

    [ObservableProperty]
    private bool _hasStorageCategories;

    [ObservableProperty]
    private int _storageSelectedCount;

    [ObservableProperty]
    private bool _isStorageCleanConfirmOpen;

    [ObservableProperty]
    private string _storageCleanConfirmText = string.Empty;

    public bool CanScanStorage => !IsStorageScanning && !IsStorageCleaning;

    public bool CanCleanSelectedStorage => StorageSelectedCount > 0 && CanScanStorage;

    public string StorageSelectionSummary => StorageSelectedCount == 0
        ? "勾选要清理的类别后点「清理选中项」。回收站里的内容删除后不可恢复。"
        : $"已选 {StorageSelectedCount} 个类别，共 {Conv.FormatSize(StorageCategories.Where(row => row.IsSelected).Sum(row => row.TotalBytes))}。";

    partial void OnIsStorageScanningChanged(bool value) => NotifyStorageCommands();

    partial void OnIsStorageCleaningChanged(bool value) => NotifyStorageCommands();

    partial void OnStorageSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(StorageSelectionSummary));
        NotifyStorageCommands();
    }

    private void NotifyStorageCommands()
    {
        OnPropertyChanged(nameof(CanScanStorage));
        OnPropertyChanged(nameof(CanCleanSelectedStorage));
        RefreshStorageCommand.NotifyCanExecuteChanged();
        RequestCleanSelectedStorageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanScanStorage))]
    private Task RefreshStorageAsync() => ScanStorageCoreAsync();

    [RelayCommand]
    private void RequestCleanStorage(StorageCategoryRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (_storage is null)
        {
            StorageStatus = "存储服务尚未接入，无法清理。";
            return;
        }

        if (!row.CanClean)
        {
            StorageStatus = $"「{row.DisplayName}」不允许清理。";
            return;
        }

        _pendingStorageKeys = [row.Key];
        StorageCleanConfirmText =
            $"将清理「{row.DisplayName}」（{row.SizeText}，{row.FileCountText}）。{row.CleanHint}";
        IsStorageCleanConfirmOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanCleanSelectedStorage))]
    private void RequestCleanSelectedStorage()
    {
        if (_storage is null)
        {
            StorageStatus = "存储服务尚未接入，无法清理。";
            return;
        }

        var selected = StorageCategories.Where(row => row.IsSelected && row.CanClean).ToList();
        if (selected.Count == 0)
        {
            StorageStatus = "没有勾选可清理的类别。";
            return;
        }

        _pendingStorageKeys = selected.Select(row => row.Key).ToList();
        var bytes = selected.Sum(row => row.TotalBytes);
        StorageCleanConfirmText =
            $"将清理 {selected.Count} 个类别（{string.Join("、", selected.Select(row => row.DisplayName))}），预计释放 {Conv.FormatSize(bytes)}。";
        IsStorageCleanConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelCleanStorage()
    {
        IsStorageCleanConfirmOpen = false;
        _pendingStorageKeys = [];
    }

    [RelayCommand]
    private async Task ConfirmCleanStorageAsync()
    {
        IsStorageCleanConfirmOpen = false;

        if (_storage is null || _pendingStorageKeys.Count == 0)
        {
            StorageStatus = "没有待清理的类别。";
            return;
        }

        var keys = _pendingStorageKeys;
        _pendingStorageKeys = [];

        IsStorageCleaning = true;
        StorageStatus = "正在清理…";
        try
        {
            var result = await _storage.CleanAsync(keys);
            StorageStatus = DescribeCleanResult(result);
        }
        catch (OperationCanceledException)
        {
            StorageStatus = "清理已取消。";
        }
        catch (Exception ex)
        {
            StorageStatus = $"清理失败：{ex.Message}";
        }
        finally
        {
            IsStorageCleaning = false;
        }

        // 清理完立刻重扫：占用数字必须反映磁盘现状，否则用户会以为「点了没反应」。
        await ScanStorageCoreAsync();
    }

    /// <summary>
    /// 扫描并刷新四个类别。异常一律在内部消化：占用统计失败不该让设置页变成一个错误页。
    /// </summary>
    private async Task ScanStorageCoreAsync()
    {
        if (_storage is null)
        {
            StorageStatus = "存储服务尚未接入，无法统计占用。";
            return;
        }

        if (IsStorageScanning)
        {
            return;
        }

        _storageCts?.Cancel();
        _storageCts?.Dispose();
        var cts = new CancellationTokenSource();
        _storageCts = cts;

        IsStorageScanning = true;
        StorageStatus = "正在统计…";
        try
        {
            var report = await _storage.ScanAsync(cts.Token);
            ApplyStorageReport(report);
            StorageStatus = report.TotalFiles == 0 ? "数据目录里没有可统计的文件。" : "统计完成。";
        }
        catch (OperationCanceledException)
        {
            StorageStatus = "统计已取消。";
        }
        catch (Exception ex)
        {
            StorageStatus = $"统计失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_storageCts, cts))
            {
                _storageCts = null;
            }

            IsStorageScanning = false;
            cts.Dispose();
        }
    }

    private void ApplyStorageReport(StorageReport report)
    {
        foreach (var row in StorageCategories)
        {
            row.PropertyChanged -= OnStorageRowPropertyChanged;
        }

        StorageCategories.Clear();
        foreach (var usage in report.Categories)
        {
            var row = new StorageCategoryRow(usage);
            row.PropertyChanged += OnStorageRowPropertyChanged;
            StorageCategories.Add(row);
        }

        HasStorageCategories = StorageCategories.Count > 0;
        StorageTotalSizeText = Conv.FormatSize(report.TotalBytes);
        StorageTotalFilesText = $"共 {report.TotalFiles} 个文件";
        StorageScannedAtText = report.ScannedAt == DateTimeOffset.MinValue
            ? "尚未扫描"
            : $"统计于 {report.ScannedAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}";

        // 重扫后所有行都是未选中，选中计数与「清理选中项」的可用状态要跟着回零。
        StorageSelectedCount = StorageCategories.Count(row => row.IsSelected);
    }

    private void OnStorageRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(StorageCategoryRow.IsSelected))
        {
            StorageSelectedCount = StorageCategories.Count(row => row.IsSelected);
        }
    }

    /// <summary>
    /// 把清理结果翻成人话。三种情况必须能分开：什么都没删、删了一部分但有失败项、整批被安全规则拒绝。
    /// </summary>
    private static string DescribeCleanResult(StorageCleanResult result)
    {
        var summary = result.DeletedNothing && result.FailedItems == 0
            ? "没有需要清理的文件。"
            : $"已清理 {result.DeletedFiles} 个文件，释放 {Conv.FormatSize(result.DeletedBytes)}";

        if (result.FailedItems > 0)
        {
            summary += $"（{result.FailedItems} 项被跳过或删除失败）";
        }

        if (result.IsRejected)
        {
            summary += $"：{result.Rejected}";
        }

        return summary.EndsWith('。') ? summary : summary + "。";
    }

    // ---------------- 关于 ----------------

    public string AppVersion
    {
        get
        {
            var assembly = typeof(SettingsViewModel).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // CI 会把 commit hash 拼在 + 后面，界面只展示语义版本。
                var plus = informational.IndexOf('+', StringComparison.Ordinal);
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "未知";
        }
    }

    public string DotNetVersion => RuntimeInformation.FrameworkDescription;

    public string PlatformName => _platform?.PlatformName ?? "未知";

    public string LogFilePath =>
        _log?.LogFilePath
        ?? (_paths is null ? "未接入日志服务" : Path.Combine(_paths.LogsDirectory, "launcher-YYYYMMDD.log"));

    public string DataDirectory => _paths?.RootDirectory ?? "未接入路径服务";

    [ObservableProperty]
    private string? _actionMessage;

    [RelayCommand]
    private async Task OpenDataFolderAsync()
    {
        if (_platform is null || _paths is null)
        {
            ActionMessage = "平台服务尚未接入，无法打开目录。";
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(_paths.RootDirectory);
            ActionMessage = "已在文件管理器中打开数据目录。";
        }
        catch (Exception ex)
        {
            ActionMessage = $"打开目录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenLogFolderAsync()
    {
        if (_platform is null)
        {
            ActionMessage = "平台服务尚未接入，无法打开日志。";
            return;
        }

        try
        {
            var logPath = LogFilePath;
            if (File.Exists(logPath))
            {
                await _platform.RevealInFileManagerAsync(logPath);
            }
            else if (_paths is not null)
            {
                await _platform.OpenFolderAsync(_paths.LogsDirectory);
            }
            else
            {
                await _platform.OpenFolderAsync(Path.GetDirectoryName(logPath) ?? ".");
            }

            ActionMessage = "已打开日志所在位置。";
        }
        catch (Exception ex)
        {
            ActionMessage = $"打开日志失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CopyLogPathAsync()
    {
        if (_platform is null)
        {
            ActionMessage = "平台服务尚未接入，无法复制路径。";
            return;
        }

        try
        {
            if (!await _platform.TryCopyTextAsync(LogFilePath))
            {
                ActionMessage = "复制失败：剪贴板不可用，详见启动器日志。";
                return;
            }

            ActionMessage = "日志路径已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            ActionMessage = $"复制失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        if (_platform is null)
        {
            ActionMessage = "平台服务尚未接入，无法复制。";
            return;
        }

        try
        {
            if (!await _platform.TryCopyTextAsync(BuildDiagnosticsText()))
            {
                ActionMessage = "复制失败：剪贴板不可用，详见启动器日志。";
                return;
            }

            ActionMessage = "诊断信息已复制到剪贴板，可直接粘贴到问题反馈里。";
        }
        catch (Exception ex)
        {
            ActionMessage = $"复制失败：{ex.Message}";
        }
    }

    // ==================== 配置包导出 / 导入 ====================

    /// <summary>配置包服务是否可用。不可用时两个按钮置灰，不留一个点了没反应的入口。</summary>
    public bool CanTransfer => _bundle is not null && !IsTransferBusy;

    /// <summary>导入时是否连带应用设置。默认关闭：设置里混着游戏目录、Java 路径这类机器相关字段。</summary>
    [ObservableProperty]
    private bool _importSettingsWithBundle;

    [ObservableProperty]
    private bool _isTransferBusy;

    partial void OnIsTransferBusyChanged(bool value) => OnPropertyChanged(nameof(CanTransfer));

    /// <summary>导出目录选择器的默认落点：数据根下的 exports/，与启动脚本导出同一处。</summary>
    public string DefaultBundleDirectory =>
        _paths is null ? string.Empty : Path.Combine(_paths.RootDirectory, "exports");

    /// <summary>
    /// 导出配置包。目录由视图层选好再传进来（TopLevel 只在视图层拿得到），
    /// 结果只写状态栏，不弹窗。
    /// </summary>
    public async Task ExportProfileBundleAsync(string targetDirectory)
    {
        if (_bundle is null)
        {
            ActionMessage = "配置服务尚未接入，无法导出。";
            return;
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            ActionMessage = "未选择导出目录。";
            return;
        }

        IsTransferBusy = true;

        try
        {
            var result = await _bundle.ExportAsync(targetDirectory);

            ActionMessage =
                $"已导出配置包：{result.FilePath}（{result.FavoriteCount} 个收藏 / {result.HiddenCount} 个隐藏 / " +
                $"{result.ServerCount} 个服务器 / {result.SettingCount} 个设置项；不含账户与令牌）";
        }
        catch (Exception ex)
        {
            ActionMessage = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsTransferBusy = false;
        }
    }

    /// <summary>
    /// 导入配置包。先 <c>InspectAsync</c> 把「会写进去多少」写进状态栏再真正合并——
    /// 无效的包在这一步就会被拒绝，不会走到写盘。
    /// </summary>
    public async Task ImportProfileBundleAsync(string bundlePath)
    {
        if (_bundle is null)
        {
            ActionMessage = "配置服务尚未接入，无法导入。";
            return;
        }

        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            ActionMessage = "未选择配置文件。";
            return;
        }

        IsTransferBusy = true;

        try
        {
            var inspection = await _bundle.InspectAsync(bundlePath);

            if (!inspection.IsValid)
            {
                ActionMessage = $"无法导入：{inspection.Error}";
                return;
            }

            ActionMessage =
                $"正在导入 {inspection.ServerCount} 个服务器、{inspection.FavoriteCount} 个收藏、{inspection.SettingCount} 个设置项…";

            var result = await _bundle.ImportAsync(bundlePath, ImportSettingsWithBundle);

            if (!result.IsValid)
            {
                ActionMessage = $"导入失败：{result.Error}";
                return;
            }

            var parts = new List<string>
            {
                $"{result.Servers} 个服务器（新增 {result.ServersAdded}）",
                $"{result.Favorites} 个收藏",
            };

            if (result.Hidden > 0)
            {
                parts.Add($"{result.Hidden} 个隐藏版本");
            }

            parts.Add(ImportSettingsWithBundle
                ? $"{result.Settings} 个设置项"
                : "设置项未导入（未勾选「同时导入设置」）");

            ActionMessage = $"已导入 {string.Join("、", parts)}。";
        }
        catch (Exception ex)
        {
            ActionMessage = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsTransferBusy = false;
        }
    }

    /// <summary>
    /// 一段可直接粘贴的诊断文本（PCL2 的「复制诊断信息」）。
    ///
    /// 只收环境与设置摘要：账户名、令牌、完整日志一律不进——这类文本通常会被贴到公开的 issue 里，
    /// 带上任何一条凭据都等于泄露。
    ///
    /// 格式定义在 <see cref="DiagnosticsSnapshot.Create"/>，崩溃导出的 <c>system.txt</c> 调的是同一个函数，
    /// 两处字段名与顺序因此不会漂移。
    /// </summary>
    public string BuildDiagnosticsText() =>
        DiagnosticsSnapshot.Create(new DiagnosticsInput
        {
            LauncherVersion = AppVersion,
            DotNetVersion = DotNetVersion,
            PlatformName = PlatformName,
            DataDirectory = DataDirectory,
            GameDirectory = ActiveDataDirectoryText,
            LogFilePath = LogFilePath,
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            MinMemoryMb = MinMemoryMb,
            MaxMemoryMb = MaxMemoryMb,
            AutoMaxMemory = AutoMaxMemory,
            DownloadSource = DownloadSource.ToString(),
            DownloadThreads = DownloadThreads,
            DownloadSpeedLimitKb = DownloadSpeedLimitKb,
            IsolationPolicy = IsolationPolicy.ToString(),
            Theme = Theme,
            WindowMaterial = WindowMaterial,
            EnableAnimations = EnableAnimations,
            RememberWindowSize = RememberWindowSize,
        });

    // ---------------- 生命周期 ----------------

    public override async Task OnNavigatedToAsync()
    {
        if (_loadAttempted)
        {
            return;
        }

        _loadAttempted = true;

        // 内存条只采一次快照。先采再读设置：LoadSettingsAsync 里设置 MaxMemoryMb 会触发第一次
        // 通知，那时快照已经在了，条与图例一次就画对，不会先闪一个「0 / 未知」。
        ApplyMemorySnapshot(MemorySnapshot.Read());

        await LoadSettingsAsync();

        // Java 扫描可能耗时数秒，放后台跑，页面先出来。异常在方法内部消化。
        // 首次进入采信探测缓存（见 DetectJavaCoreAsync）；用户要强制重扫按「重新扫描」。
        _ = DetectJavaCoreAsync(forceRefresh: false);
    }

    public override Task OnNavigatedFromAsync()
    {
        _javaCts?.Cancel();
        _probeCts?.Cancel();
        _storageCts?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// F5：设置页默认只重读设置（基类走 <see cref="OnNavigatedToAsync"/>，而后者首次之后直接返回）。
    /// 停在「存储」分区时补一次重扫——用户常在资源管理器里手删缓存再按 F5 对账，
    /// 数字不动会被当成统计坏了。
    /// </summary>
    public override async Task ReloadCurrentAsync()
    {
        await base.ReloadCurrentAsync();

        // F5 的语义是「刷新这一页」。内存占用随时在变，重采一次才对得上用户的眼睛。
        ApplyMemorySnapshot(MemorySnapshot.Read());

        if (IsStorageSection && _storage is not null)
        {
            _storageScanned = true;
            await ScanStorageCoreAsync();
        }
    }

    /// <summary>Java 扫描、代理连通性测试与存储统计的取消源在页面被释放时收尾。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _javaCts?.Cancel();
            _javaCts?.Dispose();
            _javaCts = null;

            _probeCts?.Cancel();
            _probeCts?.Dispose();
            _probeCts = null;

            _storageCts?.Cancel();
            _storageCts?.Dispose();
            _storageCts = null;
        }

        base.Dispose(disposing);
    }

    private async Task LoadSettingsAsync()
    {
        var effective = new AppSettings();
        try
        {
            if (_settings is not null)
            {
                await _settings.LoadAsync();
                effective = _settings.Current;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"读取设置失败，已使用默认值：{ex.Message}";
        }

        ApplyFromSettings(effective);
        SaveStatus = "已载入当前设置";
        IsDirty = false;
    }

    private void ApplyFromSettings(AppSettings settings)
    {
        _suppressDirty = true;
        try
        {
            SelectedLanguage = Languages[0];
            CloseLauncherAfterLaunch = settings.CloseLauncherAfterLaunch;
                CloseToTray = settings.CloseToTray;

            LauncherVisibility = settings.LauncherVisibilityOnLaunch;
            GameProcessPriority = settings.GameProcessPriority;
            AutoChineseLanguage = settings.AutoChineseLanguage;
            RegisterGpuPreference = settings.RegisterGpuPreference;
            ArchiveCrashReports = settings.ArchiveCrashReports;
            AutoBackupWorldsOnExit = settings.AutoBackupWorldsOnExit;
            WorldBackupRetention = Math.Clamp(settings.WorldBackupRetention, 1, 200);

            JavaPath = settings.JavaPath ?? string.Empty;

            GameDirectory = settings.MinecraftDirectory ?? string.Empty;
            GameDirectoryError = null;
            GameDirectoryStatus = string.Empty;

            IsolationPolicy = IsolationPolicyResolver.Parse(settings.IsolationPolicy);

            MaxMemoryMb = Math.Clamp(settings.MaxMemoryMb, MaxMemoryMinimum, MaxMemoryMaximum);
            MinMemoryMb = Math.Clamp(settings.MinMemoryMb, MinMemoryMinimum, Math.Min(MinMemoryMaximum, MaxMemoryMb));
            WindowWidthText = settings.WindowWidth.ToString(CultureInfo.InvariantCulture);
            WindowHeightText = settings.WindowHeight.ToString(CultureInfo.InvariantCulture);
            Fullscreen = settings.Fullscreen;
            ExtraJvmArguments = settings.ExtraJvmArguments ?? string.Empty;
            LoadJvmPresets(settings);
            ExtraGameArguments = settings.ExtraGameArguments ?? string.Empty;
            GameWindowTitle = settings.GameWindowTitle ?? string.Empty;
            VersionArgumentInfo = ClipVersionArgumentInfo(settings.VersionArgumentInfo);
            BackgroundImagePath = settings.BackgroundImagePath ?? string.Empty;
            BackgroundImageOverlay = Math.Clamp(settings.BackgroundImageOverlay, 0, 0.95);
            MaximizeGameWindow = settings.MaximizeGameWindow;
            AutoMaxMemory = settings.AutoMaxMemory;

            DownloadSource = settings.DownloadSource switch
            {
                DownloadSource.Auto => DownloadSource.Auto,
                DownloadSource.Official => DownloadSource.Official,
                _ => DownloadSource.Mirror,
            };
            DownloadThreads = Math.Clamp(settings.DownloadThreads, DownloadThreadsMinimum, DownloadThreadsMaximum);
            DownloadSpeedLimitKb = Math.Clamp(settings.DownloadSpeedLimitKb, 0, 1024 * 64);
            UseModrinthMirror = settings.UseModrinthMirror;

            // 代理模式只认三个值：settings.json 可能被手改成别的字符串，这里与 SettingsService.Normalize 同规则回落。
            ProxyMode = settings.ProxyMode switch
            {
                ProxyModes.None => ProxyModes.None,
                ProxyModes.Custom => ProxyModes.Custom,
                _ => ProxyModes.System,
            };
            ProxyAddress = settings.ProxyAddress ?? string.Empty;
            ProxyUserName = settings.ProxyUserName ?? string.Empty;

            // 不回显已保存的密码：磁盘上是密文，读出来的明文只给本次会话发请求用。
            ProxyPassword = string.Empty;
            ProxyCredentialHint = ProxyCredentialStore.Describe(settings.ProxyPasswordProtected);

            EnableJavaCache = settings.EnableJavaCache;

            ContentSource = string.Equals(settings.ContentSource, "curseforge", StringComparison.OrdinalIgnoreCase)
                ? "curseforge"
                : "modrinth";
            CurseForgeBaseUrl = settings.CurseForgeBaseUrl ?? string.Empty;

            // 主题的显示值跟实际渲染走：截图命令行 --theme 覆盖时，选中项也要跟着变成被截的那一套，
            // 否则「浅色截图里选中的是深色」，验收时分不清是没切换还是选错了。
            Theme = ThemeService.Effective(settings.Theme);
            EnableAnimations = settings.EnableAnimations;
            EnableBackgroundEffects = settings.EnableBackgroundEffects;
            WindowMaterial = NormalizeMaterial(settings.WindowMaterial);
            RememberWindowSize = settings.RememberWindowSize;

            // 平台不支持窗口材质时直接退回纯色，避免界面显示的选择与实际效果不一致。
            if (!CanUseWindowMaterial && WindowMaterial != "solid")
            {
                WindowMaterial = "solid";
            }

            Motion.Enabled = EnableAnimations;
        }
        finally
        {
            _suppressDirty = false;
        }

        // 卡片选中态不会因为属性赋值自动刷新（初始值不触发 OnChanged），载入后显式同步一次。
        SyncChoiceOptions(VisibilityOptions, LauncherVisibility);
        SyncChoiceOptions(PriorityOptions, GameProcessPriority);

        Validate();
    }

    private string NormalizeMaterial(string? material) => material switch
    {
        "mica" or "mica-alt" or "tabbed" => "mica",
        "acrylic" or "blur" => "acrylic",
        _ => "solid",
    };

    // ---------------- 保存 ----------------

    public bool CanSave => !HasValidationError && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validate();
        if (HasValidationError)
        {
            return;
        }

        if (_settings is null)
        {
            ErrorMessage = "核心服务尚未接入，无法保存设置。";
            return;
        }

        IsSaving = true;
        ErrorMessage = null;
        try
        {
            if (!ValidateGameDirectory())
            {
                ErrorMessage = GameDirectoryError;
                SaveStatus = "游戏目录不可用，未保存";
                return;
            }

            ApplyTo(_settings.Current);
            await _settings.SaveAsync();
            IsDirty = false;

            // 保存后：草稿清空（不回显刚存的口令），存储方式说明按磁盘事实重算。
            ProxyPassword = string.Empty;
            ProxyCredentialHint = ProxyCredentialStore.Describe(_settings.Current.ProxyPasswordProtected);

            SaveStatus = $"已保存 · {DateTime.Now:HH:mm:ss}";
            Motion.Enabled = EnableAnimations;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"保存设置失败：{ex.Message}";
            SaveStatus = "保存失败";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task PersistLiveAsync(Action<AppSettings> mutate)
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            mutate(_settings.Current);
            await _settings.SaveAsync();
            SaveStatus = $"已自动保存 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"设置写回失败：{ex.Message}";
        }
    }

    private void ApplyTo(AppSettings target)
    {
        target.CloseLauncherAfterLaunch = CloseLauncherAfterLaunch;
        target.CloseToTray = CloseToTray;

        target.BackgroundImagePath = string.IsNullOrWhiteSpace(BackgroundImagePath)
            ? null
            : BackgroundImagePath.Trim();
        target.BackgroundImageOverlay = Math.Clamp(BackgroundImageOverlay, 0, 0.95);

        // 三态可见性是新字段；旧字段继续同步，回退到旧版本时行为不至于突变。
        target.LauncherVisibilityOnLaunch = LauncherVisibility;
        target.GameProcessPriority = GameProcessPriority;
        target.AutoChineseLanguage = AutoChineseLanguage;
        target.RegisterGpuPreference = RegisterGpuPreference;
        target.ArchiveCrashReports = ArchiveCrashReports;
        target.AutoBackupWorldsOnExit = AutoBackupWorldsOnExit;
        target.WorldBackupRetention = Math.Clamp(WorldBackupRetention, 1, 200);

        target.JavaPath = string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath.Trim();

        // 空字符串 → null，表示跟随平台默认目录；实际切换由主窗口订阅 Changed 后调用 SetMinecraftDirectory。
        target.MinecraftDirectory = string.IsNullOrWhiteSpace(GameDirectory) ? null : GameDirectory.Trim();

        target.IsolationPolicy = IsolationPolicyResolver.ToSettingValue(IsolationPolicy);

        target.MaxMemoryMb = MaxMemoryMb;
        target.MinMemoryMb = Math.Min(MinMemoryMb, MaxMemoryMb);

        TryParseDimension(WindowWidthText, 320, 7680, out var width);
        TryParseDimension(WindowHeightText, 240, 4320, out var height);
        target.WindowWidth = width;
        target.WindowHeight = height;

        target.Fullscreen = Fullscreen;
        target.ExtraJvmArguments = ExtraJvmArguments ?? string.Empty;

        // 预设按行模型逐条写回并保留 Id：ActiveJvmPresetId 靠 Id 指向正确的一组，
        // 每次保存都重新生成 Id 会让选中项在下次载入时静默丢失。
        target.JvmPresets = JvmPresets
            .Select(row => new JvmPreset
            {
                Id = string.IsNullOrWhiteSpace(row.Id) ? Guid.NewGuid().ToString("N")[..8] : row.Id!.Trim(),
                Name = string.IsNullOrWhiteSpace(row.Name) ? "未命名预设" : row.Name.Trim(),
                Arguments = row.Arguments ?? string.Empty,
            })
            .ToList();

        target.ActiveJvmPresetId = SelectedJvmPreset is { IsDefault: false } selected ? selected.Id : null;

        target.ExtraGameArguments = ExtraGameArguments ?? string.Empty;
        target.GameWindowTitle = string.IsNullOrWhiteSpace(GameWindowTitle) ? null : GameWindowTitle.Trim();

        // 空格留空写成空串而不是 null：AppSettings 的这个字段是非空 string，
        // 「没配置」的唯一表达就是空串，由 GameLauncher 翻译成「把 --versionType 整段摘掉」。
        target.VersionArgumentInfo = ClipVersionArgumentInfo(VersionArgumentInfo);
        target.MaximizeGameWindow = MaximizeGameWindow;
        target.AutoMaxMemory = AutoMaxMemory;

        target.DownloadSource = DownloadSource;
        target.DownloadThreads = Math.Clamp(DownloadThreads, DownloadThreadsMinimum, DownloadThreadsMaximum);
        target.DownloadSpeedLimitKb = Math.Clamp(DownloadSpeedLimitKb, 0, 1024 * 64);
        target.UseModrinthMirror = UseModrinthMirror;

        // 代理：原文写回，规范化（补 scheme、剥路径、模式回落）交给 SettingsService.Normalize，
        // 保证「保存后的值」与「重载后的值」是同一套规则算出来的。
        target.ProxyMode = ProxyMode;
        target.ProxyAddress = string.IsNullOrWhiteSpace(ProxyAddress) ? null : ProxyAddress.Trim();
        target.ProxyUserName = string.IsNullOrWhiteSpace(ProxyUserName) ? null : ProxyUserName.Trim();
        // 密码：留空表示「保持不变」。target 是 Current 的克隆，密文字段已经带过来了；
        // 填了新值才覆盖，并由 SettingsService 在写盘时加密（明文永远不会写进 settings.json）。
        if (!string.IsNullOrEmpty(ProxyPassword))
        {
            target.ProxyPassword = ProxyPassword;
        }
        else if (string.IsNullOrWhiteSpace(ProxyUserName))
        {
            // 用户名被清空时密码没有意义，连同密文一起清掉，避免「密码还在但没人用」的悬空状态。
            target.ProxyPassword = null;
            target.ProxyPasswordProtected = null;
        }

        target.EnableJavaCache = EnableJavaCache;

        target.ContentSource = IsCurseForgeSource ? "curseforge" : "modrinth";
        target.CurseForgeBaseUrl = NormalizeCurseForgeBaseUrl(CurseForgeBaseUrl);

        target.Theme = Theme;
        target.EnableAnimations = EnableAnimations;
        target.EnableBackgroundEffects = EnableBackgroundEffects;
        target.WindowMaterial = WindowMaterial;
        target.RememberWindowSize = RememberWindowSize;
    }

    // ---------------- 校验 ----------------

    private void MarkDirty()
    {
        if (_suppressDirty)
        {
            return;
        }

        IsDirty = true;
        SaveStatus = "有未保存的更改";
        Validate();
    }

    private void Validate()
    {
        var errors = new List<string>();

        if (!TryParseDimension(WindowWidthText, 320, 7680, out _))
        {
            errors.Add("窗口宽度需为 320–7680 的整数");
        }

        if (!TryParseDimension(WindowHeightText, 240, 4320, out _))
        {
            errors.Add("窗口高度需为 240–4320 的整数");
        }

        if (MinMemoryMb > MaxMemoryMb)
        {
            errors.Add("最小内存不能大于最大内存");
        }

        if (DownloadThreads < DownloadThreadsMinimum || DownloadThreads > DownloadThreadsMaximum)
        {
            errors.Add($"下载并发需在 {DownloadThreadsMinimum}–{DownloadThreadsMaximum} 之间");
        }

        // 只校验「自定义」模式下的地址形态：地址写坏时不允许保存，
        // 否则 Core 会把模式回落成 system，用户看到的选中项在保存后突然变成「跟随系统」。
        if (IsProxyCustom && !ProxyAddressParser.TryParse(ProxyAddress, out _))
        {
            errors.Add("自定义代理地址无效，应形如 http://127.0.0.1:7890");
        }

        // 只做语法校验；目录是否存在、是否可写由保存时的 ValidateGameDirectory 探测（会创建目录）。
        if (!string.IsNullOrWhiteSpace(GameDirectory) && !TryNormalize(GameDirectory, out _, out var directoryError))
        {
            errors.Add(directoryError ?? "游戏目录路径非法");
        }

        ValidationMessage = errors.Count == 0 ? null : string.Join("；", errors);
        HasValidationError = errors.Count > 0;
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasValidationErrorChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnIsSavingChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    private static bool TryParseDimension(string? text, int min, int max, out int value)
    {
        value = 0;
        return int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
               && value >= min
               && value <= max;
    }

    private static class SettingsIcons
    {
        public const string General =
            "M4 8h5M13 8h7M4 16h9M17 16h3M11 5v6M15 13v6";

        public const string Java =
            "M6 8h12v4a6 6 0 0 1-12 0zM6 8V6h12v2M9 3v2M15 3v2M5 20h14";

        public const string Game =
            "M7 8h10a4 4 0 0 1 4 4v2a4 4 0 0 1-4 4h-1l-2-2H10l-2 2H7a4 4 0 0 1-4-4v-2a4 4 0 0 1 4-4ZM9 11v4M7 13h4M16.5 12h.01M18.5 14h.01";

        public const string Download =
            "M12 3v11m0 0 4.5-4.5M12 14l-4.5-4.5M4 17v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3";

        public const string Content =
            "M12 3 3.5 7.5v9L12 21l8.5-4.5v-9L12 3ZM3.5 7.5 12 12l8.5-4.5M12 12v9";

        public const string Appearance =
            "M12 21a9 9 0 1 1 0-18c4.5 0 8 3 8 6.5 0 2.5-2 4-4 4h-1.6c-1.2 0-2 .9-2 2 0 1 .8 1.7.8 2.6 0 1.6-1.2 2.9-1.2 2.9ZM7.5 10h.01M10 7h.01M14.5 7.5h.01";

        public const string Storage =
            "M4 8.5 5.8 5.5h12.4L20 8.5V16a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2ZM4 12h16M8 15.5h.01M11.5 15.5h4.5";

        public const string About =
            "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18ZM12 10.5v5.5M12 7.5h.01";
    }
}

/// <summary>设置页左侧分区导航项。</summary>
public sealed partial class SettingsSection : ObservableObject
{
    public SettingsSection(string key, string title, string iconData)
    {
        Key = key;
        Title = title;
        IconData = iconData;
    }

    public string Key { get; }

    public string Title { get; }

    /// <summary>24×24 视口的描边图标路径。</summary>
    public string IconData { get; }

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// 存储分区里的一张类别卡片。把 Core 的 <see cref="StorageCategoryUsage"/> 翻成可直接绑定的字段，
/// 避免 XAML 里做 null 判断与字节换算（编译绑定只校验路径，不校验格式化逻辑）。
/// </summary>
public sealed partial class StorageCategoryRow : ObservableObject
{
    private readonly StorageCategoryUsage _usage;

    public StorageCategoryRow(StorageCategoryUsage usage) => _usage = usage;

    /// <summary>稳定键，清理时按它下发，不用显示名。</summary>
    public string Key => _usage.Key;

    public string DisplayName => _usage.DisplayName;

    /// <summary>绝对路径，卡片上用小号等宽字显示，方便用户核对到底删的是哪。</summary>
    public string Path => _usage.Path;

    public long TotalBytes => _usage.TotalBytes;

    public string SizeText => Conv.FormatSize(_usage.TotalBytes);

    public string FileCountText => _usage.FileCount == 0 ? "没有文件" : $"{_usage.FileCount} 个文件";

    /// <summary>最旧文件的最后写入时间。没有文件时给一句明确说明，而不是留空。</summary>
    public string OldestText => _usage.OldestWriteTime is { } oldest
        ? $"最旧：{oldest.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)}"
        : "最旧：—";

    public bool CanClean => _usage.CanClean;

    /// <summary>清理说明。文案由 Core 提供，界面不各写一套口径。</summary>
    public string CleanHint => _usage.CleanHint;

    /// <summary>空类别不给勾选框，避免「勾了空项再点清理」这种无效操作。</summary>
    public bool HasContent => _usage.TotalBytes > 0 || _usage.FileCount > 0;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Java 检测结果在界面上的展示包装，避免每次绑定都拼字符串。</summary>
public sealed class JavaOption
{
    private readonly JavaInfo _info;

    public JavaOption(JavaInfo info) => _info = info;

    public JavaInfo Info => _info;

    public string Title => $"Java {_info.MajorVersion}";

    public string Version => string.IsNullOrWhiteSpace(_info.Version) ? "版本未知" : _info.Version;

    /// <summary>来源是否为用户手动添加。界面上用它打「手动」徽标。</summary>
    public bool IsManual => string.Equals(_info.Source, "manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>来源的中文说明。<see cref="JavaInfo.Source"/> 的取值是 Core 的约定，界面不直接显示英文。</summary>
    public string SourceLabel => _info.Source switch
    {
        "manual" => "手动添加",
        "cache" => "缓存结果",
        "registry" => "注册表",
        "bundled" => "随游戏安装",
        _ => "扫描发现",
    };

    public string Meta =>
        $"{(_info.Vendor.Length > 0 ? _info.Vendor : "未知供应商")} · {(_info.Is64Bit ? "64 位" : "32 位")} · {SourceLabel}";

    public string Path => _info.Path;
}

/// <summary>
/// 「已忽略」列表里的一项。这里只有路径：条目被移出候选时，它的版本信息已经随探测结果一起丢掉了，
/// 靠 JavaService 重新探测才能拿回来——恢复按钮做的就是这件事。
/// </summary>
public sealed class ExcludedJavaOption
{
    public ExcludedJavaOption(string path) => Path = path;

    public string Path { get; }

    /// <summary>路径末两段（<c>jdk-17\bin\java.exe</c>），折叠区里比全路径好认。</summary>
    public string DisplayName
    {
        get
        {
            try
            {
                var parent = System.IO.Path.GetDirectoryName(Path);
                var bin = parent is null ? null : System.IO.Path.GetFileName(parent);
                var file = System.IO.Path.GetFileName(Path);
                return string.IsNullOrEmpty(bin) ? file : $"{bin}\\{file}";
            }
            catch (ArgumentException)
            {
                return Path;
            }
        }
    }
}

/// <summary>Java 下载下拉框的一项。</summary>
public sealed class JavaDownloadOption
{
    public JavaDownloadOption(int majorVersion, string label, string requirement)
    {
        MajorVersion = majorVersion;
        Label = label;
        Requirement = requirement;
    }

    public int MajorVersion { get; }

    public string Label { get; }

    /// <summary>哪个 Minecraft 版本线需要它。</summary>
    public string Requirement { get; }

    public string Display => $"{Label} · {Requirement}";
}

/// <summary>
/// 版本隔离策略在设置页的一张卡片。
/// 说明文字统一取自 <see cref="IsolationPolicyResolver.Describe"/>，避免 Core 与界面各写一套。
/// </summary>
public sealed partial class IsolationOption : ObservableObject
{
    public IsolationOption(IsolationPolicyKind kind, string title, bool isRecommended = false)
    {
        Kind = kind;
        Title = title;
        IsRecommended = isRecommended;
        Description = IsolationPolicyResolver.Describe(kind);
    }

    public IsolationPolicyKind Kind { get; }

    public string Title { get; }

    /// <summary>Core 给出的策略说明，用于卡片正文。</summary>
    public string Description { get; }

    public bool IsRecommended { get; }

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// 设置页里「多选一」卡片的一条，取值为字符串（启动后可见性、进程优先级共用）。
/// 用一个通用类型而不是每种选择各写一个类：这些选项除了取值与文案没有别的差异。
/// </summary>
public sealed partial class ChoiceOption : ObservableObject
{
    public ChoiceOption(string value, string title, string description)
    {
        Value = value;
        Title = title;
        Description = description;
    }

    /// <summary>写进 <c>settings.json</c> 的取值。</summary>
    public string Value { get; }

    public string Title { get; }

    public string Description { get; }

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// JVM 预设在下拉与编辑框里的一行。<see cref="Id"/> 为 null 的是「默认」哨兵行，
/// 不参与保存——用它而不是「没有选中项」来表达默认，界面才不会显示成空白。
/// </summary>
public sealed partial class JvmPresetRow : ObservableObject
{
    public const string DefaultDisplayName = "默认（使用下面的自定义参数）";

    public JvmPresetRow(string? id, string name, string arguments)
    {
        Id = id;
        Name = name;
        Arguments = arguments;
    }

    /// <summary>持久化用的标识；null 表示这是默认哨兵行。</summary>
    public string? Id { get; }

    public bool IsDefault => Id is null;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    /// <summary>下拉里显示的名字；空名给占位，免得这一行看起来像消失了。</summary>
    public string DisplayName => IsDefault
        ? DefaultDisplayName
        : string.IsNullOrWhiteSpace(Name) ? "未命名预设" : Name.Trim();

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}

/// <summary>
/// 连通性测试的一行结果。Core 只给枚举与数值，文案与展示顺序在这里决定，
/// 这样「网络层加一种新失败分类」不会把中文字符串塞进 Core。
/// </summary>
public sealed class ProxyProbeRow
{
    private readonly NetworkProbeResult _result;

    public ProxyProbeRow(NetworkProbeResult result) => _result = result;

    public string Name => _result.Name;

    public bool IsOk => _result.Ok;

    /// <summary>耗时。失败时也是有用的信息：200ms 的连接拒绝与 12 秒的超时指向不同问题。</summary>
    public string Elapsed => $"{_result.Elapsed.TotalMilliseconds:0} ms";

    /// <summary>一句话结论。</summary>
    public string Verdict => _result.Ok
        ? $"可用 · HTTP {_result.StatusCode}"
        : _result.Failure switch
        {
            NetworkProbeFailure.Dns => "DNS 解析失败",
            NetworkProbeFailure.Refused => "连接被拒绝",
            NetworkProbeFailure.Timeout => "超时",
            NetworkProbeFailure.ProxyAuth => "代理需要认证（407）",
            NetworkProbeFailure.Tls => "TLS 握手失败",
            NetworkProbeFailure.Http => $"服务端返回 {_result.StatusCode}",
            _ => "请求失败",
        };

    /// <summary>第二行：地址 + 技术细节，便于把结果贴给别人排查。</summary>
    public string Detail => string.IsNullOrWhiteSpace(_result.Detail)
        ? _result.Url
        : $"{_result.Url} · {_result.Detail}";
}
