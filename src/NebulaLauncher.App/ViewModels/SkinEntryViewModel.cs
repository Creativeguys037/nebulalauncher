using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using NebulaLauncher.Skinning;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 皮肤库列表的一行：平面小图 + 名字 + 体型 + 人气。
///
/// 行里**不**渲染 3D（20 行 × 每行一份软光栅太重，见票 41 的范围），
/// 3D 只在右侧预览卡里渲染当前选中的那套。
/// </summary>
public sealed partial class SkinEntryViewModel(SkinLibraryItem item) : ObservableObject
{
    public int Tid { get; } = item.Tid;

    public string Name { get; } = item.Name;

    public SkinModelKind Model { get; } = item.Model;

    public int Likes { get; } = item.Likes;

    public string ModelLabel => Model == SkinModelKind.Alex ? "Alex" : "Steve";

    public string LikesLabel => Likes <= 0 ? "还没有人点赞" : $"人气 {Likes}";

    /// <summary>站方预览图；解不出来时退回原始纹理。还没拿到就是 <c>null</c>，行里显示占位。</summary>
    [ObservableProperty]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));

    /// <summary>
    /// 「下载到本地」的状态小字（票 42）：正在保存 / 已存到本地 / 失败原因。
    /// 做成行内反馈而不是弹窗：用户往往连着点好几行，弹窗会挡住的正是下一行。
    /// </summary>
    [ObservableProperty]
    private string? _localState;

    public bool HasLocalState => !string.IsNullOrWhiteSpace(LocalState);

    /// <summary>在途标记，用来禁掉这一行的按钮，防连点存出同名副本。</summary>
    [ObservableProperty]
    private bool _isSavingToLocal;

    partial void OnLocalStateChanged(string? value) => OnPropertyChanged(nameof(HasLocalState));
}
