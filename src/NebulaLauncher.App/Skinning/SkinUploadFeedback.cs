namespace NebulaLauncher.Skinning;

/// <summary>
/// 一次换肤操作的界面反馈（票 #44 AC6 / AC7 / AC8）。
///
/// 三个入口按钮各自决定「给不给重试、要不要去账户页」，而这些判断只依赖
/// <see cref="SkinUploadResult"/> 的两个标记。把它放在这里而不是三次写进命令里：
/// 三处判断迟早会漂移，而漂移的那一处一定是最少人点的那个。
/// </summary>
/// <param name="Text">直接显示给用户的句子。</param>
/// <param name="IsError">true 表示这是一条错误，界面用危险色；false 表示成功。</param>
/// <param name="CanRetry">要不要给「重试」按钮。</param>
/// <param name="GoToAccountPage">要不要给「去账户页」按钮。</param>
public sealed record SkinUploadFeedback(string Text, bool IsError, bool CanRetry, bool GoToAccountPage)
{
    /// <summary>
    /// 由结果对象决定呈现。<paramref name="successText"/> 只在成功时用得上。
    ///
    /// 401（<see cref="SkinUploadResult.IsAuthError"/>）不给重试：同一个令牌重发还是 401，
    /// 用户真正要做的是去账户页刷新。其余错误只有在 <see cref="SkinUploadResult.IsRetryable"/>
    /// 时才给重试——400 是「图不对」，重发同一张图不会变对。
    /// </summary>
    public static SkinUploadFeedback For(SkinUploadResult result, string successText)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Succeeded)
        {
            return new SkinUploadFeedback(successText, IsError: false, CanRetry: false, GoToAccountPage: false);
        }

        return new SkinUploadFeedback(
            result.ErrorText ?? "换肤失败，原因未知。",
            IsError: true,
            CanRetry: result.IsRetryable && !result.IsAuthError,
            GoToAccountPage: result.IsAuthError);
    }

    /// <summary>本地拦下的失败（没发请求）：同样是错误，但永远不给重试按钮。</summary>
    public static SkinUploadFeedback LocalError(string text) =>
        new(text, IsError: true, CanRetry: false, GoToAccountPage: false);
}
