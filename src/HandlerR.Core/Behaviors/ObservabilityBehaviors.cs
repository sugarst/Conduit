using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using HandlerR.Abstractions;
using HandlerR.Contracts;

namespace HandlerR.Core.Behaviors;

/// <summary>
/// 遥测行为：为每次请求处理创建 span 并记录失败。全局开启（默认关闭，需显式注册）。
/// </summary>
public sealed class TelemetryBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "必须观察全部异常才能标注 span 失败状态，随后原样重抛。")]
    public async ValueTask<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        using var activity = HandlerRDiagnostics.ActivitySource.StartActivity(
            $"{typeof(TRequest).Name} process", ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag("messaging.system", "handlerr");
            activity.SetTag("messaging.operation", "process");
            activity.SetTag("messaging.message.type", typeof(TRequest).FullName);
        }

        try
        {
            return await next().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().FullName);
            activity?.SetTag("error.type", exception.GetType().FullName);
            throw;
        }
    }
}

/// <summary>
/// 校验行为：只要有 <see cref="IRequestValidator{TRequest}"/> 实现就生效，聚合全部错误后抛
/// <see cref="RequestValidationException"/>。
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IRequestValidator<TRequest>> _validators;

    /// <summary>创建校验行为。</summary>
    public ValidationBehavior(IEnumerable<IRequestValidator<TRequest>> validators)
        => _validators = validators;

    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var errors = new List<ValidationError>();

        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < result.Count; i++)
            {
                errors.Add(result[i]);
            }
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        return await next().ConfigureAwait(false);
    }
}
