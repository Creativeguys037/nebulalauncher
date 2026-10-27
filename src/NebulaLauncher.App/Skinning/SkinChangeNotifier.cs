namespace NebulaLauncher.Skinning;

/// <summary>「皮肤已变更」通知携带的信息。</summary>
/// <param name="Reason">谁触发的（例如「官方换肤」「皮肤站换肤」「离线体型切换」）。只用于日志与排查。</param>
public sealed class SkinChangedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

/// <summary>
/// 皮肤变更的广播口（票 #44 AC2）。形状是给票 #43 直接复用的：
/// 谁的换肤成功，谁就 <see cref="Notify"/> 一次；谁展示皮肤，谁就订阅并自己重取。
///
/// <para>
/// 为什么需要它：侧边栏那张 3D 卡只监听 <c>IAccountService.AccountsChanged</c>。
/// 换肤改的是<b>服务端材质</b>，<c>Account</c> 的字段一个都没动，所以那条事件根本不会发，
/// 卡片会继续显示旧皮肤。让 <c>IAccountService</c> 假装账户变了是错的方向——账户没变，
/// 谎报一次会让账户页、首页、启动链全都白刷一遍。
/// </para>
///
/// <para>
/// 发布与订阅解耦：换肤的那一头（皮肤页）拿不到侧边栏卡的引用，也不该拿。
/// </para>
/// </summary>
public sealed class SkinChangeNotifier
{
    /// <summary>有人换肤成功了。订阅方必须自行切回 UI 线程（本类不假设调用线程）。</summary>
    public event EventHandler<SkinChangedEventArgs>? SkinChanged;

    /// <summary>当前订阅者数量，只用于排查「通知发出去了但没人听」。</summary>
    public int SubscriberCount => SkinChanged?.GetInvocationList().Length ?? 0;

    /// <summary>
    /// 广播一次。调用方在换肤成功后调，且必须在 UI 线程之外也能安全调用。
    ///
    /// 单个订阅方抛异常不会中断其余的：通知是换肤成功之后的附带动作，
    /// 一个订阅方有 bug 不该把一次已成功的换肤回报成失败。
    /// </summary>
    public void Notify(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var handlers = SkinChanged;
        if (handlers is null)
        {
            return;
        }

        var arguments = new SkinChangedEventArgs(reason);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<SkinChangedEventArgs>)handler)(this, arguments);
            }
            catch (Exception)
            {
                // 刻意吞掉：见方法注释。订阅方自己负责记录日志。
            }
        }
    }
}

/// <summary>
/// 进程内的默认通知实例。
///
/// 容器里注册了就优先用注册的那个（测试与将来多实例都靠它）；没注册时双方落到同一个静态实例，
/// 这样「换肤成功但侧边栏没变」不会在没注册的环境里静默复现。
/// 建议由 Lead 在 <c>ConfigureServices</c> 注册单例（见交付说明）。
/// </summary>
public static class SkinChangeBus
{
    /// <summary>进程级默认实例。</summary>
    public static SkinChangeNotifier Current { get; } = new();
}
