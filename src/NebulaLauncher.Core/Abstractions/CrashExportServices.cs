using NebulaLauncher.Core.Diagnostics;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.Core.Abstractions;

/// <summary>
/// 一次崩溃导出的输入。
///
/// 刻意只收「数据」不收路径：目标目录由调用方（用户在文件夹选择器里选的）另传，
/// 这样服务本身不知道 UI 长什么样，单测可以直接对着临时目录跑。
/// </summary>
/// <param name="Report">本次崩溃的归因结论。</param>
/// <param name="GameOutputLines">
/// 游戏进程 stdout/stderr 的行快照。导出时只取尾部
/// <see cref="Launching.CrashAnalyzer.DefaultTailLines"/> 行——崩溃前 3 小时的日志对排查没用。
/// </param>
/// <param name="HsErrText">JVM 的 <c>hs_err_pid*.log</c> 文本；没有原生崩溃时为 null。</param>
/// <param name="System">
/// 系统快照输入。为 null 时仍然导出 <c>system.txt</c>，
/// 但只有平台与 .NET 版本这些 Core 自己能取到的字段。
/// </param>
/// <param name="Mods">
/// 当前实例 mods 目录的快照。为 null 或空集合时仍然导出 <c>mods-list.txt</c>，
/// 内容写「未安装模组」——条目数恒定，接收方不会以为漏打包。
/// </param>
/// <param name="ModsError">读取 mods 目录失败的原因；非 null 时写进 <c>mods-list.txt</c> 代替列表。</param>
public sealed record CrashExportRequest(
    CrashReport Report,
    IReadOnlyList<string> GameOutputLines,
    string? HsErrText = null,
    DiagnosticsInput? System = null,
    IReadOnlyList<ModSnapshot>? Mods = null,
    string? ModsError = null);

/// <summary>
/// 一次崩溃导出的结果。
/// 失败不抛异常：<see cref="Error"/> 是可直接展示给用户的中文原因。
/// </summary>
/// <param name="Success">zip 是否已落盘。</param>
/// <param name="ArchivePath">产出的 zip 绝对路径；失败时为 null。</param>
/// <param name="Bytes">zip 字节数。</param>
/// <param name="Entries">实际写进 zip 的条目名，按写入顺序。</param>
/// <param name="Error">失败原因；成功时为 null。</param>
public sealed record CrashExportResult(
    bool Success,
    string? ArchivePath = null,
    long Bytes = 0,
    IReadOnlyList<string>? Entries = null,
    string? Error = null)
{
    /// <summary>成功。</summary>
    public static CrashExportResult Ok(string archivePath, long bytes, IReadOnlyList<string> entries) =>
        new(true, archivePath, bytes, entries);

    /// <summary>失败，带上中文原因。</summary>
    public static CrashExportResult Fail(string error) => new(false, Error: error);
}

/// <summary>
/// 崩溃报告导出：把结论、游戏输出尾部、<c>hs_err</c>、启动器日志尾部、系统快照与模组列表
/// 打成一个**脱敏** zip，让用户能直接发给他人求助。
///
/// 不做的事：不上传、不发网络请求、不删源文件。
/// </summary>
public interface ICrashExportService
{
    /// <summary>导出文件名前缀，形如 <c>nebula-crash-20261022-134000.zip</c>。</summary>
    public const string FileNamePrefix = "nebula-crash";

    /// <summary>
    /// 导出一份崩溃报告包。
    /// </summary>
    /// <param name="request">导出内容。</param>
    /// <param name="targetDirectory">目标目录绝对路径；不存在会被创建。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CrashExportResult> ExportAsync(
        CrashExportRequest request,
        string targetDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 目标目录是否可写。写一个立即删除的探针文件来判定。
    ///
    /// 与启动期的「不要用临时文件探目录」相反：那时用户没指定目录、探针会被安全软件误判；
    /// 这里是用户自己选的导出目录，写不进就是写不进，必须提前告诉用户。
    /// </summary>
    bool CanWriteTo(string? directory);
}
