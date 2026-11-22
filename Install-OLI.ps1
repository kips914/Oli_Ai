#requires -Version 5.1
<#
    OLI one-click installer.

      powershell -ExecutionPolicy Bypass -File .\Install-OLI.ps1

    1. Ensures the .NET 8 SDK (winget → official dotnet-install script)
    2. Installs llama.cpp (CUDA / Vulkan / CPU chosen automatically)
    3. Publishes dist\OLI.exe (self-contained, single file)
    4. Creates Desktop + Start Menu shortcuts that start the engine then OLI
#>
param(
    [switch]$Update,
    [ValidateSet('auto', 'ru', 'en')]
    [string]$Language = 'auto',
    [ValidateSet('auto', 'cuda', 'vulkan', 'cpu')]
    [string]$Backend = 'auto'
)
$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$scripts = Join-Path $root 'scripts'
$icon    = Join-Path $root 'installer\OLI_Setup.ico'

function Resolve-Language([string]$value) {
    if ($value -eq 'ru') { return 'ru' }
    if ($value -eq 'en') { return 'en' }
    $culture = [System.Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName
    return $(if ($culture -eq 'en') { 'en' } else { 'ru' })
}
$Language = Resolve-Language $Language

$Strings = @{
    ru = @{
        Start='Запуск установки OLI...'; DotNetMissing='Не удалось автоматически установить .NET 8 SDK.'
        Winget='Устанавливаю .NET 8 SDK через winget...'; DotNetScript='Устанавливаю .NET 8 SDK официальным скриптом...'
        LlamaFail='Установка llama.cpp завершилась с ошибкой.'; BuildFail='Сборка OLI.exe завершилась с ошибкой.'
        Done='Установка OLI завершена.'; Shortcut='Ярлык'; CreateShortcuts='Создаю ярлыки...'
    }
    en = @{
        Start='Starting the OLI setup...'; DotNetMissing='Could not install the .NET 8 SDK automatically.'
        Winget='Installing the .NET 8 SDK via winget...'; DotNetScript='Installing the .NET 8 SDK with the official script...'
        LlamaFail='llama.cpp setup failed.'; BuildFail='OLI.exe build failed.'
        Done='OLI setup complete.'; Shortcut='Shortcut'; CreateShortcuts='Creating shortcuts...'
    }
}
function T([string]$key) { return $Strings[$Language][$key] }

function Test-DotNet8Sdk {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    $output = & dotnet --list-sdks 2>$null
    return [bool]($output | Where-Object { $_ -match '^8\.' })
}

Write-Host '======================================'
Write-Host ' OLI 1.4 — installer'
Write-Host '======================================'
Write-Host (T 'Start')

if (-not $Update) {
    if (!(Test-DotNet8Sdk)) {
        if (Get-Command winget -ErrorAction SilentlyContinue) {
            Write-Host (T 'Winget')
            winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
        } else {
            Write-Host (T 'DotNetScript')
            $installerScript = Join-Path $env:TEMP 'dotnet-install.ps1'
            Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerScript -UseBasicParsing
            $installDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
            & $installerScript -Channel 8.0 -InstallDir $installDir
            $env:PATH = "$installDir;$env:PATH"
        }
        $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                    [Environment]::GetEnvironmentVariable('PATH', 'User')
        if (!(Test-DotNet8Sdk)) { throw (T 'DotNetMissing') }
    }

    & (Join-Path $scripts 'install-oli.ps1') -Language $Language -Backend $Backend
    if ($LASTEXITCODE -ne 0) { throw (T 'LlamaFail') }
}

& (Join-Path $scripts 'build-oli.ps1') -Language $Language
if ($LASTEXITCODE -ne 0) { throw (T 'BuildFail') }

Write-Host (T 'CreateShortcuts')
$shell      = New-Object -ComObject WScript.Shell
$desktop    = [Environment]::GetFolderPath('Desktop')
$startMenu  = Join-Path ([Environment]::GetFolderPath('Programs')) 'OLI'
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null

# A tiny launcher: starts the engine, waits for it, then runs OLI.exe.
$launcherCmd = Join-Path $root 'OLI.cmd'
@"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-oli.ps1"
"@ | Set-Content -Path $launcherCmd -Encoding ASCII

foreach ($dir in @($desktop, $startMenu)) {
    $shortcut = $shell.CreateShortcut((Join-Path $dir 'OLI.lnk'))
    $shortcut.TargetPath = $launcherCmd
    $shortcut.WorkingDirectory = $root
    $exe = Join-Path $root 'dist\OLI.exe'
    if (Test-Path $exe) { $shortcut.IconLocation = "$exe,0" }
    elseif (Test-Path $icon) { $shortcut.IconLocation = "$icon,0" }
    $shortcut.Description = if ($Language -eq 'en') { 'OLI - local AI assistant' } else { 'OLI — локальный ИИ-помощник' }
    $shortcut.Save()
}

Write-Host ''
Write-Host '======================================'
Write-Host (' ' + (T 'Done'))
Write-Host '======================================'
Write-Host 'Запуск: ярлык «OLI» на рабочем столе.'
Write-Host 'Первый запуск скачает модель Qwen3.5 4B (~2.9 ГБ) в models\.'
