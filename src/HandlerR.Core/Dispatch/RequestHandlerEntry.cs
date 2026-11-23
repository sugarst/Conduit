using System.Diagnostics.CodeAnalysis;
using HandlerR.Abstractions;
using HandlerR.Contracts;
using HandlerR.Core.Internal;

namespace HandlerR.Core.Dispatch;

/// <summary>
/// 调度表条目基类：把异构的 <c>(TRequest, TResponse)</c> 归一化为 <see cref="object"/> 入参。
/// </summary>
/// <remarks>条目自身无状态，可由源生成器直接构造并在启动期冻结为单例。</remarks>
public abstract class RequestHandlerEntry
{
    private KeyValuePair<string, object?>[]? _tags;

    /// <summary>该条目对应的请求类型。</summary>
    public abstract Type RequestType { get; }

    /// <summary>该条目对应的响应类型。</summary>
    public abstract Type ResponseType { get; }

    /// <summary>该条目依赖的处理器服务类型（用于诊断与去重提示）。</summary>
    public abstract Type HandlerServiceType { get; }

    /// <summary>缓存的可观测性标签，首次调用时构建，避免热路径重复分配。</summary>
    internal KeyValuePair<string, object?>[] Tags
        => _tags ??= HandlerRDiagnostics.BuildTags(RequestType, "send", HandlerServiceType);
}

/// <summary>
/// 已知响应类型的条目，供调度器在类型检查通过后做零分配调用。
/// </summary>
/// <typeparam name="TResponse">响应类型。</typeparam>
public abstract class RequestHandlerEntry<TResponse> : RequestHandlerEntry
{
    /// <inheritdoc />
    public sealed override Type ResponseType => typeof(TResponse);

    /// <summary>调用处理器（含 Behavior 链与异常映射）。</summary>
    public abstract ValueTask<TResponse> InvokeAsync(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>
/// 请求调度条目：单次字典查找后进入"Handler + 逆序 Behavior 折叠 + 异常策略"。
/// </summary>
/// <typeparam name="TRequest">请求类型。</typeparam>
/// <typeparam name="TResponse">响应类型。</typeparam>
public sealed class RequestHandlerEntry<TRequest, TResponse> : RequestHandlerEntry<TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <inheritdoc />
    public override Type RequestType => typeof(TRequest);

    /// <inheritdoc />
    public override Type HandlerServiceType => typeof(IRequestHandler<TRequest, TResponse>);

    /// <inheritdoc />
    [SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "异常映射层必须捕获全部异常，才能按注册的处理器做映射或原样重抛。")]
    public override ValueTask<TResponse> InvokeAsync(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var typedRequest = (TRequest)request;
        var handler = serviceProvider.ResolveRequired<IRequestHandler<TRequest, TResponse>>();

        RequestHandlerDelegate<TResponse> next = () => handler.HandleAsync(typedRequest, cancellationToken);

        // 逆序折叠：声明顺序 = 由外到内执行
        var behaviors = serviceProvider.ResolveAll<IPipelineBehavior<TRequest, TResponse>>();
        for (var i = behaviors.Count - 1; i >= 0; i--)
        {
            var current = behaviors[i];
            var inner = next;
            next = () => current.HandleAsync(typedRequest, inner, cancellationToken);
        }

        return InvokeWithExceptionPolicyAsync(typedRequest, serviceProvider, next, cancellationToken);
    }

    private static async ValueTask<TResponse> InvokeWithExceptionPolicyAsync(
        TRequest request,
        IServiceProvider serviceProvider,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var dispatcher = serviceProvider.Resolve<IExceptionDispatcher<TRequest, TResponse>>();
            if (dispatcher is null)
            {
                throw;
            }

            var outcome = await dispatcher
                .DispatchAsync(request, exception, serviceProvider, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.State == ExceptionHandlingState.Handled)
            {
                return outcome.Response!;
            }

            throw;
        }
    }
}
