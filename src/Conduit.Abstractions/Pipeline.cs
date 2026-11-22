using Conduit.Contracts;

namespace Conduit.Abstractions;

/// <summary>请求处理链的下一环（最内层为处理器本身）。</summary>
/// <typeparam name="TResponse">响应类型。</typeparam>
public delegate ValueTask<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>流式处理链的下一环（最内层为处理器本身）。</summary>
/// <typeparam name="TResponse">单项元素类型。</typeparam>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<TResponse>();

/// <summary>
/// 请求管道行为（责任链）。声明顺序即由外到内的执行顺序。
/// </summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
/// <typeparam name="TResponse">响应类型。</typeparam>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>执行行为并决定是否调用下一环。</summary>
    ValueTask<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}

/// <summary>
/// 流式管道行为。与请求管道独立建链，互不混用。
/// </summary>
/// <typeparam name="TRequest">流式请求类型。</typeparam>
/// <typeparam name="TResponse">单项元素类型。</typeparam>
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>包装流式处理链。</summary>
    IAsyncEnumerable<TResponse> HandleStreamAsync(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
