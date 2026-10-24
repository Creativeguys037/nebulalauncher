using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Core.Mods;

/// <summary>从压缩包内读出的模组 / 资源包元信息。</summary>
internal sealed class ModMetadata
{
    public string Id { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? Version { get; init; }

    public string? Description { get; init; }

    public string? Authors { get; init; }

    public string? McVersionRange { get; init; }

    /// <summary>资源包 <c>pack.mcmeta</c> 的 <c>min_format</c>（1.21.9+，可带小数）；其他类型为 null。</summary>
    public double? MinPackFormat { get; init; }

    /// <summary>资源包 <c>pack.mcmeta</c> 的 <c>max_format</c>（1.21.9+）；其他类型为 null。</summary>
    public double? MaxPackFormat { get; init; }

    public LoaderKind Loader { get; init; } = LoaderKind.Vanilla;

    /// <summary>图标在包内的相对路径。</summary>
    public string? IconEntry { get; init; }

    /// <summary>
    /// 项目主页。fabric 的 <c>contact.homepage</c> / <c>contact.sources</c>，mcmod.info 的 <c>url</c>。
    /// 只用于卡片右键跳转，缺失就是没有。
    /// </summary>
    public string? HomePage { get; init; }

    /// <summary>声明的前置依赖（已排除 <c>minecraft</c> 与加载器自身提供的依赖）。</summary>
    public IReadOnlyList<ModDependency> Depends { get; init; } = Array.Empty<ModDependency>();

    /// <summary>声明的不兼容项（Forge 的 <c>type="incompatible"</c> 段）。</summary>
    public IReadOnlyList<ModDependency> Breaks { get; init; } = Array.Empty<ModDependency>();

    public string? Error { get; init; }
}

/// <summary>
/// 只用 <see cref="System.IO.Compression.ZipArchive"/> 读元数据，不引第三方包。
/// 任何异常都转成 <see cref="ModMetadata.Error"/>，不让 UI 因为一个坏包崩掉。
/// </summary>
internal static class ModMetadataReader
{
    /// <summary>一次打开压缩包，同时取出元信息与图标字节。</summary>
    public static (ModMetadata Metadata, byte[]? IconBytes) ReadFile(string path, ContentKind kind)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            var metadata = kind switch
            {
                ContentKind.Mod => ReadMod(archive),
                ContentKind.ResourcePack => ReadResourcePack(archive),
                _ => ReadShaderPack(),
            };

            var iconBytes = ReadFirstEntryBytes(archive, IconEntryCandidates(metadata, kind));
            return (metadata, iconBytes);
        }
        catch (InvalidDataException ex)
        {
            return (new ModMetadata { Error = $"不是有效的压缩包：{ex.Message}" }, null);
        }
        catch (IOException ex)
        {
            return (new ModMetadata { Error = $"读取文件失败：{ex.Message}" }, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (new ModMetadata { Error = $"无权限读取：{ex.Message}" }, null);
        }
        catch (Exception ex)
        {
            return (new ModMetadata { Error = ex.Message }, null);
        }
    }

    /// <summary>
    /// 图标条目的候选列表，按优先级排列（先声明字段、后约定路径）。
    ///
    /// 为什么需要兜底：声明字段在很多真实模组里是空的——1.7.10 / 1.12.2 的 <c>mcmod.info</c>
    /// 里 <c>logoFile</c> 写空串（IC2、JEI Legacy 都是），Fabric 模组常常只把图放进
    /// <c>assets/&lt;modid&gt;/icon.png</c> 而不写 <c>icon</c> 字段。候选路径全部只在该条目
    /// 真实存在时才会被读到，猜错不会产生任何可观察行为。
    ///
    /// 资源包 / 光影包 / 数据包的图标是包根目录的 <c>pack.png</c>（资源包规范里的可选文件，
    /// 光影包与数据包沿用同一位置）；少数光影包把它放在 <c>shaders/</c> 下，作为次级候选。
    /// </summary>
    private static List<string> IconEntryCandidates(ModMetadata metadata, ContentKind kind)
    {
        var candidates = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(metadata.IconEntry))
        {
            candidates.Add(metadata.IconEntry!);
        }

