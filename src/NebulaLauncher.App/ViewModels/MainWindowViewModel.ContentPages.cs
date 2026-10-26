namespace NebulaLauncher.ViewModels;

/// <summary>
/// 内容管理三页（模组 / 服务器 / 账户）的注册。
///
/// 单独成文件是为了让三位 UI 协作者各写各的 partial 实现，
/// 不做协作者的 <see cref="MainWindowViewModel"/> 主文件改动。
/// </summary>
public partial class MainWindowViewModel
{
    partial void RegisterContentPages()
    {
        RegisterPage(NavigationKeys.Mods, () => Resolve<ModsViewModel>());
        RegisterPage(NavigationKeys.Library, () => Resolve<LibraryViewModel>());
        RegisterPage(NavigationKeys.Accounts, () => Resolve<AccountsViewModel>());
        RegisterPage(NavigationKeys.Servers, () => Resolve<ServersViewModel>());
        RegisterPage(NavigationKeys.Skins, () => Resolve<SkinsViewModel>());
    }
}
