using HandlerR.Abstractions;
using HandlerR.Contracts;

namespace HandlerR.Tests.Fixtures;

public sealed class DomainException(string message) : Exception(message);

public sealed record RiskyQuery(bool ShouldThrow, bool ThrowInvalidOperation = false) : IQuery<string>;

public sealed class RiskyQueryHandler : IRequestHandler<RiskyQuery, string>
{
    public ValueTask<string> HandleAsync(RiskyQuery request, CancellationToken cancellationToken)
    {
        if (request.ThrowInvalidOperation)
        {
            throw new InvalidOperationException("invalid");
        }

        if (request.ShouldThrow)
        {
            throw new DomainException("domain");
        }

        return ValueTask.FromResult("ok");
    }
}

/// <summary>精确匹配：把领域异常映射为业务响应。</summary>
public sealed class DomainExceptionHandler(TraceLog log) : IRequestExceptionHandler<RiskyQuery, string, DomainException>
{
    public ValueTask<ExceptionOutcome<string>> HandleAsync(
        RiskyQuery request,
        DomainException exception,
        ExceptionOutcome<string> current,
        CancellationToken cancellationToken)
    {
        log.Add("exact");
        return ValueTask.FromResult(ExceptionOutcome<string>.Handled("mapped"));
    }
}

/// <summary>宽泛匹配：记录后保持未处理，用于验证"具体 → 宽泛"顺序。</summary>
public sealed class BroadExceptionHandler(TraceLog log) : IRequestExceptionHandler<RiskyQuery, string, Exception>
{
    public ValueTask<ExceptionOutcome<string>> HandleAsync(
        RiskyQuery request,
        Exception exception,
        ExceptionOutcome<string> current,
        CancellationToken cancellationToken)
    {
        log.Add("broad");
        return ValueTask.FromResult(current);
    }
}

public sealed class RiskyExceptionAction(TraceLog log) : IRequestExceptionAction<RiskyQuery, Exception>
{
    public ValueTask ExecuteAsync(RiskyQuery request, Exception exception, CancellationToken cancellationToken)
    {
        log.Add("action");
        return default;
    }
}

// ——————————————————— 内置 Behavior 用夹具 ———————————————————

public sealed record SlowQuery : IQuery<string>, ITimeoutRequest
{
    public TimeSpan Timeout => TimeSpan.FromMilliseconds(30);
}

public sealed class SlowQueryHandler : IRequestHandler<SlowQuery, string>
{
    public async ValueTask<string> HandleAsync(SlowQuery request, CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        return "slow";
    }
}

public sealed record FlakyCommand : ICommand, IRetryable, IIdempotent
{
    public RetryPolicy Policy => new(3, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5), UseJitter: false);

    public string IdempotencyKey => "flaky";
}

public sealed class FlakyCommandHandler : IRequestHandler<FlakyCommand, Unit>
{
    private static int _attempts;

    public static int Attempts => Volatile.Read(ref _attempts);

    public static void Reset() => Interlocked.Exchange(ref _attempts, 0);

    public ValueTask<Unit> HandleAsync(FlakyCommand request, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        if (attempt < 3)
        {
            throw new InvalidOperationException($"attempt {attempt} failed");
        }

        return ValueTask.FromResult(Unit.Value);
    }
}

public sealed record IdempotentCommand(string Key) : ICommand, IIdempotent
{
    public string IdempotencyKey => Key;
}

public sealed class IdempotentCommandHandler : IRequestHandler<IdempotentCommand, Unit>
{
    private static int _invocations;

    public static int Invocations => Volatile.Read(ref _invocations);

    public static void Reset() => Interlocked.Exchange(ref _invocations, 0);

    public ValueTask<Unit> HandleAsync(IdempotentCommand request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _invocations);
        return ValueTask.FromResult(Unit.Value);
    }
}

public sealed record CachedQuery(string Key) : IQuery<int>, ICacheable, ICacheKeyProvider
{
    public TimeSpan? CacheDuration => TimeSpan.FromMinutes(1);

    public string GetCacheKey() => "cached:" + Key;
}

public sealed class CachedQueryHandler : IRequestHandler<CachedQuery, int>
{
    private static int _invocations;

    public static int Invocations => Volatile.Read(ref _invocations);

    public static void Reset() => Interlocked.Exchange(ref _invocations, 0);

    public ValueTask<int> HandleAsync(CachedQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(Interlocked.Increment(ref _invocations));
}

public sealed record TransactionalCommand(bool ShouldFail) : ICommand, ITransactionalRequest;

public sealed class TransactionalCommandHandler : IRequestHandler<TransactionalCommand, Unit>
{
    public ValueTask<Unit> HandleAsync(TransactionalCommand request, CancellationToken cancellationToken)
    {
        if (request.ShouldFail)
        {
            throw new DomainException("transaction failed");
        }

        return ValueTask.FromResult(Unit.Value);
    }
}

/// <summary>记录事务边界调用的测试替身。</summary>
public sealed class RecordingTransactionScopeFactory(TraceLog log) : ITransactionScopeFactory
{
    public ValueTask<ITransactionScope> BeginAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<ITransactionScope>(new Scope(log));

    private sealed class Scope(TraceLog log) : ITransactionScope
    {
        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            log.Add("commit");
            return default;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            log.Add("rollback");
            return default;
        }

        public ValueTask DisposeAsync() => default;
    }
}

// ——————————————————— 校验夹具 ———————————————————

public sealed record ValidatedQuery(string Name) : IQuery<string>;

public sealed class ValidatedQueryHandler : IRequestHandler<ValidatedQuery, string>
{
    public ValueTask<string> HandleAsync(ValidatedQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult("handled:" + request.Name);
}

public sealed class NameValidator : IRequestValidator<ValidatedQuery>
{
    public ValueTask<IReadOnlyList<ValidationError>> ValidateAsync(
        ValidatedQuery request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ValidationError> errors = string.IsNullOrWhiteSpace(request.Name)
            ? [new ValidationError(nameof(ValidatedQuery.Name), "Name is required.", "NAME_REQUIRED")]
            : [];

        return ValueTask.FromResult(errors);
    }
}
