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

    # ═══════════════════════════════════════════
    # 管道 A：csc.exe（Revit 2020-2024）
    # ═══════════════════════════════════════════
    'csc' {
        $cscPath = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
        if (-not (Test-Path -LiteralPath $cscPath)) {
            throw "csc.exe 未找到: $cscPath"
        }
        if (-not (Test-Path -LiteralPath $revitApi)) {
            throw "RevitAPI.dll 未找到: $revitApi"
        }

        $sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' |
            Sort-Object Name | ForEach-Object FullName)

        $refArgs = @()
        $refArgs += "/reference:$revitApi"
        $refArgs += "/reference:$revitApiUi"

        # 框架引用（简单名称，csc 自动搜索路径）
        foreach ($refName in $versionConfig.framework_references) {
            $refArgs += "/reference:$refName"
        }

        # WPF 引用（硬编码路径，与原始 build.ps1 一致）
        $wpfDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
        foreach ($refName in $versionConfig.wpf_references) {
            $refPath = Join-Path $wpfDir $refName
            if (Test-Path -LiteralPath $refPath) {
                $refArgs += "/reference:$refPath"
            } else {
                Write-Warning "WPF 引用未找到: $refPath"
            }
        }

        $defineArgs = if ($versionConfig.define_symbols.Count -gt 0) {
            @("/define:" + ($versionConfig.define_symbols -join ';'))
        } else { @() }

        $cscArgs = @(
            '/nologo', '/target:library', '/platform:anycpu',
            '/optimize+', '/debug:pdbonly'
        ) + $defineArgs + @("/out:$assemblyPath") + $refArgs + $sourceFiles

        Write-Host "[csc] 编译 Revit $RevitVersion ..."
        & $cscPath @cscArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Revit $RevitVersion 编译失败 (csc exit code: $LASTEXITCODE)"
        }
        Write-Host "[csc] $assemblyPath"
    }

    # ═══════════════════════════════════════════
    # 管道 B：dotnet build（Revit 2025+，运行时由 manifest 指定）
    # ═══════════════════════════════════════════
    'dotnet' {
        $projectFile = Join-Path $PSScriptRoot $versionConfig.project_file
        if (-not (Test-Path -LiteralPath $projectFile)) {
            throw "项目文件未找到: $projectFile"
        }

        # 验证 dotnet SDK
        $dotnetVersion = dotnet --version 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw ".NET SDK 未安装。请安装 .NET 8/10 SDK。"
        }
        Write-Host "[dotnet] SDK version: $($dotnetVersion.Trim())"

        Write-Host "[dotnet] 编译 Revit $RevitVersion ($($versionConfig.runtime)) ..."
        Write-Host "[dotnet] symbols: $($versionConfig.define_symbols -join ';')"
        dotnet build $projectFile --configuration Release `
            -p:DefineConstants="$($versionConfig.define_symbols -join ';')" `
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
        throw "未知编译器类型: $($versionConfig.compiler)。version-manifest.json 中 compiler 字段仅支持 csc 或 dotnet。"
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
    'install-revit.ps1', 'uninstall-revit.ps1', 'build-revit-adapter.ps1'))
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
