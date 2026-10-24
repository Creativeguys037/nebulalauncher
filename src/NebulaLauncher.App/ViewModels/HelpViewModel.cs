using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaLauncher.Core.Abstractions;
using NebulaLauncher.Core.Help;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 帮助中心。内容全部来自 <see cref="HelpCatalog"/>（Core 层，离线内置），
/// 本类只负责搜索、分类筛选与选中状态——这样帮助内容可以单测，UI 不用为内容变更改代码。
/// </summary>
public partial class HelpViewModel : PageViewModelBase
{
    private readonly IPlatformService? _platform;
    private readonly ILauncherPaths? _paths;

    /// <summary>构造参数可空：设计器与截图模式的容器里可能没有平台服务，页面仍要能渲染。</summary>
    public HelpViewModel(IPlatformService? platformService = null, ILauncherPaths? launcherPaths = null)
    {
        _platform = platformService;
        _paths = launcherPaths;

        foreach (var topic in HelpCatalog.Topics)
        {
            Topics.Add(topic);
        }

        Categories = ["全部", .. HelpCatalog.Categories];

        ApplyFilter();
    }

    public override string Title => "帮助";

    public override string? Subtitle => "内置离线帮助：功能说明与常见问题";

    public override string Key => NavigationKeys.Help;

    /// <summary>「全部」+ 内置分类。</summary>
    public IReadOnlyList<string> Categories { get; }

    /// <summary>全部条目，仅用于计数展示。</summary>
    public ObservableCollection<HelpTopic> Topics { get; } = new();

    /// <summary>当前筛选结果。</summary>
    public ObservableCollection<HelpTopic> FilteredTopics { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategory = "全部";

    [ObservableProperty]
    private HelpTopic? _selectedTopic;

    [ObservableProperty]
    private string _statusText = "内容随启动器一起分发，离线可用。";

    public bool HasNoMatch => FilteredTopics.Count == 0;

    public string CountText => $"共 {FilteredTopics.Count} 条 / {Topics.Count} 条";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SelectCategory(string? category)
    {
        if (!string.IsNullOrEmpty(category))
        {
            SelectedCategory = category;
        }
    }

    [RelayCommand]
    private void SelectTopic(HelpTopic? topic)
    {
        if (topic is not null)
        {
            SelectedTopic = topic;
        }
    }

    [RelayCommand]
    private async Task OpenLogFolderAsync()
    {
        if (_platform is null)
        {
            StatusText = "平台服务尚未接入，无法打开目录。";
            return;
        }

        try
        {
            await _platform.OpenFolderAsync(_paths?.LogsDirectory ?? _paths?.RootDirectory ?? string.Empty);
            StatusText = "已打开日志目录。";
        }
        catch (Exception ex)
        {
            StatusText = $"打开日志目录失败：{ex.Message}";
        }
    }

    /// <summary>先按关键词过滤，再按分类收窄；两者都为空时就是全部条目。</summary>
    private void ApplyFilter()
    {
        var filtered = HelpCatalog.Filter(HelpCatalog.Topics, SearchText);

        if (!string.Equals(SelectedCategory, "全部", StringComparison.Ordinal))
        {
            filtered = filtered
                .Where(topic => string.Equals(topic.Category, SelectedCategory, StringComparison.Ordinal))
                .ToList();
        }

        FilteredTopics.Clear();

        foreach (var topic in filtered)
        {
            FilteredTopics.Add(topic);
        }

        // 当前选中项被筛掉时自动落到第一条：右侧正文不能停在一条已经不在列表里的条目上。
        if (SelectedTopic is null || !filtered.Contains(SelectedTopic))
        {
            SelectedTopic = filtered.Count > 0 ? filtered[0] : null;
        }

        OnPropertyChanged(nameof(HasNoMatch));
        OnPropertyChanged(nameof(CountText));
    }
}
