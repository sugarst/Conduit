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
    public async Task Parallel_publisher_cancels_running_peer_before_propagating_first_failure()
    {
        var failure = new InvalidOperationException("first failure");
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new NotificationHandlerExecutor(async (_, _) =>
        {
            await siblingStarted.Task;
            throw failure;
        });
        var sibling = new NotificationHandlerExecutor(async (_, token) =>
        {
            siblingStarted.SetResult();
            try
            {
                await releaseSibling.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                siblingCanceled.TrySetResult();
                throw;
            }
        });

        var publish = ParallelPublisher.Instance
            .PublishAsync([fail, sibling], new UnhandledNotification(), CancellationToken.None).AsTask();
        bool canceledBeforeRelease;
        try
        {
            canceledBeforeRelease = await Task.WhenAny(siblingCanceled.Task, Task.Delay(TimeSpan.FromSeconds(2)))
                == siblingCanceled.Task;
        }
        finally
        {
            releaseSibling.TrySetResult();
        }

        var observed = await ExceptionCapture.CaptureAsync(
            () => new ValueTask(publish.WaitAsync(TimeSpan.FromSeconds(2))));

        await Assert.That(canceledBeforeRelease).IsTrue();
        await Assert.That(ReferenceEquals(observed, failure)).IsTrue();
    }

    [Test]
    public async Task Aggregating_publisher_propagates_cancellation_without_handler_failures()
    {
        using var cancellation = new CancellationTokenSource();
        var waitForCancellation = new NotificationHandlerExecutor(
            (_, token) => new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, token)));
        var completed = new NotificationHandlerExecutor((_, _) => ValueTask.CompletedTask);
        var publish = AggregatingParallelPublisher.Instance
            .PublishAsync([waitForCancellation, completed], new UnhandledNotification(), cancellation.Token).AsTask();

        await cancellation.CancelAsync();
        var observed = await ExceptionCapture.CaptureAsync(
            () => new ValueTask(publish.WaitAsync(TimeSpan.FromSeconds(2))));

        await Assert.That(observed is OperationCanceledException).IsTrue();
        await Assert.That(publish.IsCanceled).IsTrue();
    }

    [Test]
    public async Task Aggregating_publisher_reports_handler_failure_when_another_handler_is_canceled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var failure = new InvalidOperationException("handler failure");
        var fail = new NotificationHandlerExecutor((_, _) => throw failure);
        var canceledHandlerInvoked = false;
        var canceled = new NotificationHandlerExecutor((_, _) =>
        {
            canceledHandlerInvoked = true;
            return ValueTask.FromCanceled(cancellation.Token);
        });

        var observed = await ExceptionCapture.CaptureAsync(() => AggregatingParallelPublisher.Instance
            .PublishAsync([fail, canceled], new UnhandledNotification(), CancellationToken.None));

        await Assert.That(canceledHandlerInvoked).IsTrue();
        await Assert.That(ReferenceEquals(observed, failure)).IsTrue();
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
