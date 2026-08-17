@echo off
setlocal enabledelayedexpansion
chcp 65001 > nul

rem Swagger Dashboard'u yerelde tek tikla ayaga kaldirir.
rem
rem .NET 8 SDK varsa dogrudan calistirir. Yoksa Docker'a duser, cunku SDK kurmak
rem isteyip istemedigi kullanicinin karari; ikisi de yoksa ne indirmesi gerektigini
rem soyler. Cift tiklandiginda calisir: konum betigin kendi klasorunden alinir.

cd /d "%~dp0"

set "PORT=5238"
set "URL=http://localhost:%PORT%"
set "DB=src\SwaggerDashboard.Web\swagger-dashboard.db"

if /i "%~1"=="sifirla" (
    echo Yerel veritabani siliniyor...
    del /q "%DB%" "%DB%-shm" "%DB%-wal" 2> nul
    echo Silindi. Uygulama bos bir veritabaniyla acilacak.
    echo.
)

echo ==========================================================
echo   Swagger Dashboard - yerel calistirma
echo ==========================================================
echo.

rem --- .NET 8 SDK var mi? ------------------------------------------------------
set "HAS_SDK="
where dotnet > nul 2>&1
if !errorlevel! equ 0 (
    for /f "tokens=1 delims=." %%v in ('dotnet --list-sdks 2^>nul') do (
        if %%v geq 8 set "HAS_SDK=1"
    )
)

if defined HAS_SDK goto run_dotnet

rem --- Docker'a dus -----------------------------------------------------------
where docker > nul 2>&1
if !errorlevel! equ 0 goto run_docker

echo .NET 8 SDK da Docker da bulunamadi.
echo.
echo Ikisinden birini kurun:
echo   .NET 8 SDK : https://dotnet.microsoft.com/download/dotnet/8.0
echo   Docker     : https://www.docker.com/products/docker-desktop/
echo.
pause
exit /b 1

:run_dotnet
echo .NET SDK bulundu, uygulama derleniyor...
echo.
echo   Adres    : %URL%
echo   Kullanici: admin
echo   Parola   : development-only-password
echo.
echo Durdurmak icin bu pencerede Ctrl+C.
echo.

rem Tarayici acilisini launchSettings icindeki "http" profili yapar.
dotnet run --project src\SwaggerDashboard.Web --launch-profile http
if !errorlevel! neq 0 (
    echo.
    echo Uygulama hatayla kapandi. Yukaridaki mesaja bakin.
    pause
)
exit /b !errorlevel!

:run_docker
echo .NET SDK yok, Docker ile calistiriliyor. Ilk derleme birkac dakika surebilir...
echo.
echo   Adres    : %URL%
echo   Kullanici: admin
echo   Parola   : development-only-password
echo.
echo Durdurmak icin bu pencerede Ctrl+C.
echo.

start "" "%URL%"
docker compose -f docker-compose.local.yml up --build
exit /b !errorlevel!
