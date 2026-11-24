# Guía de ejecución local (Windows + PowerShell)

Sin Docker. SQL Server instalado directamente en tu equipo. Todos los comandos son de PowerShell.

> Estado: el backend (.NET 10) aún no se ha compilado ni probado de extremo a extremo. Esta guía incluye ese paso de validación (secciones 6 a 10).

## 1. Clonar y cambiar de rama

```powershell
git clone https://github.com/chinixdev/sales-inventory-app.git
cd sales-inventory-app
git fetch origin
git switch rama-prueba
```

## 2. Verificar requisitos

```powershell
git --version
dotnet --list-sdks        # debe aparecer 10.0.x
node --version            # Node.js LTS (22 o superior)
npm --version
dotnet ef --version       # si falta: dotnet tool install --global dotnet-ef
Get-Service | Where-Object { $_.Name -like "MSSQL*" }   # el servicio de SQL Server debe estar Running
```

Instala lo que falte: .NET SDK 10 (https://dotnet.microsoft.com/download/dotnet/10.0), Node.js LTS, SQL Server Express/Developer.

## 3. Configurar SQL Server

Comprueba que tu instancia responde (ajusta el nombre de la instancia si no es la predeterminada):

```powershell
sqlcmd -S localhost -E -Q "SELECT @@VERSION"          # autenticación de Windows
# instancia con nombre, por ejemplo: sqlcmd -S .\SQLEXPRESS -E -Q "SELECT 1"
```

La base `SalesInventoryDb` se crea al aplicar las migraciones (sección 6). Si usas otra instancia o usuario SQL, define tu cadena de conexión como secreto (sección 4); nunca la escribas en archivos del repositorio.

## 4. Credenciales con User Secrets

Los secretos quedan fuera del repositorio (en tu perfil de Windows). El Worker usa los mismos secretos que la API.

```powershell
$api = "backend/src/SalesInventory.Api"

# Cadena de conexión (ejemplo con autenticación de Windows; ajusta Server si usas una instancia con nombre)
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=SalesInventoryDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True" --project $api

# JWT
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
dotnet user-secrets set "Jwt:Issuer" "SalesInventory.Api" --project $api
dotnet user-secrets set "Jwt:Audience" "SalesInventory.Client" --project $api
dotnet user-secrets set "Jwt:SigningKey" $key --project $api

# Administrador inicial (solo se crea en Development y si aún no existe un Admin)
dotnet user-secrets set "DevelopmentAdmin:Email" "admin@local.test" --project $api
dotnet user-secrets set "DevelopmentAdmin:Password" "<elige-una-contraseña-segura>" --project $api

dotnet user-secrets list --project $api    # verifica (muestra los valores: no compartas la salida)
```

## 5. Restaurar y compilar

```powershell
dotnet restore backend/SalesInventory.sln
dotnet build backend/SalesInventory.sln -c Release --no-restore

cd frontend
npm ci
cd ..
```

## 6. Aplicar migraciones

```powershell
dotnet ef database update `
  --project backend/src/SalesInventory.Infrastructure `
  --startup-project backend/src/SalesInventory.Api
```

Las migraciones recientes (trazabilidad del Outbox, latidos del Worker) están escritas a mano. Comprueba que coinciden con el modelo:

```powershell
dotnet ef migrations has-pending-model-changes `
  --project backend/src/SalesInventory.Infrastructure `
  --startup-project backend/src/SalesInventory.Api
```

Si informa cambios pendientes, avísalo antes de continuar.

## 7. Iniciar API, Worker y Angular

Abre tres terminales de PowerShell en la raíz del repositorio.

```powershell
# Terminal 1 - API (http://localhost:5235)
dotnet run --project backend/src/SalesInventory.Api

# Terminal 2 - Worker (procesa el Outbox; opcional pero necesario para las notificaciones)
dotnet run --project backend/src/SalesInventory.Worker

# Terminal 3 - Angular (http://localhost:4200)
cd frontend
npm start
```

Comprobaciones rápidas: `Invoke-RestMethod http://localhost:5235/health/ready` debe devolver `Healthy`. Inicia sesión en http://localhost:4200 con el Admin configurado en la sección 4. La documentación de la API (solo Development) está en http://localhost:5235/scalar/v1.

## 8. Pruebas unitarias y de integración

```powershell
# Unitarias
dotnet test backend/tests/SalesInventory.UnitTests -c Release

# Integración (necesitan SQL Server; crean y eliminan su propia base temporal)
$env:SALESINVENTORY_TEST_CONNECTION = "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True"
dotnet test backend/tests/SalesInventory.IntegrationTests -c Release

# Frontend (usa PowerShell; desde Git Bash Vitest puede fallar)
cd frontend
npx ng build
npx ng test --watch=false --no-progress
```

La cuenta usada en `SALESINVENTORY_TEST_CONNECTION` debe poder crear y eliminar bases de datos. Si no defines la variable, se usa la cadena de `appsettings.Development.json`.

## 9. Pruebas de rendimiento con k6

Usa **solo** una API de pruebas y una base desechable: crean ventas y movimientos reales.

```powershell
winget install k6 --source winget      # reinicia PowerShell después de instalar

$env:BASE_URL = "http://localhost:5235"
$env:ADMIN_EMAIL = "admin@local.test"
$env:ADMIN_PASSWORD = "<la-contraseña-del-admin>"
$env:PERF_CONFIRM_TEST_ENV = "yes"     # confirma que NO es producción

k6 run tests/performance/smoke.js
k6 run tests/performance/load.js
k6 run tests/performance/concurrency.js
# k6 run tests/performance/stress.js   # bajo demanda
```

Los resultados quedan en `tests/performance/results/` (no se versionan). Los umbrales son valores iniciales configurables, no resultados medidos.

## 10. Solución de errores comunes

| Síntoma | Causa y solución |
|---|---|
| La API no arranca con un error de configuración JWT | Faltan `Jwt:Issuer`, `Jwt:Audience` o `Jwt:SigningKey` (mínimo 32 bytes). Repite la sección 4. |
| `Cannot open database` / error de login en SQL Server | Verifica el servicio `MSSQL*`, el nombre de instancia en `Server=` y que tu usuario de Windows tenga acceso. Ejecuta antes las migraciones (sección 6). |
| `dotnet ef` no se reconoce | `dotnet tool install --global dotnet-ef` y reabre PowerShell. |
| `The SDK 'Microsoft.NET.Sdk' ... net10.0` / `NETSDK1045` | No está instalado .NET SDK 10. `dotnet --list-sdks`. |
| No existe usuario Admin / 401 al iniciar sesión | Define `DevelopmentAdmin:*` y reinicia la API en Development (el Admin solo se crea si no existe ninguno). |
| El frontend no llega a la API | La API debe estar en `http://localhost:5235` y `Cors:AllowedOrigins` incluir `http://localhost:4200` (valor por defecto). |
| Vitest: `failed to find the runner` | Ejecuta `ng test` desde PowerShell, no desde Git Bash. |
| Las notificaciones no aparecen | El Worker no está corriendo: inícialo (sección 7). |
| Pruebas de integración: no se encuentra la cadena | Define `SALESINVENTORY_TEST_CONNECTION` (sección 8). |
| k6: `Protección: ...` | Falta `PERF_CONFIRM_TEST_ENV=yes` o el host no es local. Es una protección intencional. |

## 11. Subir tus futuros cambios a GitHub

```powershell
git status
git switch rama-prueba
git add -A
git status                       # revisa que no haya secretos ni archivos locales
git commit -m "feat: descripción breve del cambio"
git push origin rama-prueba
```

No uses `git push --force`. Para integrar en `main`, abre un Pull Request desde `rama-prueba` en GitHub y revísalo antes de fusionarlo. Si Git rechaza el push porque la rama remota avanzó: `git pull --rebase origin rama-prueba` y vuelve a hacer push.
