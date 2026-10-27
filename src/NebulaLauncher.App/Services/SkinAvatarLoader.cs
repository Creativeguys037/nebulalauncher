using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Services;

/// <summary>
/// 皮肤头像位图加载器：把 <see cref="ISkinAvatarService"/> 给出的 PNG 路径解成可直接绑定的位图。
///
/// 按账户缓存位图：账户页大卡、列表行与首页会各自请求同一账户的头像，
/// 每次重建页面都重新读盘解码会让切页有肉眼可见的停顿。
/// 解码放线程池：<see cref="Bitmap"/> 构造要走 Skia，几十毫秒起，不能压在 UI 线程上。
/// </summary>
public sealed class SkinAvatarLoader
{
    private readonly ISkinAvatarService? _service;
    private readonly ConcurrentDictionary<string, Bitmap> _bitmaps = new(StringComparer.Ordinal);

    public SkinAvatarLoader(IServiceProvider services)
    {
        _service = services.GetService(typeof(ISkinAvatarService)) as ISkinAvatarService;
    }

    /// <summary>
    /// 取账户头像位图。账户为空、服务未注册、解码失败都返回 null，调用方显示占位色块。
    /// </summary>
    /// <param name="account">账户。离线账户也会返回默认皮肤。</param>
    /// <param name="refresh">true 丢弃缓存，重新向皮肤服务查询并重新解码。</param>
    public async Task<Bitmap?> LoadAsync(Account? account, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (account is null || _service is null)
        {
            return null;
        }

        if (!refresh && _bitmaps.TryGetValue(account.Id, out var cached))
        {
            return cached;
        }

        var path = await _service.GetAvatarPathAsync(account, refresh, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return null;
        }

        var bitmap = await Task.Run(() => Decode(path), cancellationToken).ConfigureAwait(false);
        if (bitmap is not null)
        {
            _bitmaps[account.Id] = bitmap;
        }

        return bitmap;
    }

    /// <summary>丢掉该账户的位图缓存（换账户或清缓存后调用）。</summary>
    public void Invalidate(string accountId) => _bitmaps.TryRemove(accountId, out _);

    private static Bitmap? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            // 缓存文件被外部删掉、或半截写坏：返回 null，外层退到占位。
            return null;
        }
    }
}
