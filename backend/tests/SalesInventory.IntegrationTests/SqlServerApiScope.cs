using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SalesInventory.Application.Common.Interfaces;
using SalesInventory.Domain.Entities;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.IntegrationTests;

internal sealed class SqlServerApiScope : IAsyncDisposable
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FailingStockMovementInterceptor _movementInterceptor;

    private SqlServerApiScope(
        SqlConnection connection,
        SqlTransaction transaction,
        WebApplicationFactory<Program> factory,
        FailingStockMovementInterceptor movementInterceptor,
        HttpClient client)
    {
        _connection = connection;
        _transaction = transaction;
        _factory = factory;
        _movementInterceptor = movementInterceptor;
        Client = client;
    }

    public HttpClient Client { get; }
    public int UserId { get; private set; }

    public void AuthenticateAs(int userId, string role)
    {
        UserId = userId;
        Client.DefaultRequestHeaders.Authorization =
            JwtTestConfiguration.CreateAuthorizationHeader(userId, role);
    }

    public static async Task<SqlServerApiScope> CreateAsync()
    {
        var connectionString = GetConnectionString();
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            MultipleActiveResultSets = false
        };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var movementInterceptor = new FailingStockMovementInterceptor();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
        {
            JwtTestConfiguration.Apply(webHost);

            webHost.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<SalesInventoryDbContext>>();
                services.RemoveAll<SalesInventoryDbContext>();
                services.RemoveAll<IApplicationDbContext>();
                services.AddScoped(provider =>
                {
                    var options = new DbContextOptionsBuilder<SalesInventoryDbContext>()
                        .UseSqlServer(connection)
                        .AddInterceptors(movementInterceptor)
                        .Options;
                    var context = new SalesInventoryDbContext(options, provider.GetRequiredService<SalesInventory.Application.Common.Interfaces.IAuditActorProvider>());
                    context.Database.UseTransaction(transaction);
                    return context;
                });
                services.AddScoped<IApplicationDbContext>(
                    provider => provider.GetRequiredService<SalesInventoryDbContext>());
            });
        });

        try
        {
            var client = factory.CreateClient();
            var scope = new SqlServerApiScope(
                connection,
                transaction,
                factory,
                movementInterceptor,
                client);
            await using var context = scope.CreateContext();
            var user = new User
            {
                FullName = "API Scope User",
                Email = $"api-scope-{Guid.NewGuid():N}@example.com",
                PasswordHash = "test-only-hash",
                Role = SalesInventory.Domain.Enums.UserRole.Admin,
                IsActive = true
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            scope.AuthenticateAs(user.Id, "Admin");
            return scope;
        }
        catch
        {
            await factory.DisposeAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    public SalesInventoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SalesInventoryDbContext>()
            .UseSqlServer(_connection)
            .Options;
        var context = new SalesInventoryDbContext(options);
        context.Database.UseTransaction(_transaction);
        return context;
    }

    public void FailNextStockMovementInsert() => _movementInterceptor.FailNextInsert();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static string GetConnectionString()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("SALESINVENTORY_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(configuredConnection))
        {
            return configuredConnection;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var settingsPath = Path.Combine(
                directory.FullName,
                "src",
                "SalesInventory.Api",
                "appsettings.Development.json");

            if (File.Exists(settingsPath))
            {
                var configuration = new ConfigurationBuilder()
                    .AddJsonFile(settingsPath, optional: false)
                    .Build();
                return configuration.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("DefaultConnection no está configurada.");
            }
        }

        throw new FileNotFoundException(
            "No se encontró appsettings.Development.json. Configure SALESINVENTORY_TEST_CONNECTION.");
    }

    private sealed class FailingStockMovementInterceptor : DbCommandInterceptor
    {
        private int _failNextInsert;

        public void FailNextInsert() => Interlocked.Exchange(ref _failNextInsert, 1);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[StockMovements]", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _failNextInsert, 0) == 1)
            {
                await result.DisposeAsync();
                throw new InvalidOperationException("Fallo de prueba después de ejecutar el INSERT de movimiento.");
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
