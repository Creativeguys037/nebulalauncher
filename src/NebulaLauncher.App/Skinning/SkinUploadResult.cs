namespace NebulaLauncher.Skinning;

/// <summary>
/// 一次官方换肤调用的结果：要么成功，要么带一句能直接给界面看的话。
///
/// 与 <see cref="SkinLibraryResult{T}"/> 同形（结果对象而非抛异常，CONVENTIONS §3），
/// 但多两个「界面要不要给重试按钮」的判据：<see cref="IsAuthError"/> 让人去账户页刷新，
/// <see cref="IsRetryable"/> 才允许原地重试。
/// </summary>
public sealed class SkinUploadResult
{
    private SkinUploadResult(string? errorText, bool isAuthError, bool isRetryable)
    {
        ErrorText = errorText;
        IsAuthError = isAuthError;
        IsRetryable = isRetryable;
    }

    /// <summary>成功时为 <c>null</c>；失败时是给用户看的句子。</summary>
    public string? ErrorText { get; }

    /// <summary>401：Minecraft access token 过期或无效。界面引导去账户页刷新，<b>不要</b>原地重试。</summary>
    public bool IsAuthError { get; }

    /// <summary>429 / 5xx / 网络异常：同一请求等一会儿再发可能就成功。</summary>
    public bool IsRetryable { get; }

    public bool Succeeded => ErrorText is null;

    public static SkinUploadResult Ok() => new(null, false, false);

    public static SkinUploadResult Fail(string errorText, bool isAuthError = false, bool isRetryable = false) =>
        new(errorText, isAuthError, isRetryable);
}
