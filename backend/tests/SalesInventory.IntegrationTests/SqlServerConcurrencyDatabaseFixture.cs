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
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Persistence;
using SalesInventory.Infrastructure.Security;

namespace SalesInventory.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerConcurrencyCollection : ICollectionFixture<SqlServerConcurrencyDatabaseFixture>
{
    public const string Name = "SQL Server concurrency";
}

public sealed class SqlServerConcurrencyDatabaseFixture : IAsyncLifetime
{
    private readonly string _databaseName = $"SalesInventoryConcurrency_{Guid.NewGuid():N}";
    private readonly ConcurrencyCommandInterceptor _interceptor = new();
    private string _connectionString = string.Empty;
    private string _masterConnectionString = string.Empty;
    private bool _databaseCreated;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public HttpClient Client => _client ?? throw new InvalidOperationException("The test API is not initialized.");

    public async Task InitializeAsync()
    {
        var connectionString = GetConfiguredConnectionString();
        var databaseBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = _databaseName
        };
        _connectionString = databaseBuilder.ConnectionString;
        var masterBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master"
        };
        _masterConnectionString = masterBuilder.ConnectionString;

        await CreateDatabaseAsync();
        try
        {
            await using (var context = CreateDbContext())
            {
                await context.Database.MigrateAsync();
            }

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
            {
                JwtTestConfiguration.Apply(webHost);

                webHost.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _connectionString
                    }));

                webHost.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<SalesInventoryDbContext>>();
                    services.RemoveAll<SalesInventoryDbContext>();
                    services.RemoveAll<IApplicationDbContext>();
                    services.AddScoped(_ =>
                    {
                        var options = new DbContextOptionsBuilder<SalesInventoryDbContext>()
                            .UseSqlServer(_connectionString, sql => sql.EnableRetryOnFailure())
                            .AddInterceptors(_interceptor)
                            .Options;
                        return new SalesInventoryDbContext(options);
                    });
                    services.AddScoped<IApplicationDbContext>(
                        provider => provider.GetRequiredService<SalesInventoryDbContext>());
                });
            });
            _client = _factory.CreateClient();
        }
        catch
        {
            await DropDatabaseAsync();
            throw;
        }
    }

    public SalesInventoryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesInventoryDbContext>()
            .UseSqlServer(_connectionString, sql => sql.EnableRetryOnFailure())
            .Options;

        return new SalesInventoryDbContext(options);
    }

    public async Task<SeededProducts> SeedProductsAsync(params int[] startingStocks)
    {
        await using var context = CreateDbContext();
        var category = new Category { Name = $"Concurrency-{Guid.NewGuid():N}" };
        var user = new User
        {
            FullName = "Concurrency Test User",
            Email = $"concurrency-{Guid.NewGuid():N}@example.com",
            PasswordHash = "test-only-hash",
            Role = UserRole.Admin,
            IsActive = true
        };
        var products = startingStocks.Select((stock, index) => new Product
        {
            Category = category,
            SKU = $"CT-{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
            Name = $"Concurrency Product {index + 1}",
            Price = 10m,
            Stock = stock,
            MinimumStock = 0,
            IsActive = true
        }).ToList();

        context.AddRange(category, user);
        context.Products.AddRange(products);
        await context.SaveChangesAsync();
        _client!.DefaultRequestHeaders.Authorization =
            JwtTestConfiguration.CreateAuthorizationHeader(user.Id, UserRole.Admin.ToString());

        return new SeededProducts(user.Id, products.Select(product => product.Id).ToArray());
    }

    public void AuthenticateAs(int userId, string role) =>
        Client.DefaultRequestHeaders.Authorization =
            JwtTestConfiguration.CreateAuthorizationHeader(userId, role);

    public async Task<User> SeedUserAsync(string email, string password, bool isActive = true)
    {
        await using var context = CreateDbContext();
        var user = new User
        {
            FullName = "Authentication Test User",
            Email = email,
            PasswordHash = new Pbkdf2PasswordHasher().HashPassword(password),
            Role = UserRole.Seller,
            IsActive = isActive
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    public void SynchronizeNextProductReads(int numberOfReads) =>
        _interceptor.SynchronizeNextProductReads(numberOfReads);

    public void FailNextMovementInsert() => _interceptor.FailNextMovementInsert();

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_databaseCreated)
        {
            using var poolConnection = new SqlConnection(_connectionString);
            SqlConnection.ClearPool(poolConnection);
            await DropDatabaseAsync();
        }
    }

    private async Task CreateDatabaseAsync()
    {
        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{_databaseName}]";
        await command.ExecuteNonQueryAsync();
        _databaseCreated = true;
    }

    private async Task DropDatabaseAsync()
    {
        if (!_databaseCreated)
        {
            return;
        }

        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]";
        await command.ExecuteNonQueryAsync();
        _databaseCreated = false;
    }

    private static string GetConfiguredConnectionString()
    {
        var environmentConnection = Environment.GetEnvironmentVariable("SALESINVENTORY_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(environmentConnection))
        {
            return environmentConnection;
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

    public sealed record SeededProducts(int UserId, int[] ProductIds);

    private sealed class ConcurrencyCommandInterceptor : DbCommandInterceptor
    {
        private readonly object _sync = new();
        private TaskCompletionSource<bool>? _productReadSignal;
        private int _remainingProductReads;
        private int _failNextMovementInsert;

        public void SynchronizeNextProductReads(int numberOfReads)
        {
            if (numberOfReads < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(numberOfReads));
            }

            lock (_sync)
            {
                _remainingProductReads = numberOfReads;
                _productReadSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void FailNextMovementInsert() =>
            Interlocked.Exchange(ref _failNextMovementInsert, 1);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[StockMovements]", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _failNextMovementInsert, 0) == 1)
            {
                await result.DisposeAsync();
                throw new InvalidOperationException("Injected failure after writing the stock movement.");
            }

            var signal = SignalProductRead(command.CommandText);
            if (signal is not null)
            {
                await signal.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        private Task<bool>? SignalProductRead(string commandText)
        {
            if (!commandText.Contains("FROM [dbo].[Products]", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            lock (_sync)
            {
                if (_remainingProductReads == 0 || _productReadSignal is null)
                {
                    return null;
                }

                var signal = _productReadSignal;
                _remainingProductReads--;
                if (_remainingProductReads == 0)
                {
                    signal.TrySetResult(true);
                }

                return signal.Task;
            }
        }
    }
}
