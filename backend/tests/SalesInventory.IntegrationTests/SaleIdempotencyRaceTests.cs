using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Common.Exceptions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Application.Sales.DTOs;
using SalesInventory.Application.Sales.Services;
using SalesInventory.Domain.Entities;

namespace SalesInventory.IntegrationTests;

/// <summary>
/// Carreras deterministas sobre SQL Server real: un gancho confirma la venta «ganadora» con otro contexto en
/// el instante exacto en que la solicitud «perdedora» aún no ha escrito nada.
/// </summary>
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class SaleIdempotencyRaceTests(SqlServerConcurrencyDatabaseFixture database)
{
    /// <summary>Delegado de IApplicationDbContext que ejecuta una acción la primera vez que se accede a un DbSet.</summary>
    private sealed class HookedContext(IApplicationDbContext inner) : IApplicationDbContext
    {
        public Action? OnFirstProductsAccess { get; set; }
        public Action? OnFirstSalesAccess { get; set; }

        public DbSet<User> Users => inner.Users;
        public DbSet<Category> Categories => inner.Categories;
        public DbSet<Product> Products
        {
            get
            {
                var hook = OnFirstProductsAccess;
                OnFirstProductsAccess = null;
                hook?.Invoke();
                return inner.Products;
            }
        }

        public DbSet<Sale> Sales
        {
            get
            {
                var hook = OnFirstSalesAccess;
                OnFirstSalesAccess = null;
                hook?.Invoke();
                return inner.Sales;
            }
        }

        public DbSet<SaleDetail> SaleDetails => inner.SaleDetails;
        public DbSet<StockMovement> StockMovements => inner.StockMovements;
        public DbSet<IdempotencyRecord> IdempotencyRecords => inner.IdempotencyRecords;
        public DbSet<OutboxMessage> OutboxMessages => inner.OutboxMessages;
        public DbSet<ProcessedEvent> ProcessedEvents => inner.ProcessedEvents;
        public DbSet<Notification> Notifications => inner.Notifications;
        public IQueryable<AuditLog> AuditLogs => inner.AuditLogs;
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => inner.SaveChangesAsync(cancellationToken);
        public void ClearChangeTracker() => inner.ClearChangeTracker();
        public bool IsDuplicateIdempotencyKey(DbUpdateException exception) => inner.IsDuplicateIdempotencyKey(exception);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) =>
            inner.ExecuteInTransactionAsync(operation, cancellationToken);
    }

    private static CreateSaleRequestDto Request(int userId, Guid key, int productId, int quantity) => new()
    {
        UserId = userId,
        IdempotencyKey = key,
        Items = [new SaleItemRequestDto { ProductId = productId, Quantity = quantity }]
    };

    /// <summary>Confirma como «ganadora» una venta con su registro de idempotencia, sin tocar el stock.</summary>
    private async Task<int> CommitCompetingSaleAsync(int userId, Guid key, int productId, int quantity, int hashedQuantity)
    {
        await using var context = database.CreateDbContext();
        var sale = new Sale { UserId = userId, SaleDate = DateTime.UtcNow, Total = 10m };
        context.IdempotencyRecords.Add(new IdempotencyRecord
        {
            UserId = userId,
            Operation = IdempotencyOperations.CreateSale,
            Key = key,
            RequestHash = SaleRequestHash.Compute(
                [new SaleItemRequestDto { ProductId = productId, Quantity = hashedQuantity }]),
            Sale = sale
        });
        context.SaleDetails.Add(new SaleDetail { Sale = sale, ProductId = productId, Quantity = quantity, UnitPrice = 10m, Subtotal = 10m * quantity });
        await context.SaveChangesAsync();
        return sale.Id;
    }

    [Fact]
    public async Task UniqueIndexViolation_WithSameContent_RollsBackAndReturnsTheWinningSale()
    {
        var seeded = await database.SeedProductsAsync(5);
        var key = Guid.NewGuid();
        await using var context = database.CreateDbContext();
        var hooked = new HookedContext(context);
        var winningSaleId = 0;
        hooked.OnFirstSalesAccess = () =>
            winningSaleId = CommitCompetingSaleAsync(seeded.UserId, key, seeded.ProductIds[0], 1, 1).GetAwaiter().GetResult();

        var result = await new SaleService(hooked).CreateAsync(Request(seeded.UserId, key, seeded.ProductIds[0], 1));

        result.Id.Should().Be(winningSaleId);
        await using var check = database.CreateDbContext();
        (await check.Sales.CountAsync(s => s.UserId == seeded.UserId)).Should().Be(1);
        (await check.StockMovements.CountAsync(m => m.ProductId == seeded.ProductIds[0])).Should().Be(0, "el intento perdedor se revierte por completo");
        (await check.Products.Where(p => p.Id == seeded.ProductIds[0]).Select(p => p.Stock).SingleAsync()).Should().Be(5);
    }

    [Fact]
    public async Task UniqueIndexViolation_WithDifferentContent_ThrowsConflict()
    {
        var seeded = await database.SeedProductsAsync(5);
        var key = Guid.NewGuid();
        await using var context = database.CreateDbContext();
        var hooked = new HookedContext(context)
        {
            OnFirstSalesAccess = () =>
                CommitCompetingSaleAsync(seeded.UserId, key, seeded.ProductIds[0], 2, 2).GetAwaiter().GetResult()
        };

        var act = async () => await new SaleService(hooked).CreateAsync(Request(seeded.UserId, key, seeded.ProductIds[0], 1));

        await act.Should().ThrowAsync<ConflictException>();
        await using var check = database.CreateDbContext();
        (await check.Sales.CountAsync(s => s.UserId == seeded.UserId)).Should().Be(1);
        (await check.Products.Where(p => p.Id == seeded.ProductIds[0]).Select(p => p.Stock).SingleAsync()).Should().Be(5);
    }

    [Fact]
    public async Task StockExhaustedByTheWinner_SameContent_ReplaysInsteadOfFailingWithInsufficientStock()
    {
        var seeded = await database.SeedProductsAsync(1);
        var key = Guid.NewGuid();
        await using var context = database.CreateDbContext();
        var hooked = new HookedContext(context);
        var winningSaleId = 0;
        hooked.OnFirstProductsAccess = () =>
        {
            winningSaleId = CommitCompetingSaleAsync(seeded.UserId, key, seeded.ProductIds[0], 1, 1).GetAwaiter().GetResult();
            using var other = database.CreateDbContext();
            other.Products.Where(p => p.Id == seeded.ProductIds[0]).ExecuteUpdate(s => s.SetProperty(p => p.Stock, 0));
        };

        var result = await new SaleService(hooked).CreateAsync(Request(seeded.UserId, key, seeded.ProductIds[0], 1));

        result.Id.Should().Be(winningSaleId);
    }

    [Fact]
    public async Task StockExhaustedByTheWinner_DifferentContent_ThrowsConflict()
    {
        var seeded = await database.SeedProductsAsync(1);
        var key = Guid.NewGuid();
        await using var context = database.CreateDbContext();
        var hooked = new HookedContext(context)
        {
            OnFirstProductsAccess = () =>
            {
                CommitCompetingSaleAsync(seeded.UserId, key, seeded.ProductIds[0], 1, 1).GetAwaiter().GetResult();
                using var other = database.CreateDbContext();
                other.Products.Where(p => p.Id == seeded.ProductIds[0]).ExecuteUpdate(s => s.SetProperty(p => p.Stock, 0));
            }
        };

        var act = async () => await new SaleService(hooked).CreateAsync(Request(seeded.UserId, key, seeded.ProductIds[0], 1 + 1));

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task InsufficientStock_WithoutAnyRecord_KeepsTheOriginalError()
    {
        var seeded = await database.SeedProductsAsync(1);
        await using var context = database.CreateDbContext();

        var act = async () => await new SaleService(context)
            .CreateAsync(Request(seeded.UserId, Guid.NewGuid(), seeded.ProductIds[0], 5));

        await act.Should().ThrowAsync<SalesInventory.Domain.Exceptions.InsufficientStockException>();
    }

    [Fact]
    public void RequestHash_IsOrderIndependent_ButSensitiveToQuantitiesAndRepeatedLines()
    {
        SaleItemRequestDto Item(int product, int quantity) => new() { ProductId = product, Quantity = quantity };

        SaleRequestHash.Compute([Item(1, 2), Item(2, 3)])
            .Should().Be(SaleRequestHash.Compute([Item(2, 3), Item(1, 2)]));
        SaleRequestHash.Compute([Item(1, 2)]).Should().NotBe(SaleRequestHash.Compute([Item(1, 3)]));
        SaleRequestHash.Compute([Item(1, 1), Item(1, 1)]).Should().NotBe(SaleRequestHash.Compute([Item(1, 2)]));
        SaleRequestHash.Compute([Item(1, 1), Item(1, 2)]).Should().Be(SaleRequestHash.Compute([Item(1, 2), Item(1, 1)]));
    }

    [Fact]
    public async Task SimultaneousRequests_WithSameKeyButDifferentContent_YieldOneSaleAndOneConflict()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var seeded = await database.SeedProductsAsync(10);
            var key = Guid.NewGuid();

            var responses = await Task.WhenAll(
                database.PostSaleAsync(new { items = new[] { new { productId = seeded.ProductIds[0], quantity = 1 } } }, key),
                database.PostSaleAsync(new { items = new[] { new { productId = seeded.ProductIds[0], quantity = 2 } } }, key));
            using var a = new DisposableResponses(responses);

            a.Statuses.Should().BeEquivalentTo(new[] { System.Net.HttpStatusCode.Created, System.Net.HttpStatusCode.Conflict });
            await using var check = database.CreateDbContext();
            (await check.Sales.CountAsync(s => s.UserId == seeded.UserId)).Should().Be(1);
            (await check.IdempotencyRecords.CountAsync(r => r.Key == key)).Should().Be(1);
        }
    }

    private sealed class DisposableResponses(HttpResponseMessage[] responses) : IDisposable
    {
        public IEnumerable<System.Net.HttpStatusCode> Statuses => responses.Select(r => r.StatusCode);
        public void Dispose()
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
