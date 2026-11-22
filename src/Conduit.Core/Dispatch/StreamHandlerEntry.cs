using Conduit.Abstractions;
using Conduit.Contracts;
using Conduit.Core.Internal;

namespace Conduit.Core.Dispatch;

/// <summary>
/// 流式调度表条目基类。
/// </summary>
public abstract class StreamHandlerEntry
{
    /// <summary>该条目对应的流式请求类型。</summary>
    public abstract Type RequestType { get; }

    /// <summary>该条目对应的单项元素类型。</summary>
    public abstract Type ItemType { get; }

    /// <summary>该条目依赖的处理器服务类型（用于诊断与去重提示）。</summary>
    public abstract Type HandlerServiceType { get; }
}

/// <summary>
/// 已知元素类型的流式条目。
/// </summary>
/// <typeparam name="TResponse">单项元素类型。</typeparam>
public abstract class StreamHandlerEntry<TResponse> : StreamHandlerEntry
{
    /// <inheritdoc />
    public sealed override Type ItemType => typeof(TResponse);

    /// <summary>构建流式管道。</summary>
    public abstract IAsyncEnumerable<TResponse> InvokeStream(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>
/// 流式调度条目：与请求管道独立建链，元素级取消令牌按项透传。
/// </summary>
/// <typeparam name="TRequest">流式请求类型。</typeparam>
/// <typeparam name="TResponse">单项元素类型。</typeparam>
public sealed class StreamHandlerEntry<TRequest, TResponse> : StreamHandlerEntry<TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <inheritdoc />
    public override Type RequestType => typeof(TRequest);

    /// <inheritdoc />
    public override Type HandlerServiceType => typeof(IStreamRequestHandler<TRequest, TResponse>);

    /// <inheritdoc />
    public override IAsyncEnumerable<TResponse> InvokeStream(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var typedRequest = (TRequest)request;
        var handler = serviceProvider.ResolveRequired<IStreamRequestHandler<TRequest, TResponse>>();

        StreamHandlerDelegate<TResponse> next = () => handler.HandleStreamAsync(typedRequest, cancellationToken);

        var behaviors = serviceProvider.ResolveAll<IStreamPipelineBehavior<TRequest, TResponse>>();
        for (var i = behaviors.Count - 1; i >= 0; i--)
        {
            var current = behaviors[i];
            var inner = next;
            next = () => current.HandleStreamAsync(typedRequest, inner, cancellationToken);
        }

        return next();
    }
}
