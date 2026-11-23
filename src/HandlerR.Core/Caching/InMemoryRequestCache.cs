using System.Collections.Concurrent;
using HandlerR.Abstractions;

namespace HandlerR.Core.Caching;

/// <summary>
/// 进程内请求缓存：零依赖默认实现，适用于单实例与开发环境。
/// </summary>
/// <remarks>
/// 多实例部署应替换为分布式实现（例如基于 Redis 的 <see cref="IRequestCache"/>）。
/// 过期项在读写时惰性清理，不启动后台计时器。
/// </remarks>
public sealed class InMemoryRequestCache : IRequestCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private long _lastSweepTicks;

    /// <summary>创建缓存。</summary>
    public InMemoryRequestCache(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>清理间隔，超过后触发一次惰性扫除。</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public ValueTask<(bool Found, object? Value)> TryGetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        SweepIfDue();

        if (_entries.TryGetValue(key, out var entry) && !entry.IsExpired(_timeProvider))
        {
            return ValueTask.FromResult((true, entry.Value));
        }

        return ValueTask.FromResult<(bool, object?)>((false, null));
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, object? value, TimeSpan? timeToLive, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        SweepIfDue();

        _entries[key] = new CacheEntry(value, ExpiresAt(_timeProvider, timeToLive));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> TryAddAsync(string key, object? value, TimeSpan? timeToLive, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        SweepIfDue();

        var entry = new CacheEntry(value, ExpiresAt(_timeProvider, timeToLive));
        if (_entries.TryAdd(key, entry))
        {
            return ValueTask.FromResult(true);
        }

        // 已存在但已过期：视为可抢占
        if (_entries.TryGetValue(key, out var existing) && existing.IsExpired(_timeProvider))
        {
            return ValueTask.FromResult(_entries.TryUpdate(key, entry, existing));
        }

        return ValueTask.FromResult(false);
    }

    private static DateTimeOffset? ExpiresAt(TimeProvider timeProvider, TimeSpan? timeToLive)
        => timeToLive is { } ttl ? timeProvider.GetUtcNow() + ttl : null;

    private void SweepIfDue()
    {
        var now = _timeProvider.GetUtcNow();
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last < SweepInterval.Ticks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSweepTicks, now.UtcTicks);
        foreach (var pair in _entries)
        {
            if (pair.Value.IsExpired(_timeProvider))
            {
                _entries.TryRemove(pair);
            }
        }
    }

    private sealed class CacheEntry(object? value, DateTimeOffset? expiresAt)
    {
        public object? Value { get; } = value;

        public bool IsExpired(TimeProvider timeProvider)
            => expiresAt is { } limit && timeProvider.GetUtcNow() >= limit;
    }
}
