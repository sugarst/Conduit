using System.Collections.Frozen;
using Conduit.Abstractions;

namespace Conduit.Core.Dispatch;

/// <summary>
/// 启动期构建、运行期只读的调度表：请求类型 → 处理入口。
/// </summary>
/// <remarks>
/// 使用 <see cref="FrozenDictionary{TKey,TValue}"/>：构建后不可变，读路径为单次哈希查找，
/// 且不产生迭代器分配。
/// </remarks>
public sealed class DispatchTables
{
    /// <summary>空表（无任何已注册请求）。</summary>
    public static DispatchTables Empty { get; } = new(
        FrozenDictionary<Type, RequestHandlerEntry>.Empty,
        FrozenDictionary<Type, StreamHandlerEntry>.Empty);

    /// <summary>创建调度表。</summary>
    public DispatchTables(
        FrozenDictionary<Type, RequestHandlerEntry> requests,
        FrozenDictionary<Type, StreamHandlerEntry> streams)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(streams);

        Requests = requests;
        Streams = streams;
    }

    /// <summary>请求调度表。</summary>
    public FrozenDictionary<Type, RequestHandlerEntry> Requests { get; }

    /// <summary>流式调度表。</summary>
    public FrozenDictionary<Type, StreamHandlerEntry> Streams { get; }
}

/// <summary>
/// 调度表构建器。既供反射扫描（Mode A）使用，也供源生成器（Mode B）生成代码使用。
/// </summary>
public sealed class DispatchTableBuilder
{
    private readonly Dictionary<Type, RequestHandlerEntry> _requests = [];
    private readonly Dictionary<Type, StreamHandlerEntry> _streams = [];

    /// <summary>已添加的请求条目数。</summary>
    public int RequestCount => _requests.Count;

    /// <summary>已添加的流式条目数。</summary>
    public int StreamCount => _streams.Count;

    /// <summary>添加请求条目；同一请求类型重复添加将抛出 <see cref="DuplicateHandlerException"/>。</summary>
    public void Add(RequestHandlerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_requests.TryGetValue(entry.RequestType, out var existing))
        {
            throw new DuplicateHandlerException(
                entry.RequestType,
                [existing.HandlerServiceType, entry.HandlerServiceType]);
        }

        _requests.Add(entry.RequestType, entry);
    }

    /// <summary>添加流式条目；同一请求类型重复添加将抛出 <see cref="DuplicateHandlerException"/>。</summary>
    public void AddStream(StreamHandlerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_streams.TryGetValue(entry.RequestType, out var existing))
        {
            throw new DuplicateHandlerException(
                entry.RequestType,
                [existing.HandlerServiceType, entry.HandlerServiceType]);
        }

        _streams.Add(entry.RequestType, entry);
    }

    /// <summary>冻结为只读调度表。</summary>
    public DispatchTables Build() => new(_requests.ToFrozenDictionary(), _streams.ToFrozenDictionary());
}
