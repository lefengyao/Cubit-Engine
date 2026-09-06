@echo off
setlocal
title Cubit Sample - Minecraft-like
cd /d "%~dp0"

set "CUBIT_PROJECT_PATH=%~dp0..\我的第一个cubit项目"
set "CUBIT_SAMPLE_EXE=%~dp0hosts\Cubit.Sample\bin\Debug\net9.0\Cubit.Sample.exe"

if not exist "%CUBIT_SAMPLE_EXE%" (
    where dotnet >nul 2>&1 || (
        echo [ERROR] .NET SDK not found. Please install .NET 9.
        pause
        exit /b 1
    )

    echo Building current desktop sample...
    dotnet build "%~dp0hosts\Cubit.Sample\Cubit.Sample.csproj" -c Debug --no-restore
    if errorlevel 1 (
        echo [ERROR] Desktop sample build failed.
        pause
        exit /b 1
    )
)

echo Launching current desktop sample with the declared project...
start "" /d "%~dp0.." "%CUBIT_SAMPLE_EXE%"
exit /b 0
