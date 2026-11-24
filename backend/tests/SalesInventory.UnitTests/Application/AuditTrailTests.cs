using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Audit;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.UnitTests.Application;

public sealed class AuditableEntitiesTests
{
    [Fact]
    public void AllowList_CoversExactlyTheRequiredEntities()
    {
        AuditableEntities.All.Select(e => e.EntityName).Should().BeEquivalentTo(
            "Products", "Categories", "Sales", "SaleDetails", "StockMovements", "Users");
    }

    [Fact]
    public void AllowList_NeverContainsSensitiveOrTechnicalProperties()
    {
        var forbidden = new Regex("password|hash|token|secret|rowversion|idempotency|signing", RegexOptions.IgnoreCase);

        foreach (var entity in AuditableEntities.All)
        {
            entity.Properties.Should().NotContain(property => forbidden.IsMatch(property), $"{entity.EntityName} no debe auditar datos sensibles");
        }
    }

    [Fact]
    public void AllowList_OnlyReferencesRealPropertiesOfTheEntities()
    {
        foreach (var entity in AuditableEntities.All)
        {
            entity.ClrType.GetProperty(entity.KeyProperty).Should().NotBeNull($"{entity.EntityName}.{entity.KeyProperty}");
            foreach (var property in entity.Properties)
            {
                entity.ClrType.GetProperty(property).Should().NotBeNull($"{entity.EntityName}.{property}");
            }
        }
    }

    [Fact]
    public void Users_AuditOnlyAdministrativeFields()
    {
        AuditableEntities.TryGet(typeof(User), out var users).Should().BeTrue();
        users.Properties.Should().BeEquivalentTo(nameof(User.FullName), nameof(User.Email), nameof(User.Role), nameof(User.IsActive));
    }

    [Fact]
    public void NonAuditableEntities_AreNotInTheList()
    {
        AuditableEntities.TryGet(typeof(IdempotencyRecord), out _).Should().BeFalse();
        AuditableEntities.TryGet(typeof(OutboxMessage), out _).Should().BeFalse();
        AuditableEntities.TryGet(typeof(AuditLog), out _).Should().BeFalse("la auditoría no se audita a sí misma");
    }
}

public sealed class AuditTrailTests
{
    private sealed class FixedActor(AuditActor actor) : IAuditActorProvider
    {
        public AuditActor GetCurrent() => actor;
    }

    private static SalesInventoryDbContext CreateContext(AuditActor? actor = null) =>
        new(new DbContextOptionsBuilder<SalesInventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            new FixedActor(actor ?? new AuditActor(7, AuditActorType.User, AuditSource.API, "corr-1")));

    private static JsonElement Json(string? text) => JsonDocument.Parse(text!).RootElement;

    [Fact]
    public async Task Insert_IsAuditedWithTheGeneratedIdAndTheActorOfTheContext()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Bebidas" };
        context.Categories.Add(category);

        await context.SaveChangesAsync();

