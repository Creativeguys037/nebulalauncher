using System.Diagnostics;
using System.Reflection;
using System.Text;
using CmlLib.Core.Installer.NeoForge.Installers;
using CmlLib.Core.Installer.NeoForge.Versions;

// 用法：
//   NeoForgeProbe types                      反射打印 NeoForge / Forge 相关类型签名
//   NeoForgeProbe fetch                      实测版本目录与 installer jar 端点的可达性
//   NeoForgeProbe catalog <minecraftVersion> 按桥接的逻辑走一遍：拉目录 → 选版本 → 建安装器 → 打印地址
var mode = args.Length > 0 ? args[0] : "types";

switch (mode)
{
    case "types":
        DumpTypes();
        break;

    case "fetch":
        await FetchAsync().ConfigureAwait(false);
        break;

    case "catalog":
        await CatalogAsync(args.Length > 1 ? args[1] : "1.21.1").ConfigureAwait(false);
        break;

    default:
        Console.WriteLine("用法：NeoForgeProbe [types|fetch|catalog <mcVersion>]");
        break;
}

// ---------------- types ----------------

static void DumpTypes()
{
    var wantedAssemblies = new[]
    {
        "CmlLib.Core.Installer.NeoForge",
        "CmlLib.Core.Installer.Forge",
    };

    var sb = new StringBuilder();
    foreach (var name in wantedAssemblies)
    {
        Assembly asm;
        try
        {
            asm = Assembly.Load(name);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"### {name} LOAD FAILED: {ex.GetType().Name}: {ex.Message}");
            continue;
        }

        sb.AppendLine($"### {name} {asm.GetName().Version}");
        sb.AppendLine();

        foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (t.IsEnum)
            {
                sb.AppendLine($"enum {t.FullName}");
                foreach (var n in Enum.GetNames(t))
                {
                    sb.AppendLine("    " + n + " = " + Enum.Format(t, Enum.Parse(t, n), "D"));
                }

                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"type {t.FullName} : base={t.BaseType?.FullName ?? "<none>"} iface=[{string.Join(", ", t.GetInterfaces().Select(i => i.Name))}]");

            foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                sb.AppendLine($"    ctor({DescribeParams(c.GetParameters())})");
            }

            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                sb.AppendLine($"    prop {TypeName(p.PropertyType)} {p.Name} {{ {(p.GetMethod is not null ? "get; " : string.Empty)}{(p.SetMethod is not null ? "set; " : string.Empty)}}}");
            }

            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName)
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                sb.AppendLine($"    method {(m.IsStatic ? "static " : string.Empty)}{TypeName(m.ReturnType)} {m.Name}({DescribeParams(m.GetParameters())})");
            }

            sb.AppendLine();
        }
    }

    var path = Path.Combine(AppContext.BaseDirectory, "neoforge-api.txt");
    File.WriteAllText(path, sb.ToString());
    Console.WriteLine($"written {sb.Length} chars -> {path}");
}

static string DescribeParams(ParameterInfo[] parameters) =>
    string.Join(", ", parameters.Select(p =>
    {
        var type = TypeName(p.ParameterType);
        var def = p.HasDefaultValue ? " = " + (p.DefaultValue?.ToString() ?? "null") : string.Empty;
        return type + " " + p.Name + def;
    }));

static string TypeName(Type t)
{
    if (t.IsGenericType)
    {
        var name = t.Name[..t.Name.IndexOf('`')];
        return name + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
    }

    return t.FullName ?? t.Name;
}

// ---------------- fetch ----------------

static async Task FetchAsync()
{
    string[] urls =
    [
        "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/neoforge",
        "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/forge",
        "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge",
        "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/forge",
    ];

    using var client = CreateClient();

    foreach (var url in urls)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            sw.Stop();
            Console.WriteLine(url);
            Console.WriteLine($"  HTTP {(int)response.StatusCode} len={body.Length} elapsed={sw.ElapsedMilliseconds}ms");
            var head = body.Length > 240 ? body[..240] : body;
            Console.WriteLine("  head: " + head.Replace('\n', ' ').Replace('\r', ' '));
        }
        catch (Exception ex)
        {
            Console.WriteLine(url);
            Console.WriteLine($"  ERROR {ex.GetType().Name}: {ex.Message}");
        }
    }
}

// ---------------- catalog ----------------

