# HandlerR

进程内消息调度库：请求 / 响应、通知多播、流式请求、Pipeline Behavior、异常映射、内建可观测性。

**Independent implementation. Not affiliated with or derived from MediatR.**
本库为独立实现，与 MediatR 无任何隶属或派生关系，许可为 Apache-2.0。

## 5 分钟上手

用 .NET 10 SDK 创建一个控制台项目。主包会带入契约包和源生成器，控制台示例还需要 Microsoft DI 容器的具体实现。

```bash
dotnet new console -n HandlerR.Quickstart -f net10.0
cd HandlerR.Quickstart
dotnet add package HandlerR --version 1.0.0
dotnet add package Microsoft.Extensions.DependencyInjection --version 10.0.15
```

将以下内容保存为 `Program.cs`。

```csharp
using HandlerR.Abstractions;
using HandlerR.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHandlerR(cfg => cfg.RegisterFromAssemblyContaining<GetOrderQueryHandler>());

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
await using var scope = provider.CreateAsyncScope();
var sender = scope.ServiceProvider.GetRequiredService<ISender>();
var order = await sender.SendAsync(new GetOrderQuery(42));
Console.WriteLine($"Order {order.OrderId}");

public sealed record GetOrderQuery(int OrderId) : IQuery<OrderDto>;
public sealed record OrderDto(int OrderId);

public sealed class GetOrderQueryHandler : IRequestHandler<GetOrderQuery, OrderDto>
{
    public ValueTask<OrderDto> HandleAsync(GetOrderQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(new OrderDto(request.OrderId));
}
```

运行 `dotnet run`，输出 `Order 42`。

## 两条注册路径

| 模式 | 机制 | 适用 | 入口 |
| --- | --- | --- | --- |
| **Mode A** | 启动期反射扫描一次，构建冻结调度表 | 常规 JIT 宿主 | `services.AddHandlerR(cfg => cfg.RegisterFrom(...))` |
| **Mode B** | 增量源生成器在编译期产出调度表和处理器清单 | Native AOT / 冷启动敏感 | `services.AddGeneratedHandlerR()`（`using HandlerR.Generated;`） |

在 JIT 宿主中，两种模式共用 `HandlerRDispatcher`、Behavior 管线和诊断。JIT 宿主可以在 `AddGeneratedHandlerR` 的配置回调中注册 Behavior。

## 发布通知与读取流

在同一个 DI 作用域解析 `IPublisher`，调用 `PublishAsync(notification)` 将通知交给已登记的处理器。使用 `ISender.StreamAsync(request)` 和 `await foreach` 消费流式响应。

默认通知按顺序发布。JIT 宿主可以通过 `cfg.NotificationPublisherType` 选择 `ParallelPublisher` 或 `AggregatingParallelPublisher`。前者在首个处理器故障时请求其余处理器取消，等它们结束后抛出原故障。后者等待所有处理器，报告处理器故障。只有取消而无故障时，它会向调用方传播取消。

## 设计要点

- **全库单一 `net10.0` 资产**，零第三方运行时依赖（`Microsoft.Extensions.DependencyInjection.Abstractions` 仅 DI 适配层需要）。
- **`HandlerR.Core` 不依赖 DI 抽象包**，只接受 `IServiceProvider`，可脱离容器单测。
- **热路径无反射**：单次 `FrozenDictionary` 查找 + 逆序折叠委托；`MakeGenericType` / `Activator` 只出现在启动期。
- **生命周期默认 Transient**（对齐基准语义），Singleton 需显式通过 `AllowSingletonLifetime` 的依赖审查。
- **并行通知处理器各自开作用域**，避免并发访问同一个 Scoped 资源。
- **危险能力默认关闭**：超时 / 重试 / 幂等 / 缓存 / 事务五类 Behavior 全部需要显式注册。

## 包构成

```
HandlerR.Contracts            消息形状（6 个类型），零依赖
HandlerR                      主包：等于 Abstractions + Core + DependencyInjection
HandlerR.FluentValidation     可选适配包（自研，不依赖任何第三方 mediator 集成包）
HandlerR.SourceGenerator      增量源生成器（netstandard2.0，随主包作为 analyzers 资产分发）
```

架构合规由测试强制：依赖方向、单一 TFM、`Contracts` 公开类型数量、禁用包清单、零 `MediatR` 命中。

## 可观测性

内建 `ActivitySource` / `Meter`，语义遵循 OpenTelemetry Messaging 约定：

```
messaging.system            = handlerr
messaging.operation         = send | publish | stream | process
messaging.message.type      = 请求类型全名
messaging.destination.name  = 处理器全名
```

订阅 `HandlerRDiagnostics.ActivitySource` 与 `HandlerRDiagnostics.Meter` 即可接入任意 OTel 后端。

## Native AOT

安装 `HandlerR` 主包即可获得源生成器。在启用 `PublishAot` 的宿主中使用生成的注册入口。

```csharp
using HandlerR.Generated;

services.AddGeneratedHandlerR();
```

静态注册支持请求、流式请求和默认顺序通知。Behavior（包括 `TelemetryBehavior` 和 FluentValidation 适配）、请求前后处理器、异常策略以及通知处理器作用域隔离需要 JIT 的运行时注册路径。

## 文档

- [仓库用法和可运行样例](https://github.com/sugarst/HandlerR)
- [功能对标矩阵与已知差异](https://github.com/sugarst/HandlerR/blob/main/docs/FEATURES.md)
- [架构决策与实现边界](https://github.com/sugarst/HandlerR/blob/main/docs/DESIGN.md)

## 许可

Apache-2.0。见 `LICENSE`。
