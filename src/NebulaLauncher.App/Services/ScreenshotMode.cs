namespace NebulaLauncher.Services;

/// <summary>
/// 无人值守截图模式。
///
/// 为什么放在应用内：用系统 API 抓窗口（PrintWindow / CopyFromScreen）在后台、
/// 被遮挡、或 DPI 缩放不一致时都会失败或抓到别的窗口。让 Avalonia 自己把视觉树
/// 渲染到 RenderTargetBitmap，不依赖窗口可见性与前台状态，结果是确定性的。
///
/// 命令行：
///   NebulaLauncher.exe --screenshot out.png [--page home] [--delay 2500]
/// 截图完成后进程自动退出。
/// </summary>
public static class ScreenshotMode
{
    public static string? OutputPath { get; set; }

    public static string? PageKey { get; set; }

    public static int DelayMs { get; set; } = 2500;

    /// <summary>
    /// 覆盖启动器数据根目录。多人/多实例并行运行时必须各用一份，
    /// 否则会并发读写同一份 accounts.json / servers.json 造成互相覆盖。
    /// </summary>
    public static string? DataDirectory { get; set; }

    /// <summary>
    /// 覆盖 .minecraft 目录。与 <see cref="DataDirectory"/> 一起使用可实现完全隔离，
    /// 截图时不会读到、也不会污染本机真实的游戏目录。
    /// 未指定但指定了 <see cref="DataDirectory"/> 时，默认落到 <c>&lt;data-dir&gt;/minecraft</c>。
    /// </summary>
    public static string? MinecraftDirectory { get; set; }

    public static bool IsActive => !string.IsNullOrWhiteSpace(OutputPath);

    /// <summary>
    /// 设置页要展开的分区键（general / java / game / download / content / appearance / storage / about）。
    /// 截图只能导航到页面，无法点击左侧分区，没有这个参数就截不到「游戏目录」这类非默认分区。
    /// 存储分区（<c>-Section storage</c>）的入口就是这里：进入后页面自己会扫一次数据目录。
    /// </summary>
    public static string? SectionKey { get; set; }

    /// <summary>窗口宽度覆盖，用于验证窄屏布局。null 表示用窗口默认尺寸。</summary>
    public static double? WindowWidth { get; set; }

    /// <summary>窗口高度覆盖。</summary>
    public static double? WindowHeight { get; set; }

    /// <summary>
    /// 截图前把页面内第一个 <see cref="Avalonia.Controls.ScrollViewer"/> 滚到这个纵向偏移。
    ///
    /// 设置页加长之后，新增项落在首屏之外；窗口高度受屏幕限制无法再拉高，
    /// 而截图模式没有滚动能力，没有这个参数就只能靠代码审查验证布局。
    /// </summary>
    public static double ScrollOffset { get; set; }

    /// <summary>
    /// 版本页截图时自动展开第一个实例的设置面板。
    /// 面板只能在点击「设置」后出现，而截图模式没有点击能力，
    /// 没有这个开关就永远验证不到面板的布局。
    /// </summary>
    public static bool OpenInstanceEditor { get; set; }

    /// <summary>
    /// 模组页截图时全选当前列表。
    ///
    /// 批量操作条在没有勾选时是禁用态，截图只能证明「条在」，证明不了「勾选后是什么样」。
    /// 与 <see cref="OpenInstanceEditor"/> 同理：截图模式没有点击能力，只能靠开关把状态摆出来。
    /// </summary>
    public static bool SelectAllMods { get; set; }

    /// <summary>
    /// 版本页截图时打开第一个实例的「完整备注」浮层。
    /// 浮层只能点卡片上的「备注」按钮出现，与 <see cref="OpenInstanceEditor"/> 同理需要开关。
    /// </summary>
    public static bool OpenInstanceNotes { get; set; }

    /// <summary>
    /// 版本页截图时打开「显示已隐藏」。
    ///
    /// 仅 <c>--screenshot</c> 模式生效：截图模式没有点击能力，而这个开关本身是 ViewModel 的瞬时状态，
    /// favorites.json 只能决定「哪些版本被隐藏」，表达不了「隐藏后再显示出来是什么样」。
    /// 与 <see cref="OpenInstanceNotes"/> 同一类截图专用开关，不是调试代码。
    /// </summary>
    public static bool ShowHidden { get; set; }

