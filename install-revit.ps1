[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,
    [string]$PackageDirectory,
    [string]$RevitInstallDirectory,
    [string]$InstallDirectory,
    [string]$AddinsDirectory,
    [ValidateSet('none', 'codex', 'workbuddy', 'deepseek', 'function-api', 'openai-compatible', 'generic-mcp', 'rest')]
    [string]$Connector = 'none',
    [string[]]$SearchRoot = @('C:\Program Files\Autodesk'),
    [switch]$ListDetected
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-FileSystemPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }
    try {
        return [System.IO.File]::Exists($Path) -or [System.IO.Directory]::Exists($Path)
    }
    catch {
        return $false
    }
}

function Test-FileSystemDirectory {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }
    try {
        return [System.IO.Directory]::Exists($Path)
    }
    catch {
        return $false
    }
}

function Get-VersionFromText {
    param([string]$Text)
    if ($null -eq $Text) {
        $Text = ''
    }
    $match = [regex]::Match($Text, '(?<!\d)(20\d{2})(?!\d)')
    if ($match.Success) {
        return $match.Groups[1].Value
    }
    return $null
}

function Add-Candidate {
    param(
        [hashtable]$Candidates,
        [string]$Version,
        [string]$Directory,
        [string]$Source
    )
    if ([string]::IsNullOrWhiteSpace($Version) -or [string]::IsNullOrWhiteSpace($Directory)) {
        return
    }

    try {
        $exe = Join-Path $Directory 'Revit.exe'
        $api = Join-Path $Directory 'RevitAPI.dll'
        if (-not (Test-FileSystemPath $exe) -or -not (Test-FileSystemPath $api)) {
            return
        }
    }
    catch {
        return
    }

    try {
        $resolved = [System.IO.Path]::GetFullPath($Directory)
    }
    catch {
        return
    }
    if (-not $Candidates.ContainsKey($Version)) {
        $Candidates[$Version] = [PSCustomObject]@{
            RevitVersion = $Version
            InstallDirectory = $resolved
            Source = $Source
        }
    }
}

function Get-RevitInstallations {
    param([string[]]$Roots)
    $candidates = @{}

    foreach ($registryRoot in @('HKLM:\SOFTWARE\Autodesk\Revit', 'HKLM:\SOFTWARE\WOW6432Node\Autodesk\Revit')) {
        if (-not (Test-Path -LiteralPath $registryRoot)) {
            continue
        }
        foreach ($key in @(Get-ChildItem -LiteralPath $registryRoot -ErrorAction SilentlyContinue)) {
            $version = Get-VersionFromText $key.PSChildName
            $properties = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            foreach ($propertyName in @('InstallLocation', 'InstallPath', 'RevitInstallPath', 'INSTALLDIR')) {
                $property = $properties.PSObject.Properties[$propertyName]
                $directory = if ($null -eq $property) { $null } else { [string]$property.Value }
                Add-Candidate -Candidates $candidates -Version $version -Directory $directory -Source "registry:$($key.PSPath)"
            }
        }
    }

    foreach ($root in $Roots) {
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-FileSystemDirectory $root)) {
            continue
        }
        foreach ($exe in @(Get-ChildItem -LiteralPath $root -Filter 'Revit.exe' -File -Recurse -ErrorAction SilentlyContinue)) {
            $directory = $exe.Directory.FullName
            $version = Get-VersionFromText ($directory + ' ' + $exe.VersionInfo.ProductVersion)
            Add-Candidate -Candidates $candidates -Version $version -Directory $directory -Source "scan:$root"
        }
    }

    return @($candidates.Values | Sort-Object RevitVersion)
}

function Get-PackageMetadata {
    param([string]$Directory)
    $metadataPath = Join-Path $Directory 'bridge.config.json'
    if (-not (Test-FileSystemPath $metadataPath)) {
        return $null
    }
    try {
        return Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "Cannot parse package metadata: $metadataPath. $($_.Exception.Message)"
    }
}

$detected = Get-RevitInstallations -Roots $SearchRoot
if ($ListDetected) {
    $detected
    return
}

if ([string]::IsNullOrWhiteSpace($RevitVersion) -and -not [string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $packageMetadata = Get-PackageMetadata -Directory $PackageDirectory
    $RevitVersion = [string]$packageMetadata.revit_version
}
if ([string]::IsNullOrWhiteSpace($RevitVersion)) {
    if ($detected.Count -eq 1) {
        $RevitVersion = $detected[0].RevitVersion
    }
    else {
        throw 'Specify -RevitVersion. More than one or no Revit installation was detected.'
    }
}

if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $bundledAssembly = Join-Path $PSScriptRoot 'RevitCommandBridge.dll'
    $bundledMetadata = Join-Path $PSScriptRoot 'bridge.config.json'
    if ((Test-FileSystemPath $bundledAssembly) -and (Test-FileSystemPath $bundledMetadata)) {
        $PackageDirectory = $PSScriptRoot
    }
    else {
        $PackageDirectory = Join-Path $PSScriptRoot "dist\RevitCommandBridge-$RevitVersion"
    }
}
if (-not (Test-FileSystemDirectory $PackageDirectory)) {
    throw "Package directory was not found: $PackageDirectory"
}
$PackageDirectory = [System.IO.Path]::GetFullPath($PackageDirectory)

