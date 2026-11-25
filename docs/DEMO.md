# Modo demo (para publicar una demostración)

La demo se compone de tres piezas: frontend en Vercel, API .NET con su base SQL Server en un hosting con soporte para .NET, y datos de ejemplo cargados por el propio backend.

## 1. Datos de demostración (backend)

Con `Demo__Enabled=true` la API, al arrancar, aplica las migraciones y carga 13 productos, 3 categorías, el stock inicial y 30 ventas recientes con su kardex. Si la base ya tiene productos no hace nada (nunca pisa datos).

Variables de entorno del hosting (no se guardan en el repositorio):

| Variable | Descripción |
|---|---|
| `Demo__Enabled` | `true` para activar el modo demo |
| `Demo__SellerPassword` | Contraseña del vendedor de demostración (12 caracteres o más). Usuario: `demo.seller@salesinventory.demo` |
| `Demo__AdminPassword` | Opcional. Si se omite, no se crea administrador y la demo pública queda sin acceso a auditoría, monitoreo ni escritura de catálogo |
| `ConnectionStrings__DefaultConnection` | Cadena de conexión de la base de la demo |
| `Jwt__Issuer`, `Jwt__Audience`, `Jwt__SigningKey` | Configuración JWT (clave aleatoria de 32 bytes o más) |
| `Cors__AllowedOrigins__0` | URL del frontend en Vercel (no hace falta si usas el rewrite `/api` del paso 2) |

Para probarlo en local (PowerShell): `$env:Demo__Enabled="true"; $env:Demo__SellerPassword="<12+ caracteres>"; dotnet run --project backend/src/SalesInventory.Api`.

## 2. Frontend en Vercel

1. En Vercel importa el repositorio y define **Root Directory = `frontend`**.
2. En `frontend/vercel.json` reemplaza `REEMPLAZA-CON-LA-URL-DE-TU-API` por el host público de tu API. Ese archivo redirige `/api/*` a la API, así que el frontend sigue llamando a `/api` del mismo origen y no se necesita configurar CORS.
3. Despliega. Entra con el usuario vendedor de demostración.

## 3. Recomendaciones

- Publica solo el usuario vendedor. Si publicas el administrador, cualquiera puede modificar datos; en ese caso reinicia la base periódicamente.
- El Worker (notificaciones de stock) es un proceso aparte: sin él la demo funciona, pero las alertas de stock no se generan.
- Indica en el README la URL y las credenciales de demostración solo cuando existan; no se inventan.
