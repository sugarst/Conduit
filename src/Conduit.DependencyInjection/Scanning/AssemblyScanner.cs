using System.Reflection;
using Conduit.Abstractions;
using Conduit.Contracts;

namespace Conduit.DependencyInjection.Scanning;

/// <summary>一条待注册的服务映射。</summary>
internal readonly record struct ServiceMapping(Type ServiceType, Type ImplementationType, bool IsOpenGeneric);

/// <summary>异常处理器登记项。</summary>
internal readonly record struct ExceptionHandlerMapping(Type RequestType, Type ResponseType, Type ExceptionType, Type ImplementationType);

/// <summary>异常动作登记项。</summary>
internal readonly record struct ExceptionActionMapping(Type RequestType, Type ExceptionType, Type ImplementationType);

/// <summary>扫描结果。</summary>
internal sealed class ScanResult
{
    public List<ServiceMapping> Handlers { get; } = [];
    public List<ServiceMapping> NotificationHandlers { get; } = [];
    public List<(Type Request, Type Response)> RequestPairs { get; } = [];
    public List<(Type Request, Type Response)> StreamPairs { get; } = [];
    public List<ExceptionHandlerMapping> ExceptionHandlers { get; } = [];
    public List<ExceptionActionMapping> ExceptionActions { get; } = [];
    public List<(Type ServiceType, Type ImplementationType)> Processors { get; } = [];
    public List<(Type ServiceType, Type Implementation)> Validators { get; } = [];
    public List<string> Notes { get; } = [];

    public HashSet<Type> NotificationServiceTypes { get; } = [];
}

/// <summary>
/// Mode A 程序集扫描器：一次性枚举类型并归类，重复注册立即 fail-fast。
/// </summary>
internal sealed class AssemblyScanner(ConduitConfiguration configuration, RegistrationGuard guard)
{
    private static readonly Type RequestHandlerOpen = typeof(IRequestHandler<,>);
    private static readonly Type NotificationHandlerOpen = typeof(INotificationHandler<>);
    private static readonly Type StreamHandlerOpen = typeof(IStreamRequestHandler<,>);
    private static readonly Type ExceptionHandlerOpen = typeof(IRequestExceptionHandler<,,>);
    private static readonly Type ExceptionActionOpen = typeof(IRequestExceptionAction<,>);
    private static readonly Type PreProcessorOpen = typeof(IRequestPreProcessor<>);
    private static readonly Type PostProcessorOpen = typeof(IRequestPostProcessor<,>);
    private static readonly Type ValidatorOpen = typeof(IRequestValidator<>);

    private readonly ScanResult _result = new();
    private readonly Dictionary<Type, Type> _handlerServiceTypes = [];
    private readonly List<Type> _openHandlers = [];
    private readonly HashSet<Type> _containerClosedNotifications = [];

    public ScanResult Scan()
    {
        var candidates = new HashSet<Type>();
        foreach (var assembly in configuration.Assemblies)
        {
            guard.CheckTimeout();
            foreach (var type in SafeGetTypes(assembly))
            {
                Inspect(type);
                if (configuration.RegisterGenericHandlers && !type.IsAbstract && (type.IsClass || type.IsValueType))
                {
                    AddCandidate(candidates, type);
                }
            }
        }

        foreach (var (requestType, handlerType) in configuration.ExplicitHandlers)
        {
            InspectExplicitPair(requestType, handlerType);
        }

        if (_openHandlers.Count > 0)
        {
            foreach (var (request, response) in _result.RequestPairs.Concat(_result.StreamPairs))
            {
                AddCandidate(candidates, request);
                AddCandidate(candidates, response);
            }

            CloseGenericHandlers(candidates);
        }

        return _result;
    }

    private void Inspect(Type type)
    {
        if (type is null || !type.IsClass || type.IsAbstract || type.IsValueType)
        {
            return;
        }

        if (type.IsGenericTypeDefinition || type.ContainsGenericParameters)
        {
            InspectOpenGenericHandler(type);
            return;
        }

        if (!configuration.TypeEvaluator(type))
        {
            return;
        }

        Type[] interfaces;
        try
        {
            interfaces = type.GetInterfaces();
        }
        catch (Exception exception) when (exception is NotSupportedException or TypeLoadException or FileNotFoundException)
        {
            // 反射容错：不可读的元数据（缺失依赖 / F# 编译单元）跳过，不阻断整体扫描
            _result.Notes.Add($"Skipped unreadable type '{type.FullName}': {exception.Message}");
            return;
        }

        foreach (var @interface in interfaces)
        {
            if (!@interface.IsGenericType)
            {
                continue;
            }

            var definition = @interface.GetGenericTypeDefinition();
            var arguments = @interface.GetGenericArguments();

            if (definition == RequestHandlerOpen)
            {
                RegisterHandler(@interface, arguments[0], arguments[1], type);
            }
            else if (definition == StreamHandlerOpen)
            {
                RegisterStreamHandler(@interface, arguments[0], arguments[1], type);
            }
            else if (definition == NotificationHandlerOpen)
            {
                RegisterNotificationHandler(@interface, arguments[0], type);
            }
            else if (definition == ExceptionHandlerOpen)
            {
                _result.ExceptionHandlers.Add(new ExceptionHandlerMapping(arguments[0], arguments[1], arguments[2], type));
            }
            else if (definition == ExceptionActionOpen)
            {
                _result.ExceptionActions.Add(new ExceptionActionMapping(arguments[0], arguments[1], type));
            }
            else if (configuration.AutoRegisterRequestProcessors
                && (definition == PreProcessorOpen || definition == PostProcessorOpen))
            {
                _result.Processors.Add((@interface, type));
            }
            else if (definition == ValidatorOpen)
            {
                guard.IncrementRegistrations();
                _result.Validators.Add((@interface, type));
            }
        }
    }

