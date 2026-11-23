using HandlerR.Abstractions;
using HandlerR.Contracts;
using HandlerR.Core.Internal;

namespace HandlerR.Core.Dispatch;

/// <summary>
/// 请求前处理器运行器：按请求运行时类型查表，再调用已闭合的前处理器入口。
/// </summary>
public interface IRequestPreProcessorRunner
{
    /// <summary>执行全部已注册的前处理器。</summary>
    ValueTask RunAsync(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

/// <summary>
/// 请求后处理器运行器。
/// </summary>
public interface IRequestPostProcessorRunner
{
    /// <summary>执行全部已注册的后处理器。</summary>
    ValueTask RunAsync(
        object request,
        object? response,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>
/// 已闭合的处理器入口（前 / 后处理器共用）。
/// </summary>
public interface IProcessorInvoker
{
    /// <summary>执行处理器。</summary>
    ValueTask InvokeAsync(
        object request,
        object? response,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>闭合到具体请求类型的前处理器入口。</summary>
public sealed class PreProcessorInvoker<TRequest> : IProcessorInvoker
    where TRequest : IBaseRequest
{
    /// <inheritdoc />
    public async ValueTask InvokeAsync(
        object request,
        object? response,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var processors = serviceProvider.ResolveAll<IRequestPreProcessor<TRequest>>();
        for (var i = 0; i < processors.Count; i++)
        {
            await processors[i].ProcessAsync((TRequest)request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>闭合到具体请求 / 响应类型的后处理器入口。</summary>
public sealed class PostProcessorInvoker<TRequest, TResponse> : IProcessorInvoker
    where TRequest : IRequest<TResponse>
{
    /// <inheritdoc />
    public async ValueTask InvokeAsync(
        object request,
        object? response,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var processors = serviceProvider.ResolveAll<IRequestPostProcessor<TRequest, TResponse>>();
        if (processors.Count == 0)
        {
            return;
        }

        var typedResponse = (TResponse)response!;
        for (var i = 0; i < processors.Count; i++)
        {
            await processors[i].ProcessAsync((TRequest)request, typedResponse, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 前处理器运行器实现：启动期建立 <c>请求类型 → 入口</c> 索引，热路径零反射。
/// </summary>
public sealed class RequestPreProcessorRunner : IRequestPreProcessorRunner
{
    private readonly IReadOnlyDictionary<Type, IProcessorInvoker> _invokers;

    /// <summary>创建运行器。</summary>
    public RequestPreProcessorRunner(IReadOnlyDictionary<Type, IProcessorInvoker> invokers)
    {
        ArgumentNullException.ThrowIfNull(invokers);
        _invokers = invokers;
    }

    /// <inheritdoc />
    public ValueTask RunAsync(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _invokers.TryGetValue(request.GetType(), out var invoker)
            ? invoker.InvokeAsync(request, null, serviceProvider, cancellationToken)
            : default;
    }
}

/// <summary>
/// 后处理器运行器实现。
/// </summary>
public sealed class RequestPostProcessorRunner : IRequestPostProcessorRunner
{
    private readonly IReadOnlyDictionary<Type, IProcessorInvoker> _invokers;

    /// <summary>创建运行器。</summary>
    public RequestPostProcessorRunner(IReadOnlyDictionary<Type, IProcessorInvoker> invokers)
    {
        ArgumentNullException.ThrowIfNull(invokers);
        _invokers = invokers;
    }

    /// <inheritdoc />
    public ValueTask RunAsync(
        object request,
        object? response,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _invokers.TryGetValue(request.GetType(), out var invoker)
            ? invoker.InvokeAsync(request, response, serviceProvider, cancellationToken)
            : default;
    }
}
