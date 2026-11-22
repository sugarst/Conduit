using Conduit.Abstractions;
using Conduit.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests;

public class ExceptionMappingTests
{
    [Test]
    public async Task Exception_handler_maps_exception_to_response()
    {
        await using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        var response = await dispatcher.SendAsync(new RiskyQuery(ShouldThrow: true));

        await Assert.That(response).IsEqualTo("mapped");
        await Assert.That(log.Text).IsEqualTo("exact");
    }

    [Test]
    public async Task Handlers_run_from_specific_to_broad()
    {
        await using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        var threw = false;
        try
        {
            await dispatcher.SendAsync(new RiskyQuery(ShouldThrow: false, ThrowInvalidOperation: true));
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That(log.Text).IsEqualTo("broad>action");
    }

    [Test]
    public async Task Unmapped_exception_propagates_unchanged()
    {
        await using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        Exception? captured = null;
        try
        {
            await dispatcher.SendAsync(new RiskyQuery(ShouldThrow: false, ThrowInvalidOperation: true));
        }
        catch (Exception exception)
        {
            captured = exception;
        }

        await Assert.That(captured).IsTypeOf<InvalidOperationException>();
        await Assert.That(captured!.Message).IsEqualTo("invalid");
    }

    [Test]
    public async Task Actions_run_for_unhandled_exceptions_by_default()
    {
        await using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        try
        {
            await dispatcher.SendAsync(new RiskyQuery(ShouldThrow: false, ThrowInvalidOperation: true));
        }
        catch (InvalidOperationException)
        {
            // 预期抛出，动作已执行
        }

        await Assert.That(log.Text).IsEqualTo("broad>action");
    }

    [Test]
    public async Task Actions_are_skipped_for_handled_exceptions_by_default()
    {
        await using var provider = TestHost.Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        await dispatcher.SendAsync(new RiskyQuery(ShouldThrow: true));

        await Assert.That(log.Text).IsEqualTo("exact");
    }

    [Test]
    public async Task Actions_run_for_all_exceptions_when_configured()
    {
        await using var provider = TestHost.Build(cfg =>
            cfg.ExceptionActionStrategy = ExceptionActionStrategy.ApplyForAllExceptions);

        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var log = provider.GetRequiredService<TraceLog>();

        await dispatcher.SendAsync(new RiskyQuery(ShouldThrow: true));

        await Assert.That(log.Text).IsEqualTo("exact>action");
    }
}
