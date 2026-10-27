using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using NebulaLauncher.Core.Skinning;

namespace NebulaLauncher.ViewModels;

/// <summary>
/// 本地皮肤列表的一行（票 42）：平面小图 + 文件名。
///
/// 为什么不复用 <see cref="SkinEntryViewModel"/>：那一行绑的是站点返回的 <c>SkinLibraryItem</c>
/// （tid、人气、远端地址），本地这套只有磁盘上的一个文件，硬凑会带上一堆空字段。
///
/// 模型粗细在 Core 里叫 <see cref="LocalSkinModel"/>（Core 不认识 Steve/Alex 这种显示名），
/// 显示名映射放在这里。
/// </summary>
public sealed partial class LocalSkinEntryViewModel : ObservableObject
{
    public LocalSkinEntryViewModel(LocalSkinEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
    }

    public LocalSkinEntry Entry { get; }

    public string FileName => Entry.FileName;

    /// <summary>列表标题：去掉 .png 的名字。</summary>
    public string Name => Path.GetFileNameWithoutExtension(Entry.FileName);

    public LocalSkinModel Model => Entry.Model;

    public string ModelLabel => Model == LocalSkinModel.Slim ? "Alex" : "Steve";

    /// <summary>第二行说明：体型 + 原图尺寸。</summary>
    public string DetailLabel => $"{ModelLabel} · {Entry.Width}×{Entry.Height}";

    public string FilePath => Entry.FilePath;

    /// <summary>平面小图。解码失败时为 <c>null</c>，行里显示占位块。</summary>
    [ObservableProperty]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
}