static async Task CatalogAsync(string minecraftVersion)
{
    // 与 NeoForgeInstallerBridge 同一套选择规则（这里独立实现一份，只用于真机取证）。
    var legacy = string.Equals(minecraftVersion, "1.20.1", StringComparison.Ordinal);
    var package = legacy ? "forge" : "neoforge";
    var detailsUrl = $"https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/{package}";

    using var client = CreateClient();

    Console.WriteLine($"minecraft={minecraftVersion} legacy={legacy} coordinate=net/neoforged/{package}");
    Console.WriteLine($"catalog: {detailsUrl}");

    string json;
    try
    {
        json = await client.GetStringAsync(detailsUrl).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  目录拉取失败：{ex.GetType().Name}: {ex.Message}");
        return;
    }

    var prefix = legacy ? minecraftVersion + "-" : (minecraftVersion.StartsWith("1.", StringComparison.Ordinal) ? minecraftVersion[2..] : minecraftVersion) + ".";
    var names = new List<string>();
    using (var document = System.Text.Json.JsonDocument.Parse(json))
    {
        if (document.RootElement.TryGetProperty("files", out var files))
        {
            foreach (var file in files.EnumerateArray())
            {
                if (file.TryGetProperty("type", out var type)
                    && !string.Equals(type.GetString(), "DIRECTORY", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } value)
                {
                    names.Add(value);
                }
            }
        }
    }

    var matching = names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"  目录总数={names.Count} 匹配={matching.Count}");
    Console.WriteLine("  匹配样本：" + string.Join(", ", matching.TakeLast(5)));

    if (matching.Count == 0)
    {
        Console.WriteLine("  没有匹配项，后续步骤跳过。");
        return;
    }

    var stable = matching.LastOrDefault(n =>
        !n.Contains("alpha", StringComparison.OrdinalIgnoreCase)
        && !n.Contains("beta", StringComparison.OrdinalIgnoreCase)
        && !n.Contains("pre", StringComparison.OrdinalIgnoreCase)
        && !n.Contains('+'));

    var chosen = stable ?? matching[^1];
    var loaderPart = legacy && chosen.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
        ? chosen[(minecraftVersion.Length + 1)..]
        : chosen;

    var version = new NeoForgeVersion(minecraftVersion, chosen);
    var mapper = new NeoForgeInstallerVersionMapper();
    var installer = mapper.CreateInstaller(version);
    var installerFile = version.GetInstallerFile();

    Console.WriteLine($"  选中={chosen} 稳定={stable is not null}");
    Console.WriteLine($"  目录名（现代映射器）={installer.VersionName}；桥接用名=neoforge-{loaderPart}");
    Console.WriteLine($"  CmlLib 算出的 installer jar = {installerFile?.DirectUrl}");
    Console.WriteLine($"  镜像重写后 = {installerFile?.DirectUrl?.Replace("maven.neoforged.net/releases", "bmclapi2.bangbang93.com/maven", StringComparison.Ordinal)}");
    if (legacy)
    {
        Console.WriteLine($"  Legacy 实际地址 = https://maven.neoforged.net/releases/net/neoforged/forge/{chosen}/forge-{chosen}-installer.jar");
    }

    // 探测 installer jar 是否真的能下（只取前 1KB，避免整包）。
    var targets = new[]
    {
        installerFile?.DirectUrl,
        installerFile?.DirectUrl?.Replace("maven.neoforged.net/releases", "bmclapi2.bangbang93.com/maven", StringComparison.Ordinal),
        legacy ? $"https://maven.neoforged.net/releases/net/neoforged/forge/{chosen}/forge-{chosen}-installer.jar" : null,
        legacy ? $"https://bmclapi2.bangbang93.com/maven/net/neoforged/forge/{chosen}/forge-{chosen}-installer.jar" : null,
    };

    foreach (var target in targets.Where(t => !string.IsNullOrEmpty(t)).Distinct())
    {
        await ProbeAsync(client, target!).ConfigureAwait(false);
    }
}

static async Task ProbeAsync(HttpClient client, string url)
{
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        Console.WriteLine($"  jar {(int)response.StatusCode} {response.ReasonPhrase} <- {url}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  jar ERROR {ex.GetType().Name}: {ex.Message} <- {url}");
    }
}

static HttpClient CreateClient()
{
    var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("NebulaLauncher/1.0 (NeoForgeProbe)");
    return client;
}