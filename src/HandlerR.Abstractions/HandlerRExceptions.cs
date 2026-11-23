namespace HandlerR.Abstractions;

/// <summary>
/// HandlerR 抛出的所有异常基类，便于宿主一次性捕获。
/// </summary>
public abstract class HandlerRException : Exception
{
    /// <summary>创建异常。</summary>
    protected HandlerRException(string message) : base(message)
    {
    }

    /// <summary>创建异常。</summary>
    protected HandlerRException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// 请求类型没有可用的处理器（调度表未命中）。
/// </summary>
public sealed class HandlerNotFoundException : HandlerRException
{
    /// <summary>创建异常。</summary>
    public HandlerNotFoundException(Type requestType)
        : base($"No handler is registered for request type '{requestType.FullName}'. "
               + "Register the handler via AddHandlerR(cfg => cfg.RegisterFrom(...)), cfg.Register(...), "
               + "or AddGeneratedHandlerR() when using the source generator.")
        => RequestType = requestType;

    /// <summary>未命中处理器的请求类型。</summary>
    public Type RequestType { get; }
}

/// <summary>
/// 调度表命中，但入口的响应类型与调用方要求的响应类型不一致。
/// </summary>
public sealed class ResponseTypeMismatchException : HandlerRException
{
    /// <summary>创建异常。</summary>
    public ResponseTypeMismatchException(Type requestType, Type expectedResponseType)
        : base($"Request '{requestType.FullName}' was dispatched expecting response type "
               + $"'{expectedResponseType.FullName}', but the registered handler produces a different response type.")
    {
        RequestType = requestType;
        ExpectedResponseType = expectedResponseType;
    }

    /// <summary>请求类型。</summary>
    public Type RequestType { get; }

    /// <summary>调用方期望的响应类型。</summary>
    public Type ExpectedResponseType { get; }
}

/// <summary>
/// 同一请求类型注册了多个处理器，或同一通知处理器被重复注册。
/// </summary>
public sealed class DuplicateHandlerException : HandlerRException
{
    /// <summary>创建异常。</summary>
    public DuplicateHandlerException(Type serviceType, IReadOnlyList<Type> implementations)
        : base($"Multiple handlers registered for '{serviceType.FullName}': "
               + string.Join(", ", implementations.Select(static t => t.FullName))
               + ". A request must have exactly one handler.")
    {
        ServiceType = serviceType;
        Implementations = implementations;
    }

    /// <summary>被重复注册的处理器服务类型。</summary>
    public Type ServiceType { get; }

    /// <summary>冲突的实现类型。</summary>
    public IReadOnlyList<Type> Implementations { get; }
}

/// <summary>
/// 配置非法（生命周期组合、护栏取值、注册顺序等）。
/// </summary>
public sealed class HandlerRConfigurationException : HandlerRException
{
    /// <summary>创建异常。</summary>
    public HandlerRConfigurationException(string message) : base(message)
    {
    }

    /// <summary>创建异常。</summary>
    public HandlerRConfigurationException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// 启动期校验失败，聚合全部问题而非只报第一个。
/// </summary>
public sealed class HandlerRStartupValidationException : HandlerRException
{
    /// <summary>创建异常。</summary>
    public HandlerRStartupValidationException(IReadOnlyList<string> failures)
        : base("HandlerR startup validation failed:" + Environment.NewLine
               + string.Join(Environment.NewLine, failures.Select(static f => "  - " + f)))
        => Failures = failures;

    /// <summary>全部失败项。</summary>
    public IReadOnlyList<string> Failures { get; }
}
