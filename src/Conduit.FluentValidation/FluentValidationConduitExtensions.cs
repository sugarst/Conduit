using Conduit.Abstractions;
using Conduit.Contracts;
using Conduit.Core.Behaviors;
using Conduit.DependencyInjection;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.FluentValidation;

/// <summary>
/// 把 FluentValidation 的 <see cref="IValidator{T}"/> 适配为 Conduit 的
/// <see cref="IRequestValidator{TRequest}"/>，从而复用内置 <see cref="ValidationBehavior{TRequest,TResponse}"/>。
/// </summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
public sealed class FluentValidationRequestValidator<TRequest> : IRequestValidator<TRequest>
    where TRequest : IBaseRequest
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    /// <summary>创建适配器。</summary>
    public FluentValidationRequestValidator(IEnumerable<IValidator<TRequest>> validators)
        => _validators = validators;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ValidationError>> ValidateAsync(
        TRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = new ValidationContext<TRequest>(request);
        var errors = new List<ValidationError>();

        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
            if (result.IsValid)
            {
                continue;
            }

            foreach (var failure in result.Errors)
            {
                errors.Add(ToValidationError(failure));
            }
        }

        return errors;
    }

    private static ValidationError ToValidationError(ValidationFailure failure)
        => new(failure.PropertyName, failure.ErrorMessage, failure.ErrorCode);
}

/// <summary>
/// 适配包注册入口。
/// </summary>
public static class FluentValidationConduitExtensions
{
    /// <summary>
    /// 注册 FluentValidation 适配：<see cref="IRequestValidator{TRequest}"/> 开放泛型 + 校验行为。
    /// </summary>
    /// <remarks>必须在 <c>AddConduit</c> 的配置回调内调用。</remarks>
    public static ConduitConfiguration AddFluentValidation(this ConduitConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        configuration.AddValidatorAdapter(typeof(FluentValidationRequestValidator<>));
        return configuration;
    }
}
