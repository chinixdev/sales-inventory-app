using FluentAssertions;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Exceptions;
using Xunit;

namespace SalesInventory.UnitTests.Domain;

public class SaleDetailCalculationTests
{
    [Fact]
    public void CalculateSubtotal_WithValidQuantityAndPrice_ShouldCalculateCorrectly()
    {
        var detail = new SaleDetail
        {
            Quantity = 3,
            UnitPrice = 25.50m
        };

        detail.CalculateSubtotal();

        detail.Subtotal.Should().Be(76.50m);
    }

    [Fact]
    public void CalculateSubtotal_WithInvalidQuantity_ShouldThrowDomainException()
    {
        var detail = new SaleDetail
        {
            Quantity = 0,
            UnitPrice = 10.00m
        };

        var act = () => detail.CalculateSubtotal();

        act.Should().Throw<DomainException>()
            .WithMessage("*mayor a 0*");
    }

    [Fact]
    public void RecalculateTotal_OnSale_ShouldSumSubtotalsOfAllDetails()
    {
        var sale = new Sale();
        var detail1 = new SaleDetail { Quantity = 2, UnitPrice = 100.00m, Subtotal = 200.00m };
        var detail2 = new SaleDetail { Quantity = 1, UnitPrice = 50.00m, Subtotal = 50.00m };

        sale.SaleDetails.Add(detail1);
        sale.SaleDetails.Add(detail2);

        sale.RecalculateTotal();

        sale.Total.Should().Be(250.00m);
    }
}
