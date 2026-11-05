namespace NebulaLauncher.ViewModels;

/// <summary>
/// 系统页（设置）的注册。这个 partial 文件只属于 UI-C，
/// 与首页、内容页的注册文件互不覆盖，也不改 MainWindowViewModel.cs。
///
/// 世界并入版本的实例详情（#89）、日志与帮助并入设置的关于区（#98），
/// 旧键由 NavigationRoutes 归一化，不再单独注册页面。
/// </summary>
public partial class MainWindowViewModel
{
    partial void RegisterSystemPages()
    {
        RegisterPage(NavigationKeys.Settings, () => Resolve<SettingsViewModel>());

        // 控件示例页（票 #84）：不在导航里露出，只通过 --page controls 进入，交付后保留供复核。
        RegisterPage(NavigationKeys.Controls, () => Resolve<ControlsGalleryViewModel>());
    }
}
