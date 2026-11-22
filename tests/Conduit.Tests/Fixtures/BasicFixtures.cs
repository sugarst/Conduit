using Conduit.Abstractions;
using Conduit.Contracts;

namespace Conduit.Tests.Fixtures;

/// <summary>按顺序记录管线经过的环节，用于断言顺序语义。</summary>
public sealed class TraceLog
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public string Text
    {
        get
        {
            lock (_entries)
            {
                return string.Join(">", _entries);
            }
        }
    }

    public void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}

public sealed record PingQuery(string Trace = "") : IQuery<string>;

public sealed class PingHandler(TraceLog log) : IRequestHandler<PingQuery, string>
{
    public ValueTask<string> HandleAsync(PingQuery request, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return ValueTask.FromResult("pong");
    }
}

public sealed record NoHandlerQuery : IQuery<int>;

public sealed record CountQuery(int Value) : IQuery<int>;

public sealed class CountHandler : IRequestHandler<CountQuery, int>
{
    public ValueTask<int> HandleAsync(CountQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(request.Value + 1);
}

/// <summary>无响应命令，验证 ICommand → Unit 收敛。</summary>
public sealed record TouchCommand(string Name) : ICommand;

public sealed class TouchCommandHandler(TraceLog log) : IRequestHandler<TouchCommand, Unit>
{
    public ValueTask<Unit> HandleAsync(TouchCommand request, CancellationToken cancellationToken)
    {
        log.Add("touch:" + request.Name);
        return ValueTask.FromResult(Unit.Value);
    }
}

public sealed class OuterBehavior(TraceLog log) : IPipelineBehavior<PingQuery, string>
{
    public async ValueTask<string> HandleAsync(
        PingQuery request,
        RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        log.Add("outer:enter");
        var result = await next().ConfigureAwait(false);
        log.Add("outer:exit");
        return result;
    }
}

public sealed class InnerBehavior(TraceLog log) : IPipelineBehavior<PingQuery, string>
{
    public async ValueTask<string> HandleAsync(
        PingQuery request,
        RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        log.Add("inner:enter");
        var result = await next().ConfigureAwait(false);
        log.Add("inner:exit");
        return result;
    }
}

/// <summary>短路行为：不调用下一环。</summary>
public sealed class ShortCircuitBehavior : IPipelineBehavior<PingQuery, string>
{
    public ValueTask<string> HandleAsync(
        PingQuery request,
        RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
        => ValueTask.FromResult("short-circuited");
}
