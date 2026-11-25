# Modo demo

Hay dos formas de tener una demostración. La primera es la que se publica en Vercel.

## A. Demo solo con frontend (sin backend ni base de datos)

El frontend incluye una API simulada que corre en el navegador: datos ficticios realistas (13 productos, 3 categorías, 30 ventas recientes, kardex, notificaciones, eventos del Outbox y auditoría) guardados en `localStorage`. Se puede navegar y operar (crear productos y categorías, registrar entradas, ajustes y ventas, leer notificaciones), pero **nada llega a un servidor** y los datos son solo de ese navegador.

| | Modo normal | Modo demo |
|---|---|---|
| API | Backend real (`/api`) | Simulada en el navegador (`core/demo`) |
| Datos | SQL Server | `localStorage`, ficticios |
| Acceso | Cuenta real | Botón «Entrar como usuario demo» (usuario ficticio con rol Admin) |
| Restablecer | n/a | Botón «Restablecer datos» del aviso amarillo |
| Pantallas de monitoreo (Outbox, auditoría, estado del sistema) | Datos reales | Datos simulados coherentes con la demo; no hay Worker ni SQL Server detrás |

El modo se fija al compilar con la variable `VITE_DEMO_MODE` (`true` = demo, cualquier otro valor = API real).

### Ejecutar en local (PowerShell)

```powershell
cd frontend
npm ci
npm run start:demo        # http://localhost:4200, modo demo
npm start                 # modo normal (requiere el backend)
```

### Compilar

```powershell
$env:VITE_DEMO_MODE = "true"; npm run build     # build de producción en modo demo
npm run build:demo                              # equivalente directo
Remove-Item Env:VITE_DEMO_MODE; npm run build   # build normal (API real)
```

### Desplegar en Vercel

1. Importa el repositorio en Vercel y define **Root Directory = `frontend`**.
2. `frontend/vercel.json` ya fija el comando de build, la carpeta de salida (`dist/sales-inventory-app/browser`), `VITE_DEMO_MODE=true` y la regla que devuelve `index.html` en cualquier ruta (necesaria para las rutas de Angular).
3. Despliega. No se necesitan variables ni secretos: el usuario demo es ficticio y público.

El backend no está desplegado en este modo: no se conecta a ninguna API.

## B. Datos de ejemplo en el backend real

Con `Demo__Enabled=true` la API aplica las migraciones y carga datos de ejemplo en una base **vacía** (nunca pisa datos existentes). Variables: `Demo__SellerPassword` (obligatoria, 12 caracteres o más), `Demo__AdminPassword` (opcional) y las habituales `ConnectionStrings__DefaultConnection` y `Jwt__*`. Usuario vendedor: `demo.seller@salesinventory.demo`. Si publicas el administrador, cualquiera podría modificar datos.
