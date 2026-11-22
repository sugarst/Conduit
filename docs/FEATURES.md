# Conduit 功能对标矩阵

基准：MediatR 15.0.0 + MediatR.Contracts 2.0.1。
状态：**✅ 对齐**（功能与语义等价，API 形状独立）、**⬆️ 增强**、**⛔ 有意不对齐**。

每行后面的「验证」列指向把它钉住的测试（`tests/Conduit.Tests` / `Conduit.GeneratorTests`）。

## 1. 消息模型与分发

| # | 能力 | 状态 | 验证 |
| --- | --- | --- | --- |
| 1 | `IRequest` / `IRequest<T>` / `Unit` | ✅ | `UnitTests`、`SendTests` |
| 2 | 双泛型 `IRequestHandler<TRequest, TResponse>`（无返回值统一 `Unit`） | ✅ | `SendTests.Send_with_Unit_response_uses_command_marker` |
| 3 | `ICommand` / `IQuery<T>` 语义标记 | ⬆️ | `Messages.cs`，同时用于 Behavior 筛选 |
| 4 | `ISender.SendAsync` | ✅ | `SendTests.Send_through_ISender_facade_works` |
| 5 | `IDispatcher : ISender, IPublisher` | ✅ | `SendTests` |
| 6 | `INotification` / `INotificationHandler<T>` | ✅ | `NotificationTests` |
| 7 | `IPublisher.PublishAsync` | ✅ | `NotificationTests` |
| 8 | 可替换 `INotificationPublisher` | ✅ | `NotificationTests.Publisher_type_takes_precedence_over_instance` |
| 9 | 三种发布策略（顺序 / 并发 / 聚合并发） | ⬆️ | `NotificationTests`（顺序、并发、聚合异常） |
| 10 | 通知 Handler 去重（扫描期 + 运行期） | ✅ | `NotificationTests.Duplicate_handler_instances_are_deduplicated_at_runtime` |
| 11 | `IStreamRequest<T>` / `IStreamRequestHandler<T,R>` | ✅ | `StreamTests` |
| 12 | `IStreamPipelineBehavior<T,R>`（独立建链） | ✅ | `StreamTests.Stream_behaviors_wrap_each_item` |
| 13 | `IPipelineBehavior<T,R>` + `RequestHandlerDelegate<T>`（逆序折叠） | ✅ | `SendTests.Behaviors_execute_outside_in_with_declaration_order` |
| 14 | 开放 Behavior 多注册 | ✅ | `DependencyInjectionTests`（多个开放行为） |
| 15 | `ConduitOpenBehavior`（Type + Lifetime） | ✅ | `Configuration/BehaviorClosingStrategy.cs` |
| 16 | 泛型约束推断 + 嵌套泛型参数推断 | ✅ | `DependencyInjectionTests.Nested_generic_parameters_are_inferred_when_closing_behavior` |
| 17 | `BehaviorClosingStrategy` 两值 | ✅ | `CloseInContainer_strategy_registers_open_generic_behavior` |
| 18 | 泛型约束筛选（`where TRequest : ICacheable`） | ✅ | `Generic_constraints_filter_which_requests_a_behavior_applies_to` |
| 19 | `IRequestPreProcessor` / `IRequestPostProcessor` | ✅ | `ProcessorTests` |
| 20 | `IRequestExceptionHandler` + `ExceptionOutcome<T>` 状态机 | ✅ | `ExceptionMappingTests` |
| 21 | `IRequestExceptionAction` | ✅ | `ExceptionMappingTests` |
| 22 | `ExceptionActionStrategy` 两值 | ✅ | `Actions_are_skipped_for_handled_exceptions_by_default` / `Actions_run_for_all_exceptions_when_configured` |
| 23 | `CancellationToken` 全链透传 | ✅ | `StreamTests.Stream_stops_when_cancelled`、各处理器签名 |

## 2. 配置面与注册语义

