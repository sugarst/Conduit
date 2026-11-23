namespace HandlerR.Contracts;

/// <summary>
/// 无响应请求的形状标记。
/// </summary>
/// <remarks>
/// 本库把"无返回值"统一收敛到 <see cref="Unit"/>，因此可被调度的无响应请求
/// 除本接口外还需实现 <see cref="IRequest{TResponse}"/>（<c>ICommand</c> 已内建该组合）。
/// 这样对外只需一个双泛型 Handler 接口 <c>IRequestHandler&lt;TRequest, TResponse&gt;</c>。
/// </remarks>
public interface IRequest : IBaseRequest
{
}

/// <summary>
/// 有响应请求的形状标记。
/// </summary>
/// <typeparam name="TResponse">响应类型。</typeparam>
public interface IRequest<out TResponse> : IBaseRequest
{
}
