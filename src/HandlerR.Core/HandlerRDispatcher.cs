using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HandlerR.Abstractions;
using HandlerR.Contracts;
using HandlerR.Core.Dispatch;
using HandlerR.Core.Internal;
using HandlerR.Core.Publishing;
using NotificationRoutes = System.Collections.Generic.IReadOnlyDictionary<System.Type,
    System.Func<HandlerR.Abstractions.IDispatcher, HandlerR.Contracts.INotification, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>>;

namespace HandlerR.Core;

/// <summary>
/// 默认调度器：单次冻结字典查找 → 前处理器 → Handler/Behavior 链 → 后处理器。
/// </summary>
/// <remarks>
/// 热路径约束：零反射、零 <c>MakeGenericType</c>、零表达式编译、零 LINQ。
/// 实例无状态，可由容器按 Transient 或 Scoped 注册。
/// </remarks>
public sealed class HandlerRDispatcher : IDispatcher
{
    private readonly FrozenDictionary<Type, RequestHandlerEntry> _requests;
    private readonly FrozenDictionary<Type, StreamHandlerEntry> _streams;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRequestPreProcessorRunner _preProcessors;
    private readonly IRequestPostProcessorRunner _postProcessors;

    /// <summary>创建调度器。</summary>
    public HandlerRDispatcher(
        DispatchTables tables,
        IServiceProvider serviceProvider,
        IRequestPreProcessorRunner? preProcessors = null,
        IRequestPostProcessorRunner? postProcessors = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        _requests = tables.Requests;
        _streams = tables.Streams;
        _serviceProvider = serviceProvider;
        _preProcessors = preProcessors ?? NoopPreProcessorRunner.Instance;
        _postProcessors = postProcessors ?? NoopPostProcessorRunner.Instance;
    }

    /// <inheritdoc />
    public ValueTask<TResponse> SendAsync<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        if (!_requests.TryGetValue(requestType, out var entry))
        {
            throw new HandlerNotFoundException(requestType);
        }

        if (entry is not RequestHandlerEntry<TResponse> typedEntry)
        {
            throw new ResponseTypeMismatchException(requestType, typeof(TResponse));
        }

