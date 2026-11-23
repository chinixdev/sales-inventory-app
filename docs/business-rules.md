# Reglas de Negocio — SalesInventory

Este documento recopila las reglas de negocio, restricciones e invariantes que rigen el sistema `SalesInventory`, tanto a nivel de aplicación como en la base de datos.

---

## 1. Módulo de Usuarios y Roles

| Regla | Descripción | Implementación Técnica |
|---|---|---|
| **RN-USR-01: Roles del Sistema** | Solo existen dos roles permitidos: `Admin` (administrador) y `Seller` (vendedor). | Check constraint en SQL `[Role] IN ('Admin', 'Seller')` y Enum C# `UserRole`. |
| **RN-USR-02: Unicidad de Correo** | Cada usuario debe tener una dirección de correo única en el sistema. | Índice único `UQ_Users_Email` sobre `dbo.Users(Email)`. |
| **RN-USR-03: Seguridad de Contraseñas** | Las contraseñas nunca se almacenan en texto plano. Se debe aplicar hashing seguro (BCrypt/PBKDF2) antes de persistir. | Columna `PasswordHash NVARCHAR(255)`. |
| **RN-USR-04: Borrado Lógico** | Los usuarios no deben borrarse físicamente de la base de datos para no quebrar la auditoría histórica. | Columna `IsActive BIT DEFAULT 1`. |

---

## 2. Módulo de Categorías

| Regla | Descripción | Implementación Técnica |
|---|---|---|
| **RN-CAT-01: Unicidad de Nombre** | El nombre de una categoría debe ser único e irrepetible. | Índice único `UQ_Categories_Name` en `dbo.Categories(Name)`. |
| **RN-CAT-02: Integridad Referencial** | No se puede eliminar una categoría si tiene productos asociados. | Clave foránea `FK_Products_Categories` con regla `ON DELETE RESTRICT`. |

---

## 3. Módulo de Productos y Catálogo

| Regla | Descripción | Implementación Técnica |
|---|---|---|
| **RN-PRD-01: Unicidad de SKU** | El SKU (*Stock Keeping Unit*) es el identificador comercial único del producto. | Índice único `UQ_Products_SKU` en `dbo.Products(SKU)`. |
| **RN-PRD-02: Precio Positivo** | El precio de venta al público debe ser estrictamente mayor a 0. | Check constraint `[Price] > 0`. Tipo de dato `DECIMAL(18,2)`. |
| **RN-PRD-03: Stock No Negativo** | La existencia de producto disponible nunca puede ser menor a cero. | Check constraint `[Stock] >= 0` y validación en entidad `Product.DeductStock()`. |
| **RN-PRD-04: Umbral de Stock Mínimo** | Cada producto cuenta con un umbral mínimo de seguridad (por defecto 5 unidades). No puede ser negativo. | Check constraint `[MinimumStock] >= 0`. Propiedad `IsBelowMinimumStock => Stock <= MinimumStock`. |
| **RN-PRD-05: Concurrencia Optimista** | Si dos transacciones intentan actualizar el stock o datos del mismo producto simultáneamente, una debe ser rechazada de forma controlada. | Columna `RowVersion ROWVERSION` (`byte[]` en C#) con detección de `DbUpdateConcurrencyException`. |

---

## 4. Módulo de Ventas

| Regla | Descripción | Implementación Técnica |
|---|---|---|
| **RN-VEN-01: Total Positivo** | El monto total de una venta debe ser estrictamente superior a 0. | Check constraint `[Total] > 0`. |
| **RN-VEN-02: Coherencia de Subtotal** | Cada línea de detalle debe cumplir que el `Subtotal` sea exactamente igual a `Cantidad * PrecioUnitario`. | Check constraint `[Subtotal] = [Quantity] * [UnitPrice]` y método `CalculateSubtotal()`. |
| **RN-VEN-03: Precios y Cantidades Positivas** | La cantidad de unidades vendidas y el precio unitario pactado deben ser mayores a 0. | Check constraints `[Quantity] > 0` y `[UnitPrice] > 0`. |
| **RN-VEN-04: Unicidad de Producto en Venta** | Una venta no puede contener líneas repetidas para un mismo producto; las cantidades deben consolidarse en una sola línea. | Restricción de unicidad `UQ_SaleDetails_SaleProduct` en `(SaleId, ProductId)`. |
| **RN-VEN-05: Integridad en Cascada** | Si una venta es eliminada en desarrollo o rollback, sus detalles asociados deben removerse en cascada. | Clave foránea `FK_SaleDetails_Sales` con `ON DELETE CASCADE`. |

---

## 5. Módulo de Movimientos de Inventario (Kardex)

| Regla | Descripción | Implementación Técnica |
|---|---|---|
| **RN-INV-01: Tipos de Movimiento** | Los movimientos aceptados son: `IN` (Entrada por compra o recepción), `OUT` (Salida por venta o despacho), `ADJUSTMENT` (Ajuste por conteo físico o merma). | Check constraint `[MovementType] IN ('IN', 'OUT', 'ADJUSTMENT')`. |
| **RN-INV-02: Cantidad Positiva** | La cantidad afectada en cualquier movimiento debe ser estrictamente mayor a 0. | Check constraint `[Quantity] > 0`. |
| **RN-INV-03: Balance Matemático Invariable** | Todo movimiento debe reflejar de forma matemática exacta la transición de stock: <br>• Para `IN`: `NewStock = PreviousStock + Quantity`<br>• Para `OUT`: `NewStock = PreviousStock - Quantity`<br>• Para `ADJUSTMENT`: `ABS(NewStock - PreviousStock) = Quantity` | Check constraint `CK_StockMovements_Balance` y validación en `StockMovement.ValidateBalance()`. |
| **RN-INV-04: Asociación con Ventas** | Solo los movimientos de salida (`OUT`) pueden estar asociados a un `SaleId`. Un movimiento `IN` o `ADJUSTMENT` no puede tener `SaleId`. | Check constraint `[SaleId] IS NULL OR [MovementType] = 'OUT'`. |
| **RN-INV-05: Inmutabilidad de Auditoría** | Los registros del Kardex son de solo lectura una vez insertados. No se permite actualización (`UPDATE`) ni borrado (`DELETE`). | Política de servicios en la capa Application. |
