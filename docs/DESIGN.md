# 架构决策与实现边界

完整技术方案见 [`HandlerR-类库技术方案.md`](./HandlerR-类库技术方案.md)（v3.1）。
本文件只记录"方案 → 代码"的落点与实现期做出的补充决策。

## 1. 依赖方向

```
HandlerR.Contracts  →  HandlerR.Abstractions  →  HandlerR.Core  →  HandlerR.DependencyInjection
                                                                        ↑
                                                    HandlerR.FluentValidation / HandlerR（打包）
```

| 项目 | 允许依赖 | 禁止 |
| --- | --- | --- |
| `HandlerR.Contracts` | 无 | 任何包、任何项目引用 |
| `HandlerR.Abstractions` | `HandlerR.Contracts` | 任何 NuGet 包（含 DI 抽象） |
| `HandlerR.Core` | `HandlerR.Abstractions` + BCL | `Microsoft.Extensions.*`、DI 项目 |
| `HandlerR.DependencyInjection` | `Microsoft.Extensions.DependencyInjection.Abstractions`、Core | 反射式 AOT 声明 |

`HandlerR.Core` 如何在不引 DI 包的前提下解析多实现：统一按 `typeof(IEnumerable<TService>)` 向
`IServiceProvider` 索取（`Core/Internal/ServiceResolution.cs`）。主流容器对这一形状都有约定支持，
内核因此可用任意 provider（含手写测试替身）驱动。

## 2. 调度热路径

```
SendAsync<TResponse>
  ├─ 1 次 FrozenDictionary.TryGetValue(request.GetType())
  ├─ entry 类型检查（is RequestHandlerEntry<TResponse>）
  └─ InvokeSendAsync
       ├─ pre processor runner（无处理器时为零开销 noop）
       ├─ entry.InvokeAsync
       │    ├─ 解析 IRequestHandler（一次）
       │    ├─ 逆序折叠 behaviors（声明顺序 = 由外到内）
       │    └─ try/catch → IExceptionDispatcher<TReq,TResp>（未配置时无额外开销）
       └─ post processor runner
```

约束：零反射、零 `MakeGenericType`、零表达式编译、零 LINQ。`MakeGenericType` / `Activator`
仅出现在注册期（`HandlerRRegistrar`、`BuildTables`、`OpenBehaviorCloser`）或源生成器的编译期产物中。

可观测性标签按条目缓存（`RequestHandlerEntry.Tags`），热路径不分配标签数组。

## 3. 生命周期

- 默认 `Transient`：Dispatcher 与 Handler 同生命周期，可配置为 `Scoped`。
- `Singleton` 默认被拒绝：`ValidateConfiguration` 会产出诊断，必须显式
  `AllowSingletonLifetime = true` 才能通过（对应方案 7.2 的"依赖审查"）。
- 并发发布（`IConcurrentNotificationPublisher`）下，通知处理器由
  `ScopedNotificationHandler<THandler,TNotification>` 包装，每次调用新开作用域；
  顺序发布则共享调用方作用域（事务内串行写入的常见需求）。

## 4. 异常映射

```
ExceptionHandlerInvoker<TRequest,TResponse,TException>   // 闭合入口，启动期创建
RequestExceptionDispatcher<TRequest,TResponse>           // 按 (请求, 响应) 注册为单例
```

- 处理器按"精确类型 → 基类（按继承层级）→ 接口"排序，首个 `Handled` 即停止。
- 动作（`IRequestExceptionAction`）在映射判定后按策略执行：
  `ApplyForUnhandledExceptions` 只对最终未处理的异常执行，`ApplyForAllExceptions` 全执行。
  这是对方案"动作在映射之前执行"的等价实现——因为"是否需要执行"本身依赖映射结果。
- 异常路径使用装箱结果载体（`ExceptionOutcomeResult`），保证 AOT 下的共享泛型安全；冷路径可接受。

## 5. 注册期编排（`HandlerRRegistrar`）

1. 配置校验（调度器类型、生命周期、外部 Behavior、发布器唯一性）
2. 扫描（Mode A）或读取生成清单（Mode B）→ 注册服务
3. 冻结调度表（`FrozenDictionary`）
4. 构建异常映射调度器（按 `(TReq, TResp)`）
5. 前 / 后处理器运行器（按请求类型索引，热路径零反射）
6. 关闭开放 Behavior（`CloseForScannedRequests` 逐对关闭 / `CloseInContainer` 交容器）
7. 发布器 + 默认 `IRequestCache`（`InMemoryRequestCache`，可替换）
8. Dispatcher / ISender / IPublisher
9. 启动校验聚合抛出 `HandlerRStartupValidationException`

校验在**注册期**完成而不是 `IHostedService` 启动时：信息此时已齐备，失败更早，且不引入
`Microsoft.Extensions.Hosting.Abstractions` 依赖。

## 6. 源生成器（Mode B）

- 输入：实现 `IRequestHandler<,>` / `IStreamRequestHandler<,>` / `INotificationHandler<>` /
  `IRequestExceptionHandler<,,>` / `IRequestExceptionAction<,>` 的封闭类声明。
- 增量：`CreateSyntaxProvider` + 值语义 `EquatableList<T>` 包装，避免无关编辑触发重跑。
- 产物：`HandlerR.Generated.HandlerRGeneratedRegistration.Create()`（调度表 + 服务清单）与
  `AddGeneratedHandlerR()` 扩展；不引用 DI 包时静默跳过。
- 诊断：`CDI001`（缺 Handler，Warning）、`CDI002`（重复 Handler，Error）、`CDI003`（不支持形态，Warning）。
- 生成器自身 `netstandard2.0`，只在编译期加载，不进运行时依赖图。

## 7. 实现期补充决策

| 决策 | 理由 |
| --- | --- |
| 去掉 `ISender.SendAsync(IRequest)` 非泛型重载 | `ICommand` 同时实现 `IRequest` 与 `IRequest<Unit>`，保留两个重载会导致 `SendAsync(command)` 调用二义性 |
| `CDI001` 定为 Warning | Handler 常位于独立程序集，Error 会误伤多程序集解决方案 |
| 自定义 `IRequestCache` / `ITransactionScopeFactory` 抽象 | 让缓存与事务 Behavior 保持零第三方依赖，由宿主适配 Redis / EF Core |
| `HandlerR.DependencyInjection` 不开 `IsAotCompatible` | Mode A 反射扫描天然不满足 AOT 约定；如实声明优于用抑制属性掩盖 |
| 公开 `RequestHandlerEntry` 等基础设施类型 | 源生成器需要在用户程序集内构造调度表，跨界必须公开 |

## 8. 已知取舍

- `TimeoutBehavior` 以"竞速"实现：超时抛出后，已开始的处理器会继续执行到结束（委托链不携带令牌）。
  需要真正取消的请求应自行把令牌传入下游 I/O。
- `CachingBehavior` 默认键为"类型全名 + ToString()"：请求类型应实现 `ICacheKeyProvider`
  或具备值语义的 `ToString()`，否则会命中错误条目。
- 扫描式注册在大程序集上可能触碰 `RegistrationTimeout`，可调高阈值或改用 Mode B。
