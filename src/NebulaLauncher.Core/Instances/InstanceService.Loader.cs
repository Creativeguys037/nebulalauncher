using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Installing;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Instances;

/// <summary>
/// 实例的「原地换加载器」。
///
/// 玩家的真实诉求是「我这个 1.20.1 实例，想加装 Fabric，mods 和存档都别动」。
/// 在实现这个动作之前，唯一路径是「克隆实例 → 在新实例里重装版本 → 手动拷 mods → 切实例」。
///
/// 换加载器改的是实例的 <see cref="InstanceInfo.VersionName"/>，而实例目录就是版本目录，
/// 所以换加载器必然换游戏目录。旧的版本目录里玩家内容保持原位（PCL2 / HMCL 的版本隔离语义），
/// 成功后会摘掉旧目录的 <c>instance.json</c>，避免它被当成第二个实例。
/// </summary>
public sealed partial class InstanceService
{
    /// <inheritdoc />
    public async Task<InstanceLoaderSwitchResult> SwitchLoaderAsync(
        string instanceId,
        LoaderKind kind,
        string? loaderVersion = null,
        IProgress<DownloadProgress>? progress = null,
        string? targetMinecraftVersion = null,
        CancellationToken cancellationToken = default)
    {
        var instance = FindInstanceCopy(instanceId);

        if (instance is null)
        {
            return InstanceLoaderSwitchResult.Fail("找不到该实例，它可能已经被删除了。请刷新版本页后重试。");
        }

        if (_install is null)
        {
            return InstanceLoaderSwitchResult.Fail("安装服务未就绪，暂时无法更换加载器。");
        }

        var (minecraftVersion, currentLoader) = await ResolveInstanceVersionAsync(instance, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return InstanceLoaderSwitchResult.Fail(
                $"无法确定「{instance.Name}」绑定的 Minecraft 版本：它的版本目录「{instance.VersionName}」"
                + "可能已被删除或损坏。请先在版本页用「修复」把它补回来，再换加载器。");
        }

        // 硬约束：只能在同一个 Minecraft 版本内换加载器。
        //
        // 跨版本换（1.20.1 实例换成 1.21 的 Fabric）看起来只是换个数字，实际后果是
        // 存档里的区块数据可能不兼容、mods 全部要换一遍、config 里的版本相关键位全部对不上。
        // 那已经不是「换加载器」，而是「换游戏还复用旧目录」，拒绝并说清原因。
        if (!string.IsNullOrWhiteSpace(targetMinecraftVersion)
            && !string.Equals(targetMinecraftVersion.Trim(), minecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            return InstanceLoaderSwitchResult.Fail(
                $"只能在同一个 Minecraft 版本内更换加载器：这个实例是 {minecraftVersion}，"
                + $"不能换成 {targetMinecraftVersion.Trim()}。跨版本会让存档与现有模组全部失效——"
                + "需要新版本请新建一个实例。");
        }

        var trimmedVersion = string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim();

        // 已经是目标加载器且没指定版本：什么都不做。
        //
        // 为什么这种「同加载器 + 无版本」明确拒绝而不是「重装一遍最新版」：那是「升级加载器」，
        // 语义完全不同，而且会在用户只想打开面板看一眼时就开始一次几十 MB 的下载。
        if (kind == currentLoader && trimmedVersion is null)
        {
            return new InstanceLoaderSwitchResult
            {
                Success = true,
                AlreadyMatches = true,
                PreviousVersionName = instance.VersionName,
                NewVersionName = instance.VersionName,
                Loader = currentLoader,
                MinecraftVersion = minecraftVersion,
            };
        }

        var needsConfirmation = InstanceLoaderAdvice.RequiresModConfirmation(currentLoader, kind);

        // 实例目录就是版本目录：换加载器会换目录。先记下旧目录，成功后要把里面的
        // instance.json 摘掉，否则旧版本目录会被下次扫描当成另一个实例（幽灵实例）。
        string previousDirectory;

        try
        {
            previousDirectory = GetGameDirectoryByVersion(instance.VersionName);
        }
        catch (ArgumentException ex)
        {
            return InstanceLoaderSwitchResult.Fail(
                $"实例绑定的版本目录名不合法（「{instance.VersionName}」）：{ex.Message}");
        }

        // 装前记下「可能的」目标目录是否已存在：失败时只删本次真正新建的那一个。
        // 若目标版本早就装过（用户手动装过、或从别的实例换过来），删掉就是毁数据。
        var expectedName = PredictVersionName(kind, minecraftVersion, trimmedVersion);
        var expectedPath = expectedName is null ? null : Path.Combine(_paths.VersionsDirectory, expectedName);
        var expectedExistedBefore = expectedPath is not null && Directory.Exists(expectedPath);

        var request = new InstallRequest
        {
            MinecraftVersion = minecraftVersion,
            Loader = kind,
            LoaderVersion = trimmedVersion,

            // 刻意不填 ResultVersionName：它只影响「没有自动名字时返回什么」，
            // 而 Forge / NeoForge 的真实目录名由安装器决定（1.20.1-forge-47.2.0），
            // 强行指定会让实例指向一个根本不存在的版本目录。
        };

        string? installedName;

        try
        {
            installedName = await _install
                .InstallLoaderAsync(request, progress, overwriteExisting: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CleanupHalfInstalled(expectedName, expectedExistedBefore);
            throw;
        }
        catch (Exception ex)
        {
            CleanupHalfInstalled(expectedName, expectedExistedBefore);
            _log.Error($"换加载器失败：{instanceId} {currentLoader} → {kind}", ex);
            return InstanceLoaderSwitchResult.Fail($"更换加载器失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(installedName))
        {
            // 服务没实现 InstallLoaderAsync（走的是接口默认实现）——不能当成成功。
            CleanupHalfInstalled(expectedName, expectedExistedBefore);
            return InstanceLoaderSwitchResult.Fail(
                "安装服务没有返回装出来的版本名，无法确认更换结果。实例仍指向原来的版本。");
        }

        // 只有安装成功之后才动 instance.json。反过来说：这里的 SaveAsync 失败时
        // 磁盘上已经有了新版本目录，但实例还指向旧的——不损坏任何东西，重试即可。
        var updated = Clone(instance);
        updated.VersionName = installedName;
        updated.Loader = kind;
        updated.MinecraftVersion = minecraftVersion;
        updated.LoaderVersion = await ResolveInstalledLoaderVersionAsync(
                kind,
                installedName,
                trimmedVersion,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"换加载器已装好但写回实例失败：{instanceId}", ex);
            return InstanceLoaderSwitchResult.Fail(
                $"加载器已装好（版本 {installedName}），但写回实例配置失败：{ex.Message}"
                + "。实例仍指向原来的版本，可以重试一次换加载器。");
        }

        _log.Info(
            $"实例已换加载器：{instance.Name} ({instanceId}) {InstanceLoaderAdvice.Label(currentLoader)} → "
            + $"{InstanceLoaderAdvice.Label(kind)}，版本 {instance.VersionName} → {installedName}");

        // 旧版本目录里的玩家内容按 PCL2 / HMCL 的版本隔离语义留在原位，不自动搬。
        // 只摘掉 instance.json：它属于「实例」，继续留着会让旧目录变成另一个实例。
        if (!string.Equals(instance.VersionName, installedName, StringComparison.OrdinalIgnoreCase))
        {
            InstanceFileSystem.DeleteFileQuietly(Path.Combine(previousDirectory, "instance.json"), _log);
        }

        var consequence = InstanceLoaderAdvice.DescribeConsequence(currentLoader, kind);
        var directoryNote =
            $"实例的游戏目录已从 versions\\{instance.VersionName} 换成 versions\\{installedName}。"
            + "旧目录里的 mods / saves / config 保持原位，不会自动迁移（与 PCL2 / HMCL 的版本隔离一致）；"
            + "如果这些内容还要继续用，请把它们复制到新目录。";

        return new InstanceLoaderSwitchResult
        {
            Success = true,
            PreviousVersionName = instance.VersionName,
            NewVersionName = installedName,
            Loader = kind,
            LoaderVersion = trimmedVersion,
            MinecraftVersion = minecraftVersion,
            RequiresModConfirmation = needsConfirmation,
            ConfirmationMessage = string.IsNullOrWhiteSpace(consequence)
                ? directoryNote
                : consequence + " " + directoryNote,
        };
    }

    /// <summary>
    /// 换完加载器后要落盘的加载器版本号。
    ///
    /// 优先用调用方指定的版本（<c>trimmedVersion</c>）：那是用户明确要的，也一定和装出来的目录一致。
    /// 没指定（装最新版）时只能回读刚装好的版本目录——安装器返回的目录名（<c>1.20.1-forge-47.2.0</c>）
    /// 里的版本段由安装器决定，自己从字符串里猜会在 NeoForge 这种非标准编号上出错。
    ///
    /// 回读失败返回 null 而不是抛：加载器已经装好、实例已经改好，一个缓存字段缺失
    /// 只让 Java 上限判定退化成「无加载器级上限」，不值得让整个换加载器操作失败。
    /// </summary>
    private async Task<string?> ResolveInstalledLoaderVersionAsync(
        LoaderKind kind,
        string installedName,
        string? requestedVersion,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            return requestedVersion.Trim();
        }

        if (kind == LoaderKind.Vanilla)
        {
            return null;
        }

        if (_versions is null)
        {
            return null;
        }

        try
        {
            var info = await _versions.GetVersionAsync(installedName, cancellationToken).ConfigureAwait(false);
            var detected = info?.LoaderVersion;

            return string.IsNullOrWhiteSpace(detected) ? null : detected.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"回读加载器版本失败：{installedName}", ex);
            return null;
        }
    }

    /// <summary>
    /// 从磁盘解析实例真实的 MC 版本与加载器。
    ///
    /// <see cref="InstanceInfo"/> 上的 <c>MinecraftVersion</c> / <c>Loader</c> 是写入时的缓存，
    /// 用户手动换过 <c>versions/</c> 里的文件、或从别的启动器迁入的实例，这两个字段都可能是旧的。
    /// 换加载器要装「那个 MC 版本的加载器」，版本号错了会装出一个启动不了的组合，
    /// 所以这里以 <see cref="IVersionService"/> 读到的磁盘信息为准，读不到才退回缓存。
    /// </summary>
    private async Task<(string? MinecraftVersion, LoaderKind Loader)> ResolveInstanceVersionAsync(
        InstanceInfo instance,
        CancellationToken cancellationToken)
    {
        var cachedVersion = instance.MinecraftVersion;
        var cachedLoader = instance.Loader;

        if (_versions is null || string.IsNullOrWhiteSpace(instance.VersionName))
        {
            return (cachedVersion, cachedLoader);
        }

        try
        {
            var info = await _versions.GetVersionAsync(instance.VersionName, cancellationToken).ConfigureAwait(false);

            if (info is null)
            {
                return (cachedVersion, cachedLoader);
            }

            return (info.MinecraftVersion ?? cachedVersion, info.Loader);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取实例 {instance.Id} 的版本信息失败，按缓存字段继续", ex);
            return (cachedVersion, cachedLoader);
        }
    }

    /// <summary>
    /// 推测目标版本目录名。仅用于「失败后清理」与「装前判断是否已存在」，
    /// 不作为实例最终指向的名字——那个一律以安装服务返回值为准。
    /// </summary>
    private static string? PredictVersionName(LoaderKind kind, string minecraftVersion, string? loaderVersion)
    {
        if (kind == LoaderKind.Vanilla)
        {
            return minecraftVersion;
        }

        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            // 用户没指定版本 = 让安装器取最新，装前无从推测。返回 null 表示「不清理」。
            return null;
        }

        return kind switch
        {
            LoaderKind.Fabric or LoaderKind.Quilt =>
                InstallService.BuildLoaderVersionName(kind, minecraftVersion, loaderVersion),

            // Forge / NeoForge 的目录名由安装器按 <mc>-<loader>-<ver> 生成。
            _ => $"{minecraftVersion}-{kind.ToString().ToLowerInvariant()}-{loaderVersion}",
        };
    }

    /// <summary>
    /// 失败后清掉本次新建的半成品版本目录。
    ///
    /// 只删「装前确认不存在」的那一个目录：目标版本早就装过时删掉它等于毁掉用户已有的版本，
    /// 那种情况下宁可留一份多余文件也不能删。
    /// </summary>
    private void CleanupHalfInstalled(string? expectedName, bool existedBefore)
    {
        if (expectedName is null || existedBefore)
        {
            return;
        }

        var path = Path.Combine(_paths.VersionsDirectory, expectedName);
        InstanceFileSystem.DeleteDirectoryQuietly(path, _log);
    }
}