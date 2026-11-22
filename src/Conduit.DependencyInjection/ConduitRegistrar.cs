using System.Collections.Frozen;
using Conduit.Abstractions;
using Conduit.Contracts;
using Conduit.Core;
using Conduit.Core.Caching;
using Conduit.Core.Dispatch;
using Conduit.DependencyInjection.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conduit.DependencyInjection;

/// <summary>
/// 注册编排：扫描 → 注册服务 → 冻结调度表 → 关闭 Behavior → 启动期校验。
/// </summary>
internal static class ConduitRegistrar
{
    internal static void Register(
        IServiceCollection services,
        ConduitConfiguration configuration,
        GeneratedRegistration? generated)
    {
        var failures = new List<string>();
        var guard = new RegistrationGuard(configuration);

        ValidateConfiguration(services, configuration, failures);

        ScanResult? scan = null;
        if (generated is null)
        {
            scan = new AssemblyScanner(configuration, guard).Scan();
            RegisterScannedServices(services, configuration, scan, failures);
        }
        else
        {
            RegisterGeneratedServices(services, configuration, generated);
        }

        var processors = RegisterProcessors(services, configuration, scan);

        var tables = generated?.Tables ?? BuildTables(scan!, guard);
        services.AddSingleton(tables);

        RegisterExceptionDispatch(services, configuration, scan, generated);

        RegisterProcessorRunners(services, configuration, tables, processors);

        RegisterBehaviors(services, configuration, tables, guard, failures);

        RegisterPublisher(services, configuration, failures);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRequestCache, InMemoryRequestCache>();

        RegisterDispatcher(services, configuration, failures);

        if (configuration.EnableStartupValidation && failures.Count > 0)
        {
            throw new ConduitStartupValidationException([.. failures]);
        }
    }

    // ————————————————————————— 校验 —————————————————————————

    private static void ValidateConfiguration(
        IServiceCollection services,
        ConduitConfiguration configuration,
        List<string> failures)
    {
        var dispatcherType = configuration.DispatcherImplementationType;
        if (!typeof(IDispatcher).IsAssignableFrom(dispatcherType) || dispatcherType.IsAbstract || dispatcherType.IsInterface)
        {
            failures.Add(
                $"DispatcherImplementationType '{dispatcherType.FullName}' must be a concrete type implementing IDispatcher.");
        }
        else if (!HasDispatchTablesConstructor(dispatcherType))
        {
            failures.Add(
                $"DispatcherImplementationType '{dispatcherType.FullName}' must expose a public constructor "
                + "accepting DispatchTables as its first parameter.");
        }

        if (configuration.Lifetime == ServiceLifetime.Singleton && !configuration.AllowSingletonLifetime)
        {
            failures.Add(
                "Lifetime = Singleton requires an explicit dependency review: set "
                + "ConduitConfiguration.AllowSingletonLifetime = true only when no handler depends on a scoped service.");
        }

        if (configuration.NotificationPublisherType is { } publisherType
            && !typeof(INotificationPublisher).IsAssignableFrom(publisherType))
        {
            failures.Add(
                $"NotificationPublisherType '{publisherType.FullName}' does not implement INotificationPublisher.");
        }

        var preExistingBehaviors = services
            .Where(descriptor => IsPipelineService(descriptor.ServiceType))
            .Select(descriptor => descriptor.ServiceType.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (preExistingBehaviors.Length > 0)
        {
            failures.Add(
                "Pipeline behaviors must be registered through ConduitConfiguration (AddBehavior / AddOpenBehavior) "
                + $"so that order is deterministic, but found: {string.Join(", ", preExistingBehaviors)}.");
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(INotificationPublisher)))
        {
            failures.Add(
                "INotificationPublisher is already registered. A single publisher must be configured "
                + "via ConduitConfiguration.NotificationPublisher / NotificationPublisherType.");
        }
    }

