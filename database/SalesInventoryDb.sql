-- =========================================================================
-- Script DDL: Base de Datos SalesInventoryDb
-- Sistema de Ventas e Inventario (Clean Architecture)
-- =========================================================================

CREATE DATABASE SalesInventoryDb;
GO

USE SalesInventoryDb;
GO

-- 1. Usuarios
CREATE TABLE dbo.Users (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    FullName NVARCHAR(120) NOT NULL,
    Email NVARCHAR(255) NOT NULL,
    PasswordHash NVARCHAR(255) NOT NULL,
    Role VARCHAR(20) NOT NULL
        CONSTRAINT CK_Users_Role
        CHECK (Role IN ('Admin', 'Seller')),
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Users_Email UNIQUE (Email)
);

-- 2. Categorías
CREATE TABLE dbo.Categories (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_Categories_Name UNIQUE (Name)
);

-- 3. Productos
CREATE TABLE dbo.Products (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    CategoryId INT NOT NULL,
    SKU VARCHAR(40) NOT NULL,
    Name NVARCHAR(150) NOT NULL,
    Description NVARCHAR(500) NULL,
    Price DECIMAL(18,2) NOT NULL,
    Stock INT NOT NULL DEFAULT 0,
    MinimumStock INT NOT NULL DEFAULT 5,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    RowVersion ROWVERSION NOT NULL,

    CONSTRAINT FK_Products_Categories
        FOREIGN KEY (CategoryId) REFERENCES dbo.Categories(Id),
    CONSTRAINT UQ_Products_SKU UNIQUE (SKU),
    CONSTRAINT CK_Products_Price CHECK (Price > 0),
    CONSTRAINT CK_Products_Stock CHECK (Stock >= 0),
    CONSTRAINT CK_Products_MinStock CHECK (MinimumStock >= 0)
);

-- 4. Ventas
CREATE TABLE dbo.Sales (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    UserId INT NOT NULL,
    SaleDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    Total DECIMAL(18,2) NOT NULL,

    CONSTRAINT FK_Sales_Users
        FOREIGN KEY (UserId) REFERENCES dbo.Users(Id),
    CONSTRAINT CK_Sales_Total CHECK (Total > 0)
);

-- 5. Detalle de ventas
CREATE TABLE dbo.SaleDetails (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    SaleId INT NOT NULL,
    ProductId INT NOT NULL,
    Quantity INT NOT NULL,
    UnitPrice DECIMAL(18,2) NOT NULL,
    Subtotal DECIMAL(18,2) NOT NULL,

    CONSTRAINT FK_SaleDetails_Sales
        FOREIGN KEY (SaleId) REFERENCES dbo.Sales(Id) ON DELETE CASCADE,
    CONSTRAINT FK_SaleDetails_Products
        FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id),
    CONSTRAINT CK_SaleDetails_Quantity CHECK (Quantity > 0),
    CONSTRAINT CK_SaleDetails_Price CHECK (UnitPrice > 0),
    CONSTRAINT CK_SaleDetails_Subtotal
        CHECK (Subtotal = Quantity * UnitPrice),
    CONSTRAINT UQ_SaleDetails_SaleProduct
        UNIQUE (SaleId, ProductId)
);

-- 6. Movimientos de inventario
CREATE TABLE dbo.StockMovements (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    ProductId INT NOT NULL,
    UserId INT NOT NULL,
    SaleId INT NULL,
    MovementType VARCHAR(20) NOT NULL,
    Quantity INT NOT NULL,
    PreviousStock INT NOT NULL,
    NewStock INT NOT NULL,
    Reason NVARCHAR(300) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_StockMovements_Products
        FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id),
    CONSTRAINT FK_StockMovements_Users
        FOREIGN KEY (UserId) REFERENCES dbo.Users(Id),
    CONSTRAINT FK_StockMovements_Sales
        FOREIGN KEY (SaleId) REFERENCES dbo.Sales(Id),
    CONSTRAINT CK_StockMovements_Type
        CHECK (MovementType IN ('IN', 'OUT', 'ADJUSTMENT')),
    CONSTRAINT CK_StockMovements_Quantity
        CHECK (Quantity > 0),
    CONSTRAINT CK_StockMovements_Stock
        CHECK (PreviousStock >= 0 AND NewStock >= 0),
    CONSTRAINT CK_StockMovements_Balance
        CHECK (
            (MovementType = 'IN'
                AND NewStock = PreviousStock + Quantity)
            OR
            (MovementType = 'OUT'
                AND NewStock = PreviousStock - Quantity)
            OR
            (MovementType = 'ADJUSTMENT'
                AND ABS(NewStock - PreviousStock) = Quantity)
        ),
    CONSTRAINT CK_StockMovements_SaleType
        CHECK (SaleId IS NULL OR MovementType = 'OUT')
);

CREATE INDEX IX_Products_CategoryId
    ON dbo.Products(CategoryId);

CREATE INDEX IX_Sales_SaleDate
    ON dbo.Sales(SaleDate);

CREATE INDEX IX_Sales_UserId
    ON dbo.Sales(UserId);

CREATE INDEX IX_StockMovements_ProductDate
    ON dbo.StockMovements(ProductId, CreatedAt DESC);

CREATE INDEX IX_StockMovements_SaleId
    ON dbo.StockMovements(SaleId);

CREATE INDEX IX_StockMovements_UserId
    ON dbo.StockMovements(UserId);
GO
