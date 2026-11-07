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
        // 旧的 mods / library 两个键由 NavigationRoutes 归一化到这里。
        RegisterPage(NavigationKeys.Content, () => Resolve<ModsViewModel>());

        // 资源库独立注册（#100 验收发现 --page library 落到模组页）：
        // LibraryView 有独立的 Grid 布局（左筛选 + 右文件清单），不是内容页的子视图。
        RegisterPage(NavigationKeys.Library, () => Resolve<LibraryViewModel>());

        // 皮肤不再归一化到内容页（#92）：它有独立的来源 Tab、体型切换、3D 预览与上传冷却，
        // 塞进内容页的第五个页签会撑破 Row2 的两列布局（网格 + 固定 300 预览列）。
        // 侧栏「内容」仍然进 ModsView；皮肤页由内容页的占位卡与 --page skins 进入。
        RegisterPage(NavigationKeys.Skins, () => Resolve<SkinsViewModel>());

        // 世界独立注册（#100 验收发现 --page worlds 落到版本页）：
        // WorldsView 有独立的四行栅格，是版本详情面板的独立入口。
        RegisterPage(NavigationKeys.Worlds, () => Resolve<WorldsViewModel>());

        RegisterPage(NavigationKeys.Accounts, () => Resolve<AccountsViewModel>());
        RegisterPage(NavigationKeys.Servers, () => Resolve<ServersViewModel>());
    }
}
