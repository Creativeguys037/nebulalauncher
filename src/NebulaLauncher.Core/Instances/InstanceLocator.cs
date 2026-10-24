using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Instances;

/// <summary>
/// 实例选择的纯函数规则。
///
/// 版本页与首页都要回答同一个问题：「启动这个版本时该用哪个游戏目录」。
/// 规则集中在这里，避免两个页面各写一套，导致同一个版本在两个入口用不同目录启动。
/// </summary>
public static class InstanceLocator
{
    /// <summary>
    /// 按版本名找实例目录对应的实例。
    /// 优先使用 <paramref name="preferredInstanceId"/>（用户在版本页显式选中的实例），
    /// 其次取第一个 <see cref="InstanceInfo.VersionName"/> 相同者。
    /// 找不到返回 <c>null</c>，由调用方退回全局 .minecraft。
    /// </summary>
    public static InstanceInfo? FindForVersion(
        IReadOnlyList<InstanceInfo>? instances,
        string? versionName,
        string? preferredInstanceId = null)
    {
        if (instances is null || instances.Count == 0 || string.IsNullOrWhiteSpace(versionName))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredInstanceId))
        {
            var preferred = instances.FirstOrDefault(i =>
                string.Equals(i.Id, preferredInstanceId, StringComparison.Ordinal));

            // 选中实例绑定的版本与要启动的版本不一致时必须放弃它，
            // 否则会用 A 版本的实例目录去跑 B 版本，mods / saves 互相污染。
            if (preferred is not null &&
                string.Equals(preferred.VersionName, versionName, StringComparison.Ordinal))
            {
                return preferred;
            }
        }

        return instances.FirstOrDefault(i =>
            string.Equals(i.VersionName, versionName, StringComparison.Ordinal));
    }

    /// <summary>
    /// 生成不与现有实例重名的显示名：第一个用原名，之后追加 <c> (2)</c>、<c> (3)</c>。
    /// 同一版本创建多个实例是常态（不同模组组合），默认名直接用版本名会立刻撞名。
    /// </summary>
    public static string MakeUniqueName(string? baseName, IEnumerable<string>? existingNames)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "新实例" : baseName.Trim();

        if (existingNames is null)
        {
            return name;
        }

        var taken = new HashSet<string>(
            existingNames.Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(name))
        {
            return name;
        }

        for (var suffix = 2; suffix < 10000; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        // 同名后缀被占满（现实中不会发生）时退回时间戳，保证创建一定能成功。
        return $"{name} ({DateTime.Now:HHmmss})";
    }
}
