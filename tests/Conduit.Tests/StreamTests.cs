using Conduit.Abstractions;
using Conduit.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests;

public class StreamTests
{
    [Test]
    public async Task Stream_yields_every_item()
    {
        await using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var items = new List<int>();
        await foreach (var item in dispatcher.StreamAsync(new NumbersStream(5)))
        {
            items.Add(item);
        }

        await Assert.That(items).IsEquivalentTo([1, 2, 3, 4, 5]);
    }

    [Test]
    public async Task Stream_behaviors_wrap_each_item()
    {
        await using var provider = TestHost.Build(cfg => cfg.AddStreamBehavior<DoublingStreamBehavior>());
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var items = new List<int>();
        await foreach (var item in dispatcher.StreamAsync(new NumbersStream(3)))
        {
            items.Add(item);
        }

        await Assert.That(items).IsEquivalentTo([2, 4, 6]);
    }

    [Test]
    public async Task Stream_stops_when_cancelled()
    {
        await using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        using var cts = new CancellationTokenSource();
        var count = 0;

        await Assert.That(async () =>
        {
            await foreach (var _ in dispatcher.StreamAsync(new NumbersStream(100), cts.Token))
            {
                count++;
                if (count == 2)
                {
                    await cts.CancelAsync();
                }
            }
        }).Throws<OperationCanceledException>();

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task Stream_without_handler_throws()
    {
        await using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var threw = false;
        try
        {
            await dispatcher.StreamAsync(new UnregisteredStream()).ToListAsync();
        }
        catch (HandlerNotFoundException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}

public sealed record UnregisteredStream : Conduit.Contracts.IStreamRequest<int>;

internal static class StreamExtensions
{
    public static async Task<List<T>> ToListAsync<T>(
        this IAsyncEnumerable<T> source,
        CancellationToken cancellationToken = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            list.Add(item);
        }

        return list;
    }
}