$metadata = Get-PackageMetadata -Directory $PackageDirectory
if ($null -eq $metadata) {
    throw "Package metadata is missing: $(Join-Path $PackageDirectory 'bridge.config.json'). Build the package with build.ps1 first."
}
if ([string]$metadata.revit_version -ne $RevitVersion) {
    throw "Package targets Revit $($metadata.revit_version), but -RevitVersion is $RevitVersion."
}

if ([string]::IsNullOrWhiteSpace($RevitInstallDirectory)) {
    $match = @($detected | Where-Object RevitVersion -eq $RevitVersion)
    if ($match.Count -ne 1) {
        throw "Revit $RevitVersion was not detected uniquely. Pass -RevitInstallDirectory with the folder containing Revit.exe and RevitAPI.dll."
    }
    $RevitInstallDirectory = $match[0].InstallDirectory
}
if (-not (Test-FileSystemDirectory $RevitInstallDirectory)) {
    throw "Revit installation directory was not found: $RevitInstallDirectory"
}
$RevitInstallDirectory = [System.IO.Path]::GetFullPath($RevitInstallDirectory)
foreach ($requiredRevitFile in @('Revit.exe', 'RevitAPI.dll')) {
    if (-not (Test-FileSystemPath (Join-Path $RevitInstallDirectory $requiredRevitFile))) {
        throw "Invalid Revit installation directory; missing ${requiredRevitFile}: $RevitInstallDirectory"
    }
}

foreach ($requiredPackageFile in @('RevitCommandBridge.dll', 'deploy\RevitCommandBridge.addin.template', 'scripts\bridge-client.mjs')) {
    if (-not (Test-FileSystemPath (Join-Path $PackageDirectory $requiredPackageFile))) {
        throw "Incomplete package: $(Join-Path $PackageDirectory $requiredPackageFile)"
    }
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $env:LOCALAPPDATA "RevitCommandBridge\$RevitVersion"
}
if ([string]::IsNullOrWhiteSpace($AddinsDirectory)) {
    $AddinsDirectory = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
}

$bridgeRoot = Join-Path $env:LOCALAPPDATA "RevitCommandBridge\$RevitVersion"
$manifestPath = Join-Path $AddinsDirectory 'RevitCommandBridge.addin'
if (-not $PSCmdlet.ShouldProcess(
        "$InstallDirectory and $manifestPath",
        "Install Revit Command Bridge for Revit $RevitVersion")) {
    [PSCustomObject]@{
        State = 'preview'
        RevitVersion = $RevitVersion
        RevitInstallDirectory = $RevitInstallDirectory
        PackageDirectory = $PackageDirectory
        InstallDirectory = $InstallDirectory
        AddinManifest = $manifestPath
        BridgeRoot = $bridgeRoot
        Connector = $Connector
    }
    return
}

if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    throw 'Close all Revit processes before installing or updating the add-in.'
}

function Copy-PackageToInstallDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    $existingNode = Join-Path (Join-Path $DestinationDirectory 'runtime') 'node.exe'
    foreach ($packageItem in @(Get-ChildItem -LiteralPath $SourceDirectory -Force)) {
        if ($packageItem.Name -ieq 'runtime' -and (Test-FileSystemPath $existingNode)) {
            Write-Output ('复用已安装的 Node 运行时：' + $existingNode)
            continue
        }
        Copy-Item -LiteralPath $packageItem.FullName -Destination $DestinationDirectory -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
$packagePathForCopy = [System.IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$installPathForCopy = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if (-not [string]::Equals($packagePathForCopy, $installPathForCopy, [StringComparison]::OrdinalIgnoreCase)) {
    Copy-PackageToInstallDirectory -SourceDirectory $PackageDirectory -DestinationDirectory $InstallDirectory
}

$installedAssembly = Join-Path $InstallDirectory 'RevitCommandBridge.dll'
$manifestTemplate = Get-Content -LiteralPath (Join-Path $PackageDirectory 'deploy\RevitCommandBridge.addin.template') -Raw -Encoding UTF8
$manifest = $manifestTemplate.Replace('__ASSEMBLY_PATH__', [System.Security.SecurityElement]::Escape($installedAssembly))
New-Item -ItemType Directory -Force -Path $AddinsDirectory | Out-Null
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))

$installedMetadata = [ordered]@{
    product = 'RevitCommandBridge'
    revit_version = $RevitVersion
    protocol = [string]$metadata.protocol
    runtime = [string]$metadata.runtime
    install_directory = $InstallDirectory
    bridge_root = $bridgeRoot
    revit_install_directory = $RevitInstallDirectory
}
$installedMetadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallDirectory 'bridge.config.json') -Encoding UTF8

if ($Connector -ne 'none') {
    $connectorScript = Join-Path $InstallDirectory 'scripts\configure-connector.ps1'
    if (-not (Test-FileSystemPath $connectorScript)) {
        throw "Connector configuration script is missing: $connectorScript"
    }
    & $connectorScript -Provider $Connector -RevitVersion $RevitVersion
}

[PSCustomObject]@{
    RevitVersion = $RevitVersion
    RevitInstallDirectory = $RevitInstallDirectory
    InstallDirectory = $InstallDirectory
    AddinManifest = $manifestPath
    BridgeRoot = $installedMetadata.bridge_root
    Connector = $Connector
}
