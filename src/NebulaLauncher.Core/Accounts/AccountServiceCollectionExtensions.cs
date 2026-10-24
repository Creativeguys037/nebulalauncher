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

        return services;
    }
}
