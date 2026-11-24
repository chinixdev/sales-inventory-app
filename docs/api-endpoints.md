# API de categorías y productos

La API se ejecuta localmente en `http://localhost:5235`. En desarrollo, la
especificación OpenAPI está disponible en `/openapi/v1.json` y Scalar en
`/scalar/v1`. Los errores se devuelven como `application/problem+json`.

## Categorías

| Método | Ruta | Parámetros | Éxito | Errores |
|---|---|---|---|---|
| `GET` | `/api/categories` | `activeOnly` (opcional, booleano) | `200` | — |
| `GET` | `/api/categories/{id}` | `id` (entero) | `200` | `404` |
| `POST` | `/api/categories` | JSON de creación | `201` + `Location` | `400`, `409` |
| `PUT` | `/api/categories/{id}` | `id`, JSON de actualización | `200` | `400`, `404`, `409` |
| `DELETE` | `/api/categories/{id}` | `id` | `204` | `404`, `409` |

### Crear categoría

`POST /api/categories`

```json
{
  "name": "Papelería"
}
```

Respuesta `201 Created`:

```json
{
  "id": 1,
  "name": "Papelería",
  "isActive": true,
  "productCount": 0
}
```

### Actualizar categoría

`PUT /api/categories/1`

```json
{
  "name": "Papelería y oficina",
  "isActive": true
}
```

## Productos

| Método | Ruta | Parámetros | Éxito | Errores |
|---|---|---|---|---|
| `GET` | `/api/products` | `pageNumber`, `pageSize`, `searchTerm`, `categoryId`, `activeOnly`, `lowStockOnly` (todos opcionales) | `200` | — |
| `GET` | `/api/products/{id}` | `id` (entero) | `200` | `404` |
| `POST` | `/api/products` | JSON de creación | `201` + `Location` | `400`, `404`, `409` |
| `PUT` | `/api/products/{id}` | `id`, JSON de actualización | `200` | `400`, `404`, `409` |
| `DELETE` | `/api/products/{id}` | `id` | `204` | `404` |

La paginación utiliza `pageNumber` desde 1 y limita `pageSize` a 100 (valor
predeterminado: 10). `searchTerm` busca coincidencias en nombre o SKU. El
endpoint `PUT` no recibe ni modifica `stock`.

### Crear producto con stock inicial

`POST /api/products`

```json
{
  "categoryId": 1,
  "sku": "PEN-001",
  "name": "Bolígrafo azul",
  "description": "Punta media",
  "price": 1.25,
  "initialStock": 25,
  "minimumStock": 5
}
```

La creación persiste el producto y el movimiento `IN` de stock inicial en la
misma operación de guardado de EF Core. Si el movimiento falla, la operación de
guardado se revierte.

Respuesta `201 Created`:

```json
{
  "id": 1,
  "categoryId": 1,
  "categoryName": "Papelería",
  "sku": "PEN-001",
  "name": "Bolígrafo azul",
  "description": "Punta media",
  "price": 1.25,
  "stock": 25,
  "minimumStock": 5,
  "isActive": true,
  "isBelowMinimumStock": false,
  "createdAt": "2026-11-21T20:00:00Z"
}
```

### Actualizar producto

`PUT /api/products/1`

```json
{
  "categoryId": 1,
  "sku": "PEN-001",
  "name": "Bolígrafo azul",
  "description": "Punta media",
  "price": 1.5,
  "minimumStock": 5,
  "isActive": true
}
```

### Respuesta de error

La búsqueda de un producto inexistente devuelve `404 Not Found`:

```json
{
  "type": "about:blank",
  "title": "Recurso no encontrado",
  "status": 404,
  "detail": "La entidad 'Product' con identificador (999) no fue encontrada.",
  "instance": "/api/products/999"
}
```

`400` identifica datos inválidos; `404`, un recurso relacionado o solicitado
inexistente; y `409`, duplicados o conflictos de negocio (por ejemplo, eliminar
una categoría que tiene productos). Los endpoints no incluyen autorización y
no se consideran listos para producción.

Las solicitudes reproducibles están en
[`SalesInventory.Api.http`](../backend/src/SalesInventory.Api/SalesInventory.Api.http).
