using System.Collections.Concurrent;
using HandlerR.Abstractions;
using HandlerR.Contracts;
using HandlerR.Core.Publishing;
using HandlerR.DependencyInjection;
using HandlerR.Generated;
using Microsoft.Extensions.DependencyInjection;

namespace HandlerR.GeneratorTests;

public class RegistrationParityTests
{
    [Test]
    public async Task Notification_delivers_to_each_handler_in_both_modes()
    {
        foreach (var generated in new[] { false, true })
        {
            foreach (var (parallel, isolateScopes) in new (bool Parallel, bool? IsolateScopes)[] { (false, null), (true, null), (true, false) })
            {
                await using var provider = Build(generated, cfg =>
                {
                    if (parallel)
                    {
                        cfg.NotificationPublisherType = typeof(ParallelPublisher);
                    }

                    cfg.IsolateNotificationHandlerScopes = isolateScopes;
                });
                await using var scope = provider.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
                var concrete = new ParityNotification();
                var interfaceTyped = new ParityNotification();

                await dispatcher.PublishAsync(concrete);
                await dispatcher.PublishAsync<INotification>(interfaceTyped);

                await Assert.That(concrete.Calls.ToArray()).IsEquivalentTo(["first", "second"]);
                await Assert.That(interfaceTyped.Calls.ToArray()).IsEquivalentTo(["first", "second"]);
            }
        }
    }

    [Test]
    public async Task Exception_mappers_with_the_same_contract_are_all_available_in_both_modes()
    {
        foreach (var generated in new[] { false, true })
        {
            await using var provider = Build(generated);
            var request = new ParityMappedQuery();

            var response = await provider.GetRequiredService<IDispatcher>().SendAsync(request);

            await Assert.That(response).IsEqualTo(42);
            await Assert.That(request.Calls).IsEquivalentTo(["pass", "map"]);
        }
    }

    [Test]
    public async Task Exception_actions_run_for_an_unmapped_value_response_in_both_modes()
    {
        foreach (var generated in new[] { false, true })
        {
            await using var provider = Build(generated);
            var request = new ParityActionQuery();
            Exception? thrown = null;

            try
            {
                await provider.GetRequiredService<IDispatcher>().SendAsync(request);
            }
            catch (InvalidOperationException exception)
            {
                thrown = exception;
            }

            await Assert.That(thrown == request.Failure).IsTrue();
            await Assert.That(request.Calls).IsEquivalentTo(["first", "second"]);
        }
    }

