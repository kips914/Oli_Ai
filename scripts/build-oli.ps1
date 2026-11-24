#requires -Version 5.1
<#
    Builds a self-contained, single-file dist\OLI.exe for win-x64.

    Works on a machine without the .NET runtime installed (the runtime is bundled),
    and also emits a portable ZIP with the exe + icons for GitHub Releases.
#>
param(
    [switch]$Portable,
    [ValidateSet('auto', 'ru', 'en')]
    [string]$Language = 'auto'
)
$ErrorActionPreference = 'Stop'

function Resolve-Language([string]$value) {
    if ($value -eq 'ru') { return 'ru' }
    if ($value -eq 'en') { return 'en' }
    $culture = [System.Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName
    return $(if ($culture -eq 'en') { 'en' } else { 'ru' })
}
$Language = Resolve-Language $Language

$Strings = @{
    ru = @{ Title='OLI — сборка OLI.exe'; Missing='.NET SDK не найден. Установите .NET 8 SDK (https://dot.net) или запустите Install-OLI.ps1.'; Publishing='Публикую self-contained OLI.exe для win-x64...'; Wait='Первая сборка занимает пару минут.'; Failed='Сборка не удалась (см. вывод выше).'; Done='Готово'; MissingExe='OLI.exe не появился после сборки.' }
    en = @{ Title='OLI — building OLI.exe'; Missing='The .NET SDK was not found. Install the .NET 8 SDK (https://dot.net) or run Install-OLI.ps1.'; Publishing='Publishing a self-contained OLI.exe for win-x64...'; Wait='The first build takes a couple of minutes.'; Failed='Build failed (see the output above).'; Done='Done'; MissingExe='OLI.exe was not produced.' }
}
function T([string]$key) { return $Strings[$Language][$key] }

$root       = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $root 'OLI.Client\OLI.Client.csproj'
$publishDir = Join-Path $root 'dist'

Write-Host '======================================'
Write-Host (' ' + (T 'Title'))
Write-Host '======================================'

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Error (T 'Missing'); exit 1 }
$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks | Where-Object { $_ -match '^8\.' })) {
    Write-Host 'Внимание: .NET 8 SDK не найден, пробую собрать установленной версией SDK.'
}

Write-Host (T 'Publishing')
Write-Host (T 'Wait')
Write-Host ''

dotnet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { Write-Error (T 'Failed'); exit 1 }

$exe = Join-Path $publishDir 'OLI.exe'
if (!(Test-Path $exe)) { Write-Error ((T 'MissingExe') + " $publishDir"); exit 1 }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ''
Write-Host '======================================'
Write-Host "$(T 'Done'): $exe ($sizeMb МБ)"
Write-Host '======================================'

if ($Portable) {
    $version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
    $portableZip = Join-Path $publishDir "OLI-$version-win-x64-portable.zip"
    if (Test-Path $portableZip) { Remove-Item $portableZip -Force }
    Compress-Archive -Path $exe, (Join-Path $root 'OLI.ico'), (Join-Path $root 'Install-OLI.ps1'), `
        (Join-Path $root 'scripts'), (Join-Path $root 'README.md') -DestinationPath $portableZip
    Write-Host "Portable: $portableZip"
}

Write-Host 'Для запуска нужен движок: scripts\start-oli.ps1 (или ярлык OLI на рабочем столе).'
