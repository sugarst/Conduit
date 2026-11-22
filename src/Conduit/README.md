# Conduit

进程内消息调度库：请求 / 响应、通知多播、流式请求、Pipeline Behavior、异常映射、内建可观测性。

**Independent implementation. Not affiliated with or derived from MediatR.**
本库为独立实现，与 MediatR 无任何隶属或派生关系，许可为 Apache-2.0。

## 5 分钟上手

```bash
dotnet add package Conduit
dotnet add package Conduit.Contracts   # 契约项目可只引这个
```

```csharp
// 1. 消息（契约层，零依赖）
public sealed record GetOrderQuery(Guid OrderId) : IQuery<OrderDto>;

// 2. 处理器
public sealed class GetOrderQueryHandler : IRequestHandler<GetOrderQuery, OrderDto>
{
    public ValueTask<OrderDto> HandleAsync(GetOrderQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(new OrderDto(request.OrderId));
}

// 3. 注册
services.AddConduit(cfg =>
{
    cfg.RegisterFromAssemblyContaining<GetOrderQuery>();
    cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));   // 顺序 = 由外到内
});

// 4. 发送
var dto = await dispatcher.SendAsync(new GetOrderQuery(orderId), cancellationToken);
```

## 两条注册路径

| 模式 | 机制 | 适用 | 入口 |
| --- | --- | --- | --- |
| **Mode A** | 启动期反射扫描一次，构建冻结调度表 | 常规 JIT 宿主 | `services.AddConduit(cfg => cfg.RegisterFrom(...))` |
| **Mode B** | 增量源生成器在编译期产出调度表，零反射 | Native AOT / 冷启动敏感 | `services.AddGeneratedConduit(cfg => ...)`（`using Conduit.Generated;`） |

两种模式的运行期语义完全一致：同一套 `ConduitDispatcher`、同一套 Behavior 语义、同一套诊断。

## 设计要点

- **全库单一 `net10.0` 资产**，零第三方运行时依赖（`Microsoft.Extensions.DependencyInjection.Abstractions` 仅 DI 适配层需要）。
- **`Conduit.Core` 不依赖 DI 抽象包**，只接受 `IServiceProvider`，可脱离容器单测。
- **热路径无反射**：单次 `FrozenDictionary` 查找 + 逆序折叠委托；`MakeGenericType` / `Activator` 只出现在启动期。
- **生命周期默认 Transient**（对齐基准语义），Singleton 需显式通过 `AllowSingletonLifetime` 的依赖审查。
- **并行通知处理器各自开作用域**，避免并发访问同一个 Scoped 资源。
- **危险能力默认关闭**：超时 / 重试 / 幂等 / 缓存 / 事务五类 Behavior 全部需要显式注册。

## 包构成

```
Conduit.Contracts            消息形状（6 个类型），零依赖
Conduit                      主包：等于 Abstractions + Core + DependencyInjection
Conduit.FluentValidation     可选适配包（自研，不依赖任何第三方 mediator 集成包）
Conduit.SourceGenerator      增量源生成器（netstandard2.0，编译器加载）
```

架构合规由测试强制：依赖方向、单一 TFM、`Contracts` 公开类型数量、禁用包清单、零 `MediatR` 命中。

## 可观测性

内建 `ActivitySource` / `Meter`，语义遵循 OpenTelemetry Messaging 约定：

```
messaging.system            = conduit
messaging.operation         = send | publish | stream | process
messaging.message.type      = 请求类型全名
messaging.destination.name  = 处理器全名
```

订阅 `ConduitDiagnostics.ActivitySource` 与 `ConduitDiagnostics.Meter` 即可接入任意 OTel 后端。

## Native AOT

```xml
<ProjectReference Include="...\Conduit.SourceGenerator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

```csharp
services.AddGeneratedConduit(cfg => cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>)));
```

Mode A 的反射扫描在设计上不做 AOT 声明（`Conduit.DependencyInjection` 未开启 `IsAotCompatible`）；
AOT 场景请使用 Mode B。裁剪警告清零是 CI 的 AOT 作业职责。

## 文档

- `README.md`（本文件）：5 分钟上手与设计要点
- `FEATURES.md`：功能对标矩阵与已知差异
- `DESIGN.md`：架构决策与实现边界
- `Conduit-类库技术方案.md`：完整技术方案（v3.1）

## 许可

Apache-2.0。见 `LICENSE`。
