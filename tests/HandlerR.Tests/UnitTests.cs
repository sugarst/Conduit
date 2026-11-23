using HandlerR.Contracts;

namespace HandlerR.Tests;

public class UnitTests
{
    [Test]
    public async Task Unit_is_always_equal()
    {
        var left = default(Unit);
        var right = new Unit();

        await Assert.That(left == right).IsTrue();
        await Assert.That(left != right).IsFalse();
        await Assert.That(left.Equals(right)).IsTrue();
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
    }

    [Test]
    public async Task Unit_compares_as_zero()
    {
        var unit = Unit.Value;

        await Assert.That(unit.CompareTo(default)).IsEqualTo(0);
        await Assert.That(((IComparable)default(Unit)).CompareTo(new object())).IsEqualTo(0);
    }

    [Test]
    public async Task Unit_provides_a_cached_completed_task()
    {
        var task = Unit.Task;

        await Assert.That(task.IsCompletedSuccessfully).IsTrue();
        await Assert.That(Unit.Task).IsSameReferenceAs(task);
    }

    [Test]
    public async Task Unit_renders_as_empty_tuple()
    {
        await Assert.That(default(Unit).ToString()).IsEqualTo("()");
    }

    [Test]
    public async Task Unit_implements_the_expected_contracts()
    {
        var type = typeof(Unit);

        await Assert.That(typeof(IEquatable<Unit>).IsAssignableFrom(type)).IsTrue();
        await Assert.That(typeof(IComparable<Unit>).IsAssignableFrom(type)).IsTrue();
        await Assert.That(typeof(IComparable).IsAssignableFrom(type)).IsTrue();
        await Assert.That(type.IsValueType).IsTrue();
    }
}
