using System;
using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Services;

/// <summary>
/// 首帧之前的服务预热：把「窗口一建出来就要用」的状态从磁盘读进内存。
///
/// 为什么必须同步读，而不是交给页面的异步加载：
/// 设置决定窗口尺寸（<c>MainWindow</c> 构造时就要值，见 HANDOVER §6.20）；
/// 账户决定首页卡片、版本页启动与启动链拿到的 <see cref="IAccountService.ActiveAccount"/>，
/// 而首页页面的构造发生在 <c>new MainWindow(Services)</c> 里。
/// 账户原先只在进入账户页时才 <c>LoadAsync</c>，于是刚启动的程序处处显示「未登录」，
/// 版本页点启动被「还没有账户」挡下，必须去账户页转一圈才恢复（R59，见 HANDOVER §6.71）。
///
/// 失败只写日志、不阻断启动：读不出来就按空状态跑，用户仍进得了设置页与账户页修。
/// 这是 <c>CONVENTIONS.md</c> 「禁止 GetAwaiter().GetResult()」的唯一例外，
/// 成立的前提是被调的 <c>LoadAsync</c> 全程 <c>ConfigureAwait(false)</c>（两处都已满足）。
/// </summary>
internal static class StartupState
{
    /// <summary>建窗口之前调用。<see cref="ISettingsService"/> / <see cref="IAccountService"/> 缺失时跳过对应一项。</summary>
    public static void LoadBeforeFirstFrame(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var log = ResolveLog(services);

        LoadSettingsBlocking(services, log);
        LoadAccountsBlocking(services, log);
    }

    private static void LoadSettingsBlocking(IServiceProvider services, IAppLog? log)
    {
        try
        {
            // 取服务与等待加载放在同一个 try 里：两者都是惰性工厂，构造失败同样不能带崩启动。
            if (services.GetService(typeof(ISettingsService)) is ISettingsService settings)
            {
                settings.LoadAsync().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            // LoadAsync 内部已把损坏文件回退成默认值；这里只防同步等待与工厂构造本身抛错。
            log?.Warn("启动时同步读取设置失败", ex);
        }
    }

    private static void LoadAccountsBlocking(IServiceProvider services, IAppLog? log)
    {
        try
        {
            if (services.GetService(typeof(IAccountService)) is IAccountService accounts)
            {
                accounts.LoadAsync().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            log?.Warn("启动时同步读取账户失败", ex);
        }
    }

    private static IAppLog? ResolveLog(IServiceProvider services)
    {
        try
        {
            return services.GetService(typeof(IAppLog)) as IAppLog;
        }
        catch (Exception)
        {
            // 日志都取不到就静默继续：读到状态比写到日志重要。
            return null;
        }
    }
}