| # | 能力 | 状态 | 验证 |
| --- | --- | --- | --- |
| 24 | `RegisterFrom` / `RegisterFromAssemblyContaining` | ✅ | 全部 DI 测试 |
| 25 | `TypeEvaluator` 类型过滤（默认 `t => true`） | ✅ | `TypeEvaluator_excludes_filtered_handlers` |
| 26 | `Lifetime`（默认 Transient，Singleton 需审查） | ✅ | `Scoped_lifetime_resolves_handlers_per_scope`、`Singleton_lifetime_requires_explicit_review` |
| 27 | `DispatcherImplementationType` | ✅ | `ConduitRegistrar.ValidateConfiguration` |
| 28 | `NotificationPublisher` 实例 / `NotificationPublisherType` 类型 | ✅ | `Publisher_type_takes_precedence_over_instance` |
| 29 | `AutoRegisterRequestProcessors`（默认 false） | ✅ | `ProcessorTests` 三条 |
| 30 | 显式注册 Behavior / Pre / Post（顺序 = 由外到内） | ✅ | `ProcessorTests.Pre_processor_runs_before_behaviors_and_post_after` |
| 31 | `RegisterGenericHandlers`（默认 false，opt-in） | ✅ | `AssemblyScanner.InspectOpenGenericHandler` |
| 32 | `MaxGenericTypeParameters`（默认 10） | ✅ | `MaxGenericTypeParameters_trips_when_exceeded` |
| 33 | `MaxTypesClosing`（默认 100） | ✅ | `MaxTypesClosing_trips_when_exceeded` |
| 34 | `MaxGenericTypeRegistrations`（默认 125000） | ✅ | `RegistrationGuard` |
| 35 | `RegistrationTimeout`（默认 15000 ms） | ✅ | `RegistrationGuard.CheckTimeout` |
| 36 | 阈值为 `0` 表示禁用限制 | ✅ | `Zero_disables_every_generic_guard` |
| 37 | 嵌套泛型实参处理 | ✅ | 见 #16 |
| 38 | 反射容错（不可读类型跳过） | ✅ | `AssemblyScanner.SafeGetTypes` + 单类型 try/catch |
| 39 | 不要求 `ILoggerFactory` | ⬆️ | 依赖 `ActivitySource` / `Meter`，无 logger 依赖 |
| 40 | 零第三方运行时依赖 | ⬆️ | `ArchitectureTests` 依赖断言 |
| 41 | 独立契约包 `Conduit.Contracts` | ✅ | `ArchitectureTests.Contracts_exposes_exactly_six_public_types` |
| 42 | 契约包多目标（netstandard2.0） | ⛔ | 只发 `net10.0`，无历史包袱 |

## 3. 配置面逐项对照（默认值）

| 属性 | 默认值 | 备注 |
| --- | --- | --- |
| `TypeEvaluator` | `t => true` | — |
| `DispatcherImplementationType` | `typeof(ConduitDispatcher)` | 可替换 |
| `NotificationPublisher` | `SequentialPublisher.Instance` | 无状态共享实例 |
| `NotificationPublisherType` | `null` | 优先于实例 |
| `Lifetime` | `Transient` | `Singleton` 需 `AllowSingletonLifetime` |
| `ExceptionActionStrategy` | `ApplyForUnhandledExceptions` | — |
| `BehaviorClosingStrategy` | `CloseForScannedRequests` | — |
| `AutoRegisterRequestProcessors` | `false` | — |
| `MaxGenericTypeParameters` | `10` | 0 禁用 |
| `MaxTypesClosing` | `100` | 0 禁用 |
| `MaxGenericTypeRegistrations` | `125000` | 0 禁用 |
| `RegistrationTimeout` | `15000` ms | 0 禁用 |
| `RegisterGenericHandlers` | `false` | — |
| `EnableStartupValidation` | `true` | 注册期即校验 |
| `AllowSingletonLifetime` | `false` | 显式依赖审查开关 |
| `IsolateNotificationHandlerScopes` | `null`（自动） | 并发发布器下自动开启 |
| 许可相关（`LicenseKey` 等） | ⛔ 不提供 | 独立库无许可机制 |

