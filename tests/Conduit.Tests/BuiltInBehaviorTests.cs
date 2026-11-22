using Conduit.Abstractions;
using Conduit.Core.Behaviors;
using Conduit.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests;

public class BuiltInBehaviorTests
{
    [Test]
    public async Task Timeout_behavior_throws_when_budget_exceeded()
    {
        await using var provider = TestHost.Build(cfg => cfg.AddOpenBehavior(typeof(TimeoutBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        Exception? captured = null;
        try
        {
            await dispatcher.SendAsync(new SlowQuery());
        }
        catch (Exception exception)
        {
            captured = exception;
        }

        await Assert.That(captured).IsTypeOf<TimeoutException>();
        await Assert.That(captured!.Message).Contains("SlowQuery");
    }

    [Test]
    [NotInParallel]
    public async Task Retry_behavior_replays_idempotent_requests()
    {
        FlakyCommandHandler.Reset();
        await using var provider = TestHost.Build(cfg => cfg.AddOpenBehavior(typeof(RetryBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new FlakyCommand());

        await Assert.That(FlakyCommandHandler.Attempts).IsEqualTo(3);
    }

    [Test]
    [NotInParallel]
    public async Task Retry_behavior_ignores_non_idempotent_requests()
    {
        NonIdempotentFlakyHandler.Reset();
        await using var provider = TestHost.Build(cfg => cfg.AddOpenBehavior(typeof(RetryBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var threw = false;
        try
        {
            await dispatcher.SendAsync(new NonIdempotentFlakyCommand());
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That(NonIdempotentFlakyHandler.Attempts).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    public async Task Idempotency_behavior_rejects_duplicate_keys()
    {
        await using var provider = TestHost.Build(cfg => cfg.AddOpenBehavior(typeof(IdempotencyBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new IdempotentCommand("order-1"));

        var exception = await ExceptionCapture.CaptureAsync(
            () => dispatcher.SendAsync(new IdempotentCommand("order-1")));

        await Assert.That(exception).IsTypeOf<ConduitDuplicateRequestException>();
    }

    [Test]
    [NotInParallel]
    public async Task Idempotency_behavior_allows_distinct_keys()
    {
        IdempotentCommandHandler.Reset();
        await using var provider = TestHost.Build(cfg => cfg.AddOpenBehavior(typeof(IdempotencyBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new IdempotentCommand("order-a"));
        await dispatcher.SendAsync(new IdempotentCommand("order-b"));

        await Assert.That(IdempotentCommandHandler.Invocations).IsEqualTo(2);
    }

    [Test]
    [NotInParallel]
    public async Task Caching_behavior_reuses_handler_result()
    {
        CachedQueryHandler.Reset();
        await using var provider = TestHost.Build(cfg => cfg.AddOpenBehavior(typeof(CachingBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var first = await dispatcher.SendAsync(new CachedQuery("k1"));
        var second = await dispatcher.SendAsync(new CachedQuery("k1"));

        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(1);
        await Assert.That(CachedQueryHandler.Invocations).IsEqualTo(1);
    }

    [Test]
    public async Task Transaction_behavior_commits_on_success()
    {
        await using var provider = TestHost.Build(
            cfg => cfg.AddOpenBehavior(typeof(TransactionBehavior<,>)),
            services => services.AddSingleton<ITransactionScopeFactory, RecordingTransactionScopeFactory>());

        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        await dispatcher.SendAsync(new TransactionalCommand(ShouldFail: false));

        await Assert.That(log.Text).IsEqualTo("commit");
    }

    [Test]
    public async Task Transaction_behavior_rolls_back_on_failure()
    {
        await using var provider = TestHost.Build(
            cfg => cfg.AddOpenBehavior(typeof(TransactionBehavior<,>)),
            services => services.AddSingleton<ITransactionScopeFactory, RecordingTransactionScopeFactory>());

        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        try
        {
            await dispatcher.SendAsync(new TransactionalCommand(ShouldFail: true));
        }
        catch (DomainException)
        {
            // 预期
        }

        await Assert.That(log.Text).IsEqualTo("rollback");
    }

    [Test]
    public async Task Transaction_behavior_requires_a_scope_factory()
    {
        await using var provider = TestHost.Build(cfg => cfg.AddOpenBehavior(typeof(TransactionBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var exception = await ExceptionCapture.CaptureAsync(
            () => dispatcher.SendAsync(new TransactionalCommand(ShouldFail: false)));

        await Assert.That(exception).IsTypeOf<ConduitConfigurationException>();
    }

    [Test]
    public async Task Validation_behavior_throws_with_all_errors()
    {
        await using var provider = TestHost.Build(cfg => cfg.AddOpenBehavior(typeof(ValidationBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var exception = await ExceptionCapture.CaptureAsync(
            () => dispatcher.SendAsync(new ValidatedQuery(" ")));

        await Assert.That(exception).IsTypeOf<RequestValidationException>();

        var validation = (RequestValidationException)exception!;
        await Assert.That(validation.Errors.Count).IsEqualTo(1);
        await Assert.That(validation.Errors[0].ErrorCode).IsEqualTo("NAME_REQUIRED");
    }

    [Test]
    public async Task Validation_behavior_passes_valid_requests()
    {
        await using var provider = TestHost.Build(cfg => cfg.AddOpenBehavior(typeof(ValidationBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var response = await dispatcher.SendAsync(new ValidatedQuery("conduit"));

        await Assert.That(response).IsEqualTo("handled:conduit");
    }

    [Test]
    public async Task Telemetry_behavior_can_be_registered_as_open_generic()
    {
        await using var provider = TestHost.Build(cfg => cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var response = await dispatcher.SendAsync(new CountQuery(9));

        await Assert.That(response).IsEqualTo(10);
    }
}

public sealed record NonIdempotentFlakyCommand : ICommand, IRetryable
{
    public RetryPolicy Policy => new(3, TimeSpan.FromMilliseconds(1), UseJitter: false);
}

public sealed class NonIdempotentFlakyHandler : IRequestHandler<NonIdempotentFlakyCommand, Conduit.Contracts.Unit>
{
    private static int _attempts;

    public static int Attempts => Volatile.Read(ref _attempts);

    public static void Reset() => Interlocked.Exchange(ref _attempts, 0);

    public ValueTask<Conduit.Contracts.Unit> HandleAsync(
        NonIdempotentFlakyCommand request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        throw new InvalidOperationException("not idempotent");
    }
}
