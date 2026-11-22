using System.Diagnostics.CodeAnalysis;
using Conduit.Abstractions;
using Conduit.Contracts;

namespace Conduit.Core.Behaviors;

/// <summary>
/// 事务行为：仅对实现 <see cref="ITransactionalRequest"/> 的请求生效。
/// </summary>
/// <remarks>
/// 事务实现由宿主提供（<see cref="ITransactionScopeFactory"/>），本库不绑定任何 ORM。
/// 未注册工厂时抛出明确的配置错误，而不是静默跳过事务。
/// </remarks>
public sealed class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>创建事务行为。</summary>
    public TransactionBehavior(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "任何异常都必须触发回滚，随后原样重抛。")]
    public async ValueTask<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not ITransactionalRequest)
        {
            return await next().ConfigureAwait(false);
        }

        var factory = _serviceProvider.GetService(typeof(ITransactionScopeFactory)) as ITransactionScopeFactory
            ?? throw new ConduitConfigurationException(
                "TransactionBehavior is registered but no ITransactionScopeFactory is available. "
                + "Register an implementation that adapts your persistence layer (EF Core, Dapper, ...).");

        await using var scope = await factory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await next().ConfigureAwait(false);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch
        {
            await scope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// 缓存行为：仅对实现 <see cref="ICacheable"/> 的请求生效。
/// </summary>
/// <remarks>
/// 缓存键优先取 <see cref="ICacheKeyProvider"/>；否则退化为"类型全名 + ToString()"，
/// 因此请求类型应实现值语义的 <c>ToString()</c>，或直接实现 <see cref="ICacheKeyProvider"/>。
/// </remarks>
public sealed class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>创建缓存行为。</summary>
    public CachingBehavior(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    /// <summary>请求未显式声明缓存时长时的默认存活时间。</summary>
    public TimeSpan DefaultTimeToLive { get; init; } = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not ICacheable cacheable)
        {
            return await next().ConfigureAwait(false);
        }

        var cache = _serviceProvider.GetService(typeof(IRequestCache)) as IRequestCache
            ?? throw new ConduitConfigurationException(
                "CachingBehavior is registered but no IRequestCache is available. "
                + "AddConduit registers an in-memory implementation by default; a custom one may replace it.");

        var key = RequestCacheKey.For(request);

        var (found, value) = await cache.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        if (found && value is TResponse cached)
        {
            return cached;
        }

        var response = await next().ConfigureAwait(false);
        await cache
            .SetAsync(key, response, cacheable.CacheDuration ?? DefaultTimeToLive, cancellationToken)
            .ConfigureAwait(false);

        return response;
    }
}

/// <summary>
/// 缓存键生成：请求可自定义，否则退化为类型 + 文本表示。
/// </summary>
public static class RequestCacheKey
{
    /// <summary>生成缓存键。</summary>
    public static string For<TRequest>(TRequest request)
        where TRequest : IBaseRequest
        => request is ICacheKeyProvider provider
            ? provider.GetCacheKey()
            : string.Concat(typeof(TRequest).FullName, "|", request?.ToString() ?? string.Empty);
}
