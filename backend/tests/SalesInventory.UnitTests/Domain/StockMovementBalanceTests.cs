using FluentAssertions;
using SalesInventory.Domain.Entities;
using SalesInventory.Domain.Enums;
using SalesInventory.Domain.Exceptions;
using Xunit;

namespace SalesInventory.UnitTests.Domain;

public class StockMovementBalanceTests
{
    [Fact]
    public void ValidateBalance_ForIncomingMovement_WhenCalculationsMatch_ShouldSucceed()
    {
        // Arrange
        var movement = new StockMovement
        {
            ProductId = 1,
            UserId = 1,
            MovementType = StockMovementType.IN,
            Quantity = 10,
            PreviousStock = 5,
            NewStock = 15,
            Reason = "Recepcion de orden de compra"
        };

        // Act
        var act = () => movement.ValidateBalance();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateBalance_ForOutgoingMovement_WhenCalculationsMatch_ShouldSucceed()
    {
        // Arrange
        var movement = new StockMovement
        {
            ProductId = 1,
            UserId = 1,
            SaleId = 100,
            MovementType = StockMovementType.OUT,
            Quantity = 4,
            PreviousStock = 10,
            NewStock = 6,
            Reason = "Venta #100"
        };

        // Act
        var act = () => movement.ValidateBalance();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateBalance_ForAdjustmentMovement_WhenCalculationsMatch_ShouldSucceed()
    {
        // Arrange
        var movement = new StockMovement
        {
            ProductId = 1,
            UserId = 1,
            MovementType = StockMovementType.ADJUSTMENT,
            Quantity = 2,
            PreviousStock = 10,
            NewStock = 8,
            Reason = "Ajuste por conteo ciclico"
        };

        // Act
        var act = () => movement.ValidateBalance();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateBalance_WhenBalanceFormulaIsViolated_ShouldThrowDomainException()
    {
        // Arrange
        var movement = new StockMovement
        {
            ProductId = 1,
            UserId = 1,
            MovementType = StockMovementType.IN,
            Quantity = 10,
            PreviousStock = 5,
            NewStock = 20, // Deberia ser 15
            Reason = "Calculo erroneo"
        };

        // Act
        var act = () => movement.ValidateBalance();

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("*NewStock debe ser igual a PreviousStock + Quantity*");
    }

    [Fact]
    public void ValidateBalance_WhenSaleIdProvidedForNonOutMovement_ShouldThrowDomainException()
    {
        // Arrange
        var movement = new StockMovement
        {
            ProductId = 1,
            UserId = 1,
            SaleId = 42,
            MovementType = StockMovementType.IN, // Venta no puede asociarse a entrada
            Quantity = 5,
            PreviousStock = 5,
            NewStock = 10,
            Reason = "Error de asociacion de venta"
        };

        // Act
        var act = () => movement.ValidateBalance();

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("*asociado a una venta solo puede ser de tipo OUT*");
    }
}
