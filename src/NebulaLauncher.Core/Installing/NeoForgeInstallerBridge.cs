using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installer.NeoForge.Installers;
using CmlLib.Core.Installer.NeoForge.Versions;
using CmlLib.Core.Installers;
using NebulaLauncher.Core.Downloads;

namespace NebulaLauncher.Core.Installing;

/// <summary>
/// NeoForge 安装桥接。
///
/// **为什么不直接用 <c>CmlLib.Core.Installer.NeoForge.NeoForgeInstaller</c>**：4.0.1 里那个
/// facade 只有一个 <c>(MinecraftLauncher)</c> 构造，构造体内 <c>new HttpClient()</c> 去查
/// <c>maven.neoforged.net</c> 的版本目录（反编译 IL：
/// <c>_versionLoader = new NeoForgeVersionLoader(new HttpClient())</c>）。该主机在本机不可达，
/// 而我们所有镜像重写都挂在自建 HttpClient 上，facade 完全绕开它——这就是 NeoForge
/// 装不上的第三层原因。
///
/// PCL2 也不用那个安装器。它自己走三步（<c>ModDownloadLib.vb:1094-1119</c>）：
/// 查版本目录 → 拼 <c>{坐标}/{包名}-{版本}-installer.jar</c> 并把 <c>maven.neoforged.net/releases</c>
/// 换成镜像 → 交给安装器执行。这里照同样的顺序，只是把「查目录」与「选版本」换成我们自己的实现：
///
/// 1. 用调用方给的 <see cref="HttpClient"/> 拉版本目录：BMCLAPI 的 maven details API 优先，
///    官方 <c>/api/maven/versions</c> 兜底（两个地址的 JSON 形状不同，解析器两种都认）；
/// 2. 选版本并构造 <see cref="NeoForgeVersion"/>；
/// 3. <see cref="NeoForgeInstallerVersionMapper"/> 建 <see cref="IForgeInstaller"/>，再调
///    <see cref="IForgeInstaller.Install"/> 执行真正的安装（解包 install_profile、装库、跑处理器）。
///
/// installer jar 与全部 maven 构件仍由 <c>launcher.GameInstaller</c>（即
/// <see cref="NebulaGameInstaller"/>）与共用 HttpClient 下载，所以
/// <c>DownloadMirror.BmclApiMap</c> 里那条
/// <c>maven.neoforged.net/releases/ → bmclapi2.bangbang93.com/maven/</c>
/// 会把它们整体导到镜像；不需要在这里自己拼镜像地址。
///
/// 反射取类型的那套绕法（程序集限定名 + <c>Activator.CreateInstance</c>）本轮已删除：
/// 两个 CmlLib 包里只有 <c>NeoForgeInstaller</c> 这一个类型全文名相同，其余类型
/// （<c>NeoForgeVersion</c> / <c>NeoForgeInstallerVersionMapper</c> / <c>IForgeInstaller</c> /
/// <c>NeoForgeInstallOptions</c>）都在各自独有的命名空间下，可以直接 new。
/// 而本类现在根本不碰 facade，CS0433 的根源随之消失。
/// </summary>
internal static class NeoForgeInstallerBridge
{
    /// <summary>
    /// 走 Legacy 坐标的唯一 Minecraft 版本。NeoForge 从 1.20.1 起步，那批构件（47.x）
    /// 发布在 <c>net/neoforged/forge</c> 下、版本号形如 <c>1.20.1-47.1.99</c>；
    /// 1.20.2 起改到 <c>net/neoforged/neoforge</c>、版本号形如 <c>20.4.30</c>。
    /// PCL2 用同一个判据（<c>DlNeoForgeListEntry.UrlBase</c>：<c>If Inherit = "1.20.1"</c>）。
    /// </summary>
    internal const string LegacyMinecraftVersion = "1.20.1";

    private const string OfficialMavenRoot = "https://maven.neoforged.net/releases";
    private const string MirrorDetailsRoot = "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases";
    private const string OfficialVersionsRoot = "https://maven.neoforged.net/api/maven/versions/releases";
    private const string VersionFolderPrefix = "neoforge-";

