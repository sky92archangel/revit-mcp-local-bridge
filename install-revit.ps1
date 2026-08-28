# install-revit.ps1 — 安装/检测 Revit 命令桥
# install-revit.ps1 — Install/detect Revit Command Bridge

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,  # Revit 版本年份 / Revit version year
    [string]$PackageDirectory,  # 编译包目录 / Built package directory
    [string]$RevitInstallDirectory,  # Revit 安装目录 / Revit installation directory
    [string]$InstallDirectory,  # 命令桥安装目录 / Command bridge install directory
    [string]$AddinsDirectory,  # Revit Addins 目录 / Revit Addins directory
    [ValidateSet('none', 'codex', 'workbuddy', 'deepseek', 'function-api', 'openai-compatible', 'generic-mcp', 'rest')]
    [string]$Connector = 'none',  # AI 连接器类型 / AI connector type
    [string[]]$SearchRoot = @('C:\Program Files\Autodesk'),  # Revit 扫描根目录 / Revit scan root directories
    [switch]$ListDetected  # 仅列出检测到的 Revit 安装 / Only list detected Revit installations
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 设置 UTF-8 输出编码
# Set UTF-8 output encoding
try {
    $utf8OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    [Console]::OutputEncoding = $utf8OutputEncoding
    $OutputEncoding = $utf8OutputEncoding
}
catch { }

# 检查文件或目录是否存在
# Check if file or directory exists
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

# 检查目录是否存在
# Check if directory exists
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

# 从文本中提取 Revit 版本年份
# Extract Revit version year from text
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

# 添加候选 Revit 安装
# Add candidate Revit installation
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

# 检测所有 Revit 安装（注册表 + 文件扫描）
# Detect all Revit installations (registry + file scan)
function Get-RevitInstallations {
    param([string[]]$Roots)
    $candidates = @{}

    # 从注册表检测 Revit 安装
    # Detect Revit installations from registry
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

    # 从文件系统扫描 Revit.exe
    # Scan filesystem for Revit.exe
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

# 读取编译包的元数据
# Read package metadata from compiled build
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

# 检测本机 Revit 安装
# Detect local Revit installations
$detected = Get-RevitInstallations -Roots $SearchRoot
if ($ListDetected) {
    $detected
    return
}

# 自动推断 Revit 版本
# Auto-detect Revit version
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

# 定位编译包目录
# Locate the built package directory
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

# 校验包元数据与版本匹配
# Validate package metadata version matches
$metadata = Get-PackageMetadata -Directory $PackageDirectory
if ($null -eq $metadata) {
    throw "Package metadata is missing: $(Join-Path $PackageDirectory 'bridge.config.json'). Build the package with build.ps1 first."
}
if ([string]$metadata.revit_version -ne $RevitVersion) {
    throw "Package targets Revit $($metadata.revit_version), but -RevitVersion is $RevitVersion."
}

# 定位 Revit 安装目录并校验
# Locate and validate Revit installation directory
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

# 校验编译包完整性
# Validate built package completeness
foreach ($requiredPackageFile in @('RevitCommandBridge.dll', 'deploy\RevitCommandBridge.addin.template', 'scripts\bridge-client.mjs')) {
    if (-not (Test-FileSystemPath (Join-Path $PackageDirectory $requiredPackageFile))) {
        throw "Incomplete package: $(Join-Path $PackageDirectory $requiredPackageFile)"
    }
}

# 设置安装路径
# Set install paths
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $env:LOCALAPPDATA "RevitCommandBridge\$RevitVersion"
}
if ([string]::IsNullOrWhiteSpace($AddinsDirectory)) {
    $AddinsDirectory = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
}

$bridgeRoot = Join-Path $env:LOCALAPPDATA "RevitCommandBridge\$RevitVersion"
$manifestPath = Join-Path $AddinsDirectory 'RevitCommandBridge.addin'
# 预览模式：仅输出安装计划而不执行
# Preview mode: output installation plan without executing
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

# 检查 Revit 是否正在运行
# Check if Revit is running
if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    throw 'Close all Revit processes before installing or updating the add-in.'
}

# 将包文件复制到安装目录
# Copy package files to install directory
function Copy-PackageToInstallDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    $existingNode = Join-Path (Join-Path $DestinationDirectory 'runtime') 'node.exe'
    Write-Output 'RCB_INSTALL_STAGE=copy-files'
    foreach ($packageItem in @(Get-ChildItem -LiteralPath $SourceDirectory -Force)) {
        if ($packageItem.Name -ieq 'runtime' -and (Test-FileSystemPath $existingNode)) {
            Write-Output ('复用已安装的 Node 运行时：' + $existingNode)
            continue
        }
        Write-Output ('安装文件：' + $packageItem.Name)
        Copy-Item -LiteralPath $packageItem.FullName -Destination $DestinationDirectory -Recurse -Force
    }
    Write-Output 'RCB_INSTALL_STAGE=copy-complete'
}

