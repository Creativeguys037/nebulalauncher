using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using NebulaLauncher.ViewModels;

namespace NebulaLauncher;

/// <summary>
/// 按命名约定把 ViewModel 解析为 View：
/// <c>NebulaLauncher.ViewModels.HomeViewModel</c> → <c>NebulaLauncher.Views.HomeView</c>。
///
/// 约定比显式注册表更省事：新增页面只要成对建两个文件，不改任何注册代码。
/// 找不到对应 View 时返回显式提示而不是崩溃，便于定位漏写的文件。
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        var viewModelType = param.GetType();
        var viewName = viewModelType.FullName!.Replace("ViewModels", "Views", StringComparison.Ordinal)
                                              .Replace("ViewModel", "View", StringComparison.Ordinal);

        var viewType = viewModelType.Assembly.GetType(viewName);
        if (viewType is null)
        {
            return new TextBlock
            {
                Text = $"未找到视图: {viewName}",
                Margin = new Avalonia.Thickness(16),
                Foreground = Avalonia.Media.Brushes.OrangeRed,
            };
        }

        return (Control)Activator.CreateInstance(viewType)!;
    }

    public bool Match(object? data) => data is PageViewModelBase;
}
