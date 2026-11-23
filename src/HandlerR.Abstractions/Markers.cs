using HandlerR.Contracts;

namespace HandlerR.Abstractions;

/// <summary>可缓存请求的筛选标记。</summary>
public interface ICacheable
{
    /// <summary>缓存存活时间；<c>null</c> 表示使用全局默认值。</summary>
    TimeSpan? CacheDuration { get; }
}

/// <summary>可提供稳定缓存键的请求。</summary>
public interface ICacheKeyProvider
{
    /// <summary>返回缓存键（应包含全部影响结果的请求参数）。</summary>
    string GetCacheKey();
}

/// <summary>重试策略参数。</summary>
/// <param name="MaxAttempts">最大尝试次数（含首次）。</param>
/// <param name="BaseDelay">首次退避基数。</param>
/// <param name="MaxDelay">单次退避上限；<c>null</c> 表示不设上限。</param>
/// <param name="UseJitter">是否叠加抖动，避免重试风暴。</param>
public readonly record struct RetryPolicy(
    int MaxAttempts,
    TimeSpan BaseDelay,
    TimeSpan? MaxDelay = null,
    bool UseJitter = true)
{
    /// <summary>默认策略：3 次尝试、100ms 基数、2s 上限、带抖动。</summary>
    public static RetryPolicy Default => new(3, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
}

/// <summary>可重试请求的筛选标记。重试仅对标注为幂等的请求生效。</summary>
public interface IRetryable
{
    /// <summary>重试策略。</summary>
    RetryPolicy Policy { get; }
}

/// <summary>幂等请求的筛选标记，为幂等去重与安全重试提供键。</summary>
public interface IIdempotent
{
    /// <summary>幂等键。</summary>
    string IdempotencyKey { get; }
}

/// <summary>需要事务边界的请求标记。</summary>
public interface ITransactionalRequest
{
}

/// <summary>自带超时预算的请求标记。</summary>
public interface ITimeoutRequest
{
    /// <summary>超时时长。</summary>
    TimeSpan Timeout { get; }
}

/// <summary>事务作用域。</summary>
public interface ITransactionScope : IAsyncDisposable
{
    /// <summary>提交事务。</summary>
    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>回滚事务。</summary>
    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 事务作用域工厂。由宿主适配具体持久化框架（EF Core / Dapper 等），
/// HandlerR 自身不绑定任何 ORM。
/// </summary>
public interface ITransactionScopeFactory
{
    /// <summary>开启事务。</summary>
    ValueTask<ITransactionScope> BeginAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 请求缓存抽象（供内置 <c>CachingBehavior</c> / <c>IdempotencyBehavior</c> 使用）。
/// 宿主可适配分布式缓存；默认提供进程内实现。
/// </summary>
public interface IRequestCache
{
    /// <summary>尝试读取缓存项。</summary>
    ValueTask<(bool Found, object? Value)> TryGetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>写入缓存项。</summary>
    ValueTask SetAsync(string key, object? value, TimeSpan? timeToLive, CancellationToken cancellationToken = default);

    /// <summary>仅当键不存在时写入；返回是否写入成功（用于幂等占位）。</summary>
    ValueTask<bool> TryAddAsync(string key, object? value, TimeSpan? timeToLive, CancellationToken cancellationToken = default);
}