# 获取包中所有文件的相对路径
# Get all relative file paths in the package
function Get-PackageRelativeFiles {
    param([Parameter(Mandatory = $true)][string]$SourceDirectory)
    $root = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\') + '\'
    return @(Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse -Force | ForEach-Object {
        $_.FullName.Substring($root.Length)
    })
}

# 移除不再需要的旧文件
# Remove stale files no longer in the new package
function Remove-StaleBridgeFiles {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )
    $manifestPath = Join-Path $DestinationDirectory 'install-manifest.json'
    if (-not (Test-FileSystemPath $manifestPath)) { return }
    try {
        $oldManifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $newFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($relative in @(Get-PackageRelativeFiles -SourceDirectory $SourceDirectory)) {
            [void]$newFiles.Add($relative.Replace('/', '\'))
        }
        foreach ($relative in @($oldManifest.files)) {
            $normalized = [string]$relative
            if ([string]::IsNullOrWhiteSpace($normalized) -or $newFiles.Contains($normalized.Replace('/', '\'))) { continue }
            # 保留 AI 客户端正在使用的运行时和用户创建的连接文件
            # Keep the runtime currently used by an AI client and user-created connection files
            if ($normalized -match '^runtime[\\/]node\.exe$' -or $normalized -match '^connections[\\/]') { continue }
            $target = [System.IO.Path]::GetFullPath((Join-Path $DestinationDirectory $normalized))
            $destinationRoot = [System.IO.Path]::GetFullPath($DestinationDirectory).TrimEnd('\') + '\'
            if (-not $target.StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase)) { continue }
            if (Test-Path -LiteralPath $target -PathType Leaf) {
                try { Remove-Item -LiteralPath $target -Force; Write-Output ('清理旧文件：' + $normalized) }
                catch { Write-Warning ('旧文件仍被占用，保留：' + $normalized) }
            }
        }
    }
    catch {
        Write-Warning ('读取旧版文件清单失败，保留旧文件：' + $_.Exception.Message)
    }
}

# 执行文件复制（先清理旧文件，再复制新文件）
# Execute file copy (clean stale files first, then copy new ones)
New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
$packagePathForCopy = [System.IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$installPathForCopy = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if (-not [string]::Equals($packagePathForCopy, $installPathForCopy, [StringComparison]::OrdinalIgnoreCase)) {
    Remove-StaleBridgeFiles -SourceDirectory $PackageDirectory -DestinationDirectory $InstallDirectory
    Copy-PackageToInstallDirectory -SourceDirectory $PackageDirectory -DestinationDirectory $InstallDirectory
}

# 生成并写入 Revit .addin 清单文件
# Generate and write Revit .addin manifest file
$installedAssembly = Join-Path $InstallDirectory 'RevitCommandBridge.dll'
Write-Output 'RCB_INSTALL_STAGE=write-manifest'
$manifestTemplate = Get-Content -LiteralPath (Join-Path $PackageDirectory 'deploy\RevitCommandBridge.addin.template') -Raw -Encoding UTF8
$manifest = $manifestTemplate.Replace('__ASSEMBLY_PATH__', [System.Security.SecurityElement]::Escape($installedAssembly))
$entryClass = [string]$metadata.entry_class
if ([string]::IsNullOrWhiteSpace($entryClass)) {
    $entryClass = 'RevitCommandBridge.RevitCommandBridgeApp'
}
$manifest = $manifest.Replace('__FULL_CLASS_NAME__', $entryClass)
New-Item -ItemType Directory -Force -Path $AddinsDirectory | Out-Null
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))

# 写入安装元数据
# Write installation metadata
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
$ownedFiles = @(Get-PackageRelativeFiles -SourceDirectory $PackageDirectory)
$ownedFiles += 'bridge.config.json'
$ownedFiles += 'install-manifest.json'
[ordered]@{
    schema_version = 1
    product = 'RevitCommandBridge'
    revit_version = $RevitVersion
    files = $ownedFiles
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $InstallDirectory 'install-manifest.json') -Encoding UTF8
Write-Output 'RCB_INSTALL_STAGE=write-inventory'
Write-Output 'RCB_INSTALL_STAGE=complete'

# 配置 AI 连接器
# Configure AI connector
if ($Connector -ne 'none') {
    $connectorScript = Join-Path $InstallDirectory 'scripts\configure-connector.ps1'
    if (-not (Test-FileSystemPath $connectorScript)) {
        throw "Connector configuration script is missing: $connectorScript"
    }
    & $connectorScript -Provider $Connector -RevitVersion $RevitVersion
}

# 输出安装结果
# Output installation result
[PSCustomObject]@{
    RevitVersion = $RevitVersion
    RevitInstallDirectory = $RevitInstallDirectory
    InstallDirectory = $InstallDirectory
    AddinManifest = $manifestPath
    BridgeRoot = $installedMetadata.bridge_root
    Connector = $Connector
}
