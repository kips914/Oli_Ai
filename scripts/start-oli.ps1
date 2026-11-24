#requires -Version 5.1
<#
    One-click start: engine + OLI.exe.
      * installs llama.cpp if missing
      * starts llama-server in a hidden window (skips if 8080 already answers)
      * waits for the model to load, then launches OLI.exe
#>
param(
    [switch]$Cpu,
    [string]$Model = '',
    [switch]$NoWait
)
$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root 'third_party\llama.cpp\build\bin\Release\llama-server.exe'

function Test-EngineUp {
    try {
        $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/health' -UseBasicParsing -TimeoutSec 2
        return $response.StatusCode -eq 200
    } catch { return $false }
}

if (-not (Test-Path $server)) { & (Join-Path $PSScriptRoot 'install-oli.ps1') }

if (Test-EngineUp) {
    Write-Host 'Движок уже запущен на http://127.0.0.1:8080'
} else {
    $llamaArgs = "-ExecutionPolicy Bypass -File `"$root\scripts\start-llama.ps1`""
    if ($Model) { $llamaArgs += " -Model `"$Model`"" }
    if ($Cpu) { $llamaArgs += ' -Cpu' }
    Start-Process powershell -WindowStyle Hidden -ArgumentList $llamaArgs
    Write-Host 'Движок запускается (первый запуск скачивает модель — это долго)...'

    if (-not $NoWait) {
        $deadline = (Get-Date).AddMinutes(15)
        while ((Get-Date) -lt $deadline) {
            if (Test-EngineUp) { break }
            Start-Sleep -Seconds 3
            Write-Host '.' -NoNewline
        }
        Write-Host ''
        if (-not (Test-EngineUp)) {
            Write-Host 'Движок не поднялся за 15 минут. Лог: %LOCALAPPDATA%\OLI\logs\engine.log' -ForegroundColor Yellow
            Write-Host 'Запустите скрипт вручную, чтобы увидеть ошибку:'
            Write-Host "  powershell -ExecutionPolicy Bypass -File `"$root\scripts\start-llama.ps1`""
        }
    }
}

$exe = Join-Path $root 'dist\OLI.exe'
if (Test-Path $exe) {
    Start-Process $exe
} else {
    Write-Host "OLI.exe ещё не собран — запускаю через 'dotnet run'."
    dotnet run --project (Join-Path $root 'OLI.Client\OLI.Client.csproj') -c Release
}
