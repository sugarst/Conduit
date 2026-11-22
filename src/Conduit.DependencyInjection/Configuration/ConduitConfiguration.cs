using System.Reflection;
using Conduit.Abstractions;
using Conduit.Core;
using Conduit.Core.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.DependencyInjection;

/// <summary>
/// Conduit 注册配置，逐项对齐基准 15.0 的配置面（见技术方案 2.3 节）。
/// </summary>
public sealed class ConduitConfiguration
{
    private readonly List<Assembly> _assemblies = [];
    private readonly List<(Type Request, Type Handler)> _explicitHandlers = [];
    private readonly List<ConduitOpenBehavior> _behaviors = [];
    private readonly List<ConduitOpenBehavior> _streamBehaviors = [];
    private readonly List<Type> _preProcessors = [];
    private readonly List<Type> _postProcessors = [];
    private readonly List<Type> _validatorAdapters = [];

    /// <summary>类型过滤器，默认全部接受。</summary>
    public Func<Type, bool> TypeEvaluator { get; set; } = static _ => true;

    /// <summary>调度器实现类型。</summary>
    public Type DispatcherImplementationType { get; set; } = typeof(ConduitDispatcher);

    /// <summary>通知发布器实例。未设置 <see cref="NotificationPublisherType"/> 时生效。</summary>
    public INotificationPublisher NotificationPublisher { get; set; } = SequentialPublisher.Instance;

    /// <summary>通知发布器类型；优先于 <see cref="NotificationPublisher"/> 实例。</summary>
    public Type? NotificationPublisherType { get; set; }

    /// <summary>Handler 与调度器的统一生命周期（默认 Transient）。</summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>异常动作作用范围策略。</summary>
    public ExceptionActionStrategy ExceptionActionStrategy { get; set; } = ExceptionActionStrategy.ApplyForUnhandledExceptions;

    /// <summary>开放 Behavior 关闭策略。</summary>
    public BehaviorClosingStrategy BehaviorClosingStrategy { get; set; } = BehaviorClosingStrategy.CloseForScannedRequests;

    /// <summary>是否自动注册扫描到的前 / 后处理器（默认 false，须显式注册）。</summary>
    public bool AutoRegisterRequestProcessors { get; set; }

    /// <summary>是否自动注册开放泛型 Handler（默认 false，opt-in）。</summary>
    public bool RegisterGenericHandlers { get; set; }

    /// <summary>单个泛型类型的最大类型参数数；0 表示禁用该限制。</summary>
    public int MaxGenericTypeParameters { get; set; } = 10;

    /// <summary>每个开放泛型允许关闭的最大类型数；0 表示禁用该限制。</summary>
    public int MaxTypesClosing { get; set; } = 100;

    /// <summary>泛型注册总数上限；0 表示禁用该限制。</summary>
    public int MaxGenericTypeRegistrations { get; set; } = 125_000;

    /// <summary>扫描 / 关闭阶段的超时时间（毫秒）；0 表示禁用该限制。</summary>
    public int RegistrationTimeout { get; set; } = 15_000;

    /// <summary>是否执行启动期校验（默认 true）。</summary>
    public bool EnableStartupValidation { get; set; } = true;

    /// <summary>允许 Singleton 生命周期。默认 false：Singleton 需显式通过依赖审查。</summary>
    public bool AllowSingletonLifetime { get; set; }

    /// <summary>
    /// 并发发布时是否为每个通知处理器建立独立作用域。
    /// <c>null</c> 表示自动：使用内置并发发布器时开启。
    /// </summary>
    public bool? IsolateNotificationHandlerScopes { get; set; }

    /// <summary>已登记的程序集。</summary>
    public IReadOnlyList<Assembly> Assemblies => _assemblies;

    /// <summary>已登记的显式 Handler 对。</summary>
    public IReadOnlyList<(Type Request, Type Handler)> ExplicitHandlers => _explicitHandlers;

    /// <summary>按声明顺序排列的请求 Behavior。</summary>
    public IReadOnlyList<ConduitOpenBehavior> Behaviors => _behaviors;

    /// <summary>按声明顺序排列的流式 Behavior。</summary>
    public IReadOnlyList<ConduitOpenBehavior> StreamBehaviors => _streamBehaviors;

    /// <summary>显式注册的前处理器类型。</summary>
    public IReadOnlyList<Type> PreProcessors => _preProcessors;

    /// <summary>显式注册的后处理器类型。</summary>
    public IReadOnlyList<Type> PostProcessors => _postProcessors;

    /// <summary>开放泛型校验器适配器（绑定 IRequestValidator&lt;&gt;）。</summary>
    public IReadOnlyList<Type> ValidatorAdapters => _validatorAdapters;

    /// <summary>从指定程序集扫描 Handler。</summary>
    public ConduitConfiguration RegisterFrom(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            if (!_assemblies.Contains(assembly))
            {
                _assemblies.Add(assembly);
            }
        }

