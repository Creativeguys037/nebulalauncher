using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Mods;

/// <summary>
/// Modrinth 依赖展开器：把「用户点选的这一个版本」翻译成「这次要下哪些文件」。
///
/// 为什么单独成类而不是塞进 <see cref="ModrinthService"/>：展开逻辑只需要「按项目查版本」这一个能力，
/// 抽成委托后可以用内存字典驱动，不必为每个场景造 HTTP 响应；服务侧只负责把
/// <c>GetVersionsAsync</c> 接进来。
///
/// 规则（docs/EXTENSIONS.md 条目 8）：
/// <list type="bullet">
/// <item><c>required</c> 递归展开，按 projectId 去重防环，深度上限 <see cref="MaxDepth"/>；</item>
/// <item><c>optional</c> 只收集不递归；<c>incompatible</c> 进 Blocked；<c>embedded</c> 跳过；</item>
/// <item>只给 <c>version_id</c> 的依赖记 Warning 不阻断——定位项目要额外请求，递归时会成倍放大请求量。</item>
/// </list>
/// </summary>
public sealed class ModrinthInstallPlanner
{
    /// <summary>依赖链深度上限。到顶说明依赖图已经病态，继续展开只会放大请求量与确认清单。</summary>
    public const int MaxDepth = 8;

    private readonly Func<string, string?, LoaderKind, CancellationToken, Task<IReadOnlyList<ModrinthVersion>>> _getVersions;
    private readonly IAppLog? _log;

    /// <param name="getVersions">按项目 Id 查可用版本，返回已按发布时间降序的列表（取第一个即可）。</param>
    /// <param name="log">可选日志；查询失败时留原始异常用。</param>
    public ModrinthInstallPlanner(
        Func<string, string?, LoaderKind, CancellationToken, Task<IReadOnlyList<ModrinthVersion>>> getVersions,
        IAppLog? log = null)
    {
        _getVersions = getVersions ?? throw new ArgumentNullException(nameof(getVersions));
        _log = log;
    }

    /// <summary>
    /// 展开 <paramref name="root"/> 的全部依赖。
    ///
    /// 网络 / 解析失败只降级成 <see cref="ModrinthInstallPlan.Warnings"/>，不让整个计划失败：
    /// 用户至少还能装本体，缺哪个前置由提示条说清楚。
    /// </summary>
    public async Task<ModrinthInstallPlan> PlanAsync(
        ModrinthVersion root,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        var state = new PlanState(root, gameVersion, loader);

        await ExpandAsync(root, 0, state, cancellationToken).ConfigureAwait(false);

        return new ModrinthInstallPlan
        {
            Root = root,
            Required = state.Required,
            Optional = state.Optional,
            Blocked = state.Blocked,
            Warnings = state.Warnings,
        };
    }

    private async Task ExpandAsync(
        ModrinthVersion version,
        int depth,
        PlanState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hasRequired = version.Dependencies.Any(dependency => dependency.Type == ModrinthDependencyType.Required);

        // 深度按「层」算：根版本是 0，第一级前置是 1。到顶只提示，不丢掉已经收集到的部分。
        if (hasRequired && depth >= MaxDepth)
        {
            state.Warnings.Add($"「{LabelOf(version)}」的依赖链超过 {MaxDepth} 层，已停止继续展开。");
            return;
        }

        foreach (var dependency in version.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (dependency.Type)
            {
                case ModrinthDependencyType.Embedded:
                    // 已经内嵌进父模组，再单独下一个只会让 mods/ 里多一份重复实现。
                    continue;

                case ModrinthDependencyType.Incompatible:
                    state.Blocked.Add($"「{LabelOf(version)}」声明与「{LabelOf(dependency)}」互斥：同时安装会崩在加载器阶段。");
                    continue;

                case ModrinthDependencyType.Optional:
                    await CollectOptionalAsync(version, dependency, state, cancellationToken).ConfigureAwait(false);
                    continue;

                default:
                    await ExpandRequiredAsync(version, dependency, depth, state, cancellationToken).ConfigureAwait(false);
                    continue;
            }
        }
    }

