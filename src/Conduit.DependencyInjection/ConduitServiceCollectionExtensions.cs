using Conduit.Core.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.DependencyInjection;

/// <summary>
/// 源生成器产出的注册清单。Mode B 用它替代反射扫描，同时保持与 Mode A 完全一致的行为语义。
/// </summary>
public sealed class GeneratedRegistration
{
    /// <summary>编译期生成的调度表。</summary>
    public DispatchTables Tables { get; init; } = DispatchTables.Empty;

    /// <summary>需要注册到容器的处理器服务映射（含通知处理器）。</summary>
    public IReadOnlyList<GeneratedServiceRegistration> Services { get; init; } = [];

    /// <summary>异常处理器形状（请求, 响应, 异常）。实现类型由生成代码注册。</summary>
    public IReadOnlyList<GeneratedExceptionHandler> ExceptionHandlers { get; init; } = [];

    /// <summary>异常动作形状（请求, 异常）。</summary>
    public IReadOnlyList<GeneratedExceptionAction> ExceptionActions { get; init; } = [];
}

/// <summary>一条服务映射。</summary>
public readonly record struct GeneratedServiceRegistration(
    Type ServiceType,
    Type ImplementationType,
    ServiceLifetime Lifetime);

/// <summary>异常处理器形状。</summary>
public readonly record struct GeneratedExceptionHandler(Type RequestType, Type ResponseType, Type ExceptionType);

/// <summary>异常动作形状。</summary>
public readonly record struct GeneratedExceptionAction(Type RequestType, Type ExceptionType);

/// <summary>
/// Conduit 注册扩展。
/// </summary>
public static class ConduitServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Conduit（Mode A：启动期反射扫描）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">配置回调。</param>
    public static IServiceCollection AddConduit(
        this IServiceCollection services,
        Action<ConduitConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var configuration = new ConduitConfiguration();
        configure?.Invoke(configuration);
        ConduitRegistrar.Register(services, configuration, generated: null);
        return services;
    }

    /// <summary>
    /// 注册 Conduit（Mode B：使用源生成器产出的调度表，零反射）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="registration">生成器产出的注册清单。</param>
    /// <param name="configure">配置回调（行为、发布器、护栏等仍然生效）。</param>
    public static IServiceCollection AddConduit(
        this IServiceCollection services,
        GeneratedRegistration registration,
        Action<ConduitConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registration);

        var configuration = new ConduitConfiguration();
        configure?.Invoke(configuration);
        ConduitRegistrar.Register(services, configuration, registration);
        return services;
    }
}
