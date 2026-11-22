#requires -Version 5.1
<#
    Installs the llama.cpp inference engine for OLI.

    Picks the best prebuilt Windows x64 build for the machine:
      * NVIDIA GPU  -> CUDA 12.4 build (+ CUDA runtime DLLs)
      * otherwise   -> Vulkan build (works on AMD/Intel iGPU) or CPU build

    Everything lands in third_party\llama.cpp\build\bin\Release so
    scripts\start-llama.ps1 can find llama-server.exe.
#>
param(
    [ValidateSet('auto', 'cuda', 'vulkan', 'cpu')]
    [string]$Backend = 'auto',
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
    ru = @{
        Title='OLI — установка движка llama.cpp'; Detecting='Определяю видеокарту...'
        BackendCuda='Найдена NVIDIA: ставлю CUDA-сборку'; BackendVulkan='Ставлю Vulkan-сборку (работает на AMD/Intel)'
        BackendCpu='Ставлю CPU-сборку'; Searching='Ищу совместимый релиз llama.cpp...'
        None='Не удалось найти подходящую Windows x64 сборку llama.cpp в последних релизах.'
        Selected='Выбран релиз'; Downloading='Скачивание llama.cpp...'; Runtime='Скачивание CUDA runtime...'
        Missing='llama-server.exe не найден в архиве.'; Success='llama.cpp успешно установлена.'; Server='Сервер'
        NoToolkit='Полный CUDA Toolkit не нужен — используется готовый runtime.'; Driver='Используется установленный драйвер.'
        GitHubBlocked='GitHub API недоступен. Если вы в РФ, включите VPN или задайте зеркало: $env:OLI_GITHUB_MIRROR'
    }
    en = @{
        Title='OLI — llama.cpp engine setup'; Detecting='Detecting GPU...'
        BackendCuda='NVIDIA detected: installing the CUDA build'; BackendVulkan='Installing the Vulkan build (AMD/Intel friendly)'
        BackendCpu='Installing the CPU build'; Searching='Looking for a compatible llama.cpp release...'
        None='No compatible Windows x64 llama.cpp build found in recent releases.'
        Selected='Release'; Downloading='Downloading llama.cpp...'; Runtime='Downloading CUDA runtime...'
        Missing='llama-server.exe was not found in the archive.'; Success='llama.cpp installed successfully.'; Server='Server'
        NoToolkit='A full CUDA Toolkit is not required with this prebuilt runtime.'; Driver='The installed driver is used.'
        GitHubBlocked='GitHub API is unreachable. If you are in Russia, enable a VPN or set $env:OLI_GITHUB_MIRROR'
    }
}
function T([string]$key) { return $Strings[$Language][$key] }

$root   = Split-Path -Parent $PSScriptRoot
$binDir = Join-Path $root 'third_party\llama.cpp\build\bin\Release'
$server = Join-Path $binDir 'llama-server.exe'

Write-Host '======================================'
Write-Host (' ' + (T 'Title'))
Write-Host '======================================'

if ($Backend -eq 'auto') {
    Write-Host (T 'Detecting')
    $gpus = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue
    $nvidia = $gpus | Where-Object { $_.Name -match 'NVIDIA' } | Select-Object -First 1
    if ($nvidia) {
        $Backend = 'cuda'
        Write-Host "$(T 'BackendCuda'): $($nvidia.Name) / $($nvidia.DriverVersion)"
    } else {
        $other = $gpus | Where-Object { $_.Name -match 'AMD|Radeon|Intel' } | Select-Object -First 1
        if ($other) { $Backend = 'vulkan'; Write-Host "$(T 'BackendVulkan'): $($other.Name)" }
        else { $Backend = 'cpu'; Write-Host (T 'BackendCpu') }
    }
} else {
    Write-Host "Backend: $Backend"
}

$headers = @{ 'User-Agent' = 'OLI-Installer-1.4' }
Write-Host (T 'Searching')

