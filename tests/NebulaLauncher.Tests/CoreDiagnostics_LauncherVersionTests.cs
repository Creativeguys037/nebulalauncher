using System.Reflection;
using NebulaLauncher.Core.Diagnostics;
using NebulaLauncher.ViewModels;
using Xunit;

namespace NebulaLauncher.Tests;

/// <summary>
/// 对外版本号的唯一来源：<c>Directory.Build.props</c> 里的 <c>Version</c>。
///
/// 三处展示（设置页「关于」、崩溃导出的 <c>system.txt</c>、「复制诊断信息」）都从这里取，
/// 断言因此写在 <see cref="DiagnosticsSnapshot"/> 这个公共 seam 上，不碰 Avalonia 类型。
/// 期望值一律写死字面量：跟着被测代码算出来的断言，代码错了它也照样通过。
/// </summary>
public class CoreDiagnostics_LauncherVersionTests
{
    /// <summary>CI 会把 commit hash 拼在 <c>+</c> 后面，对外只该看到语义版本。</summary>
    [Fact]
    public void 带哈希后缀的信息版本只留语义版本()
    {
        var actual = DiagnosticsSnapshot.FormatLauncherVersion(
            "1.0.0+d375e98fbd1460c07c7214b055265cf77f562aee",
            new Version(1, 0, 0, 0));

        Assert.Equal("1.0.0", actual);
    }

    /// <summary>
    /// 唯一来源的落点：仓库级构建属性里的 <c>Version</c> 决定程序集信息。
    /// 断言的是程序集上真实挂着的字符串，不是启动器自己的解析结果——
    /// 解析层即使把脏值洗干净了，产物本身仍然要干净。
    /// </summary>
    [Fact]
    public void 程序集信息版本就是仓库级构建属性里的版本()
    {
        var informational = typeof(DiagnosticsSnapshot).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.Equal("1.0.0", informational);
    }

    /// <summary>
    /// 验收点：<see cref="DiagnosticsSnapshot.LauncherVersionOf"/> 对当前程序集返回 <c>1.0.0</c>。
    /// 用 App 程序集而不是 Core：设置页与崩溃导出读的正是它。
    /// </summary>
    [Fact]
    public void 从当前程序集读出语义版本()
    {
        var actual = DiagnosticsSnapshot.LauncherVersionOf(typeof(SettingsViewModel).Assembly);

        Assert.Equal("1.0.0", actual);
    }

    /// <summary>设置页「关于」要显示 <c>v1.0.0</c>：<c>v</c> 是展示层加的前缀，不属于版本本身。</summary>
    [Fact]
    public void 展示版本带v前缀()
    {
        Assert.Equal("v1.0.0", DiagnosticsSnapshot.DisplayVersion("1.0.0"));
    }

    /// <summary>「复制诊断信息」传进来的已经是展示串；再补一次前缀会变成 <c>vv1.0.0</c>。</summary>
    [Fact]
    public void 已经是展示串的版本不重复加前缀()
    {
        Assert.Equal("v1.0.0", DiagnosticsSnapshot.DisplayVersion("v1.0.0"));
    }

    /// <summary>读不出版本时的占位值不是版本号，加前缀会变成「v未知」这种半成品。</summary>
    [Fact]
    public void 未知版本不补前缀()
    {
        Assert.Equal(DiagnosticsSnapshot.UnknownVersion, DiagnosticsSnapshot.DisplayVersion(DiagnosticsSnapshot.UnknownVersion));
    }

    /// <summary>设置页「关于」那一行就是这个组合：程序集 → 对外展示串。</summary>
    [Fact]
    public void 展示版本按程序集取()
    {
        var actual = DiagnosticsSnapshot.DisplayVersionOf(typeof(SettingsViewModel).Assembly);

        Assert.Equal("v1.0.0", actual);
    }

    /// <summary>
    /// 崩溃导出的 <c>system.txt</c> 与设置页「复制诊断信息」都调 <see cref="DiagnosticsSnapshot.Create"/>；
    /// 版本行必须和设置页「关于」同一个字面，否则用户贴到 issue 里的两处版本对不上。
    /// </summary>
    [Fact]
    public void 诊断文本的版本行与设置页同一字面()
    {
        var text = DiagnosticsSnapshot.Create(new DiagnosticsInput
        {
            LauncherVersion = DiagnosticsSnapshot.LauncherVersionOf(typeof(SettingsViewModel).Assembly),
            GeneratedAt = new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.FromHours(8)),
        });

        Assert.Contains("版本: v1.0.0", text, StringComparison.Ordinal);
    }

    /// <summary>拿不到版本时整行省略，不能出现「版本: 」这种半截行。</summary>
    [Fact]
    public void 诊断文本没有版本时不写版本行()
    {
        var text = DiagnosticsSnapshot.Create(new DiagnosticsInput
        {
            GeneratedAt = new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.FromHours(8)),
        });

        Assert.DoesNotContain(
            text.Split(Environment.NewLine),
            line => line.StartsWith("版本:", StringComparison.Ordinal));
    }

    /// <summary>null 必须原样返回：诊断文本靠它整行省略，不能变成「版本: v」。</summary>
    [Fact]
    public void 空版本保持为空()
    {
        Assert.Null(DiagnosticsSnapshot.DisplayVersion(null));
        Assert.Equal(string.Empty, DiagnosticsSnapshot.DisplayVersion(string.Empty));
    }
}
