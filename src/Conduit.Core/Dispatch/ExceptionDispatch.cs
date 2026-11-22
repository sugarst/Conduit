using Conduit.Abstractions;
using Conduit.Contracts;
using Conduit.Core.Internal;

namespace Conduit.Core.Dispatch;

/// <summary>
/// 异常处理结果的非泛型载体，用于把异构 <c>(TRequest, TResponse, TException)</c>
/// 收敛为统一调用入口（异常路径为冷路径，装箱成本可接受）。
/// </summary>
public readonly record struct ExceptionOutcomeResult(bool Handled, object? Response);

/// <summary>
/// 异常处理入口的公共契约：声明可处理的异常类型。
/// </summary>
public interface IExceptionInvoker
{
    /// <summary>该入口可处理的异常类型。</summary>
    Type ExceptionType { get; }
}

/// <summary>
/// 已闭合的异常处理器调用入口。
/// </summary>
public interface IExceptionHandlerInvoker : IExceptionInvoker
{
    /// <summary>调用处理器并返回推进后的状态。</summary>
    ValueTask<ExceptionOutcomeResult> InvokeAsync(
        object request,
        Exception exception,
        ExceptionOutcomeResult current,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>
/// 已闭合的异常副作用动作调用入口。
/// </summary>
public interface IExceptionActionInvoker : IExceptionInvoker
{
    /// <summary>执行副作用。</summary>
    ValueTask InvokeAsync(
        object request,
        Exception exception,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>
/// 异常映射调度器。由注册层在启动期按 <c>(TRequest, TResponse)</c> 建索引后注册。
/// </summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
/// <typeparam name="TResponse">响应类型。</typeparam>
public interface IExceptionDispatcher<TRequest, TResponse>
    where TRequest : IBaseRequest
{
    /// <summary>按"具体到宽泛"顺序执行异常处理器与动作。</summary>
    ValueTask<ExceptionOutcome<TResponse>> DispatchAsync(
        TRequest request,
        Exception exception,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>
/// 闭合到具体异常类型的处理器调用入口。
/// </summary>
public sealed class ExceptionHandlerInvoker<TRequest, TResponse, TException> : IExceptionHandlerInvoker
    where TRequest : IBaseRequest
    where TException : Exception
{
    /// <inheritdoc />
    public Type ExceptionType => typeof(TException);

    /// <inheritdoc />
    public async ValueTask<ExceptionOutcomeResult> InvokeAsync(
        object request,
        Exception exception,
        ExceptionOutcomeResult current,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var handler = serviceProvider.ResolveAll<IRequestExceptionHandler<TRequest, TResponse, TException>>();
        if (handler.Count == 0)
        {
            return current;
        }

        var state = current.Handled
            ? ExceptionOutcome<TResponse>.Handled((TResponse)current.Response!)
            : ExceptionOutcome<TResponse>.Unhandled();

        for (var i = 0; i < handler.Count; i++)
        {
            var outcome = await handler[i]
                .HandleAsync((TRequest)request, (TException)exception, state, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.State == ExceptionHandlingState.Handled)
            {
                return new ExceptionOutcomeResult(true, outcome.Response);
            }

            state = outcome;
        }

        return current;
    }
}

/// <summary>
/// 闭合到具体异常类型的动作调用入口。
/// </summary>
public sealed class ExceptionActionInvoker<TRequest, TException> : IExceptionActionInvoker
    where TRequest : IBaseRequest
    where TException : Exception
{
    /// <inheritdoc />
    public Type ExceptionType => typeof(TException);

    /// <inheritdoc />
    public async ValueTask InvokeAsync(
        object request,
        Exception exception,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var actions = serviceProvider.ResolveAll<IRequestExceptionAction<TRequest, TException>>();
        for (var i = 0; i < actions.Count; i++)
        {
            await actions[i]
                .ExecuteAsync((TRequest)request, (TException)exception, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 默认异常映射调度器：处理器按"具体 → 宽泛"顺序串行推进，首个 Handled 即停止；
/// 动作在映射判定后按策略执行（<see cref="ExceptionActionStrategy"/>）。
/// </summary>
public sealed class RequestExceptionDispatcher<TRequest, TResponse> : IExceptionDispatcher<TRequest, TResponse>
    where TRequest : IBaseRequest
{
    private readonly IExceptionHandlerInvoker[] _handlers;
    private readonly IExceptionActionInvoker[] _actions;
    private readonly ExceptionActionStrategy _strategy;

    /// <summary>创建调度器。</summary>
    public RequestExceptionDispatcher(
        IEnumerable<IExceptionHandlerInvoker> handlers,
        IEnumerable<IExceptionActionInvoker> actions,
        ExceptionActionStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(actions);

        _handlers = [.. handlers];
        _actions = [.. actions];
        _strategy = strategy;
    }

    /// <inheritdoc />
    public async ValueTask<ExceptionOutcome<TResponse>> DispatchAsync(
        TRequest request,
        Exception exception,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var runtimeType = exception.GetType();
        var outcome = new ExceptionOutcomeResult(false, null);

        foreach (var invoker in OrderBySpecificity(_handlers, runtimeType))
        {
            outcome = await invoker
                .InvokeAsync(request, exception, outcome, serviceProvider, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Handled)
            {
                break;
            }
        }

        if (_strategy == ExceptionActionStrategy.ApplyForAllExceptions || !outcome.Handled)
        {
            foreach (var invoker in OrderBySpecificity(_actions, runtimeType))
            {
                await invoker.InvokeAsync(request, exception, serviceProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        return outcome.Handled
            ? ExceptionOutcome<TResponse>.Handled((TResponse)outcome.Response!)
            : ExceptionOutcome<TResponse>.Unhandled();
    }

    private static IEnumerable<TInvoker> OrderBySpecificity<TInvoker>(TInvoker[] invokers, Type runtimeType)
        where TInvoker : IExceptionInvoker
    {
        if (invokers.Length == 0)
        {
            return [];
        }

        if (invokers.Length == 1)
        {
            var single = invokers[0];
            return single.ExceptionType.IsAssignableFrom(runtimeType) ? invokers : [];
        }

        return invokers
            .Where(invoker => invoker.ExceptionType.IsAssignableFrom(runtimeType))
            .OrderBy(invoker => Distance(invoker.ExceptionType, runtimeType));
    }

    /// <summary>
    /// 排序权重：精确匹配 0，基类按继承层级递增，接口匹配排在最后。
    /// </summary>
    private static int Distance(Type handlerExceptionType, Type runtimeType)
    {
        if (handlerExceptionType == runtimeType)
        {
            return 0;
        }

        if (!handlerExceptionType.IsAssignableFrom(runtimeType))
        {
            return int.MaxValue;
        }

        if (handlerExceptionType.IsInterface)
        {
            return 10_000;
        }

        var distance = 0;
        for (var current = runtimeType.BaseType; current is not null; current = current.BaseType)
        {
            distance++;
            if (current == handlerExceptionType)
            {
                return distance;
            }
        }

        return int.MaxValue;
    }
}