$apiBase = 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=15'
if ($env:OLI_GITHUB_MIRROR) { $apiBase = $env:OLI_GITHUB_MIRROR.TrimEnd('/') + '/repos/ggml-org/llama.cpp/releases?per_page=15' }
try {
    $releases = Invoke-RestMethod -Uri $apiBase -Headers $headers
} catch {
    Write-Error ((T 'GitHubBlocked') + "`n" + $_.Exception.Message)
    exit 1
}

$patterns = switch ($Backend) {
    'cuda'   { @('llama-b*-bin-win-cuda-12.4-x64.zip', 'llama-b*-bin-win-cuda-12.2-x64.zip') }
    'vulkan' { @('llama-b*-bin-win-vulkan-x64.zip') }
    default  { @('llama-b*-bin-win-cpu-x64.zip', 'llama-b*-bin-win-avx2-x64.zip') }
}

$binary = $null; $runtime = $null; $selectedRelease = $null
foreach ($release in $releases) {
    foreach ($pattern in $patterns) {
        $candidate = $release.assets | Where-Object { $_.name -like $pattern } | Select-Object -First 1
        if ($candidate) { $binary = $candidate; $selectedRelease = $release; break }
    }
    if ($binary) { break }
}
if (-not $binary) { Write-Error (T 'None'); exit 1 }

if ($Backend -eq 'cuda') {
    $runtime = $releases.assets | Where-Object { $_.name -like 'cudart-llama-bin-win-cuda-12.4-x64.zip' } | Select-Object -First 1
    if (-not $runtime) {
        foreach ($release in $releases) {
            $runtime = $release.assets | Where-Object { $_.name -like 'cudart-llama-bin-win-cuda-*-x64.zip' } | Select-Object -First 1
            if ($runtime) { break }
        }
    }
}

Write-Host "$(T 'Selected'): $($selectedRelease.tag_name)"
Write-Host "Бинарник: $($binary.name)"

$thirdParty = Join-Path $root 'third_party\llama.cpp'
$tempDir    = Join-Path $thirdParty 'download'
$binaryZip  = Join-Path $thirdParty 'llama.zip'
New-Item -ItemType Directory -Force -Path $thirdParty | Out-Null
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

function Get-Asset([string]$url, [string]$outFile) {
    $mirrorUrl = $url
    if ($env:OLI_GITHUB_MIRROR) {
        $mirrorUrl = $env:OLI_GITHUB_MIRROR.TrimEnd('/') + '/' + $url.Substring('https://github.com/'.Length)
    }
    try {
        Invoke-WebRequest -Uri $mirrorUrl -OutFile $outFile -UseBasicParsing
    } catch {
        Write-Host "Зеркало не сработало, пробую напрямую: $($_.Exception.Message)"
        Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing
    }
}

Write-Host (T 'Downloading')
Get-Asset $binary.browser_download_url $binaryZip

if ($runtime) {
    Write-Host (T 'Runtime')
    $runtimeZip = Join-Path $thirdParty 'cudart.zip'
    Get-Asset $runtime.browser_download_url $runtimeZip
}

if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
Expand-Archive -Path $binaryZip -DestinationPath $tempDir -Force
if ($runtime) {
    $runtimeExtract = Join-Path $tempDir '_cudart'
    New-Item -ItemType Directory -Force -Path $runtimeExtract | Out-Null
    Expand-Archive -Path $runtimeZip -DestinationPath $runtimeExtract -Force
    Get-ChildItem $runtimeExtract -Recurse -Filter '*.dll' -File | Copy-Item -Destination $binDir -Force
}

$found = Get-ChildItem $tempDir -Recurse -Filter 'llama-server.exe' -File | Select-Object -First 1
if (-not $found) { Write-Error (T 'Missing'); exit 1 }
Copy-Item $found.FullName $server -Force
Get-ChildItem $found.Directory.FullName -Filter '*.dll' -File | Copy-Item -Destination $binDir -Force

Write-Host ''
Write-Host '======================================'
Write-Host (' ' + (T 'Success'))
Write-Host '======================================'
Write-Host "$(T 'Server'): $server"
if ($Backend -eq 'cuda') { Write-Host (T 'NoToolkit'); Write-Host (T 'Driver') }
Write-Host ''
Write-Host 'Проверка: llama-server.exe --version'
& $server --version
