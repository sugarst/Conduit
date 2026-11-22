namespace Conduit.Core.Internal;

/// <summary>
/// 只依赖 <see cref="IServiceProvider"/> 的解析工具：内核不引用 DI 抽象包，
/// 多实现统一按 <c>IEnumerable&lt;T&gt;</c> 形状解析（与主流容器约定一致）。
/// </summary>
internal static class ServiceResolution
{
    public static TService? Resolve<TService>(this IServiceProvider provider)
        where TService : class
        => (TService?)provider.GetService(typeof(TService));

    public static TService ResolveRequired<TService>(this IServiceProvider provider)
        where TService : class
        => provider.Resolve<TService>()
           ?? throw new InvalidOperationException(
               $"Required Conduit service '{typeof(TService).FullName}' is not registered. "
               + "Did you forget to call AddConduit()?");

    public static IReadOnlyList<TService> ResolveAll<TService>(this IServiceProvider provider)
    {
        var services = provider.GetService(typeof(IEnumerable<TService>));
        switch (services)
        {
            case null:
                return Array.Empty<TService>();
            case IReadOnlyList<TService> list:
                return list;
            case IEnumerable<TService> enumerable:
            {
                var buffer = new List<TService>();
                foreach (var item in enumerable)
                {
                    buffer.Add(item);
                }

                return buffer;
            }

            default:
                return Array.Empty<TService>();
        }
    }
}
