using HandlerR.Contracts;

namespace HandlerR.Abstractions;

/// <summary>
/// 命令语义标记：有副作用、无业务响应值（响应类型固定为 <see cref="Unit"/>）。
/// </summary>
/// <remarks>
/// 同时继承 <see cref="IRequest"/> 与 <see cref="IRequest{TResponse}"/>，使命令自动满足
/// <c>IRequestHandler&lt;TCommand, Unit&gt;</c> 的编译期约束，并可作为 Behavior 的筛选标记。
/// </remarks>
public interface ICommand : IRequest, IRequest<Unit>
{
}

/// <summary>
/// 查询语义标记：无副作用、返回业务响应（可作缓存 / 只读路由筛选依据）。
/// </summary>
/// <typeparam name="TResponse">响应类型。</typeparam>
public interface IQuery<out TResponse> : IRequest<TResponse>
{
}
