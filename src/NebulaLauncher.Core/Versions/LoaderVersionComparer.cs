namespace NebulaLauncher.Core.Versions;

/// <summary>
/// 加载器版本号比较：按分隔符切段，数字段按数值比、非数字段按忽略大小写的字符串比。
///
/// 为什么不能直接用字符串序：Forge / NeoForge 的构建号是多段数字，
/// <c>47.9.0</c> 的字典序排在 <c>47.10.0</c> 之前，回滚候选列表会把旧版本排在前面
/// （REVIEW-R34-R36 P3-6）。分段比较同时覆盖 Fabric Loader 的 <c>0.16.9</c> 与
/// NeoForge 的 <c>20.2.62-beta</c>。
/// </summary>
public sealed class LoaderVersionComparer : IComparer<string>
{
    /// <summary>无状态比较器，可安全共享。</summary>
    public static readonly LoaderVersionComparer Instance = new();

    private static readonly char[] Separators = ['.', '-', '+', '_'];

    private LoaderVersionComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var left = x.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var right = y.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var length = Math.Max(left.Length, right.Length);

        for (var index = 0; index < length; index++)
        {
            // 段数不同时短的一方按「空 → 0」处理：47.10 与 47.10.0 视为相等。
            var a = index < left.Length ? left[index] : "0";
            var b = index < right.Length ? right[index] : "0";

            var aNumeric = long.TryParse(a, out var aNumber);
            var bNumeric = long.TryParse(b, out var bNumber);

            var result = (aNumeric, bNumeric) switch
            {
                (true, true) => aNumber.CompareTo(bNumber),
                // 数字段排在非数字段之后：47.10 > 47.10-beta。
                (true, false) => 1,
                (false, true) => -1,
                _ => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
            };

            if (result != 0)
            {
                return result;
            }
        }

        // 各段全部相等即相等：47.10 与 47.10.0 视为同一个版本（短的一方补 0）。
        return 0;
    }
}
