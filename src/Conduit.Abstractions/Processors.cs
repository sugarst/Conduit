using Conduit.Contracts;

namespace Conduit.Abstractions;

/// <summary>
/// 请求前处理器。在所有 Behavior 之前执行，注册即生效。
/// </summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
public interface IRequestPreProcessor<in TRequest>
    where TRequest : IBaseRequest
{
    /// <summary>前处理。</summary>
    ValueTask ProcessAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 请求后处理器。在最外层 Behavior 正常返回之后执行；异常路径不执行。
/// </summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
/// <typeparam name="TResponse">响应类型。</typeparam>
public interface IRequestPostProcessor<in TRequest, in TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>后处理。</summary>
    ValueTask ProcessAsync(TRequest request, TResponse response, CancellationToken cancellationToken);
}
