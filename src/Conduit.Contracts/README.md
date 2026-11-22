# Conduit.Contracts

消息形状契约包：零依赖、单一日标框架 `net10.0`。

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
Conduit.Contracts  ←  Conduit.Abstractions  ←  Conduit.Core  ←  Conduit.DependencyInjection
```

本包**不含** Handler、Behavior、DI 或分发逻辑，也**不做**类型转发（没有历史包袱需要兼容）。

## 安装

```bash
dotnet add package Conduit.Contracts
```

要求消费方项目同样面向 `net10.0`。

## 许可

Apache-2.0。本库为独立实现，与 MediatR 无任何隶属或派生关系。
