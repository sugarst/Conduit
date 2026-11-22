using Conduit.Contracts;

namespace Conduit.Abstractions;

/// <summary>异常处理状态。</summary>
public enum ExceptionHandlingState
{
    /// <summary>未处理，异常继续向上抛出。</summary>
    Unhandled = 0,

    /// <summary>已处理，使用 <see cref="ExceptionOutcome{TResponse}.Response"/> 作为结果返回。</summary>
    Handled = 1,
}

/// <summary>
/// 异常映射的中间状态。多个处理器按"具体到宽泛"顺序串行推进，任一处理器标记
/// <see cref="ExceptionHandlingState.Handled"/> 后即停止。
/// </summary>
/// <typeparam name="TResponse">响应类型。</typeparam>
public readonly record struct ExceptionOutcome<TResponse>(
    ExceptionHandlingState State,
    TResponse? Response)
{
    /// <summary>构造"已处理"结果。</summary>
    public static ExceptionOutcome<TResponse> Handled(TResponse response)
        => new(ExceptionHandlingState.Handled, response);

    /// <summary>构造"未处理"结果。</summary>
    public static ExceptionOutcome<TResponse> Unhandled()
        => new(ExceptionHandlingState.Unhandled, default);
}

/// <summary>
/// 异常处理器：把指定异常映射为业务响应。
/// </summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
/// <typeparam name="TResponse">响应类型。</typeparam>
/// <typeparam name="TException">可处理的异常类型（支持基类匹配）。</typeparam>
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : IBaseRequest
    where TException : Exception
{
    /// <summary>尝试处理异常，返回推进后的状态。</summary>
    ValueTask<ExceptionOutcome<TResponse>> HandleAsync(
        TRequest request,
        TException exception,
        ExceptionOutcome<TResponse> current,
        CancellationToken cancellationToken);
}

/// <summary>
/// 异常副作用动作：只做记录 / 告警，不改变异常传播结果。
/// </summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
/// <typeparam name="TException">异常类型。</typeparam>
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : IBaseRequest
    where TException : Exception
{
    /// <summary>执行副作用。</summary>
    ValueTask ExecuteAsync(TRequest request, TException exception, CancellationToken cancellationToken);
}

/// <summary>
/// 异常动作的作用范围策略。
/// </summary>
public enum ExceptionActionStrategy
{
    /// <summary>仅对最终未被映射的异常执行动作。</summary>
    ApplyForUnhandledExceptions = 0,

    /// <summary>对所有异常执行动作（含已被映射处理的异常）。</summary>
    ApplyForAllExceptions = 1,
}
