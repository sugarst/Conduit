using System.Diagnostics.CodeAnalysis;
using HandlerR.Abstractions;
using HandlerR.Contracts;

namespace HandlerR.Core.Behaviors;

/// <summary>
/// 超时行为：仅对实现 <see cref="ITimeoutRequest"/> 的请求生效。
/// </summary>
/// <remarks>
/// 委托链不携带令牌参数，因此超时以"竞速"方式实现：超时先到即抛 <see cref="TimeoutException"/>，
/// 已开始的处理器会在后台继续执行到结束（其副作用需业务侧按幂等语义处理）。
/// </remarks>
public sealed class TimeoutBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not ITimeoutRequest timeoutRequest || timeoutRequest.Timeout <= TimeSpan.Zero)
        {
            return await next().ConfigureAwait(false);
        }

        var work = next().AsTask();
        var timeout = Task.Delay(timeoutRequest.Timeout, cancellationToken);

        var winner = await Task.WhenAny(work, timeout).ConfigureAwait(false);
        if (winner == work)
        {
            return await work.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"Request '{typeof(TRequest).FullName}' exceeded its timeout of {timeoutRequest.Timeout}.");
    }
}

/// <summary>
/// 重试行为：仅对同时实现 <see cref="IRetryable"/> 与 <see cref="IIdempotent"/> 的请求生效。
/// </summary>
/// <remarks>指数退避 + 可选抖动；取消令牌触发时立即停止，不吞掉 <see cref="OperationCanceledException"/>。</remarks>
public sealed class RetryBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "重试策略按策略捕获全部异常并在退避后重放；取消异常单独排除。")]
    public async ValueTask<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IRetryable retryable || request is not IIdempotent)
        {
            return await next().ConfigureAwait(false);
        }

        var policy = retryable.Policy;
        var maxAttempts = Math.Max(1, policy.MaxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await next().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                var delay = ComputeDelay(policy, attempt);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static TimeSpan ComputeDelay(RetryPolicy policy, int attempt)
    {
        var exponential = policy.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = policy.MaxDelay is { } max ? Math.Min(exponential, max.TotalMilliseconds) : exponential;
        if (!policy.UseJitter)
        {
            return TimeSpan.FromMilliseconds(capped);
        }

        var jitterFactor = 0.5 + (Random.Shared.NextDouble() * 0.5);
        return TimeSpan.FromMilliseconds(capped * jitterFactor);
    }
}

/// <summary>
/// 幂等行为：仅对实现 <see cref="IIdempotent"/> 的请求生效，用缓存实现键级去重。
/// </summary>
/// <remarks>
/// 首次请求写入占位键；同键并发 / 重放请求将收到 <see cref="HandlerRDuplicateRequestException"/>。
/// 这是"至少一次投递"语义下的兜底，不替代数据库唯一约束。
/// </remarks>
public sealed class IdempotencyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>创建幂等行为。</summary>
    public IdempotencyBehavior(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    /// <summary>幂等键存活时间。</summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromHours(24);

    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IIdempotent idempotent)
        {
            return await next().ConfigureAwait(false);
        }

        var cache = _serviceProvider.GetService(typeof(IRequestCache)) as IRequestCache
            ?? throw new HandlerRConfigurationException(
                "IdempotencyBehavior is registered but no IRequestCache is available.");

        var key = string.Concat("handlerr:idem:", typeof(TRequest).FullName, ":", idempotent.IdempotencyKey);
        if (!await cache.TryAddAsync(key, null, TimeToLive, cancellationToken).ConfigureAwait(false))
        {
            throw new HandlerRDuplicateRequestException(typeof(TRequest), idempotent.IdempotencyKey);
        }

        return await next().ConfigureAwait(false);
    }
}

/// <summary>
/// 重复请求异常（幂等键已存在）。
/// </summary>
public sealed class HandlerRDuplicateRequestException : HandlerRException
{
    /// <summary>创建异常。</summary>
    public HandlerRDuplicateRequestException(Type requestType, string idempotencyKey)
        : base($"Duplicate request rejected for '{requestType.FullName}' with idempotency key '{idempotencyKey}'.")
    {
        RequestType = requestType;
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>请求类型。</summary>
    public Type RequestType { get; }

    /// <summary>冲突的幂等键。</summary>
    public string IdempotencyKey { get; }
}
