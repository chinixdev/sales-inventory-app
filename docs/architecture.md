# Arquitectura del Sistema — SalesInventory

## 1. Visión General

`SalesInventory` es una solución empresarial diseñada bajo los principios de **Clean Architecture** (Arquitectura Limpia) y **Domain-Driven Design (DDD) táctico**, orientada a servir como proyecto de referencia y portafolio profesional para posiciones Backend en .NET.

El objetivo central es desacoplar completamente la lógica de negocio de las tecnologías de persistencia y presentación, asegurando un software testeable, mantenible y escalable.

---

## 2. Diagrama de Capas y Flujo de Dependencias

Siguiendo la Regla de Dependencia, las capas internas nunca conocen ni dependen de las capas externas:

```
[ SalesInventory.Api ]  --->  Presentación / API REST (ASP.NET Core)
       │        │
       │        ▼
       │   [ SalesInventory.Infrastructure ]  --->  Persistencia (EF Core 10 / SQL Server)
       ▼        │
[ SalesInventory.Application ]  --->  Casos de Uso / Interfaces
       │
       ▼
[ SalesInventory.Domain ]  --->  Entidades, Enums, Invariantes (POCO puro)
```

### 2.1. SalesInventory.Domain
- **Responsabilidad:** Núcleo de negocio y reglas empresariales críticas.
- **Dependencias:** Ninguna (cero referencias a frameworks, ASP.NET o EF Core).
- **Contenido:**
  - **Entidades de dominio:** `User`, `Category`, `Product`, `Sale`, `SaleDetail`, `StockMovement`.
  - **Enums:** `UserRole`, `StockMovementType`.
  - **Excepciones de dominio:** `DomainException`, `InsufficientStockException`.
  - **Invariantes:** Encapsulación de métodos en entidades para proteger el estado (p. ej. `Product.DeductStock()`, `StockMovement.ValidateBalance()`).

### 2.2. SalesInventory.Application
- **Responsabilidad:** Orquestación de casos de uso y contratos de aplicación.
- **Dependencias:** `SalesInventory.Domain`.
- **Contenido:**
  - **Interfaces:** `IApplicationDbContext` para desacoplar el acceso a datos.
  - **Inyección de dependencias:** Extensión `AddApplication()`.

### 2.3. SalesInventory.Infrastructure
- **Responsabilidad:** Implementación técnica de interfaces y persistencia.
- **Dependencias:** `SalesInventory.Application`, `SalesInventory.Domain`.
- **Contenido:**
  - **DbContext:** `SalesInventoryDbContext` implementando `IApplicationDbContext`.
  - **Fluent API:** Configuraciones aisladas en `Persistence/Configurations/` que mapean con exactitud los tipos, tamaños, índices y `CHECK constraints` de SQL Server.
  - **Estrategia de resiliencia:** `EnableRetryOnFailure` en SQL Server para mitigar fallos transitorios de red.
  - **Inyección de dependencias:** Extensión `AddInfrastructure(IConfiguration configuration)`.

### 2.4. SalesInventory.Api
- **Responsabilidad:** Punto de entrada HTTP y Composition Root (composición de dependencias).
- **Dependencias:** `SalesInventory.Application`, `SalesInventory.Infrastructure`.
- **Contenido:**
  - **Controladores:** Endpoints REST organizados por recursos.
  - **Health Checks:** Endpoint `/health` nativo y `/api/health` para monitorización de estado.
  - **OpenAPI & Scalar:** Documentación interactiva moderna generada en tiempo de ejecución.
  - **CORS:** Política configurada para permitir clientes web autorizados (como Angular en `http://localhost:4200`).

---

## 3. Estrategia de Concurrencia y Transaccionalidad en Inventario

El control de inventario es el componente de mayor riesgo de condiciones de carrera (*race conditions*) en un sistema de ventas. Dos usuarios vendiendo el último producto en simultáneo podrían generar stock negativo si no existe protección.

### 3.1. Concurrencia Optimista con ROWVERSION
- La entidad `Product` incluye una columna `RowVersion` mapeada al tipo `ROWVERSION` de SQL Server.
- Cada vez que un registro de producto es modificado, SQL Server incrementa automáticamente su versión.
- En EF Core, `builder.Property(p => p.RowVersion).IsRowVersion()` activa el token de concurrencia optimista.
- Si dos transacciones intentan actualizar el mismo producto con la misma versión base, la segunda fallará con una excepción `DbUpdateConcurrencyException`, permitiendo al sistema notificar al usuario y evitar sobreventas.

### 3.2. Atomicidad Transaccional
- El registro de una venta implica:
  1. Insertar el encabezado `Sale`.
  2. Insertar las líneas de detalle `SaleDetail`.
  3. Descontar el stock en `Product`.
  4. Insertar el movimiento de auditoría `StockMovement` (tipo `OUT`).
- Todas estas operaciones deben ejecutarse dentro de la misma transacción de base de datos (`IApplicationDbContext.SaveChangesAsync()` ejecuta internamente una transacción implícita que agrupa todos los cambios, asegurando atomicidad ACID).

---

## 4. Arquitectura del Frontend (Angular)

El cliente frontend se encuentra en el directorio `/frontend` y fue diseñado bajo la filosofía **Feature-Based Architecture**:

```
frontend/src/app/
├── core/         # Servicios singleton, interceptores HTTP, modelos transversales
├── shared/       # Componentes reusables (Navbar, Footer), pipes, directivas
└── features/     # Módulos funcionales desacoplados cargados bajo demanda (Lazy Loading)
    ├── dashboard/
    ├── products/
    ├── categories/
    ├── sales/
    ├── inventory/
    └── auth/
```

- **Standalone Components:** Uso de la API moderna de componentes autónomos sin `NgModules`.
- **Lazy Loading:** Cada ruta de feature carga dinámicamente su bundle mediante `loadChildren: () => import(...)`, reduciendo drásticamente el peso inicial de la aplicación.
- **Tipado estricto:** TypeScript estricto sincronizado con los contratos DTO del backend.
