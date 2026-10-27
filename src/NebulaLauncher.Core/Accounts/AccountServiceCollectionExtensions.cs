using Microsoft.Extensions.DependencyInjection;
using NebulaLauncher.Core.Abstractions;

namespace NebulaLauncher.Core.Accounts;

/// <summary>账户模块的 DI 注册。</summary>
public static class AccountServiceCollectionExtensions
{
    /// <summary>注册 <see cref="IAccountService"/>。</summary>
    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAccountService>(sp => new AccountService(
            sp.GetRequiredService<ILauncherPaths>(),
            sp.GetRequiredService<IAppLog>(),
            sp.GetRequiredService<IPlatformService>()));

        // 皮肤头像（按 UUID 取皮肤 PNG 并缓存）跟着账户模块走：它只依赖账户模型与路径。
        services.AddCoreSkinAvatars();

        return services;
    }
}
