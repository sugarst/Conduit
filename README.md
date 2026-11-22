# Conduit — 进程内消息调度库

> **Independent implementation. Not affiliated with or derived from MediatR.**
> 独立实现，Apache-2.0 许可。技术方案见 [`docs/Conduit-类库技术方案.md`](./docs/Conduit-类库技术方案.md)（v3.1）。

## 这是什么

一个面向 .NET 10 的进程内消息调度库：请求 / 响应、通知多播、流式请求、Pipeline Behavior 责任链、
预 / 后处理、异常映射、启动期扫描与诊断、内建 OpenTelemetry。

设计取向是"**零反射热路径 + 零第三方运行时依赖 + 单一 net10.0 资产**"，Native AOT 为第一等目标。

## 30 秒示例

```csharp
public sealed record PingQuery(int Value) : IQuery<int>;

public sealed class PingHandler : IRequestHandler<PingQuery, int>
{
    public ValueTask<int> HandleAsync(PingQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(request.Value);
}

var services = new ServiceCollection();
services.AddConduit(cfg =>
{
    cfg.RegisterFromAssemblyContaining<PingQuery>();
    cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
});

await using var provider = services.BuildServiceProvider();
var dispatcher = provider.GetRequiredService<IDispatcher>();

var result = await dispatcher.SendAsync(new PingQuery(41));   // 41
```

可运行样例：`dotnet run --project samples/Conduit.Samples`（查询缓存、事务 + 通知、校验短路、流式）。

## 解决方案结构

```
src/
  Conduit.Contracts           net10.0   消息形状契约，6 个类型，零依赖
  Conduit.Abstractions        net10.0   Handler / Behavior / Publisher / 门面 / 异常
  Conduit.Core                net10.0   调度内核，仅 BCL（不引 DI 抽象包）
  Conduit.DependencyInjection net10.0   注册、扫描、泛型护栏、启动诊断、作用域隔离
  Conduit.SourceGenerator     netstandard2.0  增量源生成器（Roslyn 宿主约束，以 analyzers 资产并入主包）
  Conduit.FluentValidation    net10.0   可选校验适配包
  Conduit                     net10.0   主 NuGet 包（打包 Abstractions/Core/DI 三个程序集 + 源生成器）
tests/
  Conduit.Tests               规格 / 并发 / DI / 架构合规（TUnit 1.87.12）
  Conduit.GeneratorTests      源生成器端到端 + Roslyn 驱动诊断测试
  Conduit.TestSupport         扫描与负向用例的独立程序集
benchmarks/Conduit.Benchmarks BenchmarkDotNet 基线
samples/Conduit.Samples       可运行示例
scripts/                      合规与基准门禁（.NET 10 File-Based App）
docs/                         技术方案、架构决策、功能对标矩阵
.github/workflows/ci.yaml      CI 流水线
```

依赖方向单向：`Contracts → Abstractions → Core → DependencyInjection`。

## 构建与验证

```bash
dotnet build Conduit.slnx -c Release
dotnet run --project tests/Conduit.Tests -c Release
dotnet run --project tests/Conduit.GeneratorTests -c Release
dotnet run --project samples/Conduit.Samples
dotnet pack src/Conduit/Conduit.csproj -c Release -o artifacts
```

测试栈统一 Microsoft.Testing.Platform（TUnit），不混用 VSTest。
CI 见 `.github/workflows/ci.yaml`：build → test → 合规 → pack → benchmark → AOT smoke。

仓库脚本全部是 .NET 10 File-Based App（单文件 C#，`dotnet run` 直接执行，零 extra 运行时依赖）：

```bash
dotnet run scripts/check_compliance.cs                # 红线 / 依赖 / TFM 合规
dotnet run scripts/check_compliance.cs -- --packages  # 追加 nupkg 结构校验
dotnet run scripts/check_benchmark.cs -- --report <bdn-json>   # 基准回归门禁
```

## 包结构与依赖

| 包 | 内容 | 包依赖 |
| --- | --- | --- |
| `Conduit.Contracts` | `lib/net10.0` 契约程序集 | **无**（零依赖，架构测试断言） |
| `Conduit` | `lib/net10.0` 三个运行时程序集 + `analyzers/dotnet/cs` 源生成器 | `Conduit.Contracts [1.0.0, 2.0.0)`、`Microsoft.Extensions.DependencyInjection.Abstractions` |
| `Conduit.FluentValidation` | 可选适配层 | `Conduit [1.0.0, 2.0.0)`、`FluentValidation` |

主包**不内嵌** `Conduit.Contracts.dll`，契约类型只由契约包提供——从包结构上排除"双包重复定义契约类型"导致的运行时类型不匹配。

## 关键约定（评审可逐条检查）

| 约定 | 落地方式 |
| --- | --- |
| 零第三方运行时依赖 | 架构测试断言 `Contracts` 无包引用、`Core` 无 `Microsoft.Extensions*` |
| 单一 TFM | 架构测试断言所有产品项目只声明 `net10.0`（生成器例外） |
| 契约类型只定义一次 | 架构测试断言 `Conduit.Contracts` 恰好 6 个公开类型 |
| 公开 API 变更需批准 | `PublicApiSurfaceTests` 反射快照比对 `ApiSurface.approved.txt`，签名变动即失败 |
| 无对标库痕迹 | 架构测试全局扫描 `src/`，`MediatR` 零命中 |
| Behavior 顺序可判定 | 只允许经 `ConduitConfiguration` 注册；外部注册直接 fail-fast |
| Singleton 需审查 | 默认拒绝 `Lifetime = Singleton`，需显式 `AllowSingletonLifetime = true` |
| 并行通知不共享 Scoped | 并发发布时每个处理器在独立作用域解析 |

## 与基准的已知差异

详见 [`docs/FEATURES.md`](./docs/FEATURES.md)。要点：

1. **无许可校验机制**（独立库无此概念）。
2. **无 `SendAsync(IRequest)` 非泛型重载**：`ICommand` 同时实现 `IRequest` 与 `IRequest<Unit>`，
   `SendAsync(command)` 直接推断 `Unit`，避免与非泛型重载产生调用二义性。
3. **`CDI001`（缺 Handler）为 Warning 而非 Error**：Handler 可能位于其他程序集，报错会误伤多程序集解决方案。
4. **`Conduit.DependencyInjection` 不声明 AOT 兼容**：反射扫描在设计上依赖运行时元数据，AOT 走 Mode B。

## 许可

Apache-2.0，见 [`LICENSE`](./LICENSE)。
