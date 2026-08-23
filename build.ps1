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

if ([string]::IsNullOrWhiteSpace($RevitInstallDirectory)) {
    $standardDirectory = Join-Path $env:ProgramFiles "Autodesk\Revit $RevitVersion"
    if (Test-Path -LiteralPath (Join-Path $standardDirectory 'RevitAPI.dll')) {
        $RevitInstallDirectory = $standardDirectory
    }
    else {
        throw "Revit $RevitVersion API not found. Pass -RevitInstallDirectory with the folder containing RevitAPI.dll."
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot "dist\RevitCommandBridge-$RevitVersion"
}

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$revitApi = Join-Path $RevitInstallDirectory 'RevitAPI.dll'
$revitApiUi = Join-Path $RevitInstallDirectory 'RevitAPIUI.dll'

foreach ($requiredPath in @($csc, $revitApi, $revitApiUi)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Missing build dependency: $requiredPath"
    }
}

$apiAssemblyName = [Reflection.AssemblyName]::GetAssemblyName($revitApi)
$apiVersion = $apiAssemblyName.Version
if ($null -eq $apiVersion) {
    throw "Unable to read the Revit $RevitVersion API version from $revitApi."
}

$sourceDirectory = Join-Path $PSScriptRoot 'src'
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
if ($sourceFiles.Count -eq 0) {
    throw "No C# source files found: $sourceDirectory"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$assemblyPath = Join-Path $OutputDirectory 'RevitCommandBridge.dll'
$symbols = @()
if ($apiVersion.Major -ge 21) {
    $symbols += 'REVIT_FORGE_UNITS'
}
if ($apiVersion.Major -ge 23) {
    # Revit 2023+: BuiltInParameterGroup -> GroupTypeId (ForgeTypeId)
    $symbols += 'REVIT_PARAMETER_GROUPS'
}
$compilerArguments = @(
    '/nologo',
    '/target:library',
    '/platform:anycpu',
    '/optimize+',
    '/debug:pdbonly',
    $(if ($symbols.Count -gt 0) { '/define:' + ($symbols -join ';') } else { $null }),
    ('/out:' + $assemblyPath),
    ('/reference:' + $revitApi),
    ('/reference:' + $revitApiUi),
    '/reference:System.Web.Extensions.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll'
) + $sourceFiles | Where-Object { $null -ne $_ }

& $csc @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "RevitCommandBridge compilation failed with exit code: $LASTEXITCODE"
}

foreach ($directoryName in @('scripts', 'examples', 'deploy', 'schemas', 'src', 'verification')) {
    $source = Join-Path $PSScriptRoot $directoryName
    if (Test-Path -LiteralPath $source) {
        $destination = Join-Path $OutputDirectory $directoryName
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $destination -Recurse -Force
    }
}

foreach ($fileName in @('README.md', 'PROTOCOL.md', 'ARCHITECTURE.md', 'ENGINEERING-RECORD.md', 'VERSION-SUPPORT.md', 'CONNECTORS.md', 'install-revit.ps1', 'uninstall-revit.ps1', 'install-revit2020.ps1', 'build-revit-adapter.ps1')) {
    $source = Join-Path $PSScriptRoot $fileName
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $OutputDirectory $fileName) -Force
    }
}

$packageMetadata = [ordered]@{
    product = 'RevitCommandBridge'
    revit_version = $RevitVersion
    protocol = 'revit-command-bridge/2.0'
    runtime = 'net48'
}
$packageMetadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'bridge.config.json') -Encoding UTF8

if (-not $SkipInstaller.IsPresent) {
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -DistDirectory (Join-Path $PSScriptRoot 'dist') | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Revit AI Hub setup build failed with exit code: $LASTEXITCODE"
    }
}

Write-Host "Build completed for Revit ${RevitVersion}: $assemblyPath"
