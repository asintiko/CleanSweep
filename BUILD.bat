@echo off
chcp 65001 >nul
echo.
echo  ========================================
echo     CleanSweep — Сборка в .EXE
echo  ========================================
echo.

dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [!] .NET SDK не найден!
    echo     Скачайте: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [1/2] Сборка проекта...
dotnet restore --verbosity quiet
dotnet build -c Release --no-restore --verbosity quiet
if errorlevel 1 (
    echo [!] Ошибка сборки
    pause
    exit /b 1
)

echo [2/2] Создание CleanSweep.exe...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output ./publish --verbosity quiet
if errorlevel 1 (
    echo [!] Ошибка публикации
    pause
    exit /b 1
)

echo.
echo  ========================================
echo     Готово!
echo     Файл: publish\CleanSweep.exe
echo.
echo     Копируйте на любой Windows 10/11
echo     и запускайте без установки.
echo  ========================================
echo.
pause