    /// <summary>调用 NeoForge 安装器，返回安装后的版本目录名。</summary>
    public static async Task<string> InstallAsync(
        MinecraftLauncher launcher,
        HttpClient httpClient,
        string minecraftVersion,
        string? neoForgeVersion,
        NeoForgeInstallOptions options,
        CancellationToken cancellationToken)
    {
        var result = await TryInstallAsync(
                launcher, httpClient, minecraftVersion, neoForgeVersion, options, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // 失败原因已经由 TryInstallAsync 拼成一句可读的话（含尝试过的地址与底层原因），
            // 这里只换一个类型，让上层能区分「NeoForge 安装失败」与别的 IO 异常。
            throw new NeoForgeInstallException(result.FailureReason ?? "NeoForge 安装失败。", result.LastError);
        }

        return result.VersionName!;
    }

    /// <summary>
    /// 与 <see cref="InstallAsync"/> 同一条流程，但失败时返回结果对象而不是抛异常。
    ///
    /// 只有用户取消（<paramref name="cancellationToken"/> 触发）会向上抛
    /// <see cref="OperationCanceledException"/>——取消不是失败，调用方靠它区分两种收尾。
    /// </summary>
    internal static async Task<NeoForgeInstallResult> TryInstallAsync(
        MinecraftLauncher launcher,
        HttpClient httpClient,
        string minecraftVersion,
        string? neoForgeVersion,
        NeoForgeInstallOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            return NeoForgeInstallResult.Failure("没有指定 Minecraft 版本，无法安装 NeoForge。");
        }

        // NeoFNewest.Install 第一件事就是校验 JavaPath，缺了会抛 ArgumentNullException("JavaPath")，
        // 这个异常对用户没有任何信息量。提前换成一句能照着修的话。
        if (string.IsNullOrWhiteSpace(options.JavaPath))
        {
            return NeoForgeInstallResult.Failure(
                $"没有找到可用于安装 NeoForge {minecraftVersion} 的 Java，请先在设置页指定 Java 路径。");
        }

        var catalog = await LoadCatalogAsync(httpClient, minecraftVersion, cancellationToken).ConfigureAwait(false);
        if (!catalog.Succeeded)
        {
            return NeoForgeInstallResult.Failure(catalog.FailureReason!, catalog.LastError);
        }

        var apiName = SelectApiName(catalog.ApiNames, neoForgeVersion, minecraftVersion);
        if (apiName is null)
        {
            var requested = string.IsNullOrWhiteSpace(neoForgeVersion) ? "(最新稳定版)" : neoForgeVersion;
            return NeoForgeInstallResult.Failure(
                $"Minecraft {minecraftVersion} 没有匹配的 NeoForge 版本：请求 {requested}，"
                + $"目录里共 {catalog.ApiNames.Count} 个该游戏版本的构建。");
        }

        var versionFolderName = VersionFolderName(minecraftVersion, apiName);

        if (options.SkipIfAlreadyInstalled
            && await IsAlreadyInstalledAsync(launcher, versionFolderName, cancellationToken).ConfigureAwait(false))
        {
            return NeoForgeInstallResult.Success(versionFolderName);
        }

        try
        {
            var version = new NeoForgeVersion(minecraftVersion, apiName);
            var installer = CreateInstaller(version, versionFolderName);

            // Legacy 1.20.1 的 installer jar 在 net/neoforged/forge 下，而
            // NeoForgeVersion.NeoForgeUrl 把包名硬编码成 neoforge（不可覆写）。
            // CmlLib 唯一放得进手的缝是 IGameInstaller：它收到的 GameFile 就是 installer jar，
            // 所以在这一层把地址改掉，其余下载行为不变。
            var gameInstaller = IsLegacy(minecraftVersion)
                ? new InstallerUrlRewritingGameInstaller(launcher.GameInstaller, minecraftVersion, apiName)
                : launcher.GameInstaller;

            await installer.Install(launcher.MinecraftPath, gameInstaller, options).ConfigureAwait(false);

            // NeoForge 安装器把自带的 version.json 原样复制成 <目录名>.json，里面的 id 是它自己的
            // 命名；目录名被我们改过（Legacy 去了 "1.20.1-" 前缀）时必须把 id 对回来，
            // 否则 CmlLib 的版本加载器按目录名找不到它。与 InstallService 写 Forge profile 时的
            // 做法一致（那里也是 node["id"] = versionName）。
            await PatchVersionJsonIdAsync(launcher, versionFolderName, cancellationToken).ConfigureAwait(false);

            return NeoForgeInstallResult.Success(versionFolderName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 安装阶段失败（下载 403、解包失败、处理器退出码非 0 等）一律包成结果对象，
            // 消息带上版本号，用户能判断该换源还是该换版本。
            return NeoForgeInstallResult.Failure(
                $"安装 NeoForge {apiName}（Minecraft {minecraftVersion}）失败：{ex.Message}", ex);
        }
    }