    [Test]
    public async Task Scoped_handlers_are_reused_only_within_the_same_scope_in_both_modes()
    {
        foreach (var generated in new[] { false, true })
        {
            await using var provider = Build(generated, cfg => cfg.Lifetime = ServiceLifetime.Scoped);
            await using var first = provider.CreateAsyncScope();
            await using var second = provider.CreateAsyncScope();
            var firstDispatcher = first.ServiceProvider.GetRequiredService<IDispatcher>();
            var secondDispatcher = second.ServiceProvider.GetRequiredService<IDispatcher>();

            var firstResponse = await firstDispatcher.SendAsync(new ParityProcessorQuery());
            var secondResponse = await firstDispatcher.SendAsync(new ParityProcessorQuery());
            var otherScopeResponse = await secondDispatcher.SendAsync(new ParityProcessorQuery());

            await Assert.That(firstResponse).IsEqualTo(1);
            await Assert.That(secondResponse).IsEqualTo(2);
            await Assert.That(otherScopeResponse).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Pre_and_post_processors_are_discovered_only_when_enabled_in_both_modes()
    {
        foreach (var generated in new[] { false, true })
        {
            foreach (var autoRegister in new[] { false, true })
            {
                await using var provider = Build(generated, cfg => cfg.AutoRegisterRequestProcessors = autoRegister);
                var request = new ParityProcessorQuery();

                await provider.GetRequiredService<IDispatcher>().SendAsync(request);

                var expected = autoRegister ? "pre,handler,post" : "handler";
                await Assert.That(string.Join(",", request.Calls)).IsEqualTo(expected);
            }
        }
    }

    private static ServiceProvider Build(bool generated, Action<HandlerRConfiguration>? configure = null)
    {
        var services = new ServiceCollection();
        if (generated)
        {
            services.AddGeneratedHandlerR(configure);
        }
        else
        {
            services.AddHandlerR(cfg =>
            {
                cfg.RegisterFromAssemblyContaining<RegistrationParityTests>();
                configure?.Invoke(cfg);
            });
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

public sealed record ParityNotification : INotification
{
    public ConcurrentQueue<string> Calls { get; } = new();
}

public sealed class ParityFirstNotificationHandler : INotificationHandler<ParityNotification>
{
    public ValueTask HandleAsync(ParityNotification notification, CancellationToken cancellationToken)
    {
        notification.Calls.Enqueue("first");
        return default;
    }
}

public sealed class ParitySecondNotificationHandler : INotificationHandler<ParityNotification>
{
    public ValueTask HandleAsync(ParityNotification notification, CancellationToken cancellationToken)
    {
        notification.Calls.Enqueue("second");
        return default;
    }
}

public sealed record ParityMappedQuery : IQuery<int>
{
    public List<string> Calls { get; } = [];
}

public sealed class ParityMappedQueryHandler : IRequestHandler<ParityMappedQuery, int>
{
    public ValueTask<int> HandleAsync(ParityMappedQuery request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Map this exception.");
}

public sealed class ParityPassingMapper : IRequestExceptionHandler<ParityMappedQuery, int, InvalidOperationException>
{
    public ValueTask<ExceptionOutcome<int>> HandleAsync(
        ParityMappedQuery request, InvalidOperationException exception, ExceptionOutcome<int> current, CancellationToken cancellationToken)
    {
        request.Calls.Add("pass");
        return new(current);
    }
}

public sealed class ParitySuccessfulMapper : IRequestExceptionHandler<ParityMappedQuery, int, InvalidOperationException>
{
    public ValueTask<ExceptionOutcome<int>> HandleAsync(
        ParityMappedQuery request, InvalidOperationException exception, ExceptionOutcome<int> current, CancellationToken cancellationToken)
    {
        request.Calls.Add("map");
        return new(ExceptionOutcome<int>.Handled(42));
    }
}

public sealed record ParityActionQuery : IQuery<int>
{
    public InvalidOperationException Failure { get; } = new("Observe this exception.");
    public List<string> Calls { get; } = [];
}

public sealed class ParityActionQueryHandler : IRequestHandler<ParityActionQuery, int>
{
    public ValueTask<int> HandleAsync(ParityActionQuery request, CancellationToken cancellationToken) => throw request.Failure;
}

public sealed class ParityFirstExceptionAction : IRequestExceptionAction<ParityActionQuery, InvalidOperationException>
{
    public ValueTask ExecuteAsync(ParityActionQuery request, InvalidOperationException exception, CancellationToken cancellationToken)
    {
        request.Calls.Add("first");
        return default;
    }
}

public sealed class ParitySecondExceptionAction : IRequestExceptionAction<ParityActionQuery, InvalidOperationException>
{
    public ValueTask ExecuteAsync(ParityActionQuery request, InvalidOperationException exception, CancellationToken cancellationToken)
    {
        request.Calls.Add("second");
        return default;
    }
}

public sealed record ParityProcessorQuery : IQuery<int>
{
    public List<string> Calls { get; } = [];
}

public sealed class ParityProcessorQueryHandler : IRequestHandler<ParityProcessorQuery, int>
{
    private int _invocations;

    public ValueTask<int> HandleAsync(ParityProcessorQuery request, CancellationToken cancellationToken)
    {
        request.Calls.Add("handler");
        return new(++_invocations);
    }
}

public sealed class ParityProcessors : IRequestPreProcessor<ParityProcessorQuery>, IRequestPostProcessor<ParityProcessorQuery, int>
{
    public ValueTask ProcessAsync(ParityProcessorQuery request, CancellationToken cancellationToken)
    {
        request.Calls.Add("pre");
        return default;
    }

    public ValueTask ProcessAsync(ParityProcessorQuery request, int response, CancellationToken cancellationToken)
    {
        request.Calls.Add("post");
        return default;
    }
}
