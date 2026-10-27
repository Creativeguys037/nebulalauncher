using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// 账户皮肤头像：把账户解析成一张本地皮肤 PNG，供 UI 裁出 8×8 的脸。
///
/// 返回值是文件路径而不是位图：PNG 解码属于 UI 层，Core 不引 Avalonia 类型。
/// </summary>
public interface ISkinAvatarService
{
    /// <summary>
    /// 返回该账户头像 PNG 的绝对路径。离线账户、取不到真实皮肤、网络失败时都返回内置默认皮肤，
    /// 因此正常情况下永远有图可显示；只有连内嵌默认皮肤都读不出来时才返回 null。
    /// </summary>
    /// <param name="account">账户。为空或没有 UUID 时直接走默认皮肤。</param>
    /// <param name="refresh">true 丢弃已缓存的纹理地址与 PNG，重新向皮肤服务查询。</param>
    Task<string?> GetAvatarPathAsync(Account account, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>丢弃该账户缓存的纹理地址与 PNG 文件，下一次 <see cref="GetAvatarPathAsync"/> 会重新下载。</summary>
    void Invalidate(Account account);
}