    // ---------------- 版本目录 ----------------

    /// <summary>Minecraft 版本是否走 Legacy 坐标（<c>net/neoforged/forge</c>）。</summary>
    internal static bool IsLegacy(string minecraftVersion) =>
        string.Equals(minecraftVersion, LegacyMinecraftVersion, StringComparison.Ordinal);

    /// <summary>
    /// 版本目录的候选地址，首选在前。
    ///
    /// 镜像优先是刻意的：<c>maven.neoforged.net</c> 在国内基本不可达（本机 TCP 超时），
    /// 官方地址留着只为「镜像侧目录缺文件」这一种情况，不值得为它把用户的时间花在超时上。
    /// 两个地址的响应形状不同（镜像 details 是 <c>files[]</c>，官方是 <c>versions[]</c>），
    /// 由 <see cref="ParseCatalogNames"/> 同时兼容。
    /// </summary>
    internal static IReadOnlyList<string> CatalogUrls(string minecraftVersion)
    {
        var package = IsLegacy(minecraftVersion) ? "forge" : "neoforge";

        return
        [
            $"{MirrorDetailsRoot}/net/neoforged/{package}",
            $"{OfficialVersionsRoot}/net/neoforged/{package}",
        ];
    }

    /// <summary>
    /// 从版本目录 JSON 里取出构建名。
    ///
    /// 认两种形状：
    /// <list type="bullet">
    /// <item>BMCLAPI details：<c>{"name":"neoforge","files":[{"name":"21.1.72","type":"DIRECTORY"}]}</c></item>
    /// <item>官方 maven：<c>{"isSnapshot":false,"versions":["21.1.72","21.1.72-beta"]}</c></item>
    /// </list>
    /// 坏 JSON 返回空集合，不抛——调用方按「目录为空」处理，消息里仍带着地址。
    /// </summary>
    internal static IReadOnlyList<string> ParseCatalogNames(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();

            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    // details 会把 .jar / .pom / .md5 这些同级的普通文件也列出来，只留目录。
                    if (file.TryGetProperty("type", out var type)
                        && !string.Equals(type.GetString(), "DIRECTORY", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (file.TryGetProperty("name", out var name)
                        && name.GetString() is { Length: > 0 } value)
                    {
                        names.Add(value);
                    }
                }

                return names;
            }

            if (root.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in versions.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                    {
                        names.Add(value);
                    }
                }
            }

