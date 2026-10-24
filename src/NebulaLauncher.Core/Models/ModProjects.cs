namespace NebulaLauncher.Core.Models;

/// <summary>
/// 本地模组文件的「在线身份」：它属于资源站的哪个项目、叫什么、是什么。
///
/// <para/>
/// 与 <see cref="ModUpdateInfo"/> 的区别不是重复而是分工：
/// <see cref="ModUpdateInfo"/> 回答「有没有新版本」，这个类回答「这是什么东西」。
/// 两者共用同一次哈希反查，但缓存与失效策略不同——项目名与译名几乎不变，
/// 更新状态则必须实时。
/// </summary>
public sealed class ModProjectInfo
{
    public required ContentItem Item { get; init; }

    /// <summary>命中的数据源；<c>null</c> 表示两个源都没收录。</summary>
    public ContentSourceKind? Source { get; init; }

    /// <summary>项目 Id（Modrinth 的 base62 id / CurseForge 的数字 modId）。</summary>
    public string? ProjectId { get; init; }

    /// <summary>资源站 slug。译名表按它查中文名，所以必须留着。</summary>
    public string? Slug { get; init; }

    /// <summary>资源站上的原名（通常是英文）。</summary>
    public string? Title { get; init; }

    /// <summary>MC 百科译名。离线或表里没有时为 <c>null</c>，界面回退到 <see cref="Title"/>。</summary>
    public string? ChineseName { get; init; }

    /// <summary>资源站摘要（英文，已去掉换行）。</summary>
    public string? Description { get; init; }

    public string? Authors { get; init; }

    /// <summary>下载并缓存到本地的图标路径；下载失败为 <c>null</c>。</summary>
    public string? IconPath { get; init; }

    /// <summary>项目网页地址，用于卡片右键「打开项目主页」。</summary>
    public string? WebUrl { get; init; }

    /// <summary>是否找到了对应的在线项目。</summary>
    public bool Identified => Source is not null;
}

/// <summary>一次「补全模组在线信息」的汇总。</summary>
public sealed class ModProjectLookupResult
{
    public IReadOnlyList<ModProjectInfo> Items { get; init; } = Array.Empty<ModProjectInfo>();

    public int Total { get; init; }

    /// <summary>识别成功的条目数（含命中缓存）。</summary>
    public int Matched { get; init; }

    /// <summary>整体失败原因（例如两个数据源都不可用）；逐条缺失不算失败，只是未识别。</summary>
    public string? Error { get; init; }

    public static ModProjectLookupResult Empty { get; } = new();
}
