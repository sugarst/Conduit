using System;
using System.Collections.Generic;

namespace Conduit.SourceGenerator;

/// <summary>被生成器识别的处理器形态。</summary>
internal enum HandlerKind
{
    Request = 0,
    Stream = 1,
    Notification = 2,
    ExceptionHandler = 3,
    ExceptionAction = 4,
    PreProcessor = 5,
    PostProcessor = 6,
}

/// <summary>
/// 一条处理器事实。全部字段为字符串，保证值相等可用于增量缓存比较。
/// </summary>
internal sealed class HandlerModel : IEquatable<HandlerModel>
{
    public HandlerModel(
        HandlerKind kind,
        string handlerType,
        string requestType,
        string responseType,
        string exceptionType)
    {
        Kind = kind;
        HandlerType = handlerType;
        RequestType = requestType;
        ResponseType = responseType;
        ExceptionType = exceptionType;
    }

    public HandlerKind Kind { get; }

    public string HandlerType { get; }

    public string RequestType { get; }

    public string ResponseType { get; }

    public string ExceptionType { get; }

    public bool Equals(HandlerModel? other)
        => other is not null
           && Kind == other.Kind
           && string.Equals(HandlerType, other.HandlerType, StringComparison.Ordinal)
           && string.Equals(RequestType, other.RequestType, StringComparison.Ordinal)
           && string.Equals(ResponseType, other.ResponseType, StringComparison.Ordinal)
           && string.Equals(ExceptionType, other.ExceptionType, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as HandlerModel);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (int)Kind;
            hash = (hash * 397) ^ HandlerType.GetHashCode();
            hash = (hash * 397) ^ RequestType.GetHashCode();
            hash = (hash * 397) ^ ResponseType.GetHashCode();
            hash = (hash * 397) ^ ExceptionType.GetHashCode();
            return hash;
        }
    }
}

/// <summary>
/// 编译单元内声明的请求事实，用于缺失 Handler 诊断。
/// </summary>
internal sealed class RequestModel : IEquatable<RequestModel>
{
    public RequestModel(string requestType, bool isStream, string filePath, int startLine, int startCharacter)
    {
        RequestType = requestType;
        IsStream = isStream;
        FilePath = filePath;
        StartLine = startLine;
        StartCharacter = startCharacter;
    }

    public string RequestType { get; }

    public bool IsStream { get; }

    public string FilePath { get; }

    public int StartLine { get; }

    public int StartCharacter { get; }

    public bool Equals(RequestModel? other)
        => other is not null
           && string.Equals(RequestType, other.RequestType, StringComparison.Ordinal)
           && IsStream == other.IsStream
           && string.Equals(FilePath, other.FilePath, StringComparison.Ordinal)
           && StartLine == other.StartLine
           && StartCharacter == other.StartCharacter;

    public override bool Equals(object? obj) => Equals(obj as RequestModel);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = RequestType.GetHashCode();
            hash = (hash * 397) ^ IsStream.GetHashCode();
            hash = (hash * 397) ^ FilePath.GetHashCode();
            hash = (hash * 397) ^ StartLine;
            return hash;
        }
    }
}

/// <summary>
/// 值语义集合包装：让 <c>Collect()</c> 的结果可参与增量比较，
/// 而不是每次编译都因引用变化而重跑生成。
/// </summary>
internal readonly struct EquatableList<T> : IEquatable<EquatableList<T>>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableList(T[] items) => _items = items;

    public T[] Items => _items ?? Array.Empty<T>();

    public bool Equals(EquatableList<T> other)
    {
        var left = Items;
        var right = other.Items;

        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableList<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            foreach (var item in Items)
            {
                hash = (hash * 31) + (item?.GetHashCode() ?? 0);
            }

            return hash;
        }
    }
}
