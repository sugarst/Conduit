using HandlerR.Abstractions;
using HandlerR.Core.Publishing;
using HandlerR.DependencyInjection;
using HandlerR.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace HandlerR.Tests;

public class NotificationTests
{
    [Test]
    public async Task Sequential_publisher_runs_handlers_in_registration_order()
    {
        await using var provider = TestHost.Build();
        var log = provider.GetRequiredService<TraceLog>();

        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.PublishAsync(new OrderPlaced(1));

        await Assert.That(log.Text).IsEqualTo("first>second");
    }

    [Test]
    public async Task Parallel_publisher_isolates_each_handler_scope()
    {
        await using var provider = TestHost.Build(cfg => cfg.NotificationPublisher = new ParallelPublisher());
        var recorder = provider.GetRequiredService<ScopeRecorder>();

        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        await dispatcher.PublishAsync(new OrderPlaced(2));

        var ids = recorder.Ids;
        await Assert.That(ids.Count).IsEqualTo(2);
        await Assert.That(ids[0]).IsNotEqualTo(ids[1]);
    }

    [Test]
    public async Task Sequential_publisher_shares_the_callers_scope()
    {
        await using var provider = TestHost.Build();
        var recorder = provider.GetRequiredService<ScopeRecorder>();

        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        await dispatcher.PublishAsync(new OrderPlaced(3));

        var ids = recorder.Ids;
        await Assert.That(ids.Count).IsEqualTo(2);
        await Assert.That(ids[0]).IsEqualTo(ids[1]);
    }

    [Test]
    public async Task Aggregating_publisher_collects_every_failure()
    {
        await using var provider = TestHost.Build(cfg => cfg.NotificationPublisher = new AggregatingParallelPublisher());
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var exception = await Assert
            .That(() => dispatcher.PublishAsync(new FailingNotification()).AsTask())
            .Throws<AggregateException>();

        await Assert.That(exception!.InnerExceptions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Parallel_publisher_surfaces_first_failure()
    {
        await using var provider = TestHost.Build(cfg => cfg.NotificationPublisher = new ParallelPublisher());
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Assert
            .That(() => dispatcher.PublishAsync(new FailingNotification()).AsTask())
            .Throws<Exception>();
    }

    [Test]
    public async Task Notification_without_handlers_is_a_noop()
    {
        await using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.PublishAsync(new UnhandledNotification());

        await Assert.That(provider.GetRequiredService<TraceLog>().Entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Duplicate_handler_instances_are_deduplicated_at_runtime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TraceLog>();
        services.AddSingleton<ScopeRecorder>();
        services.AddScoped<ScopedProbe>();
        services.AddTransient<INotificationHandler<DuplicateNotification>, SingleHandlerForDuplicate>();
        services.AddTransient<INotificationHandler<DuplicateNotification>, SingleHandlerForDuplicate>();

        services.AddHandlerR(cfg =>
        {
            cfg.RegisterFromAssemblyContaining<PingQuery>();
            cfg.NotificationPublisher = new SequentialPublisher();
        });

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var log = provider.GetRequiredService<TraceLog>();

        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        await dispatcher.PublishAsync(new DuplicateNotification());

        await Assert.That(log.Entries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Publisher_type_takes_precedence_over_instance()
    {
        await using var provider = TestHost.Build(cfg =>
        {
            cfg.NotificationPublisher = new SequentialPublisher();
            cfg.NotificationPublisherType = typeof(ParallelPublisher);
        });

        var publisher = provider.GetRequiredService<INotificationPublisher>();

        await Assert.That(publisher).IsTypeOf<ParallelPublisher>();
    }
}

public sealed record UnhandledNotification : HandlerR.Contracts.INotification;
