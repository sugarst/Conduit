using Conduit.Abstractions;
using Conduit.Contracts;
using Conduit.DependencyInjection;
using Conduit.FluentValidation;
using Conduit.Tests.Fixtures;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests;

public sealed record FluentQuery(string Name) : IQuery<string>;

public sealed class FluentQueryHandler : IRequestHandler<FluentQuery, string>
{
    public ValueTask<string> HandleAsync(FluentQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult("ok:" + request.Name);
}

public sealed class FluentQueryValidator : AbstractValidator<FluentQuery>
{
    public FluentQueryValidator()
        => RuleFor(static query => query.Name)
            .NotEmpty()
            .WithMessage("Name is required.")
            .WithErrorCode("NAME_REQUIRED");
}

public class FluentValidationAdapterTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TraceLog>();
        services.AddScoped<IValidator<FluentQuery>, FluentQueryValidator>();

        services.AddConduit(cfg =>
        {
            cfg.RegisterFromAssemblyContaining<PingQuery>();
            cfg.AddFluentValidation();
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Test]
    public async Task Fluent_validator_failures_surface_as_RequestValidationException()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var exception = await ExceptionCapture.CaptureAsync(
            () => dispatcher.SendAsync(new FluentQuery(string.Empty)));

        await Assert.That(exception).IsTypeOf<RequestValidationException>();

        var validation = (RequestValidationException)exception!;
        await Assert.That(validation.Errors.Count).IsEqualTo(1);
        await Assert.That(validation.Errors[0].Identifier).IsEqualTo(nameof(FluentQuery.Name));
        await Assert.That(validation.Errors[0].ErrorCode).IsEqualTo("NAME_REQUIRED");
    }

    [Test]
    public async Task Fluent_validator_pass_lets_the_request_through()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var response = await dispatcher.SendAsync(new FluentQuery("conduit"));

        await Assert.That(response).IsEqualTo("ok:conduit");
    }
}
