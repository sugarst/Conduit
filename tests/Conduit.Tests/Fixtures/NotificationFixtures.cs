using Conduit.Abstractions;
using Conduit.Contracts;

namespace Conduit.Tests.Fixtures;

public sealed record OrderPlaced(int OrderId) : INotification;

public sealed class FirstNotificationHandler(TraceLog log) : INotificationHandler<OrderPlaced>
{
    public async ValueTask HandleAsync(OrderPlaced notification, CancellationToken cancellationToken)
    {
        await Task.Yield();
        log.Add("first");
    }
}

public sealed class SecondNotificationHandler(TraceLog log) : INotificationHandler<OrderPlaced>
{
    public async ValueTask HandleAsync(OrderPlaced notification, CancellationToken cancellationToken)
    {
        await Task.Yield();
        log.Add("second");
    }
}

/// <summary>作用域探针：并行通知下用于验证每个处理器是否拿到独立作用域。</summary>
public sealed class ScopedProbe
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed class ScopeRecordingHandler(ScopedProbe probe, ScopeRecorder recorder) : INotificationHandler<OrderPlaced>
{
    public ValueTask HandleAsync(OrderPlaced notification, CancellationToken cancellationToken)
    {
        recorder.Record(probe.Id);
        return default;
    }
}

public sealed class ScopeRecordingSecondHandler(ScopedProbe probe, ScopeRecorder recorder) : INotificationHandler<OrderPlaced>
{
    public ValueTask HandleAsync(OrderPlaced notification, CancellationToken cancellationToken)
    {
        recorder.Record(probe.Id);
        return default;
    }
}

public sealed class ScopeRecorder
{
    private readonly List<Guid> _ids = [];

    public IReadOnlyList<Guid> Ids
    {
        get
        {
            lock (_ids)
            {
                return [.. _ids];
            }
        }
    }

    public void Record(Guid id)
    {
        lock (_ids)
        {
            _ids.Add(id);
        }
    }
}

public sealed record FailingNotification : INotification;

public sealed class FailingHandlerA : INotificationHandler<FailingNotification>
{
    public ValueTask HandleAsync(FailingNotification notification, CancellationToken cancellationToken)
        => throw new InvalidOperationException("a failed");
}

public sealed class FailingHandlerB : INotificationHandler<FailingNotification>
{
    public ValueTask HandleAsync(FailingNotification notification, CancellationToken cancellationToken)
        => throw new ArgumentException("b failed");
}

/// <summary>同一实现类型被注册两次时的去重验证。</summary>
public sealed record DuplicateNotification : INotification;

public sealed class SingleHandlerForDuplicate(TraceLog log) : INotificationHandler<DuplicateNotification>
{
    public ValueTask HandleAsync(DuplicateNotification notification, CancellationToken cancellationToken)
    {
        log.Add("dup");
        return default;
    }
}

public sealed record NumbersStream(int Count) : IStreamRequest<int>;

public sealed class NumbersStreamHandler : IStreamRequestHandler<NumbersStream, int>
{
    public async IAsyncEnumerable<int> HandleStreamAsync(
        NumbersStream request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return i;
        }
    }
}

/// <summary>只在元素级改写：每个元素乘 2，用于验证流管线独立建链。</summary>
public sealed class DoublingStreamBehavior : IStreamPipelineBehavior<NumbersStream, int>
{
    public async IAsyncEnumerable<int> HandleStreamAsync(
        NumbersStream request,
        StreamHandlerDelegate<int> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in next().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item * 2;
        }
    }
}

public sealed class PingPreProcessor(TraceLog log) : IRequestPreProcessor<PingQuery>
{
    public ValueTask ProcessAsync(PingQuery request, CancellationToken cancellationToken)
    {
        log.Add("pre");
        return default;
    }
}

public sealed class PingPostProcessor(TraceLog log) : IRequestPostProcessor<PingQuery, string>
{
    public ValueTask ProcessAsync(PingQuery request, string response, CancellationToken cancellationToken)
    {
        log.Add("post");
        return default;
    }
}
