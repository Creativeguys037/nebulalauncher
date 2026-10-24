namespace NebulaLauncher.ViewModels;

/// <summary>
/// 系统页（设置 / 世界与备份）的注册。这个 partial 文件只属于 UI-C，
/// 与首页、内容页的注册文件互不覆盖，也不改 MainWindowViewModel.cs。
/// </summary>
public partial class MainWindowViewModel
{
    partial void RegisterSystemPages()
    {
        RegisterPage(NavigationKeys.Worlds, () => Resolve<WorldsViewModel>());
        RegisterPage(NavigationKeys.Logs, () => Resolve<LogsViewModel>());
        RegisterPage(NavigationKeys.Help, () => Resolve<HelpViewModel>());
        RegisterPage(NavigationKeys.Settings, () => Resolve<SettingsViewModel>());
    }
}
