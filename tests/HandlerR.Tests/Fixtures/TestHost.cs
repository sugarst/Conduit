using HandlerR.DependencyInjection;
using HandlerR.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace HandlerR.Tests.Fixtures;

/// <summary>
/// 统一测试宿主：默认扫描测试程序集，可按需追加服务或改写配置。
/// </summary>
internal static class TestHost
{
    public static ServiceProvider Build(
        Action<HandlerRConfiguration>? configure = null,
        Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<TraceLog>();
        collection.AddSingleton<ScopeRecorder>();
        collection.AddScoped<ScopedProbe>();
        collection.AddSingleton<TimeProvider>(TimeProvider.System);

        services?.Invoke(collection);

        collection.AddHandlerR(cfg =>
        {
            cfg.RegisterFromAssemblyContaining<PingQuery>();
            configure?.Invoke(cfg);
        });

        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