    private static bool HasDispatchTablesConstructor(Type dispatcherType)
        => dispatcherType.GetConstructors()
            .Any(ctor => ctor.GetParameters() is [{ ParameterType: var first }, ..]
                         && first == typeof(DispatchTables));

    private static bool IsPipelineService(Type serviceType)
        => serviceType.IsGenericType
           && (serviceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>)
               || serviceType.GetGenericTypeDefinition() == typeof(IStreamPipelineBehavior<,>));

    // ————————————————————————— 服务注册 —————————————————————————

    private static void RegisterScannedServices(
        IServiceCollection services,
        ConduitConfiguration configuration,
        ScanResult scan,
        List<string> failures)
    {
        foreach (var mapping in scan.Handlers)
        {
            services.TryAdd(new ServiceDescriptor(mapping.ServiceType, mapping.ImplementationType, configuration.Lifetime));
        }

        var isolateScopes = ShouldIsolateNotificationScopes(configuration);

        foreach (var mapping in scan.NotificationHandlers)
        {
            if (isolateScopes)
            {
                var notificationType = mapping.ServiceType.GetGenericArguments()[0];
                var wrapperType = typeof(ScopedNotificationHandler<,>)
                    .MakeGenericType(mapping.ImplementationType, notificationType);

                // 包装器在独立作用域内按实现类型解析，因此实现类型自身也必须可解析
                services.TryAdd(new ServiceDescriptor(
                    mapping.ImplementationType, mapping.ImplementationType, configuration.Lifetime));

                services.Add(new ServiceDescriptor(mapping.ServiceType, wrapperType, configuration.Lifetime));
            }
            else
            {
                services.Add(new ServiceDescriptor(mapping.ServiceType, mapping.ImplementationType, configuration.Lifetime));
            }
        }

        foreach (var mapping in scan.ExceptionHandlers)
        {
            var serviceType = typeof(IRequestExceptionHandler<,,>)
                .MakeGenericType(mapping.RequestType, mapping.ResponseType, mapping.ExceptionType);

            services.TryAdd(new ServiceDescriptor(serviceType, mapping.ImplementationType, configuration.Lifetime));
        }

        foreach (var mapping in scan.ExceptionActions)
        {
            var serviceType = typeof(IRequestExceptionAction<,>)
                .MakeGenericType(mapping.RequestType, mapping.ExceptionType);

            services.TryAdd(new ServiceDescriptor(serviceType, mapping.ImplementationType, configuration.Lifetime));
        }

        foreach (var (serviceType, implementationType) in scan.Validators)
        {
            services.TryAdd(new ServiceDescriptor(serviceType, implementationType, configuration.Lifetime));
        }

        foreach (var adapter in configuration.ValidatorAdapters)
        {
            services.Add(new ServiceDescriptor(typeof(IRequestValidator<>), adapter, configuration.Lifetime));
        }
    }

    private static void RegisterGeneratedServices(
        IServiceCollection services,
        ConduitConfiguration configuration,
        GeneratedRegistration generated)
    {
        var isolateScopes = ShouldIsolateNotificationScopes(configuration);

        foreach (var mapping in generated.Services)
        {
            if (isolateScopes && IsNotificationHandlerService(mapping.ServiceType))
            {
                var notificationType = mapping.ServiceType.GetGenericArguments()[0];
                var wrapperType = typeof(ScopedNotificationHandler<,>)
                    .MakeGenericType(mapping.ImplementationType, notificationType);

                services.TryAdd(new ServiceDescriptor(
                    mapping.ImplementationType, mapping.ImplementationType, mapping.Lifetime));

                services.Add(new ServiceDescriptor(mapping.ServiceType, wrapperType, mapping.Lifetime));
                continue;
            }

            services.TryAdd(new ServiceDescriptor(mapping.ServiceType, mapping.ImplementationType, mapping.Lifetime));
        }
    }

    private static bool IsNotificationHandlerService(Type serviceType)
        => serviceType.IsGenericType
           && serviceType.GetGenericTypeDefinition() == typeof(INotificationHandler<>);

    private static bool ShouldIsolateNotificationScopes(ConduitConfiguration configuration)
    {
        if (configuration.IsolateNotificationHandlerScopes is { } explicitValue)
        {
            return explicitValue;
        }

        return configuration.NotificationPublisherType is { } publisherType
            ? typeof(IConcurrentNotificationPublisher).IsAssignableFrom(publisherType)
            : configuration.NotificationPublisher is IConcurrentNotificationPublisher;
    }

    // ————————————————————————— 处理器 —————————————————————————

    private sealed record ProcessorRegistry(HashSet<Type> PreRequests, HashSet<(Type Request, Type Response)> PostPairs);

    private static ProcessorRegistry RegisterProcessors(
        IServiceCollection services,
        ConduitConfiguration configuration,
        ScanResult? scan)
    {
        var preRequests = new HashSet<Type>();
        var postPairs = new HashSet<(Type, Type)>();

        void RegisterPre(Type implementationType)
        {
            var matched = false;
            foreach (var @interface in implementationType.GetInterfaces())
            {
                if (@interface.IsGenericType
                    && @interface.GetGenericTypeDefinition() == typeof(IRequestPreProcessor<>))
                {
                    services.Add(new ServiceDescriptor(@interface, implementationType, configuration.Lifetime));
                    preRequests.Add(@interface.GetGenericArguments()[0]);
                    matched = true;
                }
            }

            if (!matched)
            {
                throw new ConduitConfigurationException(
                    $"Type '{implementationType.FullName}' does not implement IRequestPreProcessor<TRequest>.");
            }
        }

        void RegisterPost(Type implementationType)
        {
            var matched = false;
            foreach (var @interface in implementationType.GetInterfaces())
            {
                if (@interface.IsGenericType
                    && @interface.GetGenericTypeDefinition() == typeof(IRequestPostProcessor<,>))
                {
                    services.Add(new ServiceDescriptor(@interface, implementationType, configuration.Lifetime));
                    var arguments = @interface.GetGenericArguments();
                    postPairs.Add((arguments[0], arguments[1]));
                    matched = true;
                }
            }

            if (!matched)
            {
                throw new ConduitConfigurationException(
                    $"Type '{implementationType.FullName}' does not implement IRequestPostProcessor<TRequest, TResponse>.");
            }
        }

        if (scan is not null && configuration.AutoRegisterRequestProcessors)
        {
            foreach (var (_, implementation) in scan.PreProcessors)
            {
                RegisterPre(implementation);
            }

            foreach (var (_, _, implementation) in scan.PostProcessors)
            {
                RegisterPost(implementation);
            }
        }

        foreach (var implementationType in configuration.PreProcessors)
        {
            RegisterPre(implementationType);
        }

        foreach (var implementationType in configuration.PostProcessors)
        {
            RegisterPost(implementationType);
        }

        return new ProcessorRegistry(preRequests, postPairs);
    }

    private static void RegisterProcessorRunners(
        IServiceCollection services,
        ConduitConfiguration configuration,
        DispatchTables tables,
        ProcessorRegistry processors)
    {
        var preInvokers = new Dictionary<Type, IProcessorInvoker>();
        var postInvokers = new Dictionary<Type, IProcessorInvoker>();

        foreach (var entry in tables.Requests.Values)
        {
            if (processors.PreRequests.Contains(entry.RequestType))
            {
                preInvokers[entry.RequestType] = (IProcessorInvoker)Activator.CreateInstance(
                    typeof(PreProcessorInvoker<>).MakeGenericType(entry.RequestType))!;
            }

            if (processors.PostPairs.Contains((entry.RequestType, entry.ResponseType)))
            {
                postInvokers[entry.RequestType] = (IProcessorInvoker)Activator.CreateInstance(
                    typeof(PostProcessorInvoker<,>).MakeGenericType(entry.RequestType, entry.ResponseType))!;
            }
        }

        services.AddSingleton<IRequestPreProcessorRunner>(
            new RequestPreProcessorRunner(preInvokers.ToFrozenDictionary()));

        services.AddSingleton<IRequestPostProcessorRunner>(
            new RequestPostProcessorRunner(postInvokers.ToFrozenDictionary()));
    }

    // ————————————————————————— 调度表 —————————————————————————

    private static DispatchTables BuildTables(ScanResult scan, RegistrationGuard guard)
    {
        var builder = new DispatchTableBuilder();

        foreach (var (request, response) in scan.RequestPairs.Distinct())
        {
            guard.CheckTimeout();

            var entryType = typeof(RequestHandlerEntry<,>).MakeGenericType(request, response);
            builder.Add((RequestHandlerEntry)Activator.CreateInstance(entryType)!);
        }

        foreach (var (request, response) in scan.StreamPairs.Distinct())
        {
            guard.CheckTimeout();

            var entryType = typeof(StreamHandlerEntry<,>).MakeGenericType(request, response);
            builder.AddStream((StreamHandlerEntry)Activator.CreateInstance(entryType)!);
        }

        return builder.Build();
    }

    // ————————————————————————— 异常映射 —————————————————————————

    private static void RegisterExceptionDispatch(
        IServiceCollection services,
        ConduitConfiguration configuration,
        ScanResult? scan,
        GeneratedRegistration? generated)
    {
        var handlers = new List<(Type Request, Type Response, Type Exception)>();
        var actions = new List<(Type Request, Type Exception)>();

        if (scan is not null)
        {
            handlers.AddRange(scan.ExceptionHandlers.Select(h => (h.RequestType, h.ResponseType, h.ExceptionType)));
            actions.AddRange(scan.ExceptionActions.Select(a => (a.RequestType, a.ExceptionType)));
        }

        if (generated is not null)
        {
            handlers.AddRange(generated.ExceptionHandlers.Select(h => (h.RequestType, h.ResponseType, h.ExceptionType)));
            actions.AddRange(generated.ExceptionActions.Select(a => (a.RequestType, a.ExceptionType)));
        }

        var requestTypes = handlers.Select(h => h.Request).Concat(actions.Select(a => a.Request)).Distinct();

        foreach (var requestType in requestTypes)
        {
            var responseType = handlers
                .FirstOrDefault(h => h.Request == requestType, (null!, null!, null!)).Response;

            if (responseType is null)
            {
                // 只有动作没有映射：动作仍需执行，响应类型取 Unit 语义即可
                responseType = typeof(Unit);
            }

            var invokers = handlers
                .Where(h => h.Request == requestType && h.Response == responseType)
                .Select(h => h.Exception)
                .Distinct()
                .Select(exceptionType => (IExceptionHandlerInvoker)Activator.CreateInstance(
                    typeof(ExceptionHandlerInvoker<,,>).MakeGenericType(requestType, responseType, exceptionType))!)
                .ToArray();

            var actionInvokers = actions
                .Where(a => a.Request == requestType)
                .Select(a => a.Exception)
                .Distinct()
                .Select(exceptionType => (IExceptionActionInvoker)Activator.CreateInstance(
                    typeof(ExceptionActionInvoker<,>).MakeGenericType(requestType, exceptionType))!)
                .ToArray();

            var dispatcherType = typeof(RequestExceptionDispatcher<,>).MakeGenericType(requestType, responseType);
            var dispatcher = Activator.CreateInstance(
                dispatcherType, invokers, actionInvokers, configuration.ExceptionActionStrategy)!;

            var serviceType = typeof(IExceptionDispatcher<,>).MakeGenericType(requestType, responseType);
            services.AddSingleton(serviceType, dispatcher);
        }
    }

    // ————————————————————————— Behavior —————————————————————————

    private static void RegisterBehaviors(
        IServiceCollection services,
        ConduitConfiguration configuration,
        DispatchTables tables,
        RegistrationGuard guard,
        List<string> failures)
    {
        var requestPairs = tables.Requests.Values
            .Select(entry => (entry.RequestType, entry.ResponseType))
            .ToArray();

        var streamPairs = tables.Streams.Values
            .Select(entry => (entry.RequestType, entry.ItemType))
            .ToArray();

        RegisterBehaviorList(
            services, configuration, guard, configuration.Behaviors,
            typeof(IPipelineBehavior<,>), requestPairs, failures);

        RegisterBehaviorList(
            services, configuration, guard, configuration.StreamBehaviors,
            typeof(IStreamPipelineBehavior<,>), streamPairs, failures);
    }

    private static void RegisterBehaviorList(
        IServiceCollection services,
        ConduitConfiguration configuration,
        RegistrationGuard guard,
        IReadOnlyList<ConduitOpenBehavior> behaviors,
        Type pipelineInterface,
        (Type Request, Type Response)[] pairs,
        List<string> failures)
    {
        foreach (var behavior in behaviors)
        {
            var behaviorType = behavior.BehaviorType;

            if (!behaviorType.IsGenericTypeDefinition)
            {
                var closedInterface = behaviorType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == pipelineInterface);

                if (closedInterface is null)
                {
                    failures.Add(
                        $"Behavior '{behaviorType.FullName}' does not implement '{pipelineInterface.Name}'.");
                    continue;
                }

                services.Add(new ServiceDescriptor(closedInterface, behaviorType, behavior.Lifetime));
                continue;
            }

            guard.AddTypeParameters(behaviorType.GetGenericArguments().Length);

            if (configuration.BehaviorClosingStrategy == BehaviorClosingStrategy.CloseInContainer)
            {
                services.Add(new ServiceDescriptor(pipelineInterface, behaviorType, behavior.Lifetime));
                continue;
            }

            foreach (var (request, response) in pairs)
            {
                guard.CheckTimeout();

                var result = OpenBehaviorCloser.TryClose(
                    behaviorType, request, response, pipelineInterface, out var closedType, out var failure);

                switch (result)
                {
                    case CloseResult.Closed:
                        guard.IncrementTypesClosing();
                        services.Add(new ServiceDescriptor(
                            pipelineInterface.MakeGenericType(request, response),
                            closedType,
                            behavior.Lifetime));
                        break;

                    case CloseResult.Failed:
                        failures.Add(failure!);
                        break;

                    case CloseResult.NotApplicable:
                    default:
                        break;
                }
            }
        }
    }

    // ————————————————————————— 发布器与调度器 —————————————————————————

    private static void RegisterPublisher(
        IServiceCollection services,
        ConduitConfiguration configuration,
        List<string> failures)
    {
        if (configuration.NotificationPublisherType is { } publisherType)
        {
            if (typeof(INotificationPublisher).IsAssignableFrom(publisherType))
            {
                services.AddSingleton(typeof(INotificationPublisher), publisherType);
                return;
            }

            failures.Add($"NotificationPublisherType '{publisherType.FullName}' is not a valid publisher.");
            return;
        }

        services.AddSingleton(configuration.NotificationPublisher);
    }

    private static void RegisterDispatcher(
        IServiceCollection services,
        ConduitConfiguration configuration,
        List<string> failures)
    {
        var dispatcherType = configuration.DispatcherImplementationType;
        if (!typeof(IDispatcher).IsAssignableFrom(dispatcherType) || dispatcherType.IsAbstract)
        {
            failures.Add($"Cannot register '{dispatcherType.FullName}' as IDispatcher.");
            return;
        }

        services.Add(new ServiceDescriptor(
            typeof(IDispatcher),
            provider => (IDispatcher)ActivatorUtilities.CreateInstance(provider, dispatcherType),
            configuration.Lifetime));

        services.Add(new ServiceDescriptor(
            typeof(ISender),
            provider => provider.GetRequiredService<IDispatcher>(),
            configuration.Lifetime));

        services.Add(new ServiceDescriptor(
            typeof(IPublisher),
            provider => provider.GetRequiredService<IDispatcher>(),
            configuration.Lifetime));
    }
}
