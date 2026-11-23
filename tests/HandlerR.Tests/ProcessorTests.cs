using HandlerR.Abstractions;
using HandlerR.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace HandlerR.Tests;

public class ProcessorTests
{
    [Test]
    public async Task Pre_processor_runs_before_behaviors_and_post_after()
    {
        await using var provider = TestHost.Build(cfg =>
        {
            cfg.AddBehavior<OuterBehavior>();
            cfg.AddRequestPreProcessor<PingPreProcessor>();
            cfg.AddRequestPostProcessor<PingPostProcessor>();
        });

        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        await dispatcher.SendAsync(new PingQuery());

        await Assert.That(log.Text).IsEqualTo("pre>outer:enter>handler>outer:exit>post");
    }

    [Test]
    public async Task Scanned_processors_are_not_auto_registered_by_default()
    {
        await using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        await dispatcher.SendAsync(new PingQuery());

        await Assert.That(log.Text).IsEqualTo("handler");
    }

    [Test]
    public async Task AutoRegisterRequestProcessors_registers_scanned_processors()
    {
        await using var provider = TestHost.Build(cfg => cfg.AutoRegisterRequestProcessors = true);
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        await dispatcher.SendAsync(new PingQuery());

        await Assert.That(log.Text).IsEqualTo("pre>handler>post");
    }

    [Test]
    public async Task Post_processor_is_skipped_when_handler_throws()
    {
        await using var provider = TestHost.Build(cfg => cfg.AddRequestPostProcessor<PingPostProcessor>());
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        var threw = false;
        try
        {
            await dispatcher.SendAsync(new ThrowingQuery());
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That(log.Entries.Count).IsEqualTo(0);
    }
}

public sealed record ThrowingQuery : IQuery<string>;

public sealed class ThrowingQueryHandler : IRequestHandler<ThrowingQuery, string>
{
    public ValueTask<string> HandleAsync(ThrowingQuery request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("boom");
}
