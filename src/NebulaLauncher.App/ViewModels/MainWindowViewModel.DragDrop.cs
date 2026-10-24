using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 拖拽导入分派。抄 PCL2 <c>FormMain</c> 的 <c>DragFile</c> 分派表：
/// 按扩展名决定「装模组 / 装整合包 / 提示不支持」，并把结果写到状态栏。
///
/// 放在单独 partial 文件里：主壳文件已经很长，而拖拽是一个完整的输入通道，
/// 和页面注册、导航逻辑没有交集。
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly string[] ModExtensions = [".jar", ".litemod"];

    private static readonly string[] ModpackExtensions = [".zip", ".mrpack", ".rar"];

    /// <summary>
    /// 处理一批拖进窗口的本地文件。不抛异常：失败原因写进状态栏。
    /// </summary>
    /// <param name="paths">本地绝对路径（文件夹会被忽略）。</param>
    public async Task HandleDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        var files = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToList();

        if (files.Count == 0)
        {
            StatusText = "拖入的内容不是文件（暂不支持拖入文件夹）。";
            return;
        }

        var modFiles = files.Where(IsModFile).ToList();
        var packFiles = files.Where(IsModpackFile).ToList();
        var unknown = files
            .Where(path => !IsModFile(path) && !IsModpackFile(path))
            .ToList();

        if (modFiles.Count > 0)
        {
            await ImportModsAsync(modFiles).ConfigureAwait(true);
            return;
        }

        if (packFiles.Count > 0)
        {
            await PreviewModpackAsync(packFiles).ConfigureAwait(true);
            return;
        }

        StatusText = $"不认识的文件：{string.Join("、", unknown.Select(Path.GetFileName).Take(3))}。"
                     + "支持 .jar / .litemod（模组）与 .zip / .mrpack（整合包）。";
    }

    private async Task ImportModsAsync(IReadOnlyList<string> files)
    {
        if (Services.GetService(typeof(IContentService)) is not IContentService content ||
            Services.GetService(typeof(IInstanceContext)) is not IInstanceContext context)
        {
            StatusText = "模组导入需要内容服务，当前未就绪。";
            return;
        }

        try
        {
            var target = context.GameDirectory;
            var imported = await content
                .ImportAsync(target, ContentKind.Mod, files)
                .ConfigureAwait(true);

            if (imported.Count == 0)
            {
                StatusText = "没有可导入的模组文件。";
                return;
            }

            StatusText = $"已导入 {imported.Count} 个模组到 {target}";

            // 跳到模组页：用户拖进来的下一步就是看它是否出现在列表里。
            Navigate(NavigationKeys.Mods);
        }
        catch (Exception ex)
        {
            StatusText = $"导入模组失败：{ex.Message}";
        }
    }

    private async Task PreviewModpackAsync(IReadOnlyList<string> files)
    {
        if (files.Any(path => path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "暂不支持 .rar 整合包，请先解压成 .zip 再拖入。";
            return;
        }

        if (Services.GetService(typeof(IModpackService)) is not IModpackService modpacks)
        {
            StatusText = "整合包服务未就绪。";
            return;
        }

        try
        {
            var preview = await modpacks.InspectAsync(files[0]).ConfigureAwait(true);

            StatusText = $"识别到整合包「{preview.Name}」："
                         + $"{preview.MinecraftVersion} / {preview.Loader}，"
                         + $"{preview.FileCount} 个文件。到「世界」页确认导入。";

            Navigate(NavigationKeys.Worlds);
        }
        catch (Exception ex)
        {
            StatusText = $"无法识别整合包：{ex.Message}";
        }
    }

    private static bool IsModFile(string path) =>
        ModExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsModpackFile(string path) =>
        ModpackExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