            return names;
        }
    }

    /// <summary>
    /// 只留属于 <paramref name="minecraftVersion"/> 的构建。
    ///
    /// 现代版本（1.20.2+）的构建名以去掉 "1." 的游戏版本开头：1.21.1 → <c>21.1.</c>。
    /// Legacy（1.20.1）的构建名带完整前缀：<c>1.20.1-47.1.99</c>。
    /// </summary>
    internal static IReadOnlyList<string> ForMinecraftVersion(IEnumerable<string> apiNames, string minecraftVersion)
    {
        ArgumentNullException.ThrowIfNull(apiNames);

        var legacy = IsLegacy(minecraftVersion);
        var prefix = legacy
            ? minecraftVersion + "-"
            : ManifestPrefix(minecraftVersion) + ".";

        var result = new List<string>();
        foreach (var name in apiNames)
        {
            if (!string.IsNullOrWhiteSpace(name)
                && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// 版本号排序（小的在前）。只比数字段，后缀（<c>-beta</c>）只作为同数字时的次序：
    /// 没有后缀的排在带后缀之后，取「最新稳定版」时就落在它上面。
    /// </summary>
    internal static IReadOnlyList<string> SortByLoaderVersion(
        IEnumerable<string> apiNames,
        string minecraftVersion)
    {
        ArgumentNullException.ThrowIfNull(apiNames);

        var list = apiNames.ToList();
        list.Sort((left, right) =>
            CompareLoaderVersion(LoaderPart(minecraftVersion, left), LoaderPart(minecraftVersion, right)));
        return list;
    }

    /// <summary>
    /// 选一个构建名：<paramref name="requested"/> 为空取最新稳定版（没有稳定版就取最新），
    /// 否则按请求匹配。匹配不上返回 <c>null</c>。
    ///
    /// 请求串接受三种写法，都是用户在界面上可能粘进来的：
    /// <c>21.1.72</c> / <c>neoforge-21.1.72</c> / <c>1.20.1-47.1.99</c>。
    /// Legacy 的裸版本号（<c>47.1.99</c>）会自动补回 <c>1.20.1-</c> 前缀。
    /// </summary>
    internal static string? SelectApiName(
        IReadOnlyList<string> sortedApiNames,
        string? requested,
        string minecraftVersion)
    {
        ArgumentNullException.ThrowIfNull(sortedApiNames);

        if (sortedApiNames.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            for (var index = sortedApiNames.Count - 1; index >= 0; index--)
            {
                if (IsStableBuild(sortedApiNames[index]))
                {
                    return sortedApiNames[index];
                }
            }

            return sortedApiNames[sortedApiNames.Count - 1];
        }

        var wanted = NormalizeRequested(requested, minecraftVersion);

        foreach (var name in sortedApiNames)
        {
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        // 再按「加载器版本号」比一次：用户写 47.1.99 而目录里是 1.20.1-47.1.99 时上面能命中，
        // 但目录里若出现 1.20.1-47.1.99-beta 这类形变，靠这一段仍能配上同一个构建号。
        var wantedLoader = LoaderPart(minecraftVersion, wanted);
        foreach (var name in sortedApiNames)
        {
            if (string.Equals(LoaderPart(minecraftVersion, name), wantedLoader, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>把用户输入的版本串规整成目录里的写法。</summary>
    internal static string NormalizeRequested(string requested, string minecraftVersion)
    {
        var value = requested.Trim();

        if (value.StartsWith(VersionFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[VersionFolderPrefix.Length..];
        }
        else if (value.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
        {
            value = value["forge-".Length..];
        }

        if (IsLegacy(minecraftVersion))
        {
            return value.StartsWith(LegacyMinecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
                ? value
                : LegacyMinecraftVersion + "-" + value;
        }

        return value.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? value[(minecraftVersion.Length + 1)..]
            : value;
    }

    /// <summary>构建名去掉游戏版本前缀之后的加载器版本号（<c>1.20.1-47.1.99</c> → <c>47.1.99</c>）。</summary>
    internal static string LoaderPart(string minecraftVersion, string apiName)
    {
        if (IsLegacy(minecraftVersion)
            && apiName.StartsWith(LegacyMinecraftVersion + "-", StringComparison.OrdinalIgnoreCase))
        {
            return apiName[(LegacyMinecraftVersion.Length + 1)..];
        }

        return apiName;
    }

    /// <summary>安装后的版本目录名。PCL2 装 Legacy 时也去掉 <c>1.20.1-</c> 前缀，这里跟同样的做法。</summary>
    internal static string VersionFolderName(string minecraftVersion, string apiName) =>
        VersionFolderPrefix + LoaderPart(minecraftVersion, apiName);

    private static string ManifestPrefix(string minecraftVersion) =>
        minecraftVersion.StartsWith("1.", StringComparison.Ordinal)
            ? minecraftVersion[2..]
            : minecraftVersion;

    private static bool IsStableBuild(string apiName) =>
        !apiName.Contains("alpha", StringComparison.OrdinalIgnoreCase)
        && !apiName.Contains("beta", StringComparison.OrdinalIgnoreCase)
        && !apiName.Contains("pre", StringComparison.OrdinalIgnoreCase)
        && !apiName.Contains('+');

    private static int CompareLoaderVersion(string left, string right)
    {
        var leftNumbers = NumbersOf(left);
        var rightNumbers = NumbersOf(right);

        var shared = Math.Min(leftNumbers.Count, rightNumbers.Count);
        for (var index = 0; index < shared; index++)
        {
            var diff = leftNumbers[index].CompareTo(rightNumbers[index]);
            if (diff != 0)
            {
                return diff;
            }
        }

        if (leftNumbers.Count != rightNumbers.Count)
        {
            return leftNumbers.Count.CompareTo(rightNumbers.Count);
        }

        // 数字相同：无后缀的更新（21.1.72 > 21.1.72-beta）。
        var leftHasSuffix = left.Length > TrimmedNumberLength(left);
        var rightHasSuffix = right.Length > TrimmedNumberLength(right);
        if (leftHasSuffix != rightHasSuffix)
        {
            return leftHasSuffix ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static List<long> NumbersOf(string value)
    {
        var numbers = new List<long>();
        var current = 0L;
        var inNumber = false;

        foreach (var ch in value)
        {
            if (ch is >= '0' and <= '9')
            {
                current = (current * 10) + (ch - '0');
                inNumber = true;
            }
            else if (inNumber)
            {
                numbers.Add(current);
                current = 0;
                inNumber = false;
            }
        }

        if (inNumber)
        {
            numbers.Add(current);
        }

        return numbers;
    }

    private static int TrimmedNumberLength(string value)
    {
        var index = 0;
        while (index < value.Length && (value[index] is >= '0' and <= '9' or '.'))
        {
            index++;
        }

        return index;
    }

    // ---------------- installer jar 地址 ----------------

    /// <summary>
    /// CmlLib 会算出来的 installer jar 地址（<c>NeoForgeVersion.NeoForgeUrl</c>，包名恒为 neoforge）。
    /// 镜像重写把 <c>/releases/</c> 之后的路径整体搬到 BMCLAPI，所以这里带的是官方前缀。
    /// </summary>
    internal static string CmlLibInstallerUrl(string apiName) =>
        $"{OfficialMavenRoot}/net/neoforged/neoforge/{apiName}/neoforge-{apiName}-installer.jar";

    /// <summary>Legacy 构件真正所在的地址（坐标是 <c>net/neoforged/forge</c>）。</summary>
    internal static string LegacyInstallerUrl(string apiName) =>
        $"{OfficialMavenRoot}/net/neoforged/forge/{apiName}/forge-{apiName}-installer.jar";

    /// <summary>
    /// 只改 Legacy 的 installer jar 地址，其它 URL 原样返回。
    ///
    /// 判等用完整地址比较而不是「包含 net/neoforged/neoforge/」：一次安装里还有别的一堆
    /// maven 构件，模糊匹配会把不该动的库也改掉。
    /// </summary>
    internal static string RewriteInstallerUrl(string url, string minecraftVersion, string apiName)
    {
        if (string.IsNullOrEmpty(url) || !IsLegacy(minecraftVersion))
        {
            return url;
        }

        return string.Equals(url, CmlLibInstallerUrl(apiName), StringComparison.OrdinalIgnoreCase)
            ? LegacyInstallerUrl(apiName)
            : url;
    }

    // ---------------- 安装 ----------------

    private static IForgeInstaller CreateInstaller(NeoForgeVersion version, string versionFolderName)
    {
        var mapper = new NeoForgeInstallerVersionMapper();

        // CreateInstaller 把目录名硬编码成 "neoforge-" + VersionName（反编译：它直接转调
        // Create1_12_2Installer("neoforge-" + versionName, version)）。Legacy 的 VersionName 是
        // "1.20.1-47.1.99"，装出来的目录名要去掉 "1.20.1-"，所以这两条分支走的是
        // 同一个实现的公开入口，只是显式给了目录名。
        return string.Equals(versionFolderName, VersionFolderPrefix + version.VersionName, StringComparison.Ordinal)
            ? mapper.CreateInstaller(version)
            : mapper.Create1_12_2Installer(versionFolderName, version);
    }

    private static async Task<NeoForgeCatalog> LoadCatalogAsync(
        HttpClient httpClient,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var urls = CatalogUrls(minecraftVersion);

        try
        {
            // attemptsPerUrl: 1 —— 官方地址在不可达网络下每次要等一次完整超时，
            // 重试只会把「装不上」拖成两倍等待。
            var json = await ResourceFetcher
                .GetStringAsync(httpClient, urls, cancellationToken, log: null, attemptsPerUrl: 1)
                .ConfigureAwait(false);

            var apiNames = SortByLoaderVersion(
                ForMinecraftVersion(ParseCatalogNames(json), minecraftVersion),
                minecraftVersion);

            return NeoForgeCatalog.Ok(apiNames);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NeoForgeCatalog.Failed(
                $"拉取 NeoForge 版本目录失败（已尝试 {urls.Count} 个地址：{string.Join("、", urls)}）：{ex.Message}",
                ex);
        }
    }

    private static async Task<bool> IsAlreadyInstalledAsync(
        MinecraftLauncher launcher,
        string versionFolderName,
        CancellationToken cancellationToken)
    {
        try
        {
            await launcher.GetVersionAsync(versionFolderName, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 目录不存在时抛 KeyNotFoundException，版本 JSON 坏了抛 JsonException；
            // 两种都按「没装」处理，让安装流程跑一遍去修。
            return false;
        }
    }

    private static async Task PatchVersionJsonIdAsync(
        MinecraftLauncher launcher,
        string versionFolderName,
        CancellationToken cancellationToken)
    {
        var jsonPath = launcher.MinecraftPath.GetVersionJsonPath(versionFolderName);
        if (!File.Exists(jsonPath))
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(text) is not JsonObject node)
            {
                return;
            }

            if (node["id"] is JsonValue id && string.Equals(id.ToString(), versionFolderName, StringComparison.Ordinal))
            {
                return;
            }

            node["id"] = versionFolderName;
            await File.WriteAllTextAsync(
                    jsonPath,
                    node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // id 与目录名不一致只影响跨版本继承（inheritsFrom 解析）；文件已经装好，
            // 为这一处失败把整个安装判死没有意义。调用方会在启动时看到版本缺失，
            // 那时可以靠「修复」重跑一遍。
        }
    }

    /// <summary>
    /// 只在下载 installer jar 时改写地址的 <see cref="IGameInstaller"/> 装饰器。
    ///
    /// 用 <c>with</c> 生成新的 <see cref="GameFile"/> 而不是改原对象：GameFile 是 record，
    /// init-only 属性不提供 setter，改不了原件；新对象与原件只差 Url 一个字段。
    /// </summary>
    private sealed class InstallerUrlRewritingGameInstaller : IGameInstaller
    {
        private readonly IGameInstaller _inner;
        private readonly string _minecraftVersion;
        private readonly string _apiName;

        public InstallerUrlRewritingGameInstaller(
            IGameInstaller inner,
            string minecraftVersion,
            string apiName)
        {
            _inner = inner;
            _minecraftVersion = minecraftVersion;
            _apiName = apiName;
        }

        public ValueTask Install(
            IEnumerable<GameFile> gameFiles,
            IProgress<InstallerProgressChangedEventArgs>? fileProgress,
            IProgress<ByteProgress>? byteProgress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(gameFiles);

            var rewritten = gameFiles.Select(file => string.IsNullOrEmpty(file.Url)
                ? file
                : file with { Url = RewriteInstallerUrl(file.Url, _minecraftVersion, _apiName) });

            return _inner.Install(rewritten, fileProgress, byteProgress, cancellationToken);
        }
    }
}

/// <summary>一次 NeoForge 安装的结果。失败时 <see cref="FailureReason"/> 是给用户看的一句话。</summary>
internal sealed class NeoForgeInstallResult
{
    private NeoForgeInstallResult(bool succeeded, string? versionName, string? failureReason, Exception? lastError)
    {
        Succeeded = succeeded;
        VersionName = versionName;
        FailureReason = failureReason;
        LastError = lastError;
    }

    public bool Succeeded { get; }

    /// <summary>成功时的版本目录名（<c>neoforge-21.1.72</c>）。</summary>
    public string? VersionName { get; }

    /// <summary>失败原因；成功时为 null。</summary>
    public string? FailureReason { get; }

    /// <summary>底层异常，仅用于包装抛出；成功时为 null。</summary>
    public Exception? LastError { get; }

    public static NeoForgeInstallResult Success(string versionName) =>
        new(true, versionName, null, null);

    public static NeoForgeInstallResult Failure(string reason, Exception? lastError = null) =>
        new(false, null, reason, lastError);
}

/// <summary>
/// NeoForge 安装失败。<see cref="NeoForgeInstallResult.FailureReason"/> 里已经是完整的一句话
/// （含版本、地址与底层原因），上层直接展示即可，不需要再拼前缀。
/// </summary>
internal sealed class NeoForgeInstallException : Exception
{
    public NeoForgeInstallException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>版本目录拉取结果：成功时带排序后的构建名，失败时带原因。</summary>
internal sealed class NeoForgeCatalog
{
    private NeoForgeCatalog(bool succeeded, IReadOnlyList<string> apiNames, string? failureReason, Exception? lastError)
    {
        Succeeded = succeeded;
        ApiNames = apiNames;
        FailureReason = failureReason;
        LastError = lastError;
    }

    public bool Succeeded { get; }

    /// <summary>属于该 Minecraft 版本的构建名，按版本号从小到大排。失败时为空集合。</summary>
    public IReadOnlyList<string> ApiNames { get; }

    public string? FailureReason { get; }

    public Exception? LastError { get; }

    public static NeoForgeCatalog Ok(IReadOnlyList<string> apiNames) =>
        new(true, apiNames, null, null);

    public static NeoForgeCatalog Failed(string reason, Exception? lastError = null) =>
        new(false, Array.Empty<string>(), reason, lastError);
}