    private void RegisterHandler(Type serviceType, Type requestType, Type responseType, Type implementationType)
    {
        RegisterHandlerCore(serviceType, implementationType);
        _result.RequestPairs.Add((requestType, responseType));
    }

    private void RegisterStreamHandler(Type serviceType, Type requestType, Type responseType, Type implementationType)
    {
        RegisterHandlerCore(serviceType, implementationType);
        _result.StreamPairs.Add((requestType, responseType));
    }

    private void RegisterHandlerCore(Type serviceType, Type implementationType)
    {
        if (_handlerServiceTypes.TryGetValue(serviceType, out var existing))
        {
            throw new DuplicateHandlerException(serviceType, [existing, implementationType]);
        }

        guard.IncrementRegistrations();
        _handlerServiceTypes.Add(serviceType, implementationType);
        _result.Handlers.Add(new ServiceMapping(serviceType, implementationType, false));
    }

    private void RegisterNotificationHandler(Type serviceType, Type notificationType, Type implementationType)
    {
        // 通知允许同一实现注册多个通知接口；同一 (通知, 实现) 组合重复出现则视为重复注册
        if (_result.NotificationHandlers.Any(m => m.ServiceType == serviceType && m.ImplementationType == implementationType))
        {
            return;
        }

        guard.IncrementRegistrations();
        _result.NotificationServiceTypes.Add(serviceType);
        _result.NotificationHandlers.Add(new ServiceMapping(serviceType, implementationType, false));
    }

    private void InspectOpenGenericHandler(Type type)
    {
        if (!configuration.RegisterGenericHandlers || !type.IsGenericTypeDefinition || !configuration.TypeEvaluator(type))
        {
            return;
        }

        var parameters = type.GetGenericArguments();
        var requiresClosing = false;
        foreach (var serviceType in type.GetInterfaces())
        {
            if (!serviceType.IsGenericType)
            {
                continue;
            }

            var definition = serviceType.GetGenericTypeDefinition();
            if (definition == NotificationHandlerOpen && serviceType.GetGenericArguments().SequenceEqual(parameters))
            {
                guard.AddTypeParameters(parameters.Length);
                guard.IncrementRegistrations();
                _result.Handlers.Add(new ServiceMapping(definition, type, true));
                _containerClosedNotifications.Add(type);
            }
            else if (definition == RequestHandlerOpen || definition == StreamHandlerOpen || definition == NotificationHandlerOpen)
            {
                requiresClosing = true;
            }
        }

        if (requiresClosing)
        {
            guard.AddTypeParameters(parameters.Length);
            _openHandlers.Add(type);
        }
    }

    private void CloseGenericHandlers(HashSet<Type> candidates)
    {
        foreach (var openHandler in _openHandlers)
        {
            foreach (var handler in GenericHandlerCloser.Close(openHandler, candidates, guard))
            {
                foreach (var serviceType in handler.GetInterfaces())
                {
                    if (!serviceType.IsGenericType)
                    {
                        continue;
                    }

                    var definition = serviceType.GetGenericTypeDefinition();
                    var arguments = serviceType.GetGenericArguments();
                    if (definition == NotificationHandlerOpen && !_containerClosedNotifications.Contains(openHandler))
                    {
                        RegisterNotificationHandler(serviceType, arguments[0], handler);
                    }
                    else if (definition == RequestHandlerOpen || definition == StreamHandlerOpen)
                    {
                        if (_handlerServiceTypes.TryGetValue(serviceType, out var existing) && existing == handler)
                        {
                            continue;
                        }

                        if (definition == RequestHandlerOpen)
                        {
                            RegisterHandler(serviceType, arguments[0], arguments[1], handler);
                        }
                        else
                        {
                            RegisterStreamHandler(serviceType, arguments[0], arguments[1], handler);
                        }
                    }
                }
            }
        }
    }

    private static void AddCandidate(HashSet<Type> candidates, Type type)
    {
        if (type.ContainsGenericParameters || type.IsByRefLike || !candidates.Add(type))
        {
            return;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            AddCandidate(candidates, argument);
        }
    }

    private void InspectExplicitPair(Type requestType, Type handlerType)
    {
        foreach (var @interface in handlerType.GetInterfaces())
        {
            if (!@interface.IsGenericType)
            {
                continue;
            }

            var definition = @interface.GetGenericTypeDefinition();
            var arguments = @interface.GetGenericArguments();
            var matchesRequest = arguments[0] == requestType;
            if (!matchesRequest)
            {
                continue;
            }

            if (definition == RequestHandlerOpen)
            {
                RegisterHandler(@interface, arguments[0], arguments[1], handlerType);
            }
            else if (definition == StreamHandlerOpen)
            {
                RegisterStreamHandler(@interface, arguments[0], arguments[1], handlerType);
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(static t => t is not null)!;
        }
        catch (Exception exception) when (exception is NotSupportedException or FileLoadException)
        {
            return [];
        }
    }
}
