using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Launching;
using NebulaLauncher.Core.Models;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 崩溃建议 + 它自带的那颗按钮。
///
/// Core 侧的 <see cref="CrashSuggestion"/> 从第一天起就带 <see cref="CrashSuggestion.ActionId"/>，
/// 75 条规则里也填了值，但界面层一直没消费——玩家看得到「去 Java 设置里换 17」这样的建议，
/// 却没有任何一步能直接执行。这个类型就是把那根线接上：中文标签、可点、执行期间禁点。
///
/// 动作本身不在这里做：真正要碰文件系统、导航、修版本的是 <see cref="HomeViewModel"/>，
/// 这里只负责把一次点击转成一次回调。
/// </summary>
public sealed partial class CrashSuggestionItem : ObservableObject
{
    private readonly Func<CrashSuggestion, Task> _run;

    public CrashSuggestionItem(CrashSuggestion suggestion, Func<CrashSuggestion, Task> run)
    {
        Suggestion = suggestion ?? throw new ArgumentNullException(nameof(suggestion));
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public CrashSuggestion Suggestion { get; }

    public string Title => Suggestion.Title;

    public string Detail => Suggestion.Detail;

    public bool IsError => Suggestion.Severity == CrashSeverity.Error;

    public bool IsWarning => Suggestion.Severity == CrashSeverity.Warning;

    /// <summary>动作按钮的中文标签；没有可执行动作时为 null（模板据此隐藏按钮）。</summary>
    public string? ActionLabel => Suggestion.ActionId switch
    {
        CrashAnalyzer.Actions.OpenJavaSettings => "打开 Java 设置",
        CrashAnalyzer.Actions.OpenModsFolder => "打开模组目录",
        CrashAnalyzer.Actions.RepairVersion => "修复该版本",
        CrashAnalyzer.Actions.OpenCrashReport => "打开崩溃报告",
        CrashAnalyzer.Actions.OpenLogFolder => "打开日志目录",
        CrashAnalyzer.Actions.DisableMod => "禁用这个模组",
        _ => null,
    };

    public bool HasAction => !string.IsNullOrWhiteSpace(ActionLabel);

    /// <summary>
    /// 按钮的悬停说明。<c>DisableMod</c> 必须带出具体文件名：
    /// 「禁用这个模组」这四个字在列表里出现过两次时，玩家根本不知道自己要点哪一个。
    /// </summary>
    public string? ActionTip => Suggestion.ActionArgument is { Length: > 0 } argument
        ? $"{ActionLabel}：{argument}"
        : ActionLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunAction))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isRunning;

    public bool CanRunAction => HasAction && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    private async Task RunAsync()
    {
        IsRunning = true;

        try
        {
            await _run(Suggestion).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
        }
    }
}