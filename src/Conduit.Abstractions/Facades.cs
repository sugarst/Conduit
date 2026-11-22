using Conduit.Contracts;

namespace Conduit.Abstractions;

/// <summary>
/// 请求发送门面。
/// </summary>
/// <remarks>
/// 无响应请求不单独提供非泛型重载：<c>ICommand</c> 同时实现 <see cref="IRequest"/> 与
/// <see cref="IRequest{TResponse}"/>，因此 <c>SendAsync(command)</c> 会推断 <c>TResponse = Unit</c>，
/// 既保留单一入口，也避免与非泛型重载产生调用二义性。
/// </remarks>
public interface ISender
{
    /// <summary>发送有响应请求（无响应请求推断为 <see cref="Unit"/>）。</summary>
    ValueTask<TResponse> SendAsync<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default);

    /// <summary>发送流式请求。</summary>
    IAsyncEnumerable<TResponse> StreamAsync<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 通知发布门面。
/// </summary>
public interface IPublisher
{
    /// <summary>发布通知。</summary>
    ValueTask PublishAsync<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification;
}

/// <summary>
/// 调度器门面：发送与发布的统一入口。
/// </summary>
public interface IDispatcher : ISender, IPublisher
{
}
