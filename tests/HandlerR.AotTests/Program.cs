using HandlerR.Abstractions;
using HandlerR.Contracts;
using HandlerR.DependencyInjection;
using HandlerR.Generated;
using Microsoft.Extensions.DependencyInjection;

namespace HandlerR.AotTests;

/// <summary>
/// Native AOT smoke：只走源生成路径（Mode B）。若任何一环回退到反射，AOT 发布会在裁剪阶段暴露问题。
/// </summary>
public static class Program
{
    public static async Task<int> Main()
    {
        var services = new ServiceCollection();
        services.AddGeneratedHandlerR();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var response = await dispatcher.SendAsync(new AotPingQuery(20));
        if (response != 42)
        {
            Console.Error.WriteLine($"FAIL send: expected 42, got {response}");
            return 1;
        }

        var streamed = 0;
        await foreach (var _ in dispatcher.StreamAsync(new AotNumbers(5)))
        {
            streamed++;
        }

        if (streamed != 5)
        {
            Console.Error.WriteLine($"FAIL stream: expected 5, got {streamed}");
            return 1;
        }

        INotification notification = new AotEvent("ok");
        await dispatcher.PublishAsync(notification);
        if (AotEventHandler.Count != 1 || AotAuditHandler.Count != 1)
        {
            Console.Error.WriteLine($"FAIL publish: expected one call per handler, got {AotEventHandler.Count} and {AotAuditHandler.Count}");
            return 1;
        }

        Console.WriteLine("AOT smoke passed: send + stream + publish");
        return 0;
    }
}

public sealed record AotPingQuery(int Value) : IQuery<int>;

public sealed class AotPingQueryHandler : IRequestHandler<AotPingQuery, int>
{
    public ValueTask<int> HandleAsync(AotPingQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult((request.Value * 2) + 2);
}

public sealed record AotNumbers(int Count) : IStreamRequest<int>;

public sealed class AotNumbersHandler : IStreamRequestHandler<AotNumbers, int>
{
    public async IAsyncEnumerable<int> HandleStreamAsync(
        AotNumbers request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

public sealed record AotEvent(string Payload) : INotification;

public sealed class AotEventHandler : INotificationHandler<AotEvent>
{
    public static int Count { get; private set; }

    public ValueTask HandleAsync(AotEvent notification, CancellationToken cancellationToken)
    {
        Count++;
        return ValueTask.CompletedTask;
    }
}

public sealed class AotAuditHandler : INotificationHandler<AotEvent>
{
    public static int Count { get; private set; }

    public ValueTask HandleAsync(AotEvent notification, CancellationToken cancellationToken)
    {
        Count++;
        return ValueTask.CompletedTask;
    }
}
