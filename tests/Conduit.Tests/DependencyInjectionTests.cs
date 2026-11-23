using Conduit.Abstractions;
using Conduit.Core.Behaviors;
using Conduit.DependencyInjection;
using Conduit.TestSupport;
using Conduit.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests;

public class DependencyInjectionTests
{
    private static ServiceProvider Build(
        Action<ConduitConfiguration>? configure = null,
        Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<TimeProvider>(TimeProvider.System);
        collection.AddScoped<SupportScopedDependency>();
        services?.Invoke(collection);

        collection.AddConduit(cfg =>
        {
            cfg.RegisterFrom(typeof(SupportMarker).Assembly);
            cfg.TypeEvaluator = static type => !type.Name.StartsWith("Ambiguous", StringComparison.Ordinal);
            configure?.Invoke(cfg);
        });

        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Test]
    public async Task TypeEvaluator_excludes_filtered_handlers()
    {
        await using var provider = Build(cfg =>
            cfg.TypeEvaluator = static type =>
                !type.Name.StartsWith("Ambiguous", StringComparison.Ordinal)
                && type.Name != nameof(HiddenQueryHandler));

        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var exception = await ExceptionCapture.CaptureAsync(() => dispatcher.SendAsync(new HiddenQuery()));

        await Assert.That(exception).IsTypeOf<HandlerNotFoundException>();
    }

    [Test]
    public async Task Duplicate_handler_for_same_request_is_rejected()
    {
        var collection = new ServiceCollection();

        var exception = Assert.Throws<DuplicateHandlerException>(
            () => collection.AddConduit(cfg => cfg.RegisterFrom(typeof(SupportMarker).Assembly)));

        await Assert.That(exception.Message).Contains(nameof(AmbiguousQuery));
    }

    [Test]
    public async Task Nested_generic_parameters_are_inferred_when_closing_behavior()
    {
        await using var provider = Build(cfg => cfg.AddOpenBehavior(typeof(EnvelopeBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var items = await dispatcher.SendAsync(new EnvelopeQuery(3));

        await Assert.That(items.Count).IsEqualTo(4);
    }

    [Test]
    [NotInParallel]
    public async Task Generic_constraints_filter_which_requests_a_behavior_applies_to()
    {
        CacheableOnlyBehavior<SupportCachedQuery, int>.Reset();

        await using var provider = Build(cfg => cfg.AddOpenBehavior(typeof(CacheableOnlyBehavior<,>)));
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new SupportCachedQuery());
        await dispatcher.SendAsync(new PlainSupportQuery());

        await Assert.That(CacheableOnlyBehavior<SupportCachedQuery, int>.Invocations).IsEqualTo(1);
    }

    [Test]
    public async Task Open_generic_request_handler_is_closed_and_dispatched_when_enabled()
    {
        await using var provider = Build(cfg => cfg.RegisterGenericHandlers = true);
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var response = await dispatcher.SendAsync(new OpenGenericQuery<int>(42));

        await Assert.That(response).IsEqualTo(42);
    }

    [Test]
    public async Task Open_generic_request_handler_requires_opt_in()
    {
        await using var provider = Build();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var exception = await ExceptionCapture.CaptureAsync(() => dispatcher.SendAsync(new OpenGenericQuery<int>(42)));

        await Assert.That(exception).IsTypeOf<HandlerNotFoundException>();
    }

    [Test]
    public async Task CloseInContainer_strategy_registers_open_generic_behavior()
    {
        await using var provider = Build(cfg =>
        {
            cfg.BehaviorClosingStrategy = BehaviorClosingStrategy.CloseInContainer;
            cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
        });

        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var response = await dispatcher.SendAsync(new PlainSupportQuery());

        await Assert.That(response).IsEqualTo(1);
    }

    [Test]
    public async Task MaxTypesClosing_trips_when_exceeded()
    {
        var exception = Assert.Throws<ConduitConfigurationException>(() => Build(cfg =>
        {
            cfg.MaxTypesClosing = 1;
            cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
        }).Dispose());

        await Assert.That(exception.Message).Contains("MaxTypesClosing");
    }

    [Test]
    public async Task MaxGenericTypeParameters_trips_when_exceeded()
    {
        var exception = Assert.Throws<ConduitConfigurationException>(() => Build(cfg =>
        {
            cfg.MaxGenericTypeParameters = 1;
            cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
        }).Dispose());

        await Assert.That(exception.Message).Contains("MaxGenericTypeParameters");
    }

    [Test]
    public async Task Zero_disables_every_generic_guard()
    {
        await using var provider = Build(cfg =>
        {
            cfg.MaxTypesClosing = 0;
            cfg.MaxGenericTypeParameters = 0;
            cfg.MaxGenericTypeRegistrations = 0;
            cfg.RegistrationTimeout = 0;
            cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
        });

        var dispatcher = provider.GetRequiredService<IDispatcher>();
        await Assert.That(await dispatcher.SendAsync(new PlainSupportQuery())).IsEqualTo(1);
    }

    [Test]
    public async Task Singleton_lifetime_requires_explicit_review()
    {
        var exception = Assert.Throws<ConduitStartupValidationException>(() => Build(cfg =>
        {
            cfg.Lifetime = ServiceLifetime.Singleton;
        }).Dispose());

        await Assert.That(exception.Failures.Any(f => f.Contains("Singleton", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Singleton_lifetime_is_allowed_after_explicit_review()
    {
        await using var provider = Build(cfg =>
        {
            cfg.Lifetime = ServiceLifetime.Singleton;
            cfg.AllowSingletonLifetime = true;
        });

        var dispatcher = provider.GetRequiredService<IDispatcher>();
        await dispatcher.SendAsync(new PlainSupportQuery());

        await Assert.That(dispatcher).IsTypeOf<Conduit.Core.ConduitDispatcher>();
    }

    [Test]
    public async Task Scoped_lifetime_resolves_handlers_per_scope()
    {
        await using var provider = Build(cfg => cfg.Lifetime = ServiceLifetime.Scoped);

        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        var idA = await first.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(new LifetimeQuery());
        var idB = await second.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(new LifetimeQuery());

        await Assert.That(idA).IsNotEqualTo(idB);
    }

    [Test]
    public async Task Behaviors_registered_outside_the_configuration_are_rejected()
    {
        var exception = Assert.Throws<ConduitStartupValidationException>(() => Build(
            cfg => cfg.RegisterFrom(typeof(SupportMarker).Assembly),
            services => services.AddTransient(
                typeof(IPipelineBehavior<,>), typeof(TelemetryBehavior<,>))).Dispose());

        await Assert.That(exception.Failures.Any(f => f.Contains("ConduitConfiguration", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Stream_behavior_registered_as_request_behavior_is_rejected()
    {
        var exception = Assert.Throws<ConduitConfigurationException>(() => Build(
            cfg => cfg.AddBehavior<DoublingStreamBehavior>()).Dispose());

        await Assert.That(exception.Message).Contains("stream", StringComparison.OrdinalIgnoreCase);
    }
}
