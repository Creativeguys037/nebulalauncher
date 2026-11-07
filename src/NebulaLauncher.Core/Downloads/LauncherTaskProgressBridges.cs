using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Downloads;

/// <summary>
/// 把 <see cref="DownloadProgress"/> 同时喂给调用方与任务中心。
///
/// 装在服务层而不是调用点：版本页、整合包页、世界页、设置页都会发起这些操作，
/// 每个调用方各接一次等于四份重复代码，而漏掉的那一处表现为
/// 「下载页上永远看不到这次操作」——没人会去翻调用点找原因。
/// </summary>
public static class LauncherTaskProgressBridge
{
    /// <summary>没有任务句柄时原样返回 <paramref name="inner"/>，不留一层空转发。</summary>
    public static IProgress<DownloadProgress>? Wrap(
        IProgress<DownloadProgress>? inner,
        ILauncherTaskHandle? task)
    {
        return task is null ? inner : new Bridge(inner, task);
    }

    /// <summary>
    /// 原样转发给调用方，同时折算成任务行的字节与副行。
    ///
    /// 「暂停时速度清零」由 <see cref="ILauncherTaskHandle.Pause"/> 负责，这里只按
    /// <see cref="DownloadProgress.IsPaused"/> 切状态：暂停中还显示「1.2 MB/s」
    /// 是上一版被反馈过的问题。
    /// </summary>
    private sealed class Bridge(IProgress<DownloadProgress>? inner, ILauncherTaskHandle task)
        : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            inner?.Report(value);

            if (value.IsPaused)
            {
                task.Pause();
            }
            else
            {
                task.Resume();
            }

            var stage = string.IsNullOrWhiteSpace(value.Stage) ? "下载文件" : value.Stage;
            task.Report(
                value.DownloadedBytes,
                value.TotalBytes,
                value.SpeedBytesPerSecond,
                LauncherTaskPresentation.DescribeDetail(
                    stage,
                    value.DownloadedBytes,
                    value.TotalBytes,
                    value.TotalFiles));
        }
    }
}

/// <summary>
/// 把 0–1 的比例进度接到任务行上。
///
/// 备份是唯一走比例而不是字节的链路（它压的是本地目录，没有下载速度可言），
/// 所以单独一个桥，不与字节桥混用。
/// </summary>
public static class LauncherTaskRatioBridge
{
    public static IProgress<double>? Wrap(IProgress<double>? inner, ILauncherTaskHandle? task)
    {
        return task is null ? inner : new Bridge(inner, task);
    }

    private sealed class Bridge(IProgress<double>? inner, ILauncherTaskHandle task) : IProgress<double>
    {
        public void Report(double value)
        {
            inner?.Report(value);
            task.ReportRatio(value);
        }
    }
}

/// <summary>任务句柄上处理比例进度的便捷写法。</summary>
public static class LauncherTaskHandleExtensions
{
    /// <summary>
    /// 用「千分之一」当虚拟字节通道来承载比例。
    ///
    /// 直接把 0–1 塞进字节通道会得到两个假数字：总量变成 1 B、速度恒为 0 B/s，
    /// 界面上就是「0.00/1 B」加一个永远不动的速度。换成 1000 份后百分比是对的，
    /// 速度那一格靠 <see cref="DownloadTaskCenter"/> 侧的空速度显示「—」，
    /// 不编造本地压缩的吞吐。
    /// </summary>
    public static void ReportRatio(this ILauncherTaskHandle? task, double ratio, string? detail = null)
    {
        if (task is null)
        {
            return;
        }

        var clamped = double.IsNaN(ratio) ? 0 : Math.Clamp(ratio, 0, 1);
        task.Report((long)Math.Round(clamped * RatioScale), RatioScale, 0, detail);
    }

    private const long RatioScale = 1000;
}