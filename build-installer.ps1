[CmdletBinding()]
param(
    [string]$DistDirectory,
    [string]$OutputPath,
    [string]$NodeExecutable,
    [ValidatePattern('^20(2[0-4])$')]
    [string[]]$RevitVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

$packageDirectories = @(
    Get-ChildItem -LiteralPath $DistDirectory -Directory -Filter 'RevitCommandBridge-*' |
        Where-Object {
            $metadata = Join-Path $_.FullName 'bridge.config.json'
            $assembly = Join-Path $_.FullName 'RevitCommandBridge.dll'
            (Test-Path -LiteralPath $metadata) -and (Test-Path -LiteralPath $assembly)
        } |
        Sort-Object Name
)
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

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$stageDirectory = Join-Path $PSScriptRoot ('build\setup-stage-' + [Guid]::NewGuid().ToString('N'))
$payloadDirectory = Join-Path $stageDirectory 'payload'
$payloadZip = Join-Path $stageDirectory 'revit-ai-hub.payload.zip'

try {
    New-Item -ItemType Directory -Force -Path $payloadDirectory | Out-Null
    foreach ($package in $packageDirectories) {
        $stagedPackage = Join-Path $payloadDirectory $package.Name
        Copy-Item -LiteralPath $package.FullName -Destination $stagedPackage -Recurse -Force
        $stagedSymbols = Join-Path $stagedPackage 'RevitCommandBridge.pdb'
        if (Test-Path -LiteralPath $stagedSymbols) {
            Remove-Item -LiteralPath $stagedSymbols -Force
        }
        $runtimeDirectory = Join-Path $stagedPackage 'runtime'
        New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
        Copy-Item -LiteralPath $NodeExecutable -Destination (Join-Path $runtimeDirectory 'node.exe') -Force
        & (Join-Path $runtimeDirectory 'node.exe') --version | Set-Content -LiteralPath (Join-Path $runtimeDirectory 'version.txt') -Encoding ASCII
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadDirectory,
        $payloadZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

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
    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force
    }
}

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
