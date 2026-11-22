using Conduit.Abstractions;
using Conduit.Contracts;

namespace Conduit.Samples;

public sealed record ProductDto(string Sku, string Name, decimal Price);

public sealed record GetProductQuery(string Sku) : IQuery<ProductDto>, ICacheable, ICacheKeyProvider
{
    public TimeSpan? CacheDuration => TimeSpan.FromSeconds(30);

    public string GetCacheKey() => "product:" + Sku;
}

public sealed class GetProductQueryHandler(CatalogStore store) : IRequestHandler<GetProductQuery, ProductDto>
{
    public ValueTask<ProductDto> HandleAsync(GetProductQuery request, CancellationToken cancellationToken)
        => ValueTask.FromResult(store.Find(request.Sku) ?? throw new KeyNotFoundException(request.Sku));
}

public sealed record CreateOrderCommand(string Sku, int Quantity) : ICommand, ITransactionalRequest;

public sealed class CreateOrderCommandValidator : IRequestValidator<CreateOrderCommand>
{
    public ValueTask<IReadOnlyList<ValidationError>> ValidateAsync(
        CreateOrderCommand request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ValidationError> errors = request.Quantity <= 0
            ? [new ValidationError(nameof(CreateOrderCommand.Quantity), "Quantity must be positive.", "QUANTITY_INVALID")]
            : [];

        return ValueTask.FromResult(errors);
    }
}

public sealed class CreateOrderCommandHandler(CatalogStore store, IDispatcher dispatcher)
    : IRequestHandler<CreateOrderCommand, Unit>
{
    public async ValueTask<Unit> HandleAsync(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        store.Reserve(request.Sku, request.Quantity);
        await dispatcher.PublishAsync(new OrderPlaced(request.Sku, request.Quantity), cancellationToken);
        return Unit.Value;
    }
}

public sealed record OrderPlaced(string Sku, int Quantity) : INotification;

public sealed class StockNotificationHandler(CatalogStore store) : INotificationHandler<OrderPlaced>
{
    public ValueTask HandleAsync(OrderPlaced notification, CancellationToken cancellationToken)
    {
        Console.WriteLine($"  [通知] 库存已扣减：{notification.Sku} x{notification.Quantity}，余量 {store.Stock(notification.Sku)}");
        return ValueTask.CompletedTask;
    }
}

public sealed class AuditNotificationHandler : INotificationHandler<OrderPlaced>
{
    public async ValueTask HandleAsync(OrderPlaced notification, CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);
        Console.WriteLine($"  [通知] 审计已记录：{notification.Sku} x{notification.Quantity}");
    }
}

public sealed record BrowseProductsStream(int Take) : IStreamRequest<ProductDto>;

public sealed class BrowseProductsStreamHandler(CatalogStore store) : IStreamRequestHandler<BrowseProductsStream, ProductDto>
{
    public async IAsyncEnumerable<ProductDto> HandleStreamAsync(
        BrowseProductsStream request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var product in store.All().Take(request.Take))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return product;
        }
    }
}

/// <summary>演示用的进程内"数据库"。</summary>
public sealed class CatalogStore
{
    private readonly Dictionary<string, ProductDto> _products = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SKU-1"] = new("SKU-1", "机械键盘", 899m),
        ["SKU-2"] = new("SKU-2", "人体工学椅", 2499m),
        ["SKU-3"] = new("SKU-3", "4K 显示器", 3299m),
    };

    private readonly Dictionary<string, int> _stock = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SKU-1"] = 12,
        ["SKU-2"] = 4,
        ["SKU-3"] = 7,
    };

    public int HandlerHits { get; private set; }

    public ProductDto? Find(string sku)
    {
        HandlerHits++;
        return _products.GetValueOrDefault(sku);
    }

    public IEnumerable<ProductDto> All() => _products.Values;

    public void Reserve(string sku, int quantity)
    {
        if (_stock.TryGetValue(sku, out var available) && available >= quantity)
        {
            _stock[sku] = available - quantity;
        }
    }

    public int Stock(string sku) => _stock.GetValueOrDefault(sku);
}

/// <summary>演示用的"事务"实现（仅打印边界）。</summary>
public sealed class ConsoleTransactionScopeFactory : ITransactionScopeFactory
{
    public ValueTask<ITransactionScope> BeginAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("  [事务] BEGIN");
        return ValueTask.FromResult<ITransactionScope>(new Scope());
    }

    private sealed class Scope : ITransactionScope
    {
        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("  [事务] COMMIT");
            return ValueTask.CompletedTask;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("  [事务] ROLLBACK");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
