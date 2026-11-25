using FluentAssertions;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Exceptions;
using Xunit;

namespace SalesInventory.UnitTests.Domain;

public class ProductStockTests
{
    [Fact]
    public void DeductStock_WhenSufficientStock_ShouldReduceStock()
    {
        var product = new Product
        {
            Id = 1,
            Name = "Laptop Pro",
            SKU = "LAP-001",
            Price = 1200.00m,
            Stock = 20,
            MinimumStock = 5
        };

        product.DeductStock(5);

        product.Stock.Should().Be(15);
    }

    [Fact]
    public void DeductStock_WhenInsufficientStock_ShouldThrowInsufficientStockException()
    {
        var product = new Product
        {
            Id = 1,
            Name = "Laptop Pro",
            SKU = "LAP-001",
            Price = 1200.00m,
            Stock = 3,
            MinimumStock = 5
        };

        var act = () => product.DeductStock(10);

        act.Should().Throw<InsufficientStockException>()
            .WithMessage("*Stock insuficiente*")
            .And.RequestedQuantity.Should().Be(10);
    }

    [Fact]
    public void DeductStock_WhenQuantityZeroOrNegative_ShouldThrowDomainException()
    {
        var product = new Product
        {
            Id = 1,
            Stock = 10
        };

        var actZero = () => product.DeductStock(0);
        var actNegative = () => product.DeductStock(-2);

        actZero.Should().Throw<DomainException>();
        actNegative.Should().Throw<DomainException>();
    }

    [Fact]
    public void AddStock_WhenValidQuantity_ShouldIncreaseStock()
    {
        var product = new Product
        {
            Id = 1,
            Stock = 10
        };

        product.AddStock(15);

        product.Stock.Should().Be(25);
    }

    [Theory]
    [InlineData(10, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(2, 5, true)]
    [InlineData(0, 5, true)]
    public void IsBelowMinimumStock_ShouldReflectThresholdAccurately(int currentStock, int minStock, bool expectedResult)
    {
        var product = new Product
        {
            Stock = currentStock,
            MinimumStock = minStock
        };

        product.IsBelowMinimumStock.Should().Be(expectedResult);
    }
}
