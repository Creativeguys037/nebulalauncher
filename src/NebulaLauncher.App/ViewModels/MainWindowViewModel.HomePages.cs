namespace NebulaLauncher.ViewModels;

/// <summary>
/// 首页 / 版本 / 下载三页的注册。放在独立文件里，避免和其他协作者改同一个组合文件。
/// 页面 ViewModel 不需要显式 DI 注册，<c>Resolve&lt;T&gt;()</c> 会用 ActivatorUtilities 构造。
/// </summary>
public partial class MainWindowViewModel
{
    partial void RegisterHomePages()
    {
        RegisterPage(NavigationKeys.Home, () => Resolve<HomeViewModel>());
        RegisterPage(NavigationKeys.Versions, () => Resolve<VersionsViewModel>());
        RegisterPage(NavigationKeys.Download, () => Resolve<DownloadViewModel>());
    }
}