        return InvokeSendAsync(typedEntry, request, requestType, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        if (!_streams.TryGetValue(requestType, out var entry))
        {
            throw new HandlerNotFoundException(requestType);
        }

        if (entry is not StreamHandlerEntry<TResponse> typedEntry)
        {
            throw new ResponseTypeMismatchException(requestType, typeof(TResponse));
        }

        return EnumerateStreamAsync(typedEntry, request, requestType, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask PublishAsync<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        var notificationType = notification.GetType();
        if (notificationType != typeof(TNotification)
            && _serviceProvider.Resolve<NotificationRoutes>() is { } routes
            && routes.TryGetValue(notificationType, out var dispatch))
        {
            return dispatch(this, notification, cancellationToken);
        }

        var handlers = _serviceProvider.ResolveAll<INotificationHandler<TNotification>>();
        if (handlers.Count == 0)
        {
            return default;
        }

        return PublishCoreAsync(handlers, notification, cancellationToken);
    }

    private async ValueTask PublishCoreAsync<TNotification>(
        IReadOnlyList<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        using var activity = HandlerRDiagnostics.ActivitySource.StartActivity(
            $"{typeof(TNotification).Name} publish", ActivityKind.Internal);

        activity?.SetTag("messaging.system", "handlerr");
        activity?.SetTag("messaging.operation", "publish");
        activity?.SetTag("messaging.message.type", typeof(TNotification).FullName);
        activity?.SetTag("messaging.batch.message_count", handlers.Count);

        var executors = BuildExecutors(handlers);
        var publisher = _serviceProvider.Resolve<INotificationPublisher>() ?? SequentialPublisher.Instance;

        try
        {
            HandlerRDiagnostics.PublishCount.Add(1, new KeyValuePair<string, object?>("messaging.message.type", typeof(TNotification).FullName));
            await publisher.PublishAsync(executors, notification, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HandlerRDiagnostics.FailureCount.Add(1, new KeyValuePair<string, object?>("messaging.operation", "publish"));
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().FullName);
            throw;
        }
    }

    private static NotificationHandlerExecutor[] BuildExecutors<TNotification>(
        IReadOnlyList<INotificationHandler<TNotification>> handlers)
        where TNotification : INotification
    {
        if (handlers.Count == 1)
        {
            var single = handlers[0];
            return [new NotificationHandlerExecutor((n, ct) => single.HandleAsync((TNotification)n, ct))];
        }

        // 运行期去重：同一处理器类型只执行一次（扫描期已去重，此处防御手工注册与多容器场景）
        var seen = new HashSet<Type>(handlers.Count);
        var executors = new List<NotificationHandlerExecutor>(handlers.Count);
        foreach (var handler in handlers)
        {
            if (!seen.Add(handler.GetType()))
            {
                continue;
            }

            executors.Add(new NotificationHandlerExecutor((n, ct) => handler.HandleAsync((TNotification)n, ct)));
        }

        return [.. executors];
    }

    private async ValueTask<TResponse> InvokeSendAsync<TResponse>(
        RequestHandlerEntry<TResponse> entry,
        IRequest<TResponse> request,
        Type requestType,
        CancellationToken cancellationToken)
    {
        using var activity = HandlerRDiagnostics.ActivitySource.StartActivity(
            $"{requestType.Name} send", ActivityKind.Internal);

        activity?.SetTag("messaging.system", "handlerr");
        activity?.SetTag("messaging.operation", "send");
        activity?.SetTag("messaging.message.type", requestType.FullName);

        var start = Stopwatch.GetTimestamp();
        try
        {
            await _preProcessors.RunAsync(request, _serviceProvider, cancellationToken).ConfigureAwait(false);
            var response = await entry.InvokeAsync(request, _serviceProvider, cancellationToken).ConfigureAwait(false);
            await _postProcessors.RunAsync(request, response, _serviceProvider, cancellationToken).ConfigureAwait(false);

            HandlerRDiagnostics.SendCount.Add(1, entry.Tags);
            return response;
        }
        catch (Exception exception)
        {
            HandlerRDiagnostics.FailureCount.Add(1, new KeyValuePair<string, object?>("messaging.message.type", requestType.FullName));
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().FullName);
            throw;
        }
        finally
        {
            HandlerRDiagnostics.SendDuration.Record(
                Stopwatch.GetElapsedTime(start).TotalMilliseconds, entry.Tags);
        }
    }

    private async IAsyncEnumerable<TResponse> EnumerateStreamAsync<TResponse>(
        StreamHandlerEntry<TResponse> entry,
        IStreamRequest<TResponse> request,
        Type requestType,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = HandlerRDiagnostics.ActivitySource.StartActivity(
            $"{requestType.Name} stream", ActivityKind.Internal);

        activity?.SetTag("messaging.system", "handlerr");
        activity?.SetTag("messaging.operation", "stream");
        activity?.SetTag("messaging.message.type", requestType.FullName);

        var count = 0L;
        try
        {
            await foreach (var item in entry
                .InvokeStream(request, _serviceProvider, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                count++;
                yield return item;
            }

            activity?.SetTag("messaging.batch.message_count", count);
        }
        finally
        {
            HandlerRDiagnostics.SendCount.Add(1, new KeyValuePair<string, object?>("messaging.operation", "stream"));
        }
    }

    private sealed class NoopPreProcessorRunner : IRequestPreProcessorRunner
    {
        public static readonly NoopPreProcessorRunner Instance = new();

        public ValueTask RunAsync(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
            => default;
    }

    private sealed class NoopPostProcessorRunner : IRequestPostProcessorRunner
    {
        public static readonly NoopPostProcessorRunner Instance = new();

        public ValueTask RunAsync(object request, object? response, IServiceProvider serviceProvider, CancellationToken cancellationToken)
            => default;
    }
}