## 4. 契约面对照（Conduit.Contracts）

| 类型 | 状态 | 说明 |
| --- | --- | --- |
| `IBaseRequest` | ✅ | 无基接口 |
| `IRequest : IBaseRequest` | ✅ | 无响应形状 |
| `IRequest<out TResponse> : IBaseRequest` | ✅ | 协变 |
| `INotification` | ✅ | — |
| `IStreamRequest<out TResponse>` | ✅ | 协变 |
| `Unit` | ✅ | `IEquatable` / `IComparable<Unit>` / `IComparable`、`ref readonly Value`、静态 `Task`、`ToString() == "()"` |
| 类型转发 | ⛔ | 无存量代码需要兼容 |
| Apache-2.0 | ✅ | 同样采用 Apache-2.0 |

## 5. 有意不对齐（豁免）

| 项 | 原因 |
| --- | --- |
| `LicenseKey` / `LicenseKeyFactory` / 环境变量回退 / 后台校验 | 商业许可执行机制 |
| 全部非 `net10.0` 目标 | 单一资产换取零依赖与 AOT 成立 |
| 跨进程消息 / Outbox / Inbox / Saga | 消息总线职责 |
| 兼容垫片 / 类型转发 / 迁移工具 | 与"无任何关系"目标冲突 |
| `SendAsync(IRequest)` 非泛型重载 | `ICommand` 同时实现两个形状，非泛型重载会造成调用二义性 |
| `CDI001` 为 Error | 改为 Warning，避免多程序集解决方案误报（Handler 常在另一个项目） |

## 6. 内建 Behavior

| Behavior | 触发条件 | 默认 | 验证 |
| --- | --- | --- | --- |
| `TelemetryBehavior<,>` | 全局开启 | 关闭 | `Telemetry_behavior_can_be_registered_as_open_generic` |
| `ValidationBehavior<,>` | 存在 `IRequestValidator<T>` | 关闭 | `Validation_behavior_*`、`FluentValidationAdapterTests` |
| `CachingBehavior<,>` | 实现 `ICacheable` | 关闭 | `Caching_behavior_reuses_handler_result` |
| `TransactionBehavior<,>` | 实现 `ITransactionalRequest` | 关闭 | `Transaction_behavior_commits_on_success` / `rolls_back_on_failure` |
| `TimeoutBehavior<,>` | 实现 `ITimeoutRequest` | 关闭 | `Timeout_behavior_throws_when_budget_exceeded` |
| `RetryBehavior<,>` | 实现 `IRetryable` **且** `IIdempotent` | 关闭 | `Retry_behavior_*` |
| `IdempotencyBehavior<,>` | 实现 `IIdempotent` | 关闭 | `Idempotency_behavior_*` |

> 默认关闭是刻意设计：默认开启会掩盖业务缺陷，并在有副作用的 Handler 上造成数据事故。

## 7. 启动期诊断（8 项）

| # | 检查 | 实现位置 |
| --- | --- | --- |
| 1 | 每个请求有且仅有一个 Handler | 扫描期 `DuplicateHandlerException` + 调度表构建 |
| 2 | 无重复 Handler | 同上 |
| 3 | Lifetime 组合合法（Singleton 需审查） | `ValidateConfiguration` |
| 4 | 开放 Behavior 类型参数可判定 | `OpenBehaviorCloser` + `CloseResult.Failed` |
| 5 | Behavior 均来自注册表 | `ValidateConfiguration` 检测外部 `IPipelineBehavior<>` 注册 |
| 6 | Stream 与 Request Behavior 未混用 | `AddBehaviorCore` 类型检查 |
| 7 | `INotificationPublisher` 唯一且可实现 | `ValidateConfiguration` |
| 8 | 泛型护栏触发时 fail（`0` 禁用） | `RegistrationGuard` |
