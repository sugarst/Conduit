# Changelog

本文件格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本遵循 SemVer。

## [1.0.0] - 2026-11-22

首个工程化落地版本。功能基准 MediatR 15.0.0 + MediatR.Contracts 2.0.1（仅功能与语义对标，无代码派生）。

### 新增

- `HandlerR.Contracts`：`IBaseRequest` / `IRequest` / `IRequest<T>` / `INotification` / `IStreamRequest<T>` / `Unit`，零依赖。
- `HandlerR.Abstractions`：双泛型 Handler、Pipeline / Stream Behavior、可替换 `INotificationPublisher`、
  预/后处理器、异常映射与动作、校验、语义标记、`ISender` / `IPublisher` / `IDispatcher`、异常类型族。
- `HandlerR.Core`：冻结调度表（`FrozenDictionary`）、异构条目归一化、逆序折叠管线、异常映射调度器、
  三种发布策略、内建 `ActivitySource` / `Meter`、7 个内置 Behavior、进程内 `IRequestCache`。
- `HandlerR.DependencyInjection`：`AddHandlerR` 配置面（逐项对齐基准 15.0）、程序集扫描、类型过滤、
  四类泛型护栏（`0` = 禁用）、开放 Behavior 关闭（含嵌套泛型推断与约束筛选）、并发通知作用域隔离、
  8 项启动期诊断、`AddHandlerR(GeneratedRegistration)` 入口。
- `HandlerR.SourceGenerator`：`IIncrementalGenerator`，编译期生成调度表与 `AddGeneratedHandlerR()`，
  `CDI001` / `CDI002` / `CDI003` 诊断。
- `HandlerR.FluentValidation`：自研适配包（`IRequestValidator<T>` ← FluentValidation `IValidator<T>`）。
- 测试：TUnit 1.87.12 规格 / 并发 / DI / 属性 / 架构合规测试，源生成器端到端与 Roslyn 驱动诊断测试，
  公开 API 表面审批测试（`ApiSurface.approved.txt`，反射快照）。
- 工程化：CPM 集中版本、`Directory.Build.props` 统一契约、警告即错误、BenchmarkDotNet 基线
  （`benchmarks/baseline.json` + 2 倍止损门禁）、`scripts/check_compliance.cs` 合规门禁、
  可运行样例、GitHub Actions CI（`ci.yaml`：build → test → 合规 → pack → benchmark → AOT smoke）、Apache-2.0 许可。
- 仓库脚本统一采用 .NET 10 File-Based App（`dotnet run scripts/*.cs`），不引入 shell / Python 运行时依赖。
- 依赖版本对齐：运行时依赖统一到 10.0.15；SDK 内置清单把 net10.0 的 AOT / 裁剪工具链固定为 10.0.14，
  已在 `Directory.Build.targets` 中上移到 10.0.15（`KnownILCompilerPack` / `KnownILLinkPack`），
  避免解决方案内 10.0.14 与 10.0.15 混用。

### 打包结构

- `HandlerR.Contracts`：`lib/net10.0` 契约程序集，**零包依赖**。
- `HandlerR`：`lib/net10.0` 仅含 Abstractions / Core / DependencyInjection 三个程序集，
  `analyzers/dotnet/cs` 内含源生成器；包依赖为 `HandlerR.Contracts [1.0.0, 2.0.0)` 与
  `Microsoft.Extensions.DependencyInjection.Abstractions`。主包不内嵌契约程序集，避免契约类型重复定义。
- `HandlerR.FluentValidation`：可选适配包，依赖 `HandlerR [1.0.0, 2.0.0)` 与 `FluentValidation`。

### 已知差异

见 [`docs/FEATURES.md`](./docs/FEATURES.md) 第 5 节：无许可校验机制、无跨目标资产、无非泛型 `SendAsync(IRequest)` 重载、
`CDI001` 为 Warning。
