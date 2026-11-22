using Conduit.Abstractions;
using Conduit.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.DependencyInjection;

/// <summary>
/// 并发发布下的处理作用域隔离包装：每次调用解析独立作用域，避免并行访问同一个 Scoped 资源。
/// </summary>
/// <typeparam name="THandler">真实处理器实现类型。</typeparam>
/// <typeparam name="TNotification">通知类型。</typeparam>
internal sealed class ScopedNotificationHandler<THandler, TNotification> : INotificationHandler<TNotification>
    where THandler : class, INotificationHandler<TNotification>
    where TNotification : INotification
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ScopedNotificationHandler(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async ValueTask HandleAsync(TNotification notification, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
        await handler.HandleAsync(notification, cancellationToken).ConfigureAwait(false);
    }
}