    /// <summary>
    /// 版本页截图时打开第一个版本的「组件」浮层。
    /// 浮层只能点版本行的「组件…」按钮出现，与 <see cref="OpenInstanceEditor"/> 同一类截图专用开关。
    /// </summary>
    public static bool OpenVersionComponents { get; set; }

    /// <summary>
    /// 主题覆盖：<c>dark</c> / <c>light</c> / <c>system</c>。
    ///
    /// 为什么需要覆盖而不是改 settings.json：截图要在同一份演示数据上对比两套主题，
    /// 而被截的那份 settings.json 还会被应用回写，靠改文件既脏又容易和别的协作者撞车。
    /// 命令行优先级高于设置文件（见 MainWindowViewModel.ApplyUi），空白表示不覆盖。
    /// </summary>
    public static string? Theme { get; set; }

    /// <summary>
    /// 下载页截图时预选加载器：<c>vanilla</c> / <c>fabric</c> / <c>quilt</c> / <c>forge</c> / <c>neoforge</c>。
    ///
    /// 为什么需要：加载器胶囊是 ToggleButton，只能点。而「同时安装 Fabric API」区块、
    /// 「推荐版」徽标这类新 UI 只在特定加载器下出现，截图模式没有点击能力就永远截不到。
    /// 与 <see cref="OpenVersionComponents"/> 同一类截图专用开关，不是调试代码。
    /// </summary>
    public static string? LoaderKey { get; set; }

    /// <summary>
    /// 下载页截图时预选某个 Minecraft 版本（例如 <c>1.20.1</c>）。
    ///
    /// 与 <see cref="LoaderKey"/> 配套：Forge 的「推荐版」只在部分游戏版本上有
    /// （26.x 刚发布时官方还没标），不指定就永远截到「没有推荐构建」的降级形态。
    /// </summary>
    public static string? McVersion { get; set; }

    /// <summary>
    /// 下载页截图时注入一组演示任务，并把页面摆到「有活动批次」的状态。
    ///
    /// 为什么需要：任务面板与「暂停 / 继续」按钮只有真实下载时才有内容，而截图模式
    /// 既不能点击、也不该真的联网下载（会污染演示数据、耗时不可控）。没有这个开关，
    /// 「有任务时按钮是否可用、六行任务各状态的徽标是否区分得开」就只能靠代码审查。
    /// 与 <see cref="LoaderKey"/> 同一类截图专用开关，不是调试代码。
    /// </summary>
    public static bool DownloadDemo { get; set; }

    /// <summary>
    /// 与 <see cref="DownloadDemo"/> 配套：把注入的演示批次停在「已暂停」状态。
    ///
    /// 暂停后的两件事（状态行「已暂停：在途文件完成后停止」、排队行显示「等待」）
    /// 都只在暂停期间存在，截图模式没有点击能力，只能靠这个开关把状态摆出来。
    /// </summary>
    public static bool DownloadDemoPaused { get; set; }

    /// <summary>
    /// 日志页截图时进入「跟随中 + 刚追加了几行」的状态。
    ///
    /// 为什么需要：跟随只在「文件还在增长」时才有可见效果，而截图模式既不能点击、
    /// 也没有第二个进程往日志里写。这里往演示数据目录的 logs/ 下写一个示例日志再真实跟随一次，
    /// 让状态 chip 与新行都来自真实链路而不是摆出来的布尔值。
    /// </summary>
    public static bool LogFollowDemo { get; set; }

    /// <summary>
    /// 首页截图时把页面推进「缺少 Java 25」的启动失败态。
    ///
    /// 为什么需要：真实路径要真跑一次启动、真探测本机 Java，而截图模式要确定性，
    /// 又不能真联网下载 50 MB。没有这个开关，「安装 Java N」按钮、进度行与
    /// 「已安装…重新启动」的布局只能靠代码审查。
    /// 与 <see cref="DownloadDemo"/> 同一类截图专用开关，不是调试代码。
    /// </summary>
    public static bool MissingJavaDemo { get; set; }

