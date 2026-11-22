using Conduit.Abstractions;
using Conduit.Contracts;
using Conduit.DependencyInjection;
using Conduit.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests;

public class SendTests
{
    private static ServiceProvider Build(Action<ConduitConfiguration>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TraceLog>();
        services.AddConduit(cfg =>
        {
            cfg.RegisterFromAssemblyContaining<PingQuery>();
            configure?.Invoke(cfg);
        });

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Send_returns_handler_response()
    {
        await using var provider = Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var response = await dispatcher.SendAsync(new CountQuery(41));

        await Assert.That(response).IsEqualTo(42);
    }

    [Test]
    public async Task Send_with_Unit_response_uses_command_marker()
    {
        await using var provider = Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        await dispatcher.SendAsync(new TouchCommand("alpha"));

        await Assert.That(log.Text).IsEqualTo("touch:alpha");
    }

    [Test]
    public async Task Send_without_handler_throws_HandlerNotFoundException()
    {
        await using var provider = Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await Assert
            .That(() => dispatcher.SendAsync(new NoHandlerQuery()).AsTask())
            .Throws<HandlerNotFoundException>();
    }

    [Test]
    public async Task Send_through_ISender_facade_works()
    {
        await using var provider = Build();
        var sender = provider.GetRequiredService<ISender>();

        var response = await sender.SendAsync(new CountQuery(1));

        await Assert.That(response).IsEqualTo(2);
    }

    [Test]
    public async Task Behaviors_execute_outside_in_with_declaration_order()
    {
        await using var provider = Build(cfg =>
        {
            cfg.AddBehavior<OuterBehavior>();
            cfg.AddBehavior<InnerBehavior>();
        });

        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        await dispatcher.SendAsync(new PingQuery());

        await Assert.That(log.Text).IsEqualTo("outer:enter>inner:enter>handler>inner:exit>outer:exit");
    }

    [Test]
    public async Task Behavior_can_short_circuit_the_handler()
    {
        await using var provider = Build(cfg => cfg.AddBehavior<ShortCircuitBehavior>());

        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        var response = await dispatcher.SendAsync(new PingQuery());

        await Assert.That(response).IsEqualTo("short-circuited");
        await Assert.That(log.Entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Scope_resolved_dispatcher_does_not_capture_root_container()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedDependency>();
        services.AddTransient<ScopedAwareHandler>();
        services.AddConduit(cfg => cfg.Register(typeof(ScopedRequest), typeof(ScopedAwareHandler)));

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var id = await dispatcher.SendAsync(new ScopedRequest());
        var expected = scope.ServiceProvider.GetRequiredService<ScopedDependency>().Id;

        await Assert.That(id).IsEqualTo(expected);
    }
}

public sealed class ScopedDependency
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed record ScopedRequest : IQuery<Guid>;

public sealed class ScopedAwareHandler(ScopedDependency dependency) : IRequestHandler<ScopedRequest, Guid>
{
    public ValueTask<Guid> HandleAsync(ScopedRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult(dependency.Id);
}
