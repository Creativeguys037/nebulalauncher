using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 官方档案里的一件披风（票 #61）。披风由官方发放，这里只负责展示与「使用 / 使用中」。
///
/// <para>
/// 缩略图来自官方 CDN 的 <c>capes[].url</c>；下载失败就留空，卡片显示占位而不是整块消失。
/// </para>
/// </summary>
public sealed partial class MojangCapeViewModel : ObservableObject, IDisposable
{
    public MojangCapeViewModel(MojangCape cape)
    {
        Id = cape.Id;
        Url = cape.Url;
        Alias = cape.Alias;
        _isActive = cape.IsActive;
    }

    public string Id { get; }

    public string? Url { get; }

    public string? Alias { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? "未命名披风" : Alias!;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>正在使用的披风按钮写「使用中」且不可再点。</summary>
    public string ActionLabel => IsActive ? "使用中" : "使用";

    public bool CanUse => !IsActive && !IsBusy;

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(CanUse));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanUse));

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));

    public void Dispose()
    {
        var thumbnail = Thumbnail;
        Thumbnail = null;
        thumbnail?.Dispose();
    }
}
