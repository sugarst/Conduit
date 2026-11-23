using HandlerR.Abstractions;
using HandlerR.Core.Behaviors;
using HandlerR.DependencyInjection;
using HandlerR.Samples;
using Microsoft.Extensions.DependencyInjection;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var services = new ServiceCollection();
services.AddSingleton<CatalogStore>();
services.AddSingleton<ITransactionScopeFactory, ConsoleTransactionScopeFactory>();

services.AddHandlerR(cfg =>
{
    cfg.RegisterFrom(typeof(CatalogStore).Assembly);

    // 顺序即由外到内：遥测 → 缓存 → 校验 → 事务 → Handler
    cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
    cfg.AddOpenBehavior(typeof(CachingBehavior<,>));
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
});

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
await using var scope = provider.CreateAsyncScope();

var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
var store = scope.ServiceProvider.GetRequiredService<CatalogStore>();

Section("1) 查询（第一次穿透，第二次命中缓存）");
var first = await dispatcher.SendAsync(new GetProductQuery("SKU-1"));
var second = await dispatcher.SendAsync(new GetProductQuery("SKU-1"));
Console.WriteLine($"  {first.Name} / {first.Price:C0} / 第二次相同：{ReferenceEquals(first, second)}");
Console.WriteLine($"  Handler 实际执行次数：{store.HandlerHits}（缓存生效则为 1）");

Section("2) 命令（事务边界 + 通知多播）");
await dispatcher.SendAsync(new CreateOrderCommand("SKU-1", 2));

Section("3) 校验失败（在事务之前短路）");
try
{
    await dispatcher.SendAsync(new CreateOrderCommand("SKU-1", 0));
}
catch (RequestValidationException exception)
{
    foreach (var error in exception.Errors)
    {
        Console.WriteLine($"  校验失败 → {error.Identifier}: {error.Message} [{error.ErrorCode}]");
    }
}

Section("4) 流式请求（逐项产出）");
await foreach (var product in dispatcher.StreamAsync(new BrowseProductsStream(3)))
{
    Console.WriteLine($"  → {product.Sku} {product.Name} {product.Price:C0}");
}

Section("5) 可观测性");
Console.WriteLine("  订阅 HandlerRDiagnostics.ActivitySource / Meter 即可把 send / publish / stream 导出到 OTel 后端。");
Console.WriteLine($"  ActivitySource = {HandlerR.Core.HandlerRDiagnostics.SourceName}");

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine("── " + title + " " + new string('─', Math.Max(0, 60 - title.Length)));
}
