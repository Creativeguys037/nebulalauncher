namespace NebulaLauncher.ViewModels;

/// <summary>
/// 内容 / 账户 / 服务器三页的注册。
///
/// 单独成文件是为了让三位 UI 协作者各写各的 partial 实现，
/// 不做协作者的 <see cref="MainWindowViewModel"/> 主文件改动。
/// </summary>
public partial class MainWindowViewModel
{
    partial void RegisterContentPages()
    {
        // 内容页外壳由 #90 把 ModsView 改造成五页签；在此之前「内容」先落到模组页，
        // 旧的 mods / library / skins 三个键都由 NavigationRoutes 归一化到这里。
        RegisterPage(NavigationKeys.Content, () => Resolve<ModsViewModel>());
        RegisterPage(NavigationKeys.Accounts, () => Resolve<AccountsViewModel>());
        RegisterPage(NavigationKeys.Servers, () => Resolve<ServersViewModel>());
    }
}
