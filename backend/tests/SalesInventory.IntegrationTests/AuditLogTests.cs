using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SalesInventory.Application.Audit;
using SalesInventory.Application.Common.Models;
using SalesInventory.Application.Inventory.DTOs;
using SalesInventory.Application.Sales.DTOs;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Auditing;

namespace SalesInventory.IntegrationTests;

/// <summary>Auditoría transversal (transaccional y de solo lectura) contra SQL Server real.</summary>
[Collection(SqlServerConcurrencyCollection.Name)]
public sealed class AuditLogTests(SqlServerConcurrencyDatabaseFixture database)
{
    private static readonly string[] BusinessEntities = ["Sales", "SaleDetails", "StockMovements", "Products"];

    private async Task<int> AuthenticateAdminAsync()
    {
        var seeded = await database.SeedProductsAsync(1);
        database.AuthenticateAs(seeded.UserId, nameof(UserRole.Admin));
        return seeded.UserId;
    }

    private async Task<List<AuditLog>> AuditsAsync(string entityName, string entityId)
    {
        await using var context = database.CreateDbContext();
        return await context.AuditLogs.AsNoTracking()
            .Where(a => a.EntityName == entityName && a.EntityId == entityId)
            .OrderBy(a => a.Id)
            .ToListAsync();
    }

    private async Task<int> CountAuditsAsync(params string[] entityNames)
    {
        await using var context = database.CreateDbContext();
        return await context.AuditLogs.CountAsync(a => entityNames.Contains(a.EntityName));
    }