    private async Task ExpandRequiredAsync(
        ModrinthVersion parent,
        ModrinthDependency dependency,
        int depth,
        PlanState state,
        CancellationToken cancellationToken)
    {
        var projectId = dependency.ProjectId;

        if (string.IsNullOrWhiteSpace(projectId))
        {
            WarnUnresolved(parent, dependency, state, isRequired: true);
            return;
        }

        // 按 projectId 去重就是防环：A→B→A 时根版本已登记，这里直接返回，不会无限递归。
        if (!state.Visited.Add(projectId))
        {
            return;
        }

        var child = await ResolveAsync(projectId, dependency, state, cancellationToken).ConfigureAwait(false);

        if (child is null)
        {
            return;
        }

        await ExpandAsync(child, depth + 1, state, cancellationToken).ConfigureAwait(false);

        // 后序追加：前置自己的前置先入列表，落盘顺序天然满足「前置在前」。
        state.Required.Add(child);

        // 同一个项目可能先作为可选依赖被收集过，现在确认是必需：从可选里摘掉，避免清单里出现两次。
        state.Optional.RemoveAll(version => string.Equals(version.ProjectId, child.ProjectId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task CollectOptionalAsync(
        ModrinthVersion parent,
        ModrinthDependency dependency,
        PlanState state,
        CancellationToken cancellationToken)
    {
        var projectId = dependency.ProjectId;

        if (string.IsNullOrWhiteSpace(projectId))
        {
            WarnUnresolved(parent, dependency, state, isRequired: false);
            return;
        }

        // 可选依赖只登记不递归：它的前置装不装由用户自己决定，链路一长确认清单就没法读。
        if (!state.Visited.Add(projectId))
        {
            return;
        }

        var child = await ResolveAsync(projectId, dependency, state, cancellationToken).ConfigureAwait(false);

        if (child is not null)
        {
            state.Optional.Add(child);
        }
    }

    private async Task<ModrinthVersion?> ResolveAsync(
        string projectId,
        ModrinthDependency dependency,
        PlanState state,
        CancellationToken cancellationToken)
    {
        var versions = await FetchAsync(projectId, state, cancellationToken).ConfigureAwait(false);
        var first = versions.Count > 0 ? versions[0] : null;

        if (first is null)
        {
            var gameVersionText = string.IsNullOrWhiteSpace(state.GameVersion)
                ? "任意 Minecraft 版本"
                : $"Minecraft {state.GameVersion}";

            state.Warnings.Add($"没有找到「{LabelOf(dependency)}」在 {gameVersionText} / {LoaderLabelOf(state.Loader)} 下的可用版本，已跳过自动安装。");
        }

        return first;
    }

    private async Task<IReadOnlyList<ModrinthVersion>> FetchAsync(
        string projectId,
        PlanState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var versions = await _getVersions(projectId, state.GameVersion, state.Loader, cancellationToken).ConfigureAwait(false);

            // Quilt 能直接加载 Fabric 模组，而 fabric-api 这类最常见的前置只发 fabric 构建；
            // 只按 quilt 过滤会把它们漏成「没有可用版本」。
            if (versions.Count == 0 && state.Loader == LoaderKind.Quilt)
            {
                versions = await _getVersions(projectId, state.GameVersion, LoaderKind.Fabric, cancellationToken).ConfigureAwait(false);
            }

            return versions;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 查不到依赖不等于这次安装失败：本体照装，缺前置的原因进 Warnings。
            _log?.Warn($"查询 Modrinth 依赖版本失败：{projectId}", ex);
            return Array.Empty<ModrinthVersion>();
        }
    }

    private static void WarnUnresolved(
        ModrinthVersion parent,
        ModrinthDependency dependency,
        PlanState state,
        bool isRequired)
    {
        var kind = isRequired ? "前置" : "可选依赖";
        state.Warnings.Add($"「{LabelOf(parent)}」的{kind}「{LabelOf(dependency)}」只给出了版本 ID，无法定位项目，已跳过自动安装。");
    }

    private static string LabelOf(ModrinthVersion version)
    {
        if (!string.IsNullOrWhiteSpace(version.Name))
        {
            return version.Name;
        }

        return string.IsNullOrWhiteSpace(version.VersionNumber) ? version.ProjectId : version.VersionNumber;
    }

    private static string LabelOf(ModrinthDependency dependency)
    {
        if (!string.IsNullOrWhiteSpace(dependency.FileName))
        {
            return dependency.FileName;
        }

        if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
        {
            return dependency.ProjectId;
        }

        return string.IsNullOrWhiteSpace(dependency.VersionId) ? "未知依赖" : dependency.VersionId;
    }

    private static string LoaderLabelOf(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "原版",
    };

    /// <summary>一次展开的可变状态。放一起是为了让递归方法不必挂六个 out / ref 参数。</summary>
    private sealed class PlanState
    {
        public PlanState(ModrinthVersion root, string? gameVersion, LoaderKind loader)
        {
            GameVersion = gameVersion;
            Loader = loader;

            // 根版本算作已访问：子依赖反过来指向根时不会被重复展开。
            Visited.Add(root.ProjectId);
        }

        public string? GameVersion { get; }

        public LoaderKind Loader { get; }

        /// <summary>OrdinalIgnoreCase：Modrinth 的 projectId 是 base62，大小写不同仍是同一个项目。</summary>
        public HashSet<string> Visited { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ModrinthVersion> Required { get; } = new();

        public List<ModrinthVersion> Optional { get; } = new();

        public List<string> Blocked { get; } = new();

        public List<string> Warnings { get; } = new();
    }
}
