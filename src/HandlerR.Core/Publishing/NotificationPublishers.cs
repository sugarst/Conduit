using HandlerR.Abstractions;
using HandlerR.Contracts;

namespace HandlerR.Core.Publishing;

/// <summary>
/// 顺序发布：逐个等待处理器完成，异常立即中断后续处理器。
/// </summary>
/// <remarks>默认策略。处理器共享调用方的作用域，因此可在同一事务内串行写入。</remarks>
public sealed class SequentialPublisher : INotificationPublisher
{
    /// <summary>共享实例（无状态）。</summary>
    public static SequentialPublisher Instance { get; } = new();

    /// <inheritdoc />
    public async ValueTask PublishAsync(
        IReadOnlyList<NotificationHandlerExecutor> handlers,
        INotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(notification);

        for (var i = 0; i < handlers.Count; i++)
        {
            await handlers[i].Handler(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 并发发布：全部处理器同时启动，首个异常取消其余并向上抛出。
/// </summary>
/// <remarks>
/// 每个处理器在独立作用域内解析（由注册层内建），因此并发访问 Scoped 资源是安全的。
/// </remarks>
public sealed class ParallelPublisher : IConcurrentNotificationPublisher
{
    /// <summary>共享实例（无状态）。</summary>
    public static ParallelPublisher Instance { get; } = new();

    /// <inheritdoc />
    public async ValueTask PublishAsync(
        IReadOnlyList<NotificationHandlerExecutor> handlers,
        INotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(notification);

        if (handlers.Count == 0)
        {
            return;
        }

        if (handlers.Count == 1)
        {
            await handlers[0].Handler(notification, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tasks = new Task[handlers.Count];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            for (var i = 0; i < tasks.Length; i++)
            {
                tasks[i] = handlers[i].Handler(notification, linked.Token).AsTask();
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// 聚合并发发布：等待全部处理器结束，聚合所有失败后抛出。
/// </summary>
/// <remarks>适用于"尽量都执行、最后统一报告"的审计 / 扇出场景。</remarks>
public sealed class AggregatingParallelPublisher : IConcurrentNotificationPublisher
{
    /// <summary>共享实例（无状态）。</summary>
    public static AggregatingParallelPublisher Instance { get; } = new();

    /// <inheritdoc />
    public async ValueTask PublishAsync(
        IReadOnlyList<NotificationHandlerExecutor> handlers,
        INotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(notification);

        if (handlers.Count == 0)
        {
            return;
        }

        var tasks = new Task[handlers.Count];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = handlers[i].Handler(notification, linked.Token).AsTask();
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // 全部失败已记录在 tasks 上，逐个收集后统一抛出
        }

        var failures = new List<Exception>();
        foreach (var task in tasks)
        {
            if (task.IsFaulted && task.Exception is not null)
            {
                failures.AddRange(task.Exception.InnerExceptions);
            }
        }

        if (failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            throw failures[0];
        }

        throw new AggregateException(
            $"{failures.Count} notification handlers failed.", failures);
    }
}
