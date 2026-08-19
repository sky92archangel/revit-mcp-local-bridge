[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,
    [Parameter(Mandatory = $true)]
    [string]$RevitInstallDirectory,
    [Parameter(Mandatory = $true)]
    [string]$TemplatePackageDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$StatusFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $utf8OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    [Console]::OutputEncoding = $utf8OutputEncoding
    $OutputEncoding = $utf8OutputEncoding
}
catch { }
$script:CurrentStage = 0

trap {
    $detail = $_.Exception.Message -replace '[\r\n|]+', ' '
    if (-not [string]::IsNullOrWhiteSpace($StatusFile)) {
        Set-Content -LiteralPath $StatusFile -Value ('ERROR|' + $script:CurrentStage + '|' + $detail + '|' + [DateTime]::UtcNow.ToString('o')) -Encoding ASCII
    }
    Write-Error ('RCB_ADAPTER_ERROR stage=' + $script:CurrentStage + ': ' + $detail)
    exit 1
}

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$revitApi = Join-Path $RevitInstallDirectory 'RevitAPI.dll'
$revitApiUi = Join-Path $RevitInstallDirectory 'RevitAPIUI.dll'
$sourceDirectory = Join-Path $TemplatePackageDirectory 'src'
function Set-BridgeStatus {
    param(
        [Parameter(Mandatory = $true)][int]$Stage,
        [Parameter(Mandatory = $true)][string]$Message,
        [switch]$Quiet
    )
    $status = $Stage.ToString() + '|' + $Message + '|' + [DateTime]::UtcNow.ToString('o')
    $script:CurrentStage = $Stage
    if (-not [string]::IsNullOrWhiteSpace($StatusFile)) {
        Set-Content -LiteralPath $StatusFile -Value ('RUN|' + $status) -Encoding ASCII
    }
    if (-not $Quiet) {
        Write-Output ('RCB_STAGE=' + $Stage)
    }
}

Set-BridgeStatus -Stage 1 -Message '检查本机组件'
foreach ($requiredPath in @($csc, $revitApi, $revitApiUi, $sourceDirectory)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Missing local adapter build dependency: $requiredPath"
    }
}

Set-BridgeStatus -Stage 2 -Message '准备插件文件'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Get-ChildItem -LiteralPath $TemplatePackageDirectory -Force |
    Where-Object { $_.Name -notin @('RevitCommandBridge.dll', 'RevitCommandBridge.pdb', 'bridge.config.json') } |
    Copy-Item -Destination $OutputDirectory -Recurse -Force

$sourceFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
if ($sourceFiles.Count -eq 0) {
    throw "No C# source files found in $sourceDirectory"
}

$assemblyPath = Join-Path $OutputDirectory 'RevitCommandBridge.dll'
$symbols = @()
Set-BridgeStatus -Stage 3 -Message '读取 Revit 版本'
$apiVersion = [Reflection.AssemblyName]::GetAssemblyName($revitApi).Version
if ($null -eq $apiVersion) {
    throw "Unable to read the installed Revit API version."
}
if ($apiVersion.Major -ge 21) {
    $symbols += 'REVIT_FORGE_UNITS'
}
Set-BridgeStatus -Stage 4 -Message '正在编译插件'
$compilerArguments = @(
    '/nologo',
    '/target:library',
    '/platform:anycpu',
    '/optimize+',
    '/debug:pdbonly',
    ('/out:' + $assemblyPath),
    ('/reference:' + $revitApi),
    ('/reference:' + $revitApiUi),
    '/reference:System.Web.Extensions.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll'
)
if ($symbols.Count -gt 0) { $compilerArguments += ('/define:' + ($symbols -join ';')) }
$compilerArguments += $sourceFiles
$compilerArgumentLine = (($compilerArguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join ' ')
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $csc
$startInfo.Arguments = $compilerArgumentLine
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardError = $true
$startInfo.RedirectStandardOutput = $true
$compilerProcess = New-Object System.Diagnostics.Process
$compilerProcess.StartInfo = $startInfo
[void]$compilerProcess.Start()
while (-not $compilerProcess.WaitForExit(500)) {
    Set-BridgeStatus -Stage 4 -Message '正在编译插件' -Quiet
}
$compilerOutput = $compilerProcess.StandardOutput.ReadToEnd()
$compilerError = $compilerProcess.StandardError.ReadToEnd()
if (-not [string]::IsNullOrWhiteSpace($compilerOutput)) { Write-Output $compilerOutput }
if (-not [string]::IsNullOrWhiteSpace($compilerError)) { Write-Error $compilerError }
if ($compilerProcess.ExitCode -ne 0) {
    throw "Local Revit $RevitVersion adapter compilation failed with exit code: $($compilerProcess.ExitCode)"
}
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "Local Revit $RevitVersion adapter compilation did not create: $assemblyPath"
}

Set-BridgeStatus -Stage 5 -Message '验证生成结果'
$metadata = [ordered]@{
    product = 'RevitCommandBridge'
    revit_version = $RevitVersion
    protocol = 'revit-command-bridge/2.0'
    runtime = 'net48'
    build_mode = 'local-api-adapter'
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'bridge.config.json') -Encoding UTF8
Set-BridgeStatus -Stage 5 -Message '插件生成完成'

[PSCustomObject]@{
    RevitVersion = $RevitVersion
    RevitInstallDirectory = (Resolve-Path -LiteralPath $RevitInstallDirectory).Path
    OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
    Assembly = $assemblyPath
    BuildMode = 'local-api-adapter'
}
