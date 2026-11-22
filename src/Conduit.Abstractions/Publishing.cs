using Conduit.Contracts;

namespace Conduit.Abstractions;

/// <summary>
/// 已归一化的通知处理器调用入口：把异构的处理器泛型收敛为统一委托。
/// </summary>
/// <param name="Handler">处理器调用委托。</param>
public readonly record struct NotificationHandlerExecutor(
    Func<INotification, CancellationToken, ValueTask> Handler);

/// <summary>
/// 通知发布策略。可整体替换，不锁定为枚举。
/// </summary>
public interface INotificationPublisher
{
    /// <summary>将通知派发给全部处理器。</summary>
    ValueTask PublishAsync(
        IReadOnlyList<NotificationHandlerExecutor> handlers,
        INotification notification,
        CancellationToken cancellationToken);
}

/// <summary>
/// 并发型发布策略的标记。分发器据此为每个处理器建立独立作用域，
/// 避免并行访问同一个 Scoped 资源（如 DbContext）。
/// </summary>
public interface IConcurrentNotificationPublisher : INotificationPublisher
{
}
