using Conduit.Abstractions;
using Conduit.Contracts;

namespace Conduit.TestSupport;

/// <summary>供"只扫描指定程序集"类测试使用的标记类型。</summary>
public static class SupportMarker
{
}

// ———————————————— 重复 Handler（负向用例） ————————————————

public sealed record AmbiguousQuery : IQuery<int>;

public sealed class AmbiguousHandlerA : IRequestHandler<AmbiguousQuery, int>
{
    public ValueTask<int> HandleAsync(AmbiguousQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(1);
}

public sealed class AmbiguousHandlerB : IRequestHandler<AmbiguousQuery, int>
{
    public ValueTask<int> HandleAsync(AmbiguousQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(2);
}

// ———————————————— 类型过滤器用例 ————————————————

public sealed record HiddenQuery : IQuery<string>;

public sealed class HiddenQueryHandler : IRequestHandler<HiddenQuery, string>
{
    public ValueTask<string> HandleAsync(HiddenQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult("hidden");
}

// ———————————————— 嵌套泛型推断用例 ————————————————

public sealed record EnvelopeQuery(int Count) : IQuery<List<int>>;

public sealed class EnvelopeQueryHandler : IRequestHandler<EnvelopeQuery, List<int>>
{
    public ValueTask<List<int>> HandleAsync(EnvelopeQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(Enumerable.Range(1, request.Count).ToList());
}

/// <summary>
/// 开放 Behavior 的响应形态是 <c>List&lt;TItem&gt;</c>，类型参数需从嵌套泛型实参推断。
/// </summary>
public sealed class EnvelopeBehavior<TRequest, TItem> : IPipelineBehavior<TRequest, List<TItem>>
    where TRequest : IRequest<List<TItem>>
{
    public async ValueTask<List<TItem>> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<List<TItem>> next,
        CancellationToken cancellationToken)
    {
        var items = await next().ConfigureAwait(false);
        return [.. items, default!];
    }
}

// ———————————————— 约束筛选用例 ————————————————

public sealed record SupportCachedQuery : IQuery<int>, ICacheable
{
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(1);
}

public sealed class SupportCachedQueryHandler : IRequestHandler<SupportCachedQuery, int>
{
    public ValueTask<int> HandleAsync(SupportCachedQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(1);
}

public sealed record PlainSupportQuery : IQuery<int>;

public sealed class PlainSupportQueryHandler : IRequestHandler<PlainSupportQuery, int>
{
    public ValueTask<int> HandleAsync(PlainSupportQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(1);
}

/// <summary>仅对可缓存请求生效：验证泛型约束筛选。</summary>
public sealed class CacheableOnlyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, ICacheable
{
    private static int _invocations;

    public static int Invocations => Volatile.Read(ref _invocations);

    public static void RecordInvocation() => Interlocked.Increment(ref _invocations);

    public static void Reset() => Interlocked.Exchange(ref _invocations, 0);

    public async ValueTask<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        RecordInvocation();
        return await next().ConfigureAwait(false);
    }
}

// ———————————————— 生命周期用例 ————————————————

public sealed class SupportScopedDependency
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed record OpenGenericQuery<T>(T Value) : IQuery<T> where T : struct;

public sealed class OpenGenericQueryHandler<T> : IRequestHandler<OpenGenericQuery<T>, T> where T : struct
{
    public ValueTask<T> HandleAsync(OpenGenericQuery<T> request, CancellationToken cancellationToken)
        => ValueTask.FromResult(request.Value);
}

public sealed record LifetimeQuery : IQuery<Guid>;

public sealed class LifetimeQueryHandler(SupportScopedDependency dependency) : IRequestHandler<LifetimeQuery, Guid>
{
    public ValueTask<Guid> HandleAsync(LifetimeQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(dependency.Id);
}
