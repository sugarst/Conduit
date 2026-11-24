# HandlerR.FluentValidation

为 .NET 10 应用接入 FluentValidation。HandlerR 在调用请求处理器前运行已注册的 `IValidator<TRequest>`。校验失败时抛出 `RequestValidationException`，其 `Errors` 包含字段名、错误消息和错误码。

## 安装

以下命令创建独立的 .NET 10 控制台示例。已有 DI 容器的宿主可直接从安装适配包开始。

```bash
dotnet new console -n HandlerR.ValidationExample -f net10.0
cd HandlerR.ValidationExample
dotnet add package HandlerR.FluentValidation --version 1.0.0
dotnet add package Microsoft.Extensions.DependencyInjection --version 10.0.15
```

安装适配包时会一并还原 `HandlerR` 和 `FluentValidation`。

## 注册

将以下内容保存为 `Program.cs`。它在宿主注册校验器，并在 `AddHandlerR` 的配置回调中启用适配器。

```csharp
using FluentValidation;
using HandlerR.Abstractions;
using HandlerR.Contracts;
using HandlerR.DependencyInjection;
using HandlerR.FluentValidation;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddScoped<IValidator<CreateOrder>, CreateOrderValidator>();
services.AddHandlerR(configure =>
{
    configure.RegisterFromAssemblyContaining<CreateOrderHandler>();
    configure.AddFluentValidation();
});

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
await using var scope = provider.CreateAsyncScope();
var sender = scope.ServiceProvider.GetRequiredService<ISender>();
Console.WriteLine(await sender.SendAsync(new CreateOrder(2)));

try
{
    await sender.SendAsync(new CreateOrder(0));
}
catch (RequestValidationException exception)
{
    foreach (var error in exception.Errors)
        Console.WriteLine($"{error.Identifier}: {error.ErrorCode}");
}

public sealed record CreateOrder(int Quantity) : IRequest<int>;

public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, int>
{
    public ValueTask<int> HandleAsync(CreateOrder request, CancellationToken cancellationToken)
        => ValueTask.FromResult(request.Quantity);
}

public sealed class CreateOrderValidator : AbstractValidator<CreateOrder>
{
    public CreateOrderValidator()
        => RuleFor(order => order.Quantity).GreaterThan(0).WithErrorCode("QUANTITY_INVALID");
}
```

运行 `dotnet run`。第一行输出 `2`。第二次调用会在进入处理器前触发校验，输出 `Quantity: QUANTITY_INVALID`。

`AddFluentValidation()` 会注册校验行为和适配器。此接入方式适用于 JIT 宿主。 [主包注册与 Native AOT 边界](https://github.com/sugarst/HandlerR/blob/main/src/HandlerR/README.md#native-aot)
