using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Launching;

/// <summary>
/// 解析「这次启动该用哪段 JVM 参数」。
///
/// 优先级：实例显式参数 &gt; 选中的预设 &gt; 全局原文。
/// 单独抽成纯函数是为了让「设置页选中的预设真的生效」这件事可以脱离 UI 与进程验证。
/// </summary>
public static class JvmPresetResolver
{
    /// <summary>
    /// 返回最终要传给 JVM 的用户参数原文。
    ///
    /// 注意 <see cref="AppSettings.ExtraJvmArguments"/> 与预设参数同格式（空格或换行分隔），
    /// 调用方不需要区分来源——后续的 GC 清洗与编码参数注入对两者一视同仁。
    /// </summary>
    public static string Resolve(AppSettings? settings)
    {
        if (settings is null)
        {
            return string.Empty;
        }

        var preset = Find(settings, settings.ActiveJvmPresetId);

        return preset is null
            ? settings.ExtraJvmArguments ?? string.Empty
            : preset.Arguments ?? string.Empty;
    }

    /// <summary>按 Id 找预设；Id 为空或找不到时返回 null。</summary>
    public static JvmPreset? Find(AppSettings? settings, string? presetId)
    {
        if (settings?.JvmPresets is not { Count: > 0 } presets || string.IsNullOrWhiteSpace(presetId))
        {
            return null;
        }

        foreach (var preset in presets)
        {
            if (string.Equals(preset.Id, presetId, StringComparison.Ordinal))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>
    /// 实例级参数与预设的合并结果。实例显式设置过（非 null）就用实例的，
    /// 空字符串也算显式——那是「这个实例不要任何额外参数」的意思，不能回落到预设。
    /// </summary>
    public static string ResolveForInstance(AppSettings? settings, string? instanceJvmArguments)
    {
        return instanceJvmArguments ?? Resolve(settings);
    }
}
