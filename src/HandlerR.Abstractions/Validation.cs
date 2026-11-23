using HandlerR.Contracts;

namespace HandlerR.Abstractions;

/// <summary>单条校验错误。</summary>
/// <param name="Identifier">字段或成员标识。</param>
/// <param name="Message">错误描述。</param>
/// <param name="ErrorCode">可选的稳定错误码。</param>
public readonly record struct ValidationError(string Identifier, string Message, string? ErrorCode = null);

/// <summary>
/// 请求校验器。由 <c>ValidationBehavior</c> 在管道内调用；未注册实现时不产生任何开销。
/// </summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
public interface IRequestValidator<in TRequest>
    where TRequest : IBaseRequest
{
    /// <summary>返回校验错误列表，空列表表示通过。</summary>
    ValueTask<IReadOnlyList<ValidationError>> ValidateAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 校验失败异常。携带全部错误，便于统一转换为 400 响应。
/// </summary>
public sealed class RequestValidationException : Exception
{
    /// <summary>创建校验失败异常。</summary>
    public RequestValidationException(IReadOnlyList<ValidationError> errors)
        : base($"Validation failed with {errors.Count} error(s).")
        => Errors = errors;

    /// <summary>全部校验错误。</summary>
    public IReadOnlyList<ValidationError> Errors { get; }
}
