using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Services;

/// <summary>
/// 把「全局设置 + 实例设置 + 一次启动目标」合成为一份 <see cref="LaunchOptions"/>。
///
/// 为什么要抽出来：这个仓库里已经有四个启动入口（首页、版本页启动、版本页导出脚本、
/// 服务器页加入服务器），每个入口都手写一遍启动参数对象。
/// 同一个字段漏一个入口，用户看到的就是「同一个设置在 A 页生效、B 页不生效」，
/// 而且这类偏差极难自查——它不报错，只是行为悄悄变了。事实上已经发生过三次：
/// <c>RelaxForgeChecks</c> 漏过一个入口、<c>VersionArgumentInfo</c> 漏了首页、
/// 服务器页整段实例上下文缺失（用户在隔离实例里配的模组从服务器页进服全部失效）。
/// 所以合成规则只允许存在一份，且只允许出现在本文件的 <see cref="Build"/> 里。
///
/// 为什么放 App 层而不是 Core：输入的 <see cref="AppSettings"/> 是启动器的用户配置，
/// Core 的 <see cref="LaunchOptions"/> 只认识「这次启动要什么」，不认识配置从哪来；
/// 把两者的合成放在 Core 会让 Core 反过来依赖「设置页当前值」这个概念。
///
/// 命名空间与其它 App/Services 下的类型保持一致（<c>NebulaLauncher.Services</c>），
/// 但 ViewModel 已经写过 <c>using NebulaLauncher.Services;</c>，不需要额外改动。
/// </summary>
internal static class LaunchOptionsFactory
{
    /// <summary>
    /// 合成一次启动的完整参数。
    /// </summary>
    /// <param name="account">已经校验过的账户（令牌刷新由调用方负责）。</param>
    /// <param name="versionName">要启动的版本目录名。</param>
    /// <param name="resolution">
    /// 启动目标解析结果。它同时给出 <see cref="InstanceResolution.GameDirectory"/> 与实例，
    /// **目录必须来自这里**：服务器页曾经不带目录，于是永远启动全局 <c>.minecraft</c>，
    /// 既丢掉实例的模组，也绕过了按目录加的实例运行锁。
    /// </param>
    /// <param name="settings">全局设置；<c>null</c> 时全部走默认值（与设置页初始状态一致）。</param>
    /// <param name="serverAddress">
    /// 覆盖实例里保存的服务器地址。服务器页要把「这次点的那个服务器」传进来；
    /// 不传则沿用实例自己的记录。
    /// </param>
    public static LaunchOptions Build(
        Account account,
        string versionName,
        InstanceResolution resolution,
        AppSettings? settings,
        string? serverAddress = null)
    {
        var instance = resolution.Instance;

        return new LaunchOptions
        {
            Account = account,
            VersionName = versionName,

            // 目录取解析结果而不是实例目录/全局目录二选一：解析已经涵盖了
            // 「不隔离的实例也用全局目录」与「自动新建实例」两种情况。
            GameDirectory = resolution.GameDirectory,

            // 实例级设置覆盖全局（PCL2 的 VersionAdvance* / VersionArgument* 语义）：
            // null 表示「跟随全局」，因此这里用「实例值非空才取」的写法，不能直接 ?? 链，
            // 否则空字符串的实例值会被当成有效值把全局配置抹掉。
            JavaPath = FirstNonEmpty(instance?.JavaPath, settings?.JavaPath),

            // 加载器版本：Java 上限判定要它才判得出「1.19.4 Forge 45.0.40 只能到 Java 19」。
            //
            // 取解析结果而不是 instance?.LoaderVersion：全局目录（不开隔离）根本没有实例对象，
            // 但那个目录照样可能是 1.19.4-forge-45.0.40 —— EffectiveLoaderVersion 正是为这种情况
            // 准备的（解析时从版本目录名读出来的值）；实例有值时它优先。
            //
            // 这里也是「为什么走工厂而不是给 LaunchAsync 加参数」的答案：工厂是 LaunchOptions 的
            // 唯一构造点，加一行的效果与在四个启动入口各传一个参数相同，但不会漏。
            LoaderVersion = resolution.EffectiveLoaderVersion,
            MinMemoryMb = settings?.MinMemoryMb ?? 512,
            MaxMemoryMb = instance?.MaxMemoryMb ?? settings?.MaxMemoryMb ?? 4096,
            WindowWidth = settings?.WindowWidth ?? 854,
            WindowHeight = settings?.WindowHeight ?? 480,
            Fullscreen = settings?.Fullscreen ?? false,

            // JVM 参数走预设解析：实例显式值（含空串=「这个实例不要额外参数」）优先，
            // 其次设置页选中的预设，最后退回全局原文。不能再套 FirstNonEmpty，
            // 那会把实例的空串当成「没设置」而错误回落到预设。
            ExtraJvmArguments = JvmPresetResolver.ResolveForInstance(settings, instance?.JvmArguments),
            ExtraGameArguments = FirstNonEmpty(instance?.GameArguments, settings?.ExtraGameArguments),
            ServerAddress = FirstNonEmpty(serverAddress, instance?.ServerAddress),
            GcKind = instance?.GcKind ?? GcKind.Auto,

            // 窗口标题与最大化是纯展示项，实例没配就用全局模板。
            WindowTitle = FirstNonEmpty(instance?.WindowTitle, settings?.GameWindowTitle),
            MaximizeWindow = settings?.MaximizeGameWindow ?? false,

            // 实例显式设了内存就不自动覆盖：用户给某个大整合包单独调过值，
            // 被「自动推荐」悄悄改掉是最难排查的一类配置问题。
            AutoMaxMemory = (settings?.AutoMaxMemory ?? false) && instance?.MaxMemoryMb is null,

            LaunchBeforeCommand = instance?.LaunchBeforeCommand,
            LaunchAfterCommand = instance?.LaunchAfterCommand,
            WaitForLaunchCommands = instance?.WaitForLaunchCommands ?? true,

            // 下面四项是 PCL2 的 LaunchArgumentPriority / ToolHelpChinese / ModCrash 归档 /
            // 「放宽 Forge 校验」，都是全局开关，实例不覆盖：它们描述的是
            // 「启动器怎么配合游戏」，与具体实例无关。
            ProcessPriority = GameProcessPriorityParser.Parse(settings?.GameProcessPriority),
            AutoChineseLanguage = settings?.AutoChineseLanguage ?? true,
            ArchiveCrashReport = settings?.ArchiveCrashReports ?? true,
            RelaxForgeChecks = settings?.RelaxForgeChecks ?? true,

            // 实例的三态覆盖：null = 跟随全局版本类型，非 null（含空串）就是实例自己的选择。
            // 这一行曾经只出现在版本页两处，首页漏了——所以现在四个入口必须共用这里。
            VersionArgumentInfo = instance?.VersionType,

            // 实例级登录服务器（PCL2 VersionServerAuthServer）同样三态，但它覆盖的是账户而不是全局设置：
            // null = 跟随账户，"" = 明确不注入 authlib-injector，其余 = 覆盖账户地址。
            // 不能套 FirstNonEmpty —— 那会把空串当成「没设置」而回落到账户，用户就再也关不掉了。
            InstanceAuthServerUrl = instance?.AuthServerUrl,
        };
    }

    /// <summary>
    /// 取第一个「真的设置了」的值。
    ///
    /// 空白串按未设置处理：用户把输入框清空后保存会留下 <c>""</c>，
    /// 直接当初有效值会把全局配置抹成空——这属于「看起来没问题、行为却变了」的坑。
    /// </summary>
    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second;
}