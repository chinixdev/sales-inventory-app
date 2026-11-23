# SalesInventory System 📦🚀

> **Sistema Profesional de Ventas e Inventario** desarrollado como proyecto de referencia y portafolio técnico para posiciones Backend en C# y .NET, construido con **Clean Architecture**, **ASP.NET Core Web API**, **Entity Framework Core**, **SQL Server** y un cliente SPA en **Angular**.

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Angular 21](https://img.shields.io/badge/Angular-21.2-DD0031?logo=angular)](https://angular.dev/)
[![SQL Server](https://img.shields.io/badge/SQL%20Server-2022-CC292B?logo=microsoftsqlserver)](https://www.microsoft.com/sql-server)
[![Tests Passing](https://img.shields.io/badge/Tests-18%20Passed-success)]()

---

## 🏛️ Arquitectura

El sistema implementa **Clean Architecture (Arquitectura Limpia)** con inversión de dependencias estricta:

```
sales-inventory-system/
├── backend/
│   ├── src/
│   │   ├── SalesInventory.Domain/          # Núcleo: Entidades, Enums, Invariantes, Excepciones (0 dependencias)
│   │   ├── SalesInventory.Application/     # Casos de uso, Interfaces (IApplicationDbContext)
│   │   ├── SalesInventory.Infrastructure/  # DbContext, Mapeos Fluent API, SQL Server
│   │   └── SalesInventory.Api/             # Controladores REST, Scalar OpenAPI, Health Checks, CORS
│   └── tests/
│       ├── SalesInventory.UnitTests/       # Pruebas unitarias de dominio con xUnit & FluentAssertions
│       └── SalesInventory.IntegrationTests/# Pruebas de integración con WebApplicationFactory
├── frontend/                               # SPA Angular 21 (Feature-Based, Standalone, Lazy Loading)
├── database/                               # Script DDL SQL Server (SalesInventoryDb.sql)
└── docs/                                   # Documentación técnica de arquitectura, reglas y base de datos
```

### Principios Clave
- **Independencia del Dominio:** La capa `Domain` es un POCO puro, sin librerías externas ni referencias a EF Core o ASP.NET Core.
- **Inversión de Dependencias:** La capa `Application` define `IApplicationDbContext`, que es implementado por `Infrastructure`.
- **Concurrencia Optimista:** La entidad `Product` implementa `RowVersion` (`ROWVERSION` en SQL Server) para prevenir sobreventas y condiciones de carrera concurrentes.
- **Trazabilidad Inmutable:** Cada variación de existencias registra un movimiento auditado en `StockMovements` con validación matemática de balance.

---

## 🛠️ Stack Tecnológico

| Capa | Tecnología | Versión |
|---|---|---|
| **Plataforma Backend** | .NET SDK | 10.0.401 |
| **API REST** | ASP.NET Core Web API | 10.0 |
| **ORM / Acceso a Datos** | Entity Framework Core | 10.0.15 |
| **Base de Datos** | Microsoft SQL Server | 2022 / LocalDB |
| **Documentación API** | Scalar & OpenAPI | Scalar 3.1.1 / OpenAPI 10.0.12 |
| **Pruebas Backend** | xUnit, FluentAssertions, Moq, TestHost | xUnit 2.9, FluentAssertions 8.11 |
| **Frontend SPA** | Angular (Standalone Components) | 21.2.16 |
| **Lenguaje Frontend** | TypeScript | ~5.9.2 |
| **Pruebas Frontend** | Vitest | 4.0.8 |

---

## 🗄️ Modelo de Datos (6 Tablas)

1. **`Users`**: Usuarios del sistema con roles `Admin` y `Seller`, hashing de contraseñas y borrado lógico.
2. **`Categories`**: Clasificación jerárquica con restricción única de nombre.
3. **`Products`**: Catálogo con SKU único, precio positivo, existencias, stock mínimo y control de concurrencia optimista (`RowVersion`).
4. **`Sales`**: Registro transaccional de ventas con usuario y monto total.
5. **`SaleDetails`**: Ítems de venta con validación matemática de subtotal (`Subtotal = Quantity * UnitPrice`) y unicidad por par `(SaleId, ProductId)`.
6. **`StockMovements`**: Kardex inmutable de entradas (`IN`), salidas (`OUT`) y ajustes (`ADJUSTMENT`) con chequeo estricto de balance.

El script completo de creación de la base de datos se encuentra en [`database/SalesInventoryDb.sql`](database/SalesInventoryDb.sql).

---

## 🚀 Puesta en Marcha

### Requisitos Previos
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) instalado.
- [Node.js 20+](https://nodejs.org/) y npm instalados.
- SQL Server (MSSQLSERVER o LocalDB) activo.

### 1. Clonar el Repositorio
```bash
git clone <url-del-repositorio>
cd sales-inventory-system
```

### 2. Configuración de Cadena de Conexión
La configuración sensible **no se almacena en el repositorio**. En desarrollo, la aplicación lee `ConnectionStrings:DefaultConnection`.

Puedes configurar tu conexión local mediante **User Secrets** o variable de entorno:

```bash
# Opción A: User Secrets (Recomendado para desarrollo)
dotnet user-secrets init --project backend/src/SalesInventory.Api
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=SalesInventoryDb;Trusted_Connection=True;TrustServerCertificate=True" --project backend/src/SalesInventory.Api

# Opción B: Variable de entorno
# Windows PowerShell:
$env:ConnectionStrings__DefaultConnection="Server=localhost;Database=SalesInventoryDb;Trusted_Connection=True;TrustServerCertificate=True"
```

### 3. Ejecutar el Backend
```bash
cd backend
dotnet restore
dotnet build
dotnet run --project src/SalesInventory.Api
```

- **Health Check nativo:** `http://localhost:5000/health`
- **Health Check detallado:** `http://localhost:5000/api/health`
- **Documentación Interactiva Scalar:** `http://localhost:5000/scalar/v1`
- **Especificación OpenAPI:** `http://localhost:5000/openapi/v1.json`

### 4. Ejecutar el Frontend
```bash
cd frontend
npm install
npm start
```
La aplicación se abrirá en `http://localhost:4200/`.

---

## 🧪 Ejecución de Pruebas

### Pruebas Backend (Unitarias e Integración)
```bash
dotnet test backend/SalesInventory.sln
```
> Ejecuta 16 pruebas unitarias de dominio (control de stock, validación de balance Kardex, cálculo de subtotales) y 2 pruebas de integración HTTP contra el endpoint `/health` mediante `WebApplicationFactory`.

### Pruebas Frontend (Vitest)
```bash
npm --prefix frontend test -- --watch=false
```

---

## 📚 Documentación Técnica Detallada

- 🏛️ [Arquitectura del Sistema](docs/architecture.md)
- 📋 [Reglas de Negocio e Invariantes](docs/business-rules.md)
- 🗃️ [Diseño de Base de Datos y Diccionario](docs/database-design.md)
- 💾 [Script DDL SQL Server](database/SalesInventoryDb.sql)

---

## 📋 Próximos Pasos (Día 2)

- [ ] Implementación del servicio de autenticación y generación de tokens JWT.
- [ ] Endpoints de Login y Registro de usuarios con hashing de contraseñas.
- [ ] CRUD básico de Categorías y Productos con validaciones FluentValidation.
- [ ] Creación y ejecución de la migración inicial EF Core (`InitialCreate`).
- [ ] Integración inicial de autenticación en la interfaz Angular.
