using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Instances;
using NebulaLauncher.Core.Java;
using NebulaLauncher.Core.Models;
using NebulaLauncher.Core.Paths;
using NebulaLauncher.Core.Servers;
using NebulaLauncher.Core.Settings;
using NebulaLauncher.Core.Versions;

namespace NebulaLauncher.Core.Diagnostics;

/// <summary>
/// 发布包自检：在真实的单文件裁剪产物里，把几条会落盘的 JSON 通路各跑一遍往返。
///
/// 背景见 #26：<c>PublishTrimmed=true</c> 下 System.Text.Json 的反射元数据被整关掉，
/// 任何一个漏改的 <c>JsonSerializer</c> 调用点都不会在编译期报错，只会等到用户点
/// 「保存设置」时才抛 <c>NotSupportedException</c>，而且被上层 catch 成一句中文提示。
/// 单元测试跑的是未裁剪的 Debug 程序集，永远碰不到这个面。所以裁剪包必须自己验一次。
///
/// 输出是给脚本读的：每行 <c>json-selfcheck.[OK|FAIL].&lt;名称&gt;</c>，
/// 末尾 <c>json-selfcheck.summary=passed/total</c>。任一项失败，调用方应以非零退出码收场。
/// </summary>
public static class JsonSelfCheck
{
    /// <summary>在临时目录里跑完全部往返并清理，返回逐行结果。</summary>
    /// <param name="workRoot">
    /// 自检工作目录的父目录；null 表示系统临时目录。发布自检传产物目录：
    /// 部分机器的安全软件按进程拦 <c>%TEMP%</c> 写入（见 <c>Program.TryProbeDirectory</c> 的注释），
    /// 那会把目录问题伪装成 JSON 通路失败。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<IReadOnlyList<string>> RunAsync(
        string? workRoot = null,
        CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        var passed = 0;
        var total = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            total++;
            if (ok)
            {
                passed++;
                lines.Add($"json-selfcheck.OK.{name}");
            }
            else
            {
                lines.Add($"json-selfcheck.FAIL.{name}" + (detail is null ? string.Empty : ";" + detail));
            }
        }

        var parent = string.IsNullOrWhiteSpace(workRoot) ? Path.GetTempPath() : workRoot;
        var root = Path.Combine(parent, ".json-selfcheck-" + Guid.NewGuid().ToString("N"));
        var minecraft = Path.Combine(root, "minecraft");

        try
        {
            Directory.CreateDirectory(minecraft);

            // enableFallback: false —— 自检要的是「我指定的目录到底能不能读写」，
            // 回退到 %LOCALAPPDATA% 会把失败掩盖成成功。
            var paths = new LauncherPaths(root, minecraft, enableFallback: false);
            paths.EnsureCreated();

            var log = new SilentLog();

            // ---- 设置：issue #26 的原始症状就在这里（保存设置失败）----
            var settings = new SettingsService(paths, log);
            await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            settings.Current.MaxMemoryMb = 2048;
            settings.Current.MinMemoryMb = 1024;
            settings.Current.DownloadSource = DownloadSource.Official;
            settings.Current.ExtraJvmArguments = "-XX:+UseG1GC";
            await settings.SaveAsync(cancellationToken).ConfigureAwait(false);

            var settingsJson = await File.ReadAllTextAsync(paths.SettingsFile, cancellationToken).ConfigureAwait(false);

            // 落盘格式是兼容性契约：camelCase 字段名 + 枚举写名字。
            // 改了口径的回归（例如直接用 JsonSerializerContext.Default）在这里就会现形。
            Check("settings.camelCase", settingsJson.Contains("\"maxMemoryMb\"", StringComparison.Ordinal), settingsJson);
            Check(
                "settings.enumAsName",
                settingsJson.Contains("\"downloadSource\": \"Official\"", StringComparison.Ordinal),
                settingsJson);

            var settingsReread = new SettingsService(paths, log);
            await settingsReread.LoadAsync(cancellationToken).ConfigureAwait(false);
            Check("settings.roundTrip", settingsReread.Current.MaxMemoryMb == 2048
                                        && settingsReread.Current.DownloadSource == DownloadSource.Official
                                        && settingsReread.Current.ExtraJvmArguments == "-XX:+UseG1GC");

            // ---- 服务器列表 ----
            var servers = new ServerService(paths, log);
            await servers.LoadAsync(cancellationToken).ConfigureAwait(false);
            await servers.AddAsync(new ServerEntry { Name = "自检", Host = "localhost", Port = 25565 }, cancellationToken)
                .ConfigureAwait(false);

            var serversReread = new ServerService(paths, log);
            await serversReread.LoadAsync(cancellationToken).ConfigureAwait(false);
            Check("servers.roundTrip", serversReread.Servers.Any(s => s.Host == "localhost"));

            // ---- 版本收藏 ----
            var favorites = new VersionFavorites(paths, log);
            await favorites.LoadAsync(cancellationToken).ConfigureAwait(false);
            await favorites.ToggleFavoriteAsync("1.20.1", cancellationToken).ConfigureAwait(false);

            var favoritesReread = new VersionFavorites(paths, log);
            await favoritesReread.LoadAsync(cancellationToken).ConfigureAwait(false);
            Check("favorites.roundTrip", favoritesReread.IsFavorite("1.20.1"));

            // ---- 实例（含 InstanceIconKind / GcKind 两个枚举字段）----
            var instances = new InstanceService(paths, log);
            await instances.LoadAsync(cancellationToken).ConfigureAwait(false);
            var created = await instances.CreateAsync("自检实例", "1.20.1", cancellationToken).ConfigureAwait(false);
            created.IconKind = InstanceIconKind.Custom;
            created.GcKind = GcKind.G1;
            await instances.SaveAsync(created, cancellationToken).ConfigureAwait(false);

            var instancesReread = new InstanceService(paths, log);
            await instancesReread.LoadAsync(cancellationToken).ConfigureAwait(false);
            var back = instancesReread.Instances.FirstOrDefault(i => i.Id == created.Id);
            Check(
                "instances.roundTrip",
                back is not null && back.IconKind == InstanceIconKind.Custom && back.GcKind == GcKind.G1);

            // ---- Java 路径列表 ----
            const string JavaPath = @"C:\Program Files\Java\jdk-17\bin\javaw.exe";
            using (var javaPaths = new JavaPathList(root, "java-paths.json", log))
            {
                await javaPaths.LoadAsync(cancellationToken).ConfigureAwait(false);
                await javaPaths.AddAsync(JavaPath, cancellationToken).ConfigureAwait(false);
            }

            using (var javaPathsReread = new JavaPathList(root, "java-paths.json", log))
            {
                await javaPathsReread.LoadAsync(cancellationToken).ConfigureAwait(false);
                Check("javaPaths.roundTrip", javaPathsReread.Contains(JavaPath));
            }
        }
        catch (Exception ex)
        {
            // 走到这里说明有通路直接抛异常（不是静默回退），把类型与消息交给脚本，便于直接定位。
            total++;
            lines.Add($"json-selfcheck.FAIL.unexpected;{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录清不掉不影响自检结论。
            }
        }

        lines.Add($"json-selfcheck.summary={passed}/{total}");
        return lines;
    }

    /// <summary>自检用的静默日志：不让业务日志混进脚本要读的 stdout。</summary>
    private sealed class SilentLog : IAppLog
    {
        public string LogFilePath => string.Empty;

        public void Debug(string message)
        {
        }

        public void Info(string message)
        {
        }

        public void Warn(string message, Exception? ex = null)
        {
        }

        public void Error(string message, Exception? ex = null)
        {
        }
    }
}
