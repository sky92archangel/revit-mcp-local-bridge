# build.ps1 — 单一 Revit 版本编译脚本
# build.ps1 — Single Revit version build script

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,
    [string]$OutputDirectory,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── 1. 加载版本清单 ──
$manifestPath = Join-Path $PSScriptRoot 'build\version-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "版本清单未找到: $manifestPath"
}
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$versionConfig = $manifest.versions | Where-Object { $_.year -eq [int]$RevitVersion }
if ($null -eq $versionConfig) {
    $allYears = ($manifest.versions | ForEach-Object { $_.year }) -join ', '
    throw "版本清单中未定义 Revit $RevitVersion。可用版本: $allYears"
}

if ($versionConfig.compiler -ne 'dotnet') {
    throw "不支持的编译器类型: $($versionConfig.compiler)。仅支持 dotnet。"
}

# ── 2. 映射配置名称（如 Revit 2026 → "Release R26"）──
$configSuffix = "R" + $RevitVersion.Substring(2)
$configName = "Release $configSuffix"

# ── 3. 输出目录 ──
$outputDir = if ($OutputDirectory) {
    $OutputDirectory
} else {
    Join-Path $PSScriptRoot "dist\RevitCommandBridge-$RevitVersion"
}
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$assemblyPath = Join-Path $outputDir 'RevitCommandBridge.dll'

# ── 4. dotnet 编译 ──
$projectFile = Join-Path $PSScriptRoot $versionConfig.project_file
Write-Host "[dotnet] SDK version: $((dotnet --version 2>&1).Trim())"
Write-Host "[dotnet] 编译 Revit $RevitVersion ($($versionConfig.runtime)) ..."
Write-Host "[dotnet] 配置: $configName"

dotnet build $projectFile --configuration $configName `
    -p:OutputPath="$outputDir" `
    -p:AssemblyName=RevitCommandBridge

if ($LASTEXITCODE -ne 0) {
    throw "Revit $RevitVersion 编译失败 (dotnet exit code: $LASTEXITCODE)"
}
Write-Host "[dotnet] $assemblyPath"

# ── 5. 清理 NuGet 运行时产物 ──
Get-ChildItem -LiteralPath $outputDir -Directory |
    Where-Object { $_.Name -match '^[a-z]{2}-[A-Z]{2}$' } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
$publishDir = Join-Path $outputDir 'publish'
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
Get-ChildItem -LiteralPath $outputDir -File -Filter '*.dll' |
    Where-Object { $_.Name -ne 'RevitCommandBridge.dll' } |
    Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $outputDir -File -Filter '*.pdb' |
    Where-Object { $_.Name -ne 'RevitCommandBridge.pdb' } |
    Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $outputDir -File -Filter '*.deps.json' |
    Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $outputDir -File -Filter '*.xml' |
    Where-Object { $_.BaseName -match '^RevitAPI' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

# ── 6. 公共打包步骤 ──
foreach ($directoryName in @('scripts', 'examples', 'deploy', 'schemas', 'src', 'verification')) {
    $source = Join-Path $PSScriptRoot $directoryName
    if (Test-Path -LiteralPath $source) {
        $destination = Join-Path $outputDir $directoryName
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $destination -Recurse -Force
    }
}

foreach ($fileName in @('README.md', 'PROTOCOL.md', 'ARCHITECTURE.md',
    'ENGINEERING-RECORD.md', 'VERSION-SUPPORT.md', 'CONNECTORS.md',
    'install-revit.ps1', 'uninstall-revit.ps1'))
{
    $source = Join-Path $PSScriptRoot $fileName
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $outputDir $fileName) -Force
    }
}

# ── 7. 版本元数据 ──
$entryClass = 'RevitCommandBridge.' + $versionConfig.entry_class
$metadata = [ordered]@{
    product       = 'RevitCommandBridge'
    revit_version = $RevitVersion
    protocol      = 'revit-command-bridge/2.0'
    runtime       = $versionConfig.runtime
    entry_class   = $entryClass
}
$metadata | ConvertTo-Json | Set-Content (Join-Path $outputDir 'bridge.config.json') -Encoding UTF8

# ── 8. 可选安装器 ──
if (-not $SkipInstaller.IsPresent) {
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -DistDirectory (Join-Path $PSScriptRoot 'dist') | Out-Host
}

Write-Host "Build completed for Revit ${RevitVersion}: $assemblyPath"
