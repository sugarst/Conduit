using Conduit.Abstractions;
using Conduit.Contracts;
using Conduit.Generated;
using Conduit.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.GeneratorTests;

// —— 被测应用的消息与处理器：全部由源生成器在编译期登记 ——

public sealed record GeneratedQuery(int Value) : IQuery<int>;

public sealed class GeneratedQueryHandler : IRequestHandler<GeneratedQuery, int>
{
    public ValueTask<int> HandleAsync(GeneratedQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(request.Value * 2);
}

public sealed record GeneratedCommand(string Name) : ICommand;

public sealed class GeneratedCommandHandler : IRequestHandler<GeneratedCommand, Unit>
{
    public static readonly List<string> Executed = [];

    public ValueTask<Unit> HandleAsync(GeneratedCommand request, CancellationToken cancellationToken)
    {
        Executed.Add(request.Name);
        return ValueTask.FromResult(Unit.Value);
    }
}

public sealed record GeneratedNumbers(int Count) : IStreamRequest<int>;

public sealed class GeneratedNumbersHandler : IStreamRequestHandler<GeneratedNumbers, int>
{
    public async IAsyncEnumerable<int> HandleStreamAsync(
        GeneratedNumbers request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

public sealed record GeneratedEvent(string Payload) : INotification;

public sealed class GeneratedEventHandler : INotificationHandler<GeneratedEvent>
{
    public static readonly List<string> Received = [];

    public ValueTask HandleAsync(GeneratedEvent notification, CancellationToken cancellationToken)
    {
        Received.Add(notification.Payload);
        return default;
    }
}

public class GeneratedRegistrationTests
{
    private static ServiceProvider Build(Action<ConduitConfiguration>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddGeneratedConduit(configure);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Test]
    public async Task Generated_tables_dispatch_requests_without_reflection()
    {
        await using var provider = Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var response = await dispatcher.SendAsync(new GeneratedQuery(21));

        await Assert.That(response).IsEqualTo(42);
    }

    [Test]
    public async Task Generated_tables_handle_commands_with_unit_response()
    {
        await using var provider = Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new GeneratedCommand("order-1"));

        await Assert.That(GeneratedCommandHandler.Executed).Contains("order-1");
    }

    [Test]
    public async Task Generated_tables_cover_streams()
    {
        await using var provider = Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var items = new List<int>();
        await foreach (var item in dispatcher.StreamAsync(new GeneratedNumbers(3)))
        {
            items.Add(item);
        }

        await Assert.That(items).IsEquivalentTo([0, 1, 2]);
    }

    [Test]
    public async Task Generated_tables_cover_notifications()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.PublishAsync(new GeneratedEvent("hello"));

        await Assert.That(GeneratedEventHandler.Received).Contains("hello");
    }

    [Test]
    public async Task Generated_mode_still_honours_behavior_configuration()
    {
        await using var provider = Build(cfg => cfg.AddBehavior<GeneratedCountingBehavior>());
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new GeneratedQuery(1));

        await Assert.That(GeneratedCountingBehavior.Invocations).IsGreaterThan(0);
    }
}

public sealed class GeneratedCountingBehavior : IPipelineBehavior<GeneratedQuery, int>
{
    private static int _invocations;

    public static int Invocations => Volatile.Read(ref _invocations);

    public async ValueTask<int> HandleAsync(
        GeneratedQuery request,
        RequestHandlerDelegate<int> next,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _invocations);
        return await next().ConfigureAwait(false);
    }
}
