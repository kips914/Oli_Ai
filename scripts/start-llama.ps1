#requires -Version 5.1
<#
    Starts the llama.cpp server with the model selected in OLI settings.

    Model selection order:
      1. -Model <id|hf-spec> CLI argument
      2. settings.json  ->  ModelGguf
      3. models.json    ->  default  (Qwen3.5 4B Q4_K_M)

    The GGUF is fetched by llama-server itself via -hf and cached under models\.
#>
param(
    [string]$Model = '',
    [switch]$Cpu,
    [switch]$NoDownload
)
$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $PSScriptRoot
$server     = Join-Path $root 'third_party\llama.cpp\build\bin\Release\llama-server.exe'
$modelsDir  = Join-Path $root 'models'
$dataRoot   = Join-Path $env:LOCALAPPDATA 'OLI'
$settings   = Join-Path $dataRoot 'settings.json'
$catalog    = Join-Path $PSScriptRoot 'models.json'

if (!(Test-Path $server)) {
    Write-Host 'llama-server.exe не найден — запускаю установку...'
    & (Join-Path $PSScriptRoot 'install-llama.ps1')
}
if (!(Test-Path $server)) { throw 'llama-server.exe отсутствует после установки.' }
New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null

# ---------------------------------------------------------------- model choice
$gguf = $null
if ($Model) { $gguf = $Model }
if (-not $gguf -and (Test-Path $settings)) {
    try {
        $s = Get-Content $settings -Raw | ConvertFrom-Json
        if ($s.ModelGguf) { $gguf = $s.ModelGguf }
    } catch { }
}
if (-not $gguf -and (Test-Path $catalog)) {
    $map = Get-Content $catalog -Raw | ConvertFrom-Json
    $default = $map.models | Where-Object { $_.id -eq $map.default } | Select-Object -First 1
    if ($default) { $gguf = $default.gguf }
}
if (-not $gguf) { $gguf = 'unsloth/Qwen3.5-4B-GGUF:Q4_K_M' }

# Allow a short id (e.g. qwen3.5-2b-q4km) instead of a full HF spec.
if (Test-Path $catalog) {
    $map = Get-Content $catalog -Raw | ConvertFrom-Json
    $match = $map.models | Where-Object { $_.id -eq $gguf } | Select-Object -First 1
    if ($match) { $gguf = $match.gguf }
}

# ---------------------------------------------------------------- runtime knobs
$ctx = 8192; $gpu = 99; $threads = 8; $batch = 128; $ubatch = 64; $parallel = 1
$temp = 0.7; $topP = 0.95; $topK = 20; $repeat = 1.05; $thinking = $false
if (Test-Path $settings) {
    try {
        $s = Get-Content $settings -Raw | ConvertFrom-Json
        if ($s.ContextSize)   { $ctx = [int]$s.ContextSize }
        if ($null -ne $s.GpuLayers -and [int]$s.GpuLayers -ge 0) { $gpu = [int]$s.GpuLayers }
        if ($s.CpuThreads -ge 1) { $threads = [int]$s.CpuThreads }
        if ($s.BatchSize -ge 1)  { $batch = [int]$s.BatchSize }
        if ($s.UBatchSize -ge 1) { $ubatch = [int]$s.UBatchSize }
        if ($s.Parallel -ge 1)   { $parallel = [int]$s.Parallel }
        if ($null -ne $s.Temperature)    { $temp = [double]$s.Temperature }
        if ($null -ne $s.TopP)           { $topP = [double]$s.TopP }
        if ($null -ne $s.TopK)           { $topK = [int]$s.TopK }
        if ($null -ne $s.RepeatPenalty)  { $repeat = [double]$s.RepeatPenalty }
        if ($s.ThinkingEnabled -eq $true) { $thinking = $true }
    } catch { }
}
if (-not (Test-Path $settings)) {
    # No settings yet: pick CPU-only on machines without an NVIDIA GPU.
    $nvidia = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -match 'NVIDIA|Radeon|Intel Arc' } | Select-Object -First 1
    if (-not $nvidia) { $gpu = 0 }
}
if ($Cpu) { $gpu = 0 }

# Qwen3.5 honours /think and /no_think control tokens.
$thinkFlag = if ($thinking) { '/think' } else { '/no_think' }

Write-Host '======================================'
Write-Host " OLI engine — $gguf"
Write-Host '======================================'
Write-Host " ctx=$ctx  gpu-layers=$gpu  threads=$threads  batch=$batch  ubatch=$ubatch  parallel=$parallel"
Write-Host " sampling: temp=$temp top_p=$topP top_k=$topK repeat=$repeat  thinking=$thinking"

$arguments = @(
    '-hf', $gguf,
    '--models-dir', $modelsDir,
    '--host', '127.0.0.1',
    '--port', '8080',
    '--ctx-size', $ctx,
    '--n-gpu-layers', $gpu,
    '--batch-size', $batch,
    '--ubatch-size', $ubatch,
    '--threads', $threads,
    '--parallel', $parallel,
    '--temp', $temp,
    '--top-p', $topP,
    '--top-k', $topK,
    '--repeat-penalty', $repeat,
    '--chat-template-kwargs', "{`"enable_thinking`": $($thinking.ToString().ToLower())}",
    '--jinja',
    '--alias', 'oli'
)
if ($NoDownload) { $arguments += '--no-download' }

# Log everything to %LOCALAPPDATA%\OLI\logs\engine.log so a hidden start is debuggable.
$logDir = Join-Path $env:LOCALAPPDATA 'OLI\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir 'engine.log'

Write-Host "Лог движка: $logFile"
& $server @arguments *>&1 | Tee-Object -FilePath $logFile -Append
$exit = $LASTEXITCODE
if ($exit -ne 0) {
    Write-Host ''
    Write-Host "!!! llama-server завершился с кодом $exit." -ForegroundColor Red
    Write-Host "Смотрите $logFile"
    Write-Host 'Частые причины: не скачалась модель (нет интернета), не хватает VRAM,'
    Write-Host 'повреждённые DLL CUDA. Попробуйте: scripts\start-llama.ps1 -Cpu'
    Read-Host 'Нажмите Enter, чтобы закрыть окно'
}
