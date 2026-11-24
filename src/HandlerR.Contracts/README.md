# HandlerR.Contracts

为 .NET 10 共享项目提供零依赖的消息契约。

```
IBaseRequest
IRequest                        // 无响应请求标记
IRequest<TResponse>             // 有响应请求
INotification                   // 一对多通知
IStreamRequest<TResponse>       // 流式请求
Unit                            // 无值响应占位符
```

## 为什么单独拆包

API / gRPC / Blazor 等契约项目常常只需要"消息长什么样"，不需要调度器、DI 与可观测性代码。
把这些标记接口与 `Unit` 独立成包后，契约项目只引用本包即可，依赖图保持单向直线：

```
HandlerR.Contracts  ←  HandlerR.Abstractions  ←  HandlerR.Core  ←  HandlerR.DependencyInjection
```

本包**不含** Handler、Behavior、DI 或分发逻辑，也**不做**类型转发（没有历史包袱需要兼容）。

## 安装

```bash
dotnet add package HandlerR.Contracts --version 1.0.0
```

在面向 `net10.0` 的共享类库里定义消息和响应 DTO。

```csharp
using HandlerR.Contracts;

public sealed record GetProduct(string Sku) : IRequest<ProductDto>;
public sealed record ProductDto(string Sku, decimal Price);
public sealed record OrderPlaced(int OrderId) : INotification;
public sealed record BrowseProducts(int Take) : IStreamRequest<ProductDto>;
```

宿主引用共享类库和 `HandlerR` 主包，在宿主或业务模块中实现这些消息的处理器，并向 DI 容器注册。主包会自动还原本契约包。 [主包使用说明](https://github.com/sugarst/HandlerR/blob/main/src/HandlerR/README.md)

## 许可

Apache-2.0。本库为独立实现，与 MediatR 无任何隶属或派生关系。
