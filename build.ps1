[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,
    [string]$RevitInstallDirectory,
    [string]$OutputDirectory,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── 1. 加载版本清单 ──
$manifestPath = Join-Path $PSScriptRoot 'build\version-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "版本清单未找到: $manifestPath。"
}
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$versionConfig = $manifest.versions | Where-Object { $_.year -eq [int]$RevitVersion }
if ($null -eq $versionConfig) {
    throw "版本清单中未定义 Revit $RevitVersion，请在 build/version-manifest.json 中添加。"
}

# 处理继承：若 inherits_from 存在，从父版本复制 references
$inheritsFrom = $null
if (Get-Member -InputObject $versionConfig -Name 'inherits_from' -MemberType Properties) {
    $inheritsFrom = $versionConfig.inherits_from
}
if ($inheritsFrom) {
    $parentConfig = $manifest.versions | Where-Object { $_.year -eq [int]$inheritsFrom }
    if ($parentConfig) {
        if (-not (Get-Member -InputObject $versionConfig -Name 'framework_references' -MemberType Properties)) {
            $versionConfig | Add-Member -NotePropertyName 'framework_references' -NotePropertyValue $parentConfig.framework_references
        }
        if (-not (Get-Member -InputObject $versionConfig -Name 'wpf_references' -MemberType Properties)) {
            $versionConfig | Add-Member -NotePropertyName 'wpf_references' -NotePropertyValue $parentConfig.wpf_references
        }
    }
}

# ── 2. 解析 RevitAPI 路径 ──
if ([string]::IsNullOrWhiteSpace($RevitInstallDirectory)) {
    $standardDirectory = Join-Path $env:ProgramFiles "Autodesk\Revit $RevitVersion"
    if (Test-Path -LiteralPath (Join-Path $standardDirectory 'RevitAPI.dll')) {
        $RevitInstallDirectory = $standardDirectory
    } else {
        throw "Revit $RevitVersion API 未找到。请通过 -RevitInstallDirectory 参数指定 RevitAPI.dll 所在目录。"
    }
}

$revitApi = Join-Path $RevitInstallDirectory 'RevitAPI.dll'
$revitApiUi = Join-Path $RevitInstallDirectory 'RevitAPIUI.dll'
$outputDir = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $PSScriptRoot "dist\RevitCommandBridge-$RevitVersion" }
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$assemblyPath = Join-Path $outputDir 'RevitCommandBridge.dll'

# ── 3. 按编译器分派 ──
switch ($versionConfig.compiler) {
    'dotnet' {
        $projectFile = Join-Path $PSScriptRoot $versionConfig.project_file
        if (-not (Test-Path -LiteralPath $projectFile)) {
            throw "项目文件未找到: $projectFile"
        }

        $dotnetVersion = dotnet --version 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw ".NET SDK 未安装。请安装 .NET 8/10 SDK。"
        }
        Write-Host "[dotnet] SDK version: $($dotnetVersion.Trim())"

        Write-Host "[dotnet] 编译 Revit $RevitVersion ($($versionConfig.runtime)) ..."
        dotnet build $projectFile --configuration Release `
            -p:RevitAPI="$revitApi" `
            -p:RevitAPIUI="$revitApiUi" `
            -p:OutputPath="$outputDir" `
            -p:AssemblyName=RevitCommandBridge

        if ($LASTEXITCODE -ne 0) {
            throw "Revit $RevitVersion 编译失败 (dotnet exit code: $LASTEXITCODE)"
        }
        Write-Host "[dotnet] $assemblyPath"
    }

    default {
        throw "未知编译器类型: $($versionConfig.compiler)。version-manifest.json 中 compiler 字段仅支持 dotnet。"
    }
}

# ── 4. 公共打包步骤 ──
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

# ── 5. 版本元数据 ──
$entryClass = 'RevitCommandBridge.RevitCommandBridgeApp'
if (Get-Member -InputObject $versionConfig -Name 'entry_class' -MemberType Properties -ErrorAction SilentlyContinue) {
    $entryClass = 'RevitCommandBridge.' + $versionConfig.entry_class
}
$metadata = [ordered]@{
    product       = 'RevitCommandBridge'
    revit_version = $RevitVersion
    protocol      = 'revit-command-bridge/2.0'
    runtime       = $versionConfig.runtime
    symbols       = $versionConfig.define_symbols -join ','
    entry_class   = $entryClass
}
$metadata | ConvertTo-Json | Set-Content (Join-Path $outputDir 'bridge.config.json') -Encoding UTF8

# ── 6. 可选安装器 ──
if (-not $SkipInstaller.IsPresent) {
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -DistDirectory (Join-Path $PSScriptRoot 'dist') | Out-Host
}

Write-Host "Build completed for Revit ${RevitVersion}: $assemblyPath"
