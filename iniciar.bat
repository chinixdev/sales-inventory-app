@echo off
cd /d "%~dp0"

set "PROJECT=sales-inventory-system"


wt -w "%PROJECT%" ^
    new-tab --title "%PROJECT% - API" --tabColor "#E74C3C" --suppressApplicationTitle -d "%~dp0backend\src\SalesInventory.Api" cmd /k "dotnet run" ^
    ; new-tab --title "%PROJECT% - Frontend" --tabColor "#3498DB" --suppressApplicationTitle -d "%~dp0frontend" cmd /k "npm start"

exit