        return this;
    }

    /// <summary>从包含 <typeparamref name="TMarker"/> 的程序集扫描 Handler。</summary>
    public ConduitConfiguration RegisterFromAssemblyContaining<TMarker>()
        => RegisterFrom(typeof(TMarker).Assembly);

    /// <summary>显式登记一对请求 / 处理器类型（补充扫描，不替代扫描）。</summary>
    public ConduitConfiguration Register(Type requestType, Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(handlerType);

        _explicitHandlers.Add((requestType, handlerType));
        return this;
    }

    /// <summary>注册请求 Behavior。声明顺序即由外到内的执行顺序。</summary>
    public ConduitConfiguration AddBehavior<TBehavior>(ServiceLifetime lifetime = ServiceLifetime.Transient)
        where TBehavior : class
        => AddBehaviorCore(typeof(TBehavior), lifetime, _behaviors, "request");

    /// <summary>注册流式 Behavior。</summary>
    public ConduitConfiguration AddStreamBehavior<TBehavior>(ServiceLifetime lifetime = ServiceLifetime.Transient)
        where TBehavior : class
        => AddBehaviorCore(typeof(TBehavior), lifetime, _streamBehaviors, "stream");

    /// <summary>注册开放泛型 / 封闭的请求 Behavior。</summary>
    public ConduitConfiguration AddOpenBehavior(Type behaviorType, ServiceLifetime lifetime = ServiceLifetime.Transient)
        => AddBehaviorCore(behaviorType, lifetime, _behaviors, "request");

    /// <summary>注册开放泛型 / 封闭的流式 Behavior。</summary>
    public ConduitConfiguration AddOpenStreamBehavior(Type behaviorType, ServiceLifetime lifetime = ServiceLifetime.Transient)
        => AddBehaviorCore(behaviorType, lifetime, _streamBehaviors, "stream");

    /// <summary>注册请求前处理器。</summary>
    public ConduitConfiguration AddRequestPreProcessor<TProcessor>()
        where TProcessor : class
    {
        _preProcessors.Add(typeof(TProcessor));
        return this;
    }

    /// <summary>注册请求后处理器。</summary>
    public ConduitConfiguration AddRequestPostProcessor<TProcessor>()
        where TProcessor : class
    {
        _postProcessors.Add(typeof(TProcessor));
        return this;
    }

    /// <summary>
    /// 注册开放泛型校验器适配器，绑定到 <c>IRequestValidator&lt;&gt;</c>。
    /// </summary>
    /// <remarks>适配包（如 Conduit.FluentValidation）用它把第三方校验器接入内置校验行为。</remarks>
    public ConduitConfiguration AddValidatorAdapter(Type openGenericAdapter)
    {
        ArgumentNullException.ThrowIfNull(openGenericAdapter);

        if (!openGenericAdapter.IsGenericTypeDefinition
            || !typeof(IRequestValidator<>).IsAssignableFromOpenGeneric(openGenericAdapter))
        {
            throw new ConduitConfigurationException(
                $"Validator adapter '{openGenericAdapter.FullName}' must be an open generic type "
                + "implementing IRequestValidator<TRequest>.");
        }

        _validatorAdapters.Add(openGenericAdapter);
        return this;
    }

    private ConduitConfiguration AddBehaviorCore(
        Type behaviorType,
        ServiceLifetime lifetime,
        List<ConduitOpenBehavior> target,
        string kind)
    {
        ArgumentNullException.ThrowIfNull(behaviorType);

        if (!typeof(IPipelineBehavior<,>).IsAssignableFromOpenGeneric(behaviorType)
            && !typeof(IStreamPipelineBehavior<,>).IsAssignableFromOpenGeneric(behaviorType))
        {
            throw new ConduitConfigurationException(
                $"Type '{behaviorType.FullName}' does not implement IPipelineBehavior<,> or IStreamPipelineBehavior<,>.");
        }

        if (kind == "request" && typeof(IStreamPipelineBehavior<,>).IsAssignableFromOpenGeneric(behaviorType)
            && !typeof(IPipelineBehavior<,>).IsAssignableFromOpenGeneric(behaviorType))
        {
            throw new ConduitConfigurationException(
                $"Type '{behaviorType.FullName}' implements IStreamPipelineBehavior<,> and must be registered "
                + "via AddStreamBehavior / AddOpenStreamBehavior. Stream and request pipelines are kept separate.");
        }

        if (kind == "stream" && typeof(IPipelineBehavior<,>).IsAssignableFromOpenGeneric(behaviorType)
            && !typeof(IStreamPipelineBehavior<,>).IsAssignableFromOpenGeneric(behaviorType))
        {
            throw new ConduitConfigurationException(
                $"Type '{behaviorType.FullName}' implements IPipelineBehavior<,> and must be registered "
                + "via AddBehavior / AddOpenBehavior.");
        }

        target.Add(new ConduitOpenBehavior(behaviorType, lifetime));
        return this;
    }
}

/// <summary>开放泛型判定辅助。</summary>
internal static class TypeShapeExtensions
{
    public static bool IsAssignableFromOpenGeneric(this Type openGenericInterface, Type candidate)
    {
        if (candidate.IsGenericTypeDefinition)
        {
            return candidate.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGenericInterface);
        }

        return openGenericInterface.IsAssignableFrom(candidate)
            || candidate.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGenericInterface);
    }
}