    private async Task<int> CreateCategoryAsync(string? name = null)
    {
        using var response = await database.Client.PostAsJsonAsync("/api/categories", new { name = name ?? $"Aud-{Guid.NewGuid():N}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<SalesInventory.Application.Categories.DTOs.CategoryResponseDto>();
        return created!.Id;
    }

    private static JsonElement Json(string? text) => JsonDocument.Parse(text!).RootElement;

    // ─── INSERT / UPDATE / DELETE ────────────────────────────────────────────

    [Fact]
    public async Task Insert_RecordsTheDefinitiveSqlServerIdentifier_ActorAndNewValues()
    {
        var adminId = await AuthenticateAdminAsync();
        var name = $"Aud-{Guid.NewGuid():N}";

        var id = await CreateCategoryAsync(name);

        var audits = await AuditsAsync("Categories", id.ToString());
        var insert = audits.Should().ContainSingle().Subject;
        insert.Action.Should().Be(AuditAction.INSERT);
        insert.EntityId.Should().Be(id.ToString(), "el Id es el definitivo asignado por SQL Server, no uno temporal");
        insert.OldValues.Should().BeNull();
        Json(insert.NewValues).GetProperty("Name").GetString().Should().Be(name);
        Json(insert.NewValues).GetProperty("IsActive").GetBoolean().Should().BeTrue();
        insert.ChangedFields.Should().Be("Name,IsActive");
        insert.UserId.Should().Be(adminId);
        insert.ActorType.Should().Be(AuditActorType.User);
        insert.Source.Should().Be(AuditSource.API);
        insert.CorrelationId.Should().NotBeNullOrWhiteSpace();
        insert.OccurredAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Update_RecordsOnlyTheModifiedProperties_WithOldAndNewValues()
    {
        await AuthenticateAdminAsync();
        var oldName = $"Aud-{Guid.NewGuid():N}";
        var newName = $"Aud-{Guid.NewGuid():N}";
        var id = await CreateCategoryAsync(oldName);

        using var response = await database.Client.PutAsJsonAsync($"/api/categories/{id}", new { name = newName, isActive = true });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var update = (await AuditsAsync("Categories", id.ToString())).Single(a => a.Action == AuditAction.UPDATE);
        update.ChangedFields.Should().Be("Name", "IsActive no cambió y no debe figurar");
        Json(update.OldValues).EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("Name");
        Json(update.OldValues).GetProperty("Name").GetString().Should().Be(oldName);
        Json(update.NewValues).GetProperty("Name").GetString().Should().Be(newName);
    }

    [Fact]
    public async Task SavingWithoutRealChanges_DoesNotCreateAuditRecords()
    {
        await AuthenticateAdminAsync();
        var name = $"Aud-{Guid.NewGuid():N}";
        var id = await CreateCategoryAsync(name);

        using var response = await database.Client.PutAsJsonAsync($"/api/categories/{id}", new { name, isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditsAsync("Categories", id.ToString())).Should().ContainSingle(a => a.Action == AuditAction.INSERT)
            .And.HaveCount(1, "actualizar con los mismos valores no genera auditoría");
    }

    [Fact]
    public async Task Delete_RecordsTheLastKnownValues()
    {
        await AuthenticateAdminAsync();
        var name = $"Aud-{Guid.NewGuid():N}";
        var id = await CreateCategoryAsync(name);

        using var response = await database.Client.DeleteAsync($"/api/categories/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var delete = (await AuditsAsync("Categories", id.ToString())).Single(a => a.Action == AuditAction.DELETE);
        Json(delete.OldValues).GetProperty("Name").GetString().Should().Be(name);
        delete.NewValues.Should().BeNull();
    }

    [Fact]
    public async Task InventoryEntry_AuditsTheStockChangeAndTheMovementWithItsDefinitiveId()
    {
        var seeded = await database.SeedProductsAsync(5);
        database.AuthenticateAs(seeded.UserId, nameof(UserRole.Admin));

        using var response = await database.Client.PostAsJsonAsync("/api/inventory/entries",
            new { productId = seeded.ProductIds[0], quantity = 3, reason = "Auditoría de entrada" });
        var movement = await response.Content.ReadFromJsonAsync<StockMovementResponseDto>();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var productAudit = (await AuditsAsync("Products", seeded.ProductIds[0].ToString())).Last(a => a.Action == AuditAction.UPDATE);
        productAudit.ChangedFields.Should().Be("Stock");
        Json(productAudit.OldValues).GetProperty("Stock").GetInt32().Should().Be(5);
        Json(productAudit.NewValues).GetProperty("Stock").GetInt32().Should().Be(8);

        var movementAudit = (await AuditsAsync("StockMovements", movement!.Id.ToString())).Should().ContainSingle().Subject;
        movementAudit.Action.Should().Be(AuditAction.INSERT);
        Json(movementAudit.NewValues).GetProperty("Quantity").GetInt32().Should().Be(3);
        productAudit.CorrelationId.Should().Be(movementAudit.CorrelationId, "ambos cambios pertenecen a la misma solicitud");
    }

    // ─── Datos sensibles ─────────────────────────────────────────────────────

    [Fact]
    public async Task SensitiveProperties_AreNeverAudited_AndPasswordOnlyChangesLeaveNoRecord()
    {
        const string secretHash = "SECRET-HASH-DO-NOT-LEAK";
        int userId;
        await using (var context = database.CreateDbContext())
        {
            var user = new User
            {
                FullName = "Usuario Auditado",
                Email = $"audit-{Guid.NewGuid():N}@example.com",
                PasswordHash = secretHash,
                Role = UserRole.Seller,
                IsActive = true
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            userId = user.Id;

            user.PasswordHash = "OTHER-SECRET-HASH";
            await context.SaveChangesAsync();   // solo cambia la contraseña: no debe auditarse

            user.FullName = "Usuario Renombrado";
            await context.SaveChangesAsync();
        }

        var audits = await AuditsAsync("Users", userId.ToString());
        audits.Select(a => a.Action).Should().Equal(AuditAction.INSERT, AuditAction.UPDATE);
        audits[1].ChangedFields.Should().Be("FullName");
        foreach (var audit in audits)
        {
            (audit.OldValues + audit.NewValues + audit.ChangedFields).Should()
                .NotContainEquivalentOf("hash").And.NotContainEquivalentOf("password").And.NotContain("SECRET");
        }

        Json(audits[0].NewValues).EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("FullName", "Email", "Role", "IsActive");
    }

    [Fact]
    public async Task IdempotencyKeysAndRequestHashes_NeverReachTheAuditTrail()
    {
        var seeded = await database.SeedProductsAsync(5);
        var key = Guid.NewGuid();

        using var response = await database.PostSaleAsync(
            new { items = new[] { new { productId = seeded.ProductIds[0], quantity = 1 } } }, key);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        await using var context = database.CreateDbContext();
        var record = await context.IdempotencyRecords.AsNoTracking().SingleAsync(r => r.Key == key);
        var everything = await context.AuditLogs.AsNoTracking().Select(a => a.OldValues + a.NewValues + a.EntityName).ToListAsync();
        string.Join("|", everything).Should().NotContain(key.ToString()).And.NotContain(record.RequestHash);
        (await context.AuditLogs.AnyAsync(a => a.EntityName == "IdempotencyRecords" || a.EntityName == "OutboxMessages")).Should().BeFalse();
    }

    // ─── Actor ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Actor_ComesFromTheAuthenticatedPrincipal_NeverFromClientValues()
    {
        var seeded = await database.SeedProductsAsync(5);
        database.AuthenticateAs(seeded.UserId, nameof(UserRole.Seller));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sales")
        {
            Content = JsonContent.Create(new
            {
                userId = int.MaxValue,
                items = new[] { new { productId = seeded.ProductIds[0], quantity = 1 } }
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-User-Id", "999999");
        request.Headers.Add("X-Correlation-Id", "cliente-intenta-fijarlo");

        using var response = await database.Client.SendAsync(request);
        var sale = await response.Content.ReadFromJsonAsync<SaleResponseDto>();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var saleAudit = (await AuditsAsync("Sales", sale!.Id.ToString())).Should().ContainSingle().Subject;
        saleAudit.UserId.Should().Be(seeded.UserId);
        saleAudit.ActorType.Should().Be(AuditActorType.User);
        saleAudit.CorrelationId.Should().NotBe("cliente-intenta-fijarlo");
    }

    [Fact]
    public async Task OperationsWithoutAnAuthenticatedUser_AreRecordedAsSystem_AndTheWorkerActorAsSystemWorker()
    {
        string systemEntityId;
        await using (var context = database.CreateDbContext())
        {
            var category = new Category { Name = $"Sys-{Guid.NewGuid():N}" };
            context.Categories.Add(category);
            await context.SaveChangesAsync();
            systemEntityId = category.Id.ToString();
        }

        string workerEntityId;
        await using (var context = database.CreateDbContext(new WorkerAuditActorProvider()))
        {
            var category = new Category { Name = $"Wrk-{Guid.NewGuid():N}" };
            context.Categories.Add(category);
            await context.SaveChangesAsync();
            workerEntityId = category.Id.ToString();
        }

        var system = (await AuditsAsync("Categories", systemEntityId)).Single();
        system.ActorType.Should().Be(AuditActorType.System);
        system.UserId.Should().BeNull();
        system.Source.Should().Be(AuditSource.API);

        var worker = (await AuditsAsync("Categories", workerEntityId)).Single();
        worker.ActorType.Should().Be(AuditActorType.System);
        worker.UserId.Should().BeNull();
        worker.Source.Should().Be(AuditSource.Worker);
        worker.CorrelationId.Should().NotBeNullOrWhiteSpace();
    }

    // ─── Atomicidad ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FailedBusinessOperation_LeavesNoAuditRecords()
    {
        var seeded = await database.SeedProductsAsync(4);
        var before = await CountAuditsAsync(BusinessEntities);

        database.FailNextMovementInsert();
        using var response = await database.PostSaleAsync(
            new { items = new[] { new { productId = seeded.ProductIds[0], quantity = 2 } } });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await CountAuditsAsync(BusinessEntities)).Should().Be(before, "negocio y auditoría se revierten juntos");
    }

    [Fact]
    public async Task FailedAuditInsert_RollsBackTheBusinessOperationToo()
    {
        await AuthenticateAdminAsync();
        var name = $"Aud-{Guid.NewGuid():N}";
        var before = await CountAuditsAsync("Categories");

        database.FailNextAuditInsert();
        using var response = await database.Client.PostAsJsonAsync("/api/categories", new { name });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await using var context = database.CreateDbContext();
        (await context.Categories.AnyAsync(c => c.Name == name)).Should().BeFalse("sin auditoría no se confirma el negocio");
        (await CountAuditsAsync("Categories")).Should().Be(before);
    }

    [Fact]
    public async Task IdempotentReplay_DoesNotDuplicateAuditRecords()
    {
        var seeded = await database.SeedProductsAsync(5);
        var key = Guid.NewGuid();
        var body = new { items = new[] { new { productId = seeded.ProductIds[0], quantity = 2 } } };

        using var first = await database.PostSaleAsync(body, key);
        var sale = await first.Content.ReadFromJsonAsync<SaleResponseDto>();
        var afterFirst = await CountAuditsAsync(BusinessEntities);
        using var replay = await database.PostSaleAsync(body, key);

        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        (await CountAuditsAsync(BusinessEntities)).Should().Be(afterFirst, "el replay no escribe nada");
        (await AuditsAsync("Sales", sale!.Id.ToString())).Should().ContainSingle();
        (await AuditsAsync("Products", seeded.ProductIds[0].ToString())).Count(a => a.Action == AuditAction.UPDATE).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequests_LeaveASingleSetOfAuditRecords()
    {
        var seeded = await database.SeedProductsAsync(5);
        var key = Guid.NewGuid();
        var body = new { items = new[] { new { productId = seeded.ProductIds[0], quantity = 1 } } };

        var responses = await Task.WhenAll(
            database.PostSaleAsync(body, key), database.PostSaleAsync(body, key), database.PostSaleAsync(body, key));
        try
        {
            responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);
            (await AuditsAsync("Products", seeded.ProductIds[0].ToString())).Count(a => a.Action == AuditAction.UPDATE).Should().Be(1);
            await using var context = database.CreateDbContext();
            (await context.AuditLogs.CountAsync(a => a.EntityName == "Sales"
                && a.NewValues!.Contains($"\"UserId\":{seeded.UserId},"))).Should().Be(1);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task MultipleSaveChangesInOneTransaction_AreAuditedAtomically()
    {
        string committedId;
        await using (var context = database.CreateDbContext())
        {
            committedId = await context.ExecuteInTransactionAsync(async token =>
            {
                var category = new Category { Name = $"Multi-{Guid.NewGuid():N}" };
                context.Categories.Add(category);
                await context.SaveChangesAsync(token);       // 1.er guardado: INSERT con Id definitivo
                category.Name += "-renombrada";
                await context.SaveChangesAsync(token);       // 2.º guardado: UPDATE
                return category.Id.ToString();
            });
        }

        var audits = await AuditsAsync("Categories", committedId);
        audits.Select(a => a.Action).Should().Equal(AuditAction.INSERT, AuditAction.UPDATE);
        audits[0].EntityId.Should().Be(committedId);

        string? rolledBackName = null;
        var act = async () =>
        {
            await using var context = database.CreateDbContext();
            await context.ExecuteInTransactionAsync<int>(async token =>
            {
                rolledBackName = $"Rollback-{Guid.NewGuid():N}";
                context.Categories.Add(new Category { Name = rolledBackName });
                await context.SaveChangesAsync(token);
                throw new InvalidOperationException("fallo posterior al guardado");
            });
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
        await using var check = database.CreateDbContext();
        (await check.Categories.AnyAsync(c => c.Name == rolledBackName)).Should().BeFalse();
        (await check.AuditLogs.AnyAsync(a => a.EntityName == "Categories" && a.NewValues!.Contains(rolledBackName!))).Should().BeFalse();
    }

    // ─── Solo lectura desde la aplicación ────────────────────────────────────

    [Fact]
    public async Task TheApplicationCannotCreateEditOrDeleteAuditRecords()
    {
        await AuthenticateAdminAsync();
        await CreateCategoryAsync();

        await using var context = database.CreateDbContext();
        context.AuditLogs.Add(new AuditLog { EntityName = "Categories", EntityId = "1", ChangedFields = "x", CorrelationId = "c" });
        var insert = async () => await context.SaveChangesAsync();
        await insert.Should().ThrowAsync<InvalidOperationException>().WithMessage("*solo lectura*");

        await using var other = database.CreateDbContext();
        var existing = await other.AuditLogs.FirstAsync();
        existing.ChangedFields = "manipulado";
        var update = async () => await other.SaveChangesAsync();
        await update.Should().ThrowAsync<InvalidOperationException>().WithMessage("*solo lectura*");

        await using var third = database.CreateDbContext();
        third.AuditLogs.Remove(await third.AuditLogs.FirstAsync());
        var delete = async () => await third.SaveChangesAsync();
        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*solo lectura*");
    }

    // ─── API: autorización, filtros, paginación, detalle ─────────────────────

    [Theory]
    [InlineData("/api/audit-logs")]
    [InlineData("/api/audit-logs/1")]
    public async Task Endpoints_Return401WithoutTokenAnd403ForSellers(string path)
    {
        var seeded = await database.SeedProductsAsync(1);
        using var anonymousClient = database.CreateAnonymousClient();
        using var anonymous = await anonymousClient.GetAsync(path);
        database.AuthenticateAs(seeded.UserId, nameof(UserRole.Seller));
        using var seller = await database.Client.GetAsync(path);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        seller.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("POST", "/api/audit-logs")]
    [InlineData("PUT", "/api/audit-logs/1")]
    [InlineData("PATCH", "/api/audit-logs/1")]
    [InlineData("DELETE", "/api/audit-logs/1")]
    public async Task NoEndpointAllowsWritingAuditRecords_EvenForAdmins(string method, string path)
    {
        await AuthenticateAdminAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };

        using var response = await database.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Admin_CanFilterPaginateAndReadSafeDetails()
    {
        var adminId = await AuthenticateAdminAsync();
        var name = $"Aud-{Guid.NewGuid():N}";
        var id = await CreateCategoryAsync(name);
        using var rename = await database.Client.PutAsJsonAsync($"/api/categories/{id}", new { name = name + "-v2", isActive = true });
        rename.EnsureSuccessStatusCode();
        var correlation = (await AuditsAsync("Categories", id.ToString())).First().CorrelationId;

        var byEntity = await GetPageAsync($"?entityName=Categories&entityId={id}");
        byEntity.TotalItems.Should().Be(2);
        byEntity.Items.Select(i => i.Action).Should().Equal("UPDATE", "INSERT");   // más recientes primero

        (await GetPageAsync($"?entityName=Categories&entityId={id}&action=update")).Items.Should().ContainSingle()
            .Which.ChangedFields.Should().Equal("Name");
        (await GetPageAsync($"?entityId={id}&userId={adminId}")).TotalItems.Should().BeGreaterThanOrEqualTo(2);
        (await GetPageAsync($"?correlationId={correlation}")).Items.Should().OnlyContain(i => i.CorrelationId == correlation);
        // Las fechas se envían como hora UTC sin sufijo «Z» (convención de la API para evitar conversiones de zona).
        string UtcWallClock(TimeSpan offset) => Uri.EscapeDataString(DateTime.UtcNow.Add(offset).ToString("yyyy-MM-dd'T'HH:mm:ss.fff"));
        (await GetPageAsync($"?entityName=Categories&entityId={id}&from={UtcWallClock(TimeSpan.FromHours(1))}"))
            .Items.Should().BeEmpty();
        (await GetPageAsync($"?entityName=Categories&entityId={id}&to={UtcWallClock(TimeSpan.FromHours(1))}"))
            .Items.Should().HaveCount(2);
        (await GetPageAsync($"?entityName=Categories&entityId={id}&from={UtcWallClock(TimeSpan.FromHours(-1))}"))
            .Items.Should().HaveCount(2);

        var firstPage = await GetPageAsync($"?entityName=Categories&entityId={id}&pageSize=1&pageNumber=1");
        firstPage.Items.Should().ContainSingle();
        firstPage.TotalPages.Should().Be(2);

        using var invalidEntity = await database.Client.GetAsync("/api/audit-logs?entityName=IdempotencyRecords");
        using var invalidAction = await database.Client.GetAsync("/api/audit-logs?action=TRUNCATE");
        invalidEntity.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        invalidAction.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var detail = await database.Client.GetFromJsonAsync<AuditLogDetailDto>($"/api/audit-logs/{byEntity.Items[0].Id}");
        detail!.EntityName.Should().Be("Categories");
        detail.OldValues!.Value.GetProperty("Name").GetString().Should().Be(name);
        detail.NewValues!.Value.GetProperty("Name").GetString().Should().Be(name + "-v2");

        using var missing = await database.Client.GetAsync("/api/audit-logs/999999999");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Detail_DropsAnyKeyOutsideTheAllowList_EvenIfAnomalousRowsExist()
    {
        await AuthenticateAdminAsync();
        var correlation = $"anomalous-{Guid.NewGuid():N}";
        var anomalousJson = "{\"FullName\":\"A\",\"PasswordHash\":\"leak-me\"}";
        long anomalousId;
        await using (var context = database.CreateDbContext())
        {
            // Fila anómala insertada fuera de la aplicación con una clave no permitida.
            await context.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO dbo.AuditLogs (EntityName, EntityId, [Action], UserId, ActorType, [Source], OldValues, NewValues, ChangedFields, OccurredAtUtc, CorrelationId)
                VALUES ('Users', '1', 'UPDATE', NULL, 'System', 'API', NULL, {anomalousJson}, 'FullName', SYSUTCDATETIME(), {correlation})");
            anomalousId = await context.AuditLogs.AsNoTracking().Where(a => a.CorrelationId == correlation).Select(a => a.Id).SingleAsync();
        }

        using var response = await database.Client.GetAsync($"/api/audit-logs/{anomalousId}");
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        raw.Should().Contain("FullName").And.NotContain("leak-me").And.NotContainEquivalentOf("PasswordHash");
    }

    private async Task<PagedResult<AuditLogListItemDto>> GetPageAsync(string query) =>
        (await database.Client.GetFromJsonAsync<PagedResult<AuditLogListItemDto>>($"/api/audit-logs{query}"))!;
}
