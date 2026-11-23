using SystemTask = System.Threading.Tasks.Task;

namespace HandlerR.Contracts;

/// <summary>
/// 无值响应类型的占位符（语义等价于一元空元组 <c>()</c>）。
/// </summary>
/// <remarks>
/// 成员集合刻意保持完整：值语义、可比较（排序 / 缓存键）、同步完成的静态任务缓存。
/// 全部成员对任意两个实例恒等，因此实现上不保存任何状态。
/// 关系运算符（<c>&lt;</c> / <c>&gt;</c> 等）刻意不提供：所有实例相等，任何大小比较都无意义。
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Unit 的全部实例相等，提供关系运算符会产生误导性的 API 面。")]
public readonly struct Unit : IEquatable<Unit>, IComparable<Unit>, IComparable
{
    private static readonly Unit Cached;

    /// <summary>
    /// 唯一实例的只读引用，供 <c>in</c> 参数场景零拷贝使用。
    /// </summary>
    public static ref readonly Unit Value => ref Cached;

    /// <summary>
    /// 已完成的 <see cref="System.Threading.Tasks.Task"/>，避免每次返回空响应都分配新任务。
    /// </summary>
    public static SystemTask Task { get; } = SystemTask.FromResult(Cached);

    /// <inheritdoc />
    public int CompareTo(Unit other) => 0;

    int IComparable.CompareTo(object? obj) => 0;

    /// <inheritdoc />
    public override int GetHashCode() => 0;

    /// <inheritdoc />
    public bool Equals(Unit other) => true;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Unit;

    /// <summary>两个 <see cref="Unit"/> 恒等。</summary>
    public static bool operator ==(Unit left, Unit right) => true;

    /// <summary>两个 <see cref="Unit"/> 永不不等。</summary>
    public static bool operator !=(Unit left, Unit right) => false;

    /// <summary>固定返回 <c>()</c>。</summary>
    public override string ToString() => "()";
}
