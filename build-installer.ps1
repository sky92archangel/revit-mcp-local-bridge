# build-installer.ps1 — 安装器打包脚本
# build-installer.ps1 — Installer packaging script

[CmdletBinding()]
param(
    [string]$DistDirectory,  # 编译产物目录 / Build output directory
    [string]$OutputPath,  # 安装器输出路径 / Installer output path
    [string]$NodeExecutable,  # Node.js 可执行文件路径 / Node.js executable path
    [ValidatePattern('^20(2[5-7])$')]
    [string[]]$RevitVersion  # 要打包的 Revit 版本（可选）/ Revit versions to bundle (optional)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 设置默认路径
# Set default paths
if ([string]::IsNullOrWhiteSpace($DistDirectory)) {
    $DistDirectory = Join-Path $PSScriptRoot 'dist'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot 'dist\RevitCommandBridgeSetup.exe'
}
if ([string]::IsNullOrWhiteSpace($NodeExecutable)) {
    $nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($null -ne $nodeCommand) {
        $NodeExecutable = $nodeCommand.Source
    }
}

# 检查依赖项
# Check dependencies
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$setupSource = Join-Path $PSScriptRoot 'setup\RevitAIHubSetup.cs'
$setupIcon = Join-Path $PSScriptRoot 'setup\RevitCommandBridge.ico'
foreach ($requiredPath in @($csc, $setupSource, $setupIcon, $DistDirectory)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Missing installer build dependency: $requiredPath"
    }
}
if ([string]::IsNullOrWhiteSpace($NodeExecutable) -or -not (Test-Path -LiteralPath $NodeExecutable -PathType Leaf)) {
    throw 'Node.js was not found. Pass -NodeExecutable with the path to node.exe so the installer can remain self-contained.'
}

# 扫描已编译的版本包目录
# Scan compiled version package directories
$packageDirectories = @(
    Get-ChildItem -LiteralPath $DistDirectory -Directory -Filter 'RevitCommandBridge-*' |
        Where-Object {
            $metadata = Join-Path $_.FullName 'bridge.config.json'
            $assembly = Join-Path $_.FullName 'RevitCommandBridge.dll'
            (Test-Path -LiteralPath $metadata) -and (Test-Path -LiteralPath $assembly)
        } |
        Sort-Object Name
)
# 如果指定了版本，过滤出匹配的包
# If versions specified, filter matching packages
$requestedVersions = @($RevitVersion | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($requestedVersions.Count -gt 0) {
    $packageDirectories = @(
        $packageDirectories | Where-Object {
            $metadata = Get-Content -LiteralPath (Join-Path $_.FullName 'bridge.config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $requestedVersions -contains [string]$metadata.revit_version
        }
    )
}
if ($packageDirectories.Count -eq 0) {
    throw "No RevitCommandBridge package found in $DistDirectory. Build at least one Revit version first."
}

# 创建临时构建目录
# Create temporary staging directory
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$stageDirectory = Join-Path $PSScriptRoot ('build\setup-stage-' + [Guid]::NewGuid().ToString('N'))
$payloadDirectory = Join-Path $stageDirectory 'payload'
$payloadZip = Join-Path $stageDirectory 'revit-ai-hub.payload.zip'

try {
    # 准备有效载荷：复制包并嵌入 Node.js
    # Prepare payload: copy packages and embed Node.js
    New-Item -ItemType Directory -Force -Path $payloadDirectory | Out-Null
    foreach ($package in $packageDirectories) {
        $stagedPackage = Join-Path $payloadDirectory $package.Name
        Copy-Item -LiteralPath $package.FullName -Destination $stagedPackage -Recurse -Force
        # 移除调试符号以减小体积
        # Remove debug symbols to reduce size
        $stagedSymbols = Join-Path $stagedPackage 'RevitCommandBridge.pdb'
        if (Test-Path -LiteralPath $stagedSymbols) {
            Remove-Item -LiteralPath $stagedSymbols -Force
        }
        # 复制 Node.js 运行时
        # Copy Node.js runtime
        $runtimeDirectory = Join-Path $stagedPackage 'runtime'
        New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
        Copy-Item -LiteralPath $NodeExecutable -Destination (Join-Path $runtimeDirectory 'node.exe') -Force
        & (Join-Path $runtimeDirectory 'node.exe') --version | Set-Content -LiteralPath (Join-Path $runtimeDirectory 'version.txt') -Encoding ASCII
    }

    # 压缩有效载荷为 ZIP
    # Compress payload into ZIP
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadDirectory,
        $payloadZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    # 编译 GUI 安装器（嵌入 ZIP 资源）
    # Compile GUI installer (embedding ZIP as resource)
    $compilerArguments = @(
        '/nologo',
        '/target:winexe',
        '/platform:anycpu',
        '/optimize+',
        '/debug:pdbonly',
        ('/out:' + $OutputPath),
        '/reference:System.Web.Extensions.dll',
        '/reference:System.Security.dll',
        '/reference:System.Windows.Forms.dll',
        '/reference:System.Drawing.dll',
        '/reference:System.IO.Compression.dll',
        '/reference:System.IO.Compression.FileSystem.dll',
        ('/win32icon:' + $setupIcon),
        ('/resource:' + $payloadZip + ',RevitAIHub.payload.zip'),
        $setupSource
    )
    & $csc @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Revit AI Hub setup compilation failed with exit code: $LASTEXITCODE"
    }
}
finally {
    # 清理临时构建目录
    # Clean up temporary staging directory
    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force
    }
}

# 输出安装器信息
# Output installer information
$bundledVersions = @(
    $packageDirectories | ForEach-Object {
        $metadata = Get-Content -LiteralPath (Join-Path $_.FullName 'bridge.config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        [string]$metadata.revit_version
    }
)
[PSCustomObject]@{
    SetupExe = $OutputPath
    BundledRevitVersions = $bundledVersions
    BundledNode = (& $NodeExecutable --version).Trim()
    Sha256 = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
}
