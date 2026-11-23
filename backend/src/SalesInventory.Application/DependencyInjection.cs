using Microsoft.Extensions.DependencyInjection;
using SalesInventory.Application.Auth.Interfaces;
using SalesInventory.Application.Auth.Services;
using SalesInventory.Application.Categories.Interfaces;
using SalesInventory.Application.Categories.Services;
using SalesInventory.Application.Inventory.Interfaces;
using SalesInventory.Application.Inventory.Services;
using SalesInventory.Application.Products.Interfaces;
using SalesInventory.Application.Products.Services;
using SalesInventory.Application.Sales.Interfaces;
using SalesInventory.Application.Sales.Services;

namespace SalesInventory.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<ISaleService, SaleService>();

        return services;
    }
}
