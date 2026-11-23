# HandlerR.FluentValidation

为 .NET 10 应用接入 FluentValidation。HandlerR 在调用请求处理器前运行已注册的 `IValidator<TRequest>`。校验失败时抛出 `RequestValidationException`，其 `Errors` 包含字段名、错误消息和错误码。

## 安装

```bash
dotnet add package HandlerR.FluentValidation
```

安装适配包时会一并还原 `HandlerR` 和 `FluentValidation`。

## 注册

在宿主的 `IServiceCollection` 中注册校验器，并在 `AddHandlerR` 的配置回调中启用适配器。这里假设 `CreateOrderHandler` 是应用已有的请求处理器。

```csharp
using FluentValidation;
using HandlerR.DependencyInjection;
using HandlerR.FluentValidation;
using Microsoft.Extensions.DependencyInjection;

services.AddScoped<IValidator<CreateOrder>, CreateOrderValidator>();
services.AddHandlerR(configure =>
{
    configure.RegisterFromAssemblyContaining<CreateOrderHandler>();
    configure.AddFluentValidation();
});

public sealed class CreateOrderValidator : AbstractValidator<CreateOrder>
{
    public CreateOrderValidator() => RuleFor(order => order.Quantity).GreaterThan(0);
}
```

`AddFluentValidation()` 会注册 HandlerR 的校验行为和适配器。此接入方式使用 `AddHandlerR` 的运行时注册路径，适用于 JIT 宿主。