    /// <summary>
    /// 版本页截图时摆出「缺 Java + 安装按钮」的运行状态条。
    ///
    /// 与 <see cref="MissingJavaDemo"/> 分开是因为两者在**不同页面**：首页那段是完整的失败面板，
    /// 版本页这段是嵌入在状态条下的区块，宽度与布局约束完全不同（版本页更窄，按钮不能换行）。
    /// 同一个开关同时改两页，截图里只能看到一页的效果，另一页的布局就永远没被看过。
    /// </summary>
    public static bool MissingJavaVersionsDemo { get; set; }

    /// <summary>
    /// 下载页截图时摆出一次成功的「源测速」结论（官方 320ms · BMCLAPI 镜像 190ms · 建议用镜像）。
    ///
    /// 为什么需要：测速要真发 4 个网络请求，结果取决于当天网络，截图不可复现；
    /// 而结果行的排版（长文案是否换行、按钮是否被挤走）用眼睛看过才放心。
    /// 摆出来的值是构造样本，但文案由 Core 的 <c>SourceProbeRanking.Describe</c> 真实生成，
    /// 与线上完全同源。与 <see cref="DownloadDemo"/> 同一类截图专用开关，不是调试代码。
    /// </summary>
    public static bool SourceProbeDemo { get; set; }

    /// <summary>解析后实际生效的 .minecraft 目录；两者都未指定时为 null（即平台默认）。</summary>
    public static string? EffectiveMinecraftDirectory =>
        MinecraftDirectory
        ?? (string.IsNullOrWhiteSpace(DataDirectory) ? null : Path.Combine(DataDirectory, "minecraft"));

    /// <summary>从命令行解析启动参数。无法解析时保持默认值。</summary>
    public static void Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--screenshot" when i + 1 < args.Length:
                    OutputPath = args[++i];
                    break;
                case "--page" when i + 1 < args.Length:
                    PageKey = args[++i];
                    break;
                case "--delay" when i + 1 < args.Length && int.TryParse(args[i + 1], out var ms):
                    DelayMs = ms;
                    i++;
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    DataDirectory = args[++i];
                    break;
                case "--minecraft-dir" when i + 1 < args.Length:
                    MinecraftDirectory = args[++i];
                    break;
                case "--section" when i + 1 < args.Length:
                    SectionKey = args[++i];
                    break;
                case "--instance-editor":
                    OpenInstanceEditor = true;
                    break;
                case "--select-all-mods":
                    SelectAllMods = true;
                    break;
                case "--instance-notes":
                    OpenInstanceNotes = true;
                    break;
                case "--show-hidden":
                    ShowHidden = true;
                    break;
                case "--version-components":
                    OpenVersionComponents = true;
                    break;
                case "--theme" when i + 1 < args.Length:
                    Theme = args[++i];
                    break;
                case "--loader" when i + 1 < args.Length:
                    LoaderKey = args[++i];
                    break;
                case "--mc" when i + 1 < args.Length:
                    McVersion = args[++i];
                    break;
                case "--download-demo":
                    DownloadDemo = true;
                    break;
                case "--download-demo-paused":
                    DownloadDemo = true;
                    DownloadDemoPaused = true;
                    break;
                case "--log-follow":
                    LogFollowDemo = true;
                    break;
                case "--missing-java":
                    MissingJavaDemo = true;
                    break;

                case "--missing-java-versions":
                    MissingJavaVersionsDemo = true;
                    break;
                case "--source-probe":
                    SourceProbeDemo = true;
                    break;
                case "--scroll" when i + 1 < args.Length && double.TryParse(args[i + 1], out var offset):
                    ScrollOffset = Math.Max(0, offset);
                    i++;
                    break;
                case "--size" when i + 1 < args.Length:
                    var parts = args[++i].Split('x', 'X');
                    if (parts.Length == 2
                        && double.TryParse(parts[0], out var w)
                        && double.TryParse(parts[1], out var h)
                        && w > 0 && h > 0)
                    {
                        WindowWidth = w;
                        WindowHeight = h;
                    }

                    break;
            }
        }
    }
}
