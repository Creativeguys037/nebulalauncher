using System.Threading.Tasks;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 尚未实现的页面占位。用于导航骨架可运行、且页面实现被并行推进时保持程序可用。
/// 任何真实页面就绪后，把对应 <c>RegisterPage</c> 换成真实 ViewModel 类型即可。
/// </summary>
public sealed class PlaceholderPageViewModel : PageViewModelBase
{
    public PlaceholderPageViewModel(string key, string title, string description)
    {
        Key = key;
        Title = title;
        Description = description;
    }

    public override string Title { get; }

    public override string Key { get; }

    public override string? Subtitle => "建设中";

    public string Description { get; }

    public override Task OnNavigatedToAsync() => Task.CompletedTask;
}
