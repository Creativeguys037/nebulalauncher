namespace NebulaLauncher.ViewModels;

/// <summary>
/// 「把网络皮肤应用到皮肤站」的结果（票 #61）。
///
/// <para>
/// 这是跨页面的动作：发起方在皮肤页，执行方是云端衣柜。结果里带
/// <see cref="NeedsLogin"/>，发起方据此切到云端衣柜页签，而不是在这里偷偷登录。
/// </para>
/// </summary>
public sealed record CloudClosetApplyResult(
    bool Succeeded,
    bool NeedsLogin,
    string Text,
    bool CanRetry = false,
    int Tid = 0,
    string? PlayerName = null)
{
    public static CloudClosetApplyResult Ok(string text, int tid, string playerName) =>
        new(true, false, text, false, tid, playerName);

    public static CloudClosetApplyResult Fail(string text, bool needsLogin = false, bool canRetry = false) =>
        new(false, needsLogin, text, canRetry);

    public static CloudClosetApplyResult LoginRequired(string text) => new(false, true, text);
}

/// <summary>
/// 网络皮肤卡片下方的结果条（票 #61）。和官方换肤的 <c>OfficialFeedback</c> 分开：
/// 两条链路的成功/失败不能互相覆盖。
/// </summary>
public sealed record CloudApplyFeedback(string Text, bool IsError, bool CanRetry, bool GoToCloset);
