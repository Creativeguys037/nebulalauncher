namespace NebulaLauncher.Core.Models;

/// <summary>Minecraft 版本发行渠道。</summary>
public enum VersionChannel
{
    Release = 0,
    Snapshot = 1,
    OldBeta = 2,
    OldAlpha = 3,

    /// <summary>
    /// 愚人节版。Mojang 在清单里把它标成 <c>snapshot</c>，但 PCL2 单列一档
    /// （<c>PageDownloadInstall.xaml.vb:452-478</c>）：按版本 id 白名单与 4 月 1 日发布日期识别。
    /// </summary>
    Special = 4,
}

/// <summary>模组加载器种类。Vanilla 表示未装任何加载器。</summary>
public enum LoaderKind
{
    Vanilla = 0,
    Fabric = 1,
    Quilt = 2,
    Forge = 3,
    NeoForge = 4,
}

/// <summary>远程版本清单中的一条（来自 Mojang version_manifest_v2.json）。</summary>
public sealed class RemoteVersion
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public required string Url { get; init; }

    public DateTimeOffset ReleaseTime { get; init; }

    public VersionChannel Channel => VersionChannelClassifier.Classify(Id, Type, ReleaseTime);
}

/// <summary>
/// 版本清单里一条记录的发行渠道判定。照抄 PCL2 <c>PageDownloadInstall.xaml.vb:452-478</c>，
/// 多出两件事：Mojang 把少数快照误标成 <c>snapshot</c> 的类型纠偏，以及愚人节版单独成档。
///
/// 单独抽成纯函数是为了让「清单渠道」「本地已装渠道」「服务器版本下拉」三处能用同一判据——
/// 三处各写一套时，同一个版本在不同页面会显示成不同渠道。
/// </summary>
public static class VersionChannelClassifier
{
    /// <summary>
    /// Mojang 清单里 4 月 1 日发布的版本。值为版本 id 的小写形态。
    ///
    /// 为什么要有白名单：只按日期判会把真正的 4/1 快照也算进去，而只按 id 判会漏掉
    /// 未来新增的愚人节版本——PCL2 两条都用（先白名单、再按发布日期兜底），这里保持一致。
    /// </summary>
    private static readonly HashSet<string> AprilFoolsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "20w14infinite",
        "20w14∞",
        "3d shareware v1.34",
        "1.rv-pre1",
        "15w14a",
        "2.0",
        "22w13oneblockatatime",
        "23w13a_or_b",
        "24w14potato",
        "25w14craftmine",
        "26w14a",
    };

    /// <summary>
    /// 判定一条清单记录的渠道。
    /// </summary>
    /// <param name="id">版本 id，例如 <c>1.20.1</c>、<c>24w14potato</c>。</param>
    /// <param name="type">清单里的 <c>type</c> 字段：release / snapshot / old_beta / old_alpha。</param>
    /// <param name="releaseTime">清单里的 <c>releaseTime</c>（UTC）。</param>
    public static VersionChannel Classify(string? id, string? type, DateTimeOffset releaseTime)
    {
        switch (type)
        {
            case "release":
                return VersionChannel.Release;
            case "special":
                return VersionChannel.Special;
            case "old_beta":
                return VersionChannel.OldBeta;
            case "old_alpha":
                return VersionChannel.OldAlpha;
            case "snapshot":
                // Mojang 把 1.x 的预发布 / 候选版标成 snapshot，但它们在清单里确实是正式版序列
                // （PCL2 :457-463 把它们改回 release）。combat / rc / experimental / pre 四类除外。
                if (IsMisclassifiedRelease(id))
                {
                    return VersionChannel.Release;
                }

                if (id is not null && AprilFoolsIds.Contains(id))
                {
                    return VersionChannel.Special;
                }

                return IsAprilFirst(releaseTime) ? VersionChannel.Special : VersionChannel.Snapshot;
            default:
                // 未知 type（含旧清单的缺失字段）按正式版处理：比归到「远古版」安全，
                // 不会让用户以为装的是 2011 年的版本。
                return VersionChannel.Release;
        }
    }

    /// <summary>
    /// <c>1.</c> 开头、但不是 combat / rc / experimental / pre 的快照，是 Mojang 误标的正式版。
    /// 判断顺序不能反：先排除这四类，再看前缀。
    /// </summary>
    private static bool IsMisclassifiedRelease(string? id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("1.", StringComparison.Ordinal))
        {
            return false;
        }

        return !id.Contains("combat", StringComparison.OrdinalIgnoreCase)
            && !id.Contains("rc", StringComparison.OrdinalIgnoreCase)
            && !id.Contains("experimental", StringComparison.OrdinalIgnoreCase)
            && !id.Contains("pre", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 发布日期是不是 4 月 1 日。用 UTC+2 而不是 UTC：愚人节版本在欧洲时间 4/1 上午发布，
    /// 换算成 UTC 会落到 3 月 31 日（PCL2 同样用 <c>AddHours(2)</c>）。
    /// </summary>
    private static bool IsAprilFirst(DateTimeOffset releaseTime)
    {
        var local = releaseTime.ToUniversalTime().AddHours(2);
        return local.Month == 4 && local.Day == 1;
    }
}

/// <summary>本地已安装的一个版本目录。</summary>
public sealed class VersionInfo
{
    /// <summary>版本目录名，同时是启动时的 versionName。</summary>
    public required string Name { get; init; }

    public VersionChannel Channel { get; init; } = VersionChannel.Release;

    /// <summary>该版本继承自哪个原版版本。加载器版本会指向它。</summary>
    public string? InheritsFrom { get; init; }

    public LoaderKind Loader { get; init; } = LoaderKind.Vanilla;

    /// <summary>加载器自身版本号，例如 <c>0.16.9</c>；原版为空。</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>该版本解析后的原版 Minecraft 版本号，用于模组兼容性判断。</summary>
    public string? MinecraftVersion { get; init; }

    public DateTimeOffset? LastPlayedAt { get; init; }

    public long TotalPlayTimeSeconds { get; init; }

    /// <summary>版本目录占用字节数；-1 表示未统计。</summary>
    public long SizeOnDisk { get; init; } = -1;

    /// <summary>JSON 解析失败等异常信息；非空说明该版本不可用。</summary>
    public string? BrokenReason { get; init; }
}

/// <summary>Fabric / Quilt / Forge 加载器的可选安装版本。</summary>
public sealed class LoaderVersion
{
    public required string Version { get; init; }

    public bool Stable { get; init; }

    public string? GameVersion { get; init; }

    /// <summary>
    /// 是否为加载器官方标记的「推荐版」。
    ///
    /// 只有 Forge 有这个概念：<c>files.minecraftforge.net/.../promotions_slim.json</c> 的
    /// <c>&lt;mc&gt;-recommended</c>（PCL2 <c>ModDownloadLib.vb:1351-1361</c> 把它单独插在列表首位）。
    /// NeoForge 没有官方推荐列表，Fabric / Quilt 的 <see cref="Stable"/> 也来自另一个字段，
    /// 所以这个标记**只对 Forge 可能为 true**——不要给别的加载器编一个推荐值。
    /// </summary>
    public bool IsRecommended { get; init; }

    /// <summary>
    /// 本机已经装有这个加载器版本（<c>versions/&lt;合成名&gt;</c> 已存在）。
    ///
    /// 放在列表里的用途与 PCL2 组件修改器的「还原」同类：Forge 的上游只公开推荐版，
    /// 没有这个标记时用户无法把实例切回自己已经装过的旧构建。
    /// </summary>
    public bool IsInstalled { get; init; }
}
