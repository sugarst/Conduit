namespace HandlerR.Tests.Fixtures;

/// <summary>
/// TUnit 的 <c>Assert.That(...).Throws&lt;T&gt;()</c> 对返回泛型 Task 的委托存在可空性摩擦，
/// 统一用显式捕获助手，断言更直白。
/// </summary>
internal static class ExceptionCapture
{
    public static async Task<Exception?> CaptureAsync(Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    public static async Task<Exception?> CaptureAsync<T>(Func<ValueTask<T>> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
