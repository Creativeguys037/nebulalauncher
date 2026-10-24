using System.Text.RegularExpressions;

namespace NebulaLauncher.DocsCheck;

/// <summary>
/// 一个探测点：仓库相对路径 + 可选符号（<c>Type</c> 或 <c>Type.Member</c>）。
/// 符号为 null 表示只要求路径存在。
/// </summary>
/// <param name="Path">仓库相对路径（<c>/</c> 分隔）。</param>
/// <param name="Symbol">符号；<c>Type.Member</c> 时按 <c>.</c> 拆段，逐段要求标识符在该文件文本中出现。</param>
public sealed record CodeProbe(string Path, string? Symbol = null);

/// <summary>
/// 探测点成立判定。引用校验与三态扫描共用这一份规则：
/// 路径存在（文件或目录）；给了符号时，按 <c>.</c> 拆段，逐段要求标识符在该文件文本中出现。
///
/// 不要求符号在该文件里「声明」：文档引用的是使用点，例如 <c>App.axaml.cs:56 StartupState</c> 只有调用，
/// 声明在另一个文件；<c>*.csproj:26 Avalonia</c> 引的是包名，也没有类型声明。要求声明会把这两类正确引用全部误判。
/// </summary>
public static class CodeProbeResolver
{
    /// <summary>合法标识符（类型名、成员名）。</summary>
    private static readonly Regex Identifier = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>路径存在且（可选）符号出现即成立。</summary>
    /// <param name="repoRoot">仓库根绝对路径。</param>
    /// <param name="path">仓库相对路径。</param>
    /// <param name="symbol">符号，可为 null。</param>
    /// <param name="cache">文件文本缓存，可复用以减少重复读取。</param>
    public static bool Exists(
        string repoRoot,
        string path,
        string? symbol,
        IDictionary<string, string?>? cache)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        ArgumentNullException.ThrowIfNull(path);

        var fullPath = RepoPaths.Resolve(repoRoot, path);
        if (Directory.Exists(fullPath))
        {
            // 目录只判存在性：符号没法在目录里定位。
            return string.IsNullOrEmpty(symbol);
        }

        if (!File.Exists(fullPath))
        {
            return false;
        }

        if (string.IsNullOrEmpty(symbol))
        {
            return true;
        }

        return SymbolExists(fullPath, symbol, cache);
    }

    /// <summary>
    /// 符号是否出现在文件里。调用方保证文件存在。
    /// </summary>
    /// <param name="fullPath">文件的绝对路径。</param>
    /// <param name="symbol">符号：<c>Type</c>、<c>Type.Member</c>，或 <c>A.B</c> 这类多段名。</param>
    /// <param name="cache">文件文本缓存，可复用以减少重复读取。</param>
    public static bool SymbolExists(string fullPath, string symbol, IDictionary<string, string?>? cache)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(symbol);

        // 文档里常写成 Foo.Run()，按名字判，别把括号带进标识符。
        if (symbol.EndsWith("()", StringComparison.Ordinal))
        {
            symbol = symbol[..^2];
        }

        var text = ReadText(fullPath, cache);
        if (text is null)
        {
            return false;
        }

        // 按 . 拆段逐段判：Type.Member 两段都要出现，A.B 这类包名/命名空间同样按段判。
        foreach (var segment in symbol.Split('.'))
        {
            if (!Identifier.IsMatch(segment))
            {
                return false;
            }

            if (!Regex.IsMatch(text, $@"\b{Regex.Escape(segment)}\b", RegexOptions.CultureInvariant))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ReadText(string fullPath, IDictionary<string, string?>? cache)
    {
        if (cache is not null && cache.TryGetValue(fullPath, out var cached))
        {
            return cached;
        }

        var text = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
        cache?.TryAdd(fullPath, text);
        return text;
    }
}
