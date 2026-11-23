using HandlerR.Contracts;

namespace HandlerR.Abstractions;

/// <summary>
/// 请求处理器。每个可调度请求类型必须恰好有一个实现。
/// </summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
/// <typeparam name="TResponse">响应类型。</typeparam>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>处理请求。</summary>
    ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 通知处理器。同一通知允许零个或多个实现，按注册顺序（或并发）全部执行。
/// </summary>
/// <typeparam name="TNotification">通知类型。</typeparam>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    /// <summary>处理通知。</summary>
    ValueTask HandleAsync(TNotification notification, CancellationToken cancellationToken);
}

/// <summary>
/// 流式请求处理器。响应以异步序列逐项产出，取消令牌按项透传。
/// </summary>
/// <typeparam name="TRequest">流式请求类型。</typeparam>
/// <typeparam name="TResponse">单项元素类型。</typeparam>
public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>处理流式请求。</summary>
    IAsyncEnumerable<TResponse> HandleStreamAsync(TRequest request, CancellationToken cancellationToken);
}
