using System.Runtime.CompilerServices;

// 任务栏进度的节流判定（ShellProgress.ShouldReport）是纯函数，时间源由参数传入，
// 只有直接调用才能在「不 sleep」的前提下验证 100ms / 0.01 两条阈值。
// 只给测试程序集开放这一个 internal 成员，不把节流器变成公开 API。
[assembly: InternalsVisibleTo("NebulaLauncher.Tests")]
