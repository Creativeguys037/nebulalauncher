namespace NebulaLauncher.Skinning;

/// <summary>
/// 官方档案里的一件披风。<paramref name="IsActive"/> 为 true 表示账户此刻正披着它。
///
/// <para>
/// 披风由官方发放，客户端只能读取与切换，不能上传（票 #61）。
/// <paramref name="Url"/> 是官方 CDN 的纹理地址，列表缩略图直接拿它显示，
/// 会话服务器还没同步时也用它先顶上即时预览。
/// </para>
/// </summary>
public sealed record MojangCape(string Id, bool IsActive, string? Url, string? Alias);

/// <summary>官方 <c>GET /minecraft/profile</c> 里这一屏需要的两样东西：披风列表与当前皮肤图。</summary>
public sealed record MojangProfile(IReadOnlyList<MojangCape> Capes, string? SkinUrl);

/// <summary>
/// 读取官方档案的结果。失败映射与 <see cref="SkinUploadResult"/> 同源（401 去账户页、429/5xx 可重试），
/// 只是多带一个成功值；单独一个类型比给上传结果硬塞泛型更不容易误用。
/// </summary>
public sealed class MojangProfileResult
{
    private MojangProfileResult(MojangProfile? value, string? errorText, bool isAuthError, bool isRetryable)
    {
        Value = value;
        ErrorText = errorText;
        IsAuthError = isAuthError;
        IsRetryable = isRetryable;
    }

    public MojangProfile? Value { get; }

    public string? ErrorText { get; }

    public bool IsAuthError { get; }

    public bool IsRetryable { get; }

    public bool Succeeded => Value is not null;

    public static MojangProfileResult Ok(MojangProfile profile) => new(profile, null, false, false);

    public static MojangProfileResult Fail(string errorText, bool isAuthError = false, bool isRetryable = false) =>
        new(null, errorText, isAuthError, isRetryable);
}
