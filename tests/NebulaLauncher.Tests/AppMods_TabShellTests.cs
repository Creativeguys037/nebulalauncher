using NebulaLauncher.Core.Models;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher.Tests;

/// <summary>
/// 内容页外壳的页签状态（票 #90）：五个页签 → 四类内容 + 皮肤占位。
///
/// 这里只验证状态机，不碰布局。为什么皮肤要走单独的开关而不是第五个
/// <see cref="ContentKind"/>：皮肤有独立的来源、体型与 3D 预览流程（#92），
/// 不是「mods/ 旁边的一个目录」，塞进 ContentKind 会让扫描器为它凭空造一套目录约定。
/// </summary>
public class AppMods_TabShellTests
{
    [Fact]
    public void 默认停在模组页签且内容视图可见()
    {
        var viewModel = new ModsViewModel(new EmptyServices());

        Assert.Equal(0, viewModel.TabIndex);
        Assert.True(viewModel.IsModTabActive);
        Assert.False(viewModel.IsSkinTabActive);
        Assert.True(viewModel.IsContentViewActive);
    }

    [Fact]
    public void 切到皮肤页签不改变当前内容分类()
    {
        var viewModel = new ModsViewModel(new EmptyServices());

        viewModel.TabIndex = 4;

        Assert.Equal(4, viewModel.TabIndex);
        Assert.True(viewModel.IsSkinTabActive);
        Assert.False(viewModel.IsContentViewActive);

        // 分类保持在切走之前那一类：切回来不必重扫，也不会因为「皮肤」把 ActiveKind 改成未定义值。
        Assert.Equal(ContentKind.Mod, viewModel.ActiveKind);
    }

    [Fact]
    public void 从皮肤切回内容页签后内容视图恢复()
    {
        var viewModel = new ModsViewModel(new EmptyServices());

        viewModel.TabIndex = 4;
        viewModel.TabIndex = 0;

        Assert.Equal(0, viewModel.TabIndex);
        Assert.False(viewModel.IsSkinTabActive);
        Assert.True(viewModel.IsContentViewActive);
        Assert.True(viewModel.IsModTabActive);
    }

    [Fact]
    public void 皮肤页签下统计文案为空()
    {
        var viewModel = new ModsViewModel(new EmptyServices());

        viewModel.TabIndex = 4;

        // 皮肤没有模组数可报：这里留着「0 个模组 · 0 个可更新 · 0 个已选中」只会误导。
        Assert.Equal(string.Empty, viewModel.StatsText);
    }

    /// <summary>
    /// 票面「五页签各保留自己的筛选」：模组页搜过、筛过，切去资源包再切回来要原样恢复，
    /// 而资源包自己的那份是空的（没搜过就是空搜索词 + 全部）。
    /// </summary>
    [Fact]
    public void 切走再切回保留各自的搜索词与筛选()
    {
        var viewModel = new ModsViewModel(new EmptyServices());

        viewModel.SearchText = "sodium";
        viewModel.SetFilterCommand.Execute(ModsFilter.Enabled);

        // 切到资源包：它自己还没有状态，应当是空搜索词 + 全部。
        viewModel.TabIndex = 1;
        Assert.Equal(ContentKind.ResourcePack, viewModel.ActiveKind);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.True(viewModel.IsFilterAll);

        // 在资源包页搜别的词，然后切回模组：两边各记各的。
        viewModel.SearchText = "faithful";
        viewModel.TabIndex = 0;
        Assert.Equal("sodium", viewModel.SearchText);
        Assert.True(viewModel.IsFilterEnabled);

        viewModel.TabIndex = 1;
        Assert.Equal("faithful", viewModel.SearchText);
        Assert.True(viewModel.IsFilterAll);
    }

    /// <summary>没有注册任何服务的容器：构造 ModsViewModel 时每个服务都取到 null，走「服务未接入」分支。</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}