        if (kind == ContentKind.Mod && !string.IsNullOrWhiteSpace(metadata.Id))
        {
            var modId = metadata.Id.Trim();
            AddCandidate(candidates, $"assets/{modId}/icon.png");
            AddCandidate(candidates, $"assets/{modId}/logo.png");
        }

        if (kind is ContentKind.ResourcePack or ContentKind.ShaderPack or ContentKind.DataPack)
        {
            AddCandidate(candidates, "pack.png");
        }

        if (kind == ContentKind.ShaderPack)
        {
            AddCandidate(candidates, "shaders/pack.png");
        }

        return candidates;
    }

    /// <summary>候选去重：资源包的 <see cref="ModMetadata.IconEntry"/> 本来就是 <c>pack.png</c>。</summary>
    private static void AddCandidate(List<string> candidates, string path)
    {
        if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(path);
        }
    }

    /// <summary>按候选顺序读第一个存在的条目；全部不存在时返回 null。</summary>
    private static byte[]? ReadFirstEntryBytes(ZipArchive archive, IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (ReadEntryBytes(archive, candidate) is { Length: > 0 } bytes)
            {
                return bytes;
            }
        }

        return null;
    }

    /// <summary>
    /// 从磁盘上的压缩包按候选顺序读条目。给不走 <see cref="ReadFile"/> 的路径复用：
    /// 数据包的元数据与图标分属两套读取逻辑（目录形态还要单独处理）。
    /// </summary>
    internal static byte[]? ReadFirstEntryBytesFromFile(string archivePath, IReadOnlyList<string> candidates)
    {
        try
        {
            using var file = File.OpenRead(archivePath);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            return ReadFirstEntryBytes(archive, candidates);
        }
        catch (Exception)
        {
            // 压缩包损坏 / 被占用 / 权限不足：图标只是锦上添花，不能让它拖垮整个扫描。
            return null;
        }
    }

    /// <summary>取出指定条目的字节；找不到返回 null。</summary>
    private static byte[]? ReadEntryBytes(ZipArchive archive, string? entryName, long maxBytes = 4 * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return null;
        }

        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        var entry = archive.GetEntry(normalized)
                    ?? archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.FullName.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length <= 0 || entry.Length > maxBytes)
        {
            return null;
        }

        using var entryStream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        entryStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 按「信息完整度」而非「加载器新贵」排序。
    ///
    /// <para/>
    /// <c>quilt.mod.json</c> 排第一是因为 Quilt 模组通常**同时**带 <c>fabric.mod.json</c>（兼容层），
    /// 先读 fabric 会把加载器显示成 Fabric。反过来先读 mcmod.info 也不行：个别现代 jar 为了
    /// 兼容旧工具链会顺手塞一个空壳 mcmod.info，那会把 <c>mods.toml</c> 里的真实信息盖掉。
    /// </summary>
    private static ModMetadata ReadMod(ZipArchive archive)
    {
        if (TryReadJsonEntry(archive, "quilt.mod.json") is { } quilt)
        {
            if (quilt.RootElement.ValueKind == JsonValueKind.Object)
            {
                var metadata = ReadQuilt(quilt.RootElement);
                quilt.Dispose();
                return metadata;
            }

            quilt.Dispose();
        }

        if (TryReadJsonEntry(archive, "fabric.mod.json") is { } fabric)
        {
            if (fabric.RootElement.ValueKind != JsonValueKind.Object)
            {
                fabric.Dispose();
                return new ModMetadata { Error = "fabric.mod.json 不是对象" };
            }

            var id = GetString(fabric.RootElement, "id") ?? string.Empty;
            var name = GetString(fabric.RootElement, "name");
            var version = GetString(fabric.RootElement, "version");
            var description = GetString(fabric.RootElement, "description");
            var authors = ReadAuthors(fabric.RootElement);
            var icon = ReadIconEntry(fabric.RootElement);
            var mcRange = ReadFabricMinecraftRange(fabric.RootElement);
            var homePage = ReadFabricContact(fabric.RootElement);
            // 必须在 Dispose 之前读完：JsonDocument 释放后 Element 不能再访问。
            var depends = ReadFabricDependencies(fabric.RootElement, "depends");
            var breaks = ReadFabricDependencies(fabric.RootElement, "breaks");

            fabric.Dispose();
            return new ModMetadata
            {
                Id = id,
                Name = name,
                Version = version,
                Description = description,
                Authors = authors,
                IconEntry = icon,
                McVersionRange = mcRange,
                HomePage = homePage,
                Depends = depends,
                Breaks = breaks,
                Loader = LoaderKind.Fabric,
            };
        }

        foreach (var candidate in new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" })
        {
            var text = ReadTextEntry(archive, candidate);
            if (text is null)
            {
                continue;
            }

            var loader = candidate.Contains("neoforge", StringComparison.OrdinalIgnoreCase)
                ? LoaderKind.NeoForge
                : LoaderKind.Forge;
            return ReadToml(text, loader);
        }

        // mcmod.info 是 Forge 1.7.10–1.12.2 唯一的元数据文件，没有它这些年份的模组
        // 只能退到 MANIFEST.MF（通常只有 Implementation-Title），表现为「名字是文件名、没有描述」。
        if (TryReadJsonEntry(archive, "mcmod.info") is { } mcmodInfo)
        {
            var metadata = ReadMcmodInfo(mcmodInfo.RootElement);
            mcmodInfo.Dispose();
            return metadata;
        }

        if (TryReadJsonEntry(archive, "litemod.json") is { } litemod)
        {
            var metadata = ReadLitemod(litemod.RootElement);
            litemod.Dispose();
            return metadata;
        }

        var manifest = ReadTextEntry(archive, "META-INF/MANIFEST.MF");
        if (manifest is not null)
        {
            var values = ParseManifest(manifest);
            var title = values.GetValueOrDefault("Implementation-Title")
                        ?? values.GetValueOrDefault("Specification-Title")
                        ?? Path.GetFileNameWithoutExtension("mod.jar");
            return new ModMetadata
            {
                Id = values.GetValueOrDefault("Automatic-Module-Name") ?? title,
                Name = title,
                Version = values.GetValueOrDefault("Implementation-Version"),
                Description = values.GetValueOrDefault("Implementation-Vendor"),
            };
        }

        return new ModMetadata();
    }

    /// <summary>
    /// Quilt 模组：展示信息在 <c>quilt_loader.metadata</c>，依赖在 <c>quilt_loader.depends</c>
    /// （数组，元素 <c>{ "id": ..., "versions": ... }</c>）。少数 jar 省略 <c>quilt_loader</c> 外壳，两种都认。
    /// </summary>
    private static ModMetadata ReadQuilt(JsonElement root)
    {
        var loader = root.TryGetProperty("quilt_loader", out var shell)
                     && shell.ValueKind == JsonValueKind.Object
            ? shell
            : root;

        var metadata = loader.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            ? meta
            : default;

        var depends = new List<ModDependency>();
        var breaks = new List<ModDependency>();
        var mcRange = ReadQuiltDependencies(loader, "depends", depends);
        ReadQuiltDependencies(loader, "breaks", breaks);

        return new ModMetadata
        {
            Id = GetString(loader, "id") ?? string.Empty,
            Name = ReadText(metadata, "name") ?? GetString(root, "name"),
            Version = GetString(loader, "version"),
            Description = ReadText(metadata, "description"),
            Authors = ReadQuiltContributors(metadata),
            IconEntry = NormalizeOptional(ReadText(metadata, "icon")),
            McVersionRange = NormalizeOptional(mcRange),
            HomePage = ReadQuiltContact(metadata),
            Depends = depends,
            Breaks = breaks,
            Loader = LoaderKind.Quilt,
        };
    }

    /// <summary>Quilt 的依赖数组。返回值是该文件声明的 <c>minecraft</c> 区间（可为 null）。</summary>
    private static string? ReadQuiltDependencies(JsonElement loader, string property, List<ModDependency> target)
    {
        if (!loader.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? mcRange = null;

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(entry, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var versions = GetString(entry, "versions") ?? GetString(entry, "version");

            if (string.Equals(id, "minecraft", StringComparison.OrdinalIgnoreCase))
            {
                mcRange ??= NormalizeOptional(versions);
                continue;
            }

            if (IsPlatformDependency(id))
            {
                continue;
            }

            target.Add(new ModDependency(id, NormalizeOptional(versions), Optional: false));
        }

        return mcRange;
    }

    /// <summary>Quilt 的 <c>metadata.contributors</c>：对象，值可以是字符串或字符串数组。</summary>
    private static string? ReadQuiltContributors(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("contributors", out var contributors)
            || contributors.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var names = new List<string>();

        foreach (var entry in contributors.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
            {
                var name = entry.Value.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name!);
                }
            }
            else if (entry.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in entry.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                    {
                        names.Add(name);
                    }
                }
            }
        }

        return names.Count == 0 ? null : string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Forge 1.7.10–1.12.2 的 <c>mcmod.info</c>。三种形态都要认：
    /// <c>{ "modListVersion": 2, "modList": [ ... ] }</c>（1.8+ 标准）、裸数组（1.7.10 早期）、
    /// 以及裸对象（部分构建工具产物）。一个 jar 含多个 mod 时只取第一个——卡片只显示一行标题。
    /// </summary>
    private static ModMetadata ReadMcmodInfo(JsonElement root)
    {
        var entry = FirstModEntry(root);

        if (entry.ValueKind != JsonValueKind.Object)
        {
            return new ModMetadata();
        }

        var authors = ReadStringList(entry, "authorList") ?? GetString(entry, "authors") ?? GetString(entry, "author");
        // acceptedMinecraftVersions 是 Maven 区间（如 [1.7.10,1.12.2)），mcversion 是单值，优先单值。
        var mcRange = GetString(entry, "mcversion") ?? GetString(entry, "acceptedMinecraftVersions");

        return new ModMetadata
        {
            Id = GetString(entry, "modid") ?? GetString(entry, "modId") ?? string.Empty,
            Name = NormalizePlaceholder(GetString(entry, "name")),
            Version = NormalizePlaceholder(GetString(entry, "version")),
            Description = NormalizePlaceholder(ReadText(entry, "description")),
            Authors = NormalizePlaceholder(authors),
            IconEntry = NormalizeOptional(GetString(entry, "logoFile")),
            McVersionRange = NormalizeOptional(mcRange),
            HomePage = NormalizeOptional(GetString(entry, "url")),
            Loader = LoaderKind.Forge,
        };
    }

    /// <summary>从 mcmod.info 的三种形态里取出第一个 mod 对象。</summary>
    private static JsonElement FirstModEntry(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    return item;
                }
            }

            return default;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (root.TryGetProperty("modList", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    return item;
                }
            }

            return default;
        }

        return root;
    }

    /// <summary>
    /// LiteLoader 的 <c>litemod.json</c>。LiteLoader 不在 <see cref="LoaderKind"/> 里
    /// （扫描按扩展名把它当普通 jar 处理），所以这里留 <see cref="LoaderKind.Vanilla"/>：
    /// 硬塞成 Forge 会让用户在按加载器筛选时看到错的分类。
    /// </summary>
    private static ModMetadata ReadLitemod(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ModMetadata();
        }

        var version = GetString(root, "version") ?? GetString(root, "revision");

        return new ModMetadata
        {
            Id = GetString(root, "name") ?? string.Empty,
            Name = NormalizePlaceholder(GetString(root, "name")),
            Version = NormalizePlaceholder(version),
            Description = NormalizePlaceholder(ReadText(root, "description")),
            Authors = NormalizePlaceholder(GetString(root, "author")),
            McVersionRange = NormalizeOptional(GetString(root, "mcversion")),
            Loader = LoaderKind.Vanilla,
        };
    }

    /// <summary>fabric 的 <c>contact</c>：优先 homepage，其次 sources、issues。</summary>
    private static string? ReadFabricContact(JsonElement root) =>
        root.TryGetProperty("contact", out var contact) ? ReadContactValue(contact) : null;

    /// <summary>Quilt 把同样的结构放在 <c>metadata.contact</c>。</summary>
    private static string? ReadQuiltContact(JsonElement metadata) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("contact", out var contact)
            ? ReadContactValue(contact)
            : null;

    private static string? ReadContactValue(JsonElement contact)
    {
        if (contact.ValueKind == JsonValueKind.String)
        {
            return NormalizeOptional(contact.GetString());
        }

        if (contact.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return NormalizeOptional(ReadText(contact, "homepage"))
               ?? NormalizeOptional(ReadText(contact, "sources"))
               ?? NormalizeOptional(ReadText(contact, "issues"));
    }

    /// <summary>字符串数组 → 逗号分隔（mcmod.info 的 <c>authorList</c>）。</summary>
    private static string? ReadStringList(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new List<string>();

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                items.Add(text.Trim());
            }
        }

        return items.Count == 0 ? null : string.Join(", ", items);
    }

    /// <summary>
    /// Gradle 模板没替换掉的占位符（<c>${version}</c>、<c>@VERSION@</c> 等）比没有更糟：
    /// 卡片会显示一串花括号。认出来就当没有。
    /// </summary>
    private static string? NormalizePlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Contains("${", StringComparison.Ordinal) || trimmed.Contains("@VERSION@", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ModMetadata ReadToml(string text, LoaderKind loader)
    {
        var document = TomlDocument.Parse(text);
        var modSection = document.FirstSection("mods");

        var id = modSection?.Get("modId") ?? string.Empty;
        var range = string.Empty;
        var depends = new List<ModDependency>();
        var breaks = new List<ModDependency>();

        foreach (var section in document.Sections)
        {
            if (!TryMatchDependencySection(section.Name, out var isBreak))
            {
                continue;
            }

            var dependencyId = section.Get("modId");
            if (string.IsNullOrWhiteSpace(dependencyId))
            {
                continue;
            }

            if (string.Equals(dependencyId, "minecraft", StringComparison.OrdinalIgnoreCase))
            {
                // minecraft 的区间仍归 McVersionRange：保持旧行为，取第一段声明的 versionRange。
                if (!isBreak
                    && string.IsNullOrEmpty(range)
                    && section.Get("versionRange") is { Length: > 0 } declared)
                {
                    range = declared;
                }

                continue;
            }

            if (IsPlatformDependency(dependencyId))
            {
                continue;
            }

            var type = section.Get("type");
            var optional = IsOptionalDependency(section, type);

            // Forge / NeoForge 把「不兼容」也写在 [[dependencies.xxx]] 里，用 type 区分；
            // 单独的 [[breaks.xxx]] 段同样归入 Breaks。
            var isIncompatible = isBreak
                                 || string.Equals(type, "incompatible", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(type, "discouraged", StringComparison.OrdinalIgnoreCase);

            var dependency = new ModDependency(
                dependencyId,
                section.Get("versionRange") is { Length: > 0 } versionRange ? versionRange : null,
                optional);

            (isIncompatible ? breaks : depends).Add(dependency);
        }

        return new ModMetadata
        {
            Id = id,
            Name = modSection?.Get("displayName") ?? modSection?.Get("modId"),
            Version = modSection?.Get("version"),
            Description = modSection?.Get("description"),
            Authors = modSection?.Get("authors"),
            IconEntry = modSection?.Get("logoFile"),
            McVersionRange = string.IsNullOrEmpty(range) ? null : range,
            Depends = depends,
            Breaks = breaks,
            Loader = loader,
        };
    }

    /// <summary>
    /// 判定一个 section 是否描述依赖：<c>dependencies.&lt;parentModId&gt;</c> 或 <c>breaks.&lt;parentModId&gt;</c>。
    /// <c>TomlLite</c> 不做点号拆分，section 名原样保留（已核实 <c>[[dependencies.jei]]</c> 得到 <c>"dependencies.jei"</c>），
    /// 因此这里只做前缀匹配，不解析 parentModId（依赖目标来自段内的 <c>modId</c> 键）。
    /// </summary>
    private static bool TryMatchDependencySection(string name, out bool isBreak)
    {
        if (HasTablePrefix(name, "dependencies"))
        {
            isBreak = false;
            return true;
        }

        if (HasTablePrefix(name, "breaks"))
        {
            isBreak = true;
            return true;
        }

        isBreak = false;
        return false;
    }

    private static bool HasTablePrefix(string name, string prefix) =>
        name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Forge 用 <c>mandatory</c>，NeoForge 用 <c>type</c>，两种写法都要认成 optional。
    /// 注意 <see cref="TomlSection.GetBool"/> 的 fallback 默认是 false，缺省时必须显式传 true，
    /// 否则所有依赖都会被当成可选。
    /// </summary>
    private static bool IsOptionalDependency(TomlSection section, string? type)
    {
        if (string.Equals(type, "optional", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "discouraged", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !section.GetBool("mandatory", fallback: true);
    }

    /// <summary>
    /// 这些依赖由加载器 / JVM 提供，不是 mods 目录里的 jar：当成前置检查会 100% 误报。
    /// <c>minecraft</c> 不在此列，它单独归 <see cref="ModMetadata.McVersionRange"/>。
    /// </summary>
    private static bool IsPlatformDependency(string id) =>
        id.Equals("fabricloader", StringComparison.OrdinalIgnoreCase)
        || id.Equals("java", StringComparison.OrdinalIgnoreCase)
        || id.Equals("quilt_loader", StringComparison.OrdinalIgnoreCase)
        || id.Equals("quiltloader", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读 fabric.mod.json 的 <c>depends</c> / <c>breaks</c> 对象。<c>minecraft</c> 与加载器依赖跳过。
    /// 值可以是字符串或字符串数组：数组表示多个可接受区间，这里只取第一条非空项
    /// （<see cref="ModDependency.VersionRange"/> 只用于提示文案，拼成 <c>"&gt;=0.9, &lt;1.0"</c>
    /// 反而容易误读；缺失判定只看 modid，不受影响）。
    /// </summary>
    private static IReadOnlyList<ModDependency> ReadFabricDependencies(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ModDependency>();
        }

        var result = new List<ModDependency>();
        foreach (var entry in map.EnumerateObject())
        {
            var id = entry.Name.Trim();
            if (id.Length == 0
                || string.Equals(id, "minecraft", StringComparison.OrdinalIgnoreCase)
                || IsPlatformDependency(id))
            {
                continue;
            }

            result.Add(new ModDependency(id, ReadFabricVersionRange(entry.Value), Optional: false));
        }

        return result;
    }

    /// <summary>Fabric 的依赖值：字符串，或字符串数组（取第一条非空项）。</summary>
    private static string? ReadFabricVersionRange(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in value.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String && candidate.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static ModMetadata ReadResourcePack(ZipArchive archive)
    {
        var metaText = ReadTextEntry(archive, "pack.mcmeta");
        if (metaText is null)
        {
            return new ModMetadata { IconEntry = "pack.png" };
        }

        try
        {
            using var document = JsonDocument.Parse(metaText);
            if (document.RootElement.TryGetProperty("pack", out var pack))
            {
                // 旧式整数 pack_format；1.21.8 及更早只有它。
                var legacyFormat = pack.TryGetProperty("pack_format", out var formatNode)
                                   && formatNode.ValueKind == JsonValueKind.Number
                                   && formatNode.TryGetInt32(out var formatValue)
                    ? formatValue
                    : (int?)null;

                // 1.21.9+ 的区间写法（min_format / max_format，可带小数）。
                var minFormat = TryReadFormatNumber(pack, "min_format");
                var maxFormat = TryReadFormatNumber(pack, "max_format");
                var range = minFormat is null && maxFormat is null
                    ? null
                    : new PackFormatRange(minFormat, maxFormat);

                var description = ReadText(pack, "description");

                // 旧包保持「裸数字」的展示（资源包卡片一直显示 46 这类），区间包才用完整文案。
                var versionText = range is null
                    ? legacyFormat?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : PackFormatTable.DescribeDeclaration(legacyFormat, range);

                return new ModMetadata
                {
                    Id = string.IsNullOrEmpty(versionText) ? string.Empty : $"pack_format {StripFormatPrefix(versionText)}",
                    Name = description,
                    Version = versionText,
                    Description = description,
                    IconEntry = "pack.png",
                    MinPackFormat = minFormat,
                    MaxPackFormat = maxFormat,
                };
            }
        }
        catch (JsonException)
        {
            return new ModMetadata { IconEntry = "pack.png", Error = "pack.mcmeta 解析失败" };
        }

        return new ModMetadata { IconEntry = "pack.png" };
    }

    private static double? TryReadFormatNumber(JsonElement pack, string propertyName) =>
        pack.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetDouble(out var value)
        && value >= 0
            ? value
            : null;

    /// <summary>把 <c>format 88–94.1</c> 这类文案去掉前缀，只留数值区间；裸数字原样返回。</summary>
    private static string StripFormatPrefix(string versionText) =>
        versionText.StartsWith("format ", StringComparison.Ordinal)
            ? versionText["format ".Length..]
            : versionText;

    private static ModMetadata ReadShaderPack() => new() { Loader = LoaderKind.Vanilla };

    private static JsonDocument? TryReadJsonEntry(ZipArchive archive, string entryName)
    {
        var text = ReadTextEntry(archive, entryName);
        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadTextEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
                    ?? archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length > 8 * 1024 * 1024)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? ReadText(value) : null;

    private static string? ReadText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? ReadText(value) : null;

    /// <summary>MOTD 风格的 JSON 文本可能是字符串，也可能是 <c>{"text": ...}</c>。</summary>
    private static string? ReadText(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Object:
                return value.TryGetProperty("text", out var text) ? ReadText(text) : null;
            case JsonValueKind.Array:
                return string.Concat(value.EnumerateArray().Select(ReadText));
            default:
                return null;
        }
    }

    private static string? ReadAuthors(JsonElement element)
    {
        if (!element.TryGetProperty("authors", out var authors))
        {
            return null;
        }

        if (authors.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var author in authors.EnumerateArray())
            {
                var name = author.ValueKind == JsonValueKind.Object
                    ? author.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null
                    : author.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            return names.Count == 0 ? null : string.Join(", ", names);
        }

        return authors.ValueKind == JsonValueKind.String ? authors.GetString() : null;
    }

    private static string? ReadIconEntry(JsonElement element)
    {
        if (!element.TryGetProperty("icon", out var icon))
        {
            return null;
        }

        if (icon.ValueKind == JsonValueKind.String)
        {
            return icon.GetString();
        }

        if (icon.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // {"16": "a.png", "128": "b.png"}：取分辨率最大的那张。
        string? best = null;
        var bestSize = -1;
        foreach (var property in icon.EnumerateObject())
        {
            if (int.TryParse(property.Name, out var size) && size > bestSize
                && property.Value.ValueKind == JsonValueKind.String)
            {
                bestSize = size;
                best = property.Value.GetString();
            }
        }

        return best;
    }

    private static string? ReadFabricMinecraftRange(JsonElement element)
    {
        if (!element.TryGetProperty("depends", out var depends)
            || depends.ValueKind != JsonValueKind.Object
            || !depends.TryGetProperty("minecraft", out var minecraft))
        {
            return null;
        }

        if (minecraft.ValueKind == JsonValueKind.String)
        {
            return minecraft.GetString();
        }

        if (minecraft.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in minecraft.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String && candidate.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseManifest(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? currentKey = null;
        var builder = new StringBuilder();

        void Flush()
        {
            if (currentKey is not null)
            {
                result[currentKey] = builder.ToString().Trim();
            }

            currentKey = null;
            builder.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith(' ') && currentKey is not null)
            {
                builder.Append(line[1..]);
                continue;
            }

            Flush();
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            currentKey = line[..separator].Trim();
            builder.Append(line[(separator + 1)..].Trim());
        }

        Flush();
        return result;
    }
}
