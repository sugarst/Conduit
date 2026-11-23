using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using HandlerR.Abstractions;
using HandlerR.Contracts;
using HandlerR.Core.Publishing;
using HandlerR.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace HandlerR.Benchmarks;

public sealed record PingQuery(int Value) : IQuery<int>;

public sealed class PingQueryHandler : IRequestHandler<PingQuery, int>
{
    public ValueTask<int> HandleAsync(PingQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(request.Value);
}

public sealed class FirstBehavior : IPipelineBehavior<PingQuery, int>
{
    public ValueTask<int> HandleAsync(PingQuery request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next();
}

public sealed class SecondBehavior : IPipelineBehavior<PingQuery, int>
{
    public ValueTask<int> HandleAsync(PingQuery request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next();
}

public sealed class ThirdBehavior : IPipelineBehavior<PingQuery, int>
{
    public ValueTask<int> HandleAsync(PingQuery request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next();
}

public sealed record OrderPlaced(int OrderId) : INotification;

public sealed class HandlerA : INotificationHandler<OrderPlaced>
{
    public ValueTask HandleAsync(OrderPlaced notification, CancellationToken cancellationToken) => default;
}

public sealed class HandlerB : INotificationHandler<OrderPlaced>
{
    public ValueTask HandleAsync(OrderPlaced notification, CancellationToken cancellationToken) => default;
}

public sealed class HandlerC : INotificationHandler<OrderPlaced>
{
    public ValueTask HandleAsync(OrderPlaced notification, CancellationToken cancellationToken) => default;
}

public sealed record CountStream(int Count) : IStreamRequest<int>;

public sealed class CountStreamHandler : IStreamRequestHandler<CountStream, int>
{
    public async IAsyncEnumerable<int> HandleStreamAsync(
        CountStream request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            yield return i;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// 性能基线：对应技术方案 10.1 的五个场景。结果写入 <c>benchmarks/baseline.json</c>，
/// PR 退化超过 10% 由 CI 拦截。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class DispatchBenchmarks
{
    private ServiceProvider _provider = null!;
    private ServiceProvider _behaviorProvider = null!;
    private IDispatcher _dispatcher = null!;
    private IDispatcher _behaviorDispatcher = null!;
    private IDispatcher _scopedDispatcher = null!;

    [GlobalSetup]
    public void Setup()
    {
        _provider = BuildProvider(withBehaviors: false);
        _behaviorProvider = BuildProvider(withBehaviors: true);
        _dispatcher = _provider.GetRequiredService<IDispatcher>();
        _behaviorDispatcher = _behaviorProvider.GetRequiredService<IDispatcher>();
        _scopedDispatcher = _provider.CreateScope().ServiceProvider.GetRequiredService<IDispatcher>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _provider.Dispose();
        _behaviorProvider.Dispose();
    }

    private static ServiceProvider BuildProvider(bool withBehaviors)
    {
        var services = new ServiceCollection();
        services.AddHandlerR(cfg =>
        {
            cfg.RegisterFrom(typeof(PingQuery).Assembly);
            cfg.NotificationPublisher = new SequentialPublisher();

            if (withBehaviors)
            {
                cfg.AddBehavior<FirstBehavior>();
                cfg.AddBehavior<SecondBehavior>();
                cfg.AddBehavior<ThirdBehavior>();
            }
        });

        return services.BuildServiceProvider();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> Send_NoBehavior() => _dispatcher.SendAsync(new PingQuery(1));

    [Benchmark]
    public ValueTask<int> Send_SyncCompleted() => _dispatcher.SendAsync(new PingQuery(2));

    [Benchmark]
    public ValueTask<int> Send_ThreeBehaviors() => _behaviorDispatcher.SendAsync(new PingQuery(4));

    [Benchmark]
    public ValueTask Publish_ThreeHandlers() => _dispatcher.PublishAsync(new OrderPlaced(1));

    [Benchmark]
    public async Task<int> Stream_100_Items()
    {
        var count = 0;
        await foreach (var _ in _dispatcher.StreamAsync(new CountStream(100)))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public ValueTask<int> Send_ScopedDispatcher() => _scopedDispatcher.SendAsync(new PingQuery(3));
}

public static class Program
{
    public static void Main(string[] args)
        => BenchmarkDotNet.Running.BenchmarkRunner.Run<DispatchBenchmarks>(args: args);
}