        var log = await context.AuditLogs.AsNoTracking().SingleAsync();
        log.EntityName.Should().Be("Categories");
        log.EntityId.Should().Be(category.Id.ToString());
        log.Action.Should().Be(AuditAction.INSERT);
        log.UserId.Should().Be(7);
        log.ActorType.Should().Be(AuditActorType.User);
        log.Source.Should().Be(AuditSource.API);
        log.CorrelationId.Should().Be("corr-1");
        Json(log.NewValues).GetProperty("Name").GetString().Should().Be("Bebidas");
        log.OldValues.Should().BeNull();
    }

    [Fact]
    public async Task Update_RecordsOnlyModifiedAuditableProperties()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Bebidas", IsActive = true };
        context.Categories.Add(category);
        await context.SaveChangesAsync();

        category.Name = "Gaseosas";
        await context.SaveChangesAsync();

        var update = await context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditAction.UPDATE);
        update.ChangedFields.Should().Be("Name");
        Json(update.OldValues).GetProperty("Name").GetString().Should().Be("Bebidas");
        Json(update.NewValues).GetProperty("Name").GetString().Should().Be("Gaseosas");
        Json(update.NewValues).EnumerateObject().Should().ContainSingle();
    }

    [Fact]
    public async Task AssigningTheSameValues_CreatesNoAuditRecord()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Bebidas" };
        context.Categories.Add(category);
        await context.SaveChangesAsync();
        var before = await context.AuditLogs.CountAsync();

        category.Name = "Bebidas";
        category.IsActive = true;
        await context.SaveChangesAsync();

        (await context.AuditLogs.CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Delete_RecordsTheOldValues()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "Bebidas" };
        context.Categories.Add(category);
        await context.SaveChangesAsync();
        var id = category.Id;

        context.Categories.Remove(category);
        await context.SaveChangesAsync();

        var delete = await context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditAction.DELETE);
        delete.EntityId.Should().Be(id.ToString());
        Json(delete.OldValues).GetProperty("Name").GetString().Should().Be("Bebidas");
        delete.NewValues.Should().BeNull();
    }

    [Fact]
    public async Task PasswordHash_IsNeverAudited_AndAPasswordOnlyChangeLeavesNoRecord()
    {
        await using var context = CreateContext();
        var user = new User { FullName = "Ana", Email = "ana@x.pe", PasswordHash = "TOP-SECRET-HASH", Role = UserRole.Admin };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var afterInsert = await context.AuditLogs.CountAsync();

        user.PasswordHash = "ANOTHER-SECRET-HASH";
        await context.SaveChangesAsync();

        (await context.AuditLogs.CountAsync()).Should().Be(afterInsert);
        var everything = string.Join("|", (await context.AuditLogs.AsNoTracking().ToListAsync())
            .Select(a => a.OldValues + a.NewValues + a.ChangedFields));
        everything.Should().NotContain("SECRET").And.NotContainEquivalentOf("PasswordHash");
    }

    [Fact]
    public async Task ProductStockChange_RecordsOnlyStock()
    {
        await using var context = CreateContext();
        var product = new Product { Category = new Category { Name = "C" }, SKU = "P1", Name = "Teclado", Price = 10m, Stock = 5 };
        context.Products.Add(product);
        await context.SaveChangesAsync();

        product.Stock = 3;
        await context.SaveChangesAsync();

        var update = await context.AuditLogs.AsNoTracking().SingleAsync(a => a.EntityName == "Products" && a.Action == AuditAction.UPDATE);
        update.ChangedFields.Should().Be("Stock");
        Json(update.OldValues).GetProperty("Stock").GetInt32().Should().Be(5);
        Json(update.NewValues).GetProperty("Stock").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task SystemActor_NeverCarriesAUserId()
    {
        await using var context = CreateContext(new AuditActor(99, AuditActorType.System, AuditSource.Worker, "w-1"));
        context.Categories.Add(new Category { Name = "Sistema" });

        await context.SaveChangesAsync();

        var log = await context.AuditLogs.AsNoTracking().SingleAsync();
        log.UserId.Should().BeNull();
        log.ActorType.Should().Be(AuditActorType.System);
        log.Source.Should().Be(AuditSource.Worker);
    }

    [Fact]
    public async Task NonAuditableEntities_ProduceNoAuditRecords()
    {
        await using var context = CreateContext();
        context.IdempotencyRecords.Add(new IdempotencyRecord { UserId = 1, Operation = "CreateSale", Key = Guid.NewGuid(), RequestHash = "x", SaleId = 1 });
        context.OutboxMessages.Add(OutboxMessage.CreatePending(Guid.NewGuid(), "t", "{}", DateTime.UtcNow));

        await context.SaveChangesAsync();

        (await context.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ApplicationCodeCannotWriteAuditLogs()
    {
        await using var context = CreateContext();
        context.AuditLogs.Add(new AuditLog { EntityName = "Categories", EntityId = "1", ChangedFields = "x", CorrelationId = "c" });

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*solo lectura*");
    }

    [Fact]
    public async Task SeveralSaves_AuditEachOneWithoutRecursionOrDuplicates()
    {
        await using var context = CreateContext();
        var category = new Category { Name = "A" };
        context.Categories.Add(category);
        await context.SaveChangesAsync();
        category.Name = "B";
        await context.SaveChangesAsync();
        category.Name = "C";
        await context.SaveChangesAsync();

        (await context.AuditLogs.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Action).ToListAsync())
            .Should().Equal(AuditAction.INSERT, AuditAction.UPDATE, AuditAction.UPDATE);
    }
}
