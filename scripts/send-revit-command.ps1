[CmdletBinding(DefaultParameterSetName = 'Json')]
param(
    [Parameter(Mandatory = $true, Position = 0, ParameterSetName = 'Json')]
    [string]$Json,

    [Parameter(Mandatory = $true, ParameterSetName = 'File')]
    [string]$RequestPath,

    [string]$Id,
    [string]$Source = 'cli',
    [switch]$Preview,
    [switch]$AllowOffline,
    [ValidateRange(0, 120)]
    [int]$WaitSeconds = 60,
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,
    [string]$RootDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RevitVersion)) {
    $packageDirectory = Split-Path -Parent $PSScriptRoot
    $candidateVersion = Split-Path -Leaf $packageDirectory
    $versionMatch = [regex]::Match($candidateVersion, '20\d{2}')
    if (-not $versionMatch.Success) { throw 'Cannot infer Revit version from the bridge directory; pass -RevitVersion.' }
    $RevitVersion = $versionMatch.Value
}
if ([string]::IsNullOrWhiteSpace($RootDirectory)) {
    $RootDirectory = Join-Path (
        Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'RevitCommandBridge'
    ) $RevitVersion
}

function Get-Value {
    param([object]$Object, [string[]]$Names)
    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property) {
            return $property.Value
        }
    }
    return $null
}

function Test-ValidRequestId {
    param([string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value.Length -le 128 -and
        $Value -match '^[A-Za-z0-9._-]+$'
}

function Test-BridgeRunning {
    param([object]$Status)
    if ($null -eq $Status -or @('running', 'busy') -notcontains [string]$Status.state) {
        return $false
    }
    try {
        return ([DateTime]::UtcNow - [DateTime]::Parse([string]$Status.updated_utc).ToUniversalTime()).TotalSeconds -le 5
    }
    catch {
        return $false
    }
}

if ($PSCmdlet.ParameterSetName -eq 'File') {
    $Json = Get-Content -LiteralPath $RequestPath -Raw -Encoding UTF8
}

try {
    $inputRequest = $Json | ConvertFrom-Json
}
catch {
    throw "Unable to parse JSON: $($_.Exception.Message)"
}

if ($null -eq $inputRequest -or $inputRequest -is [Array]) {
    throw 'The command request must be a JSON object.'
}

$operation = [string](Get-Value $inputRequest @('operation', 'command'))
if ([string]::IsNullOrWhiteSpace($operation)) {
    throw 'Missing operation.'
}

$arguments = Get-Value $inputRequest @('args', 'arguments')
if ($null -eq $arguments) {
    $arguments = [ordered]@{}
}
if ($arguments -is [Array] -or $arguments -is [string] -or $arguments -is [ValueType]) {
    throw 'args must be a JSON object.'
}

$requestId = if ($Id) { $Id } else { [string](Get-Value $inputRequest @('id')) }
if ([string]::IsNullOrWhiteSpace($requestId)) {
    $requestId = [Guid]::NewGuid().ToString('N')
}
if (-not (Test-ValidRequestId $requestId)) {
    throw 'Command id may contain only letters, digits, period, underscore, and hyphen; maximum 128 characters.'
}

$requestPreview = Get-Value $inputRequest @('preview', 'dry_run')
if ($Preview.IsPresent) {
    $requestPreview = $true
}
elseif ($null -eq $requestPreview) {
    $requestPreview = $false
}
elseif ($requestPreview -isnot [bool]) {
    if ([string]$requestPreview -ieq 'true') {
        $requestPreview = $true
    }
    elseif ([string]$requestPreview -ieq 'false') {
        $requestPreview = $false
    }
    else {
        throw 'preview must be true or false.'
    }
}

$documentTitle = Get-Value $inputRequest @('document_title', 'documentTitle')
$inboxDirectory = Join-Path $RootDirectory 'inbox'
$processingDirectory = Join-Path $RootDirectory 'processing'
$outboxDirectory = Join-Path $RootDirectory 'outbox'
$statusPath = Join-Path $RootDirectory 'status.json'
foreach ($directory in @($inboxDirectory, $processingDirectory, $outboxDirectory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$status = $null
if (Test-Path -LiteralPath $statusPath) {
    try {
        $status = Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        $status = $null
    }
}
if (-not $AllowOffline.IsPresent -and -not (Test-BridgeRunning $status)) {
    throw 'Revit Command Bridge is not running. In Revit, click Start Bridge; use -AllowOffline only for intentional offline queueing.'
}

$inboxPath = Join-Path $inboxDirectory ($requestId + '.request.json')
$processingPath = Join-Path $processingDirectory ($requestId + '.processing.json')
$resultPath = Join-Path $outboxDirectory ($requestId + '.result.json')
if ((Test-Path -LiteralPath $inboxPath) -or (Test-Path -LiteralPath $processingPath) -or (Test-Path -LiteralPath $resultPath)) {
    throw "Command id already exists: $requestId. Read its existing result or use a new id."
}

$request = [ordered]@{
    id = $requestId
    operation = $operation.Trim()
    args = $arguments
    preview = [bool]$requestPreview
    document_title = if ([string]::IsNullOrWhiteSpace([string]$documentTitle)) { $null } else { [string]$documentTitle }
    source = $Source
    created_utc = [DateTime]::UtcNow.ToString('o')
}
$serializedRequest = $request | ConvertTo-Json -Depth 20 -Compress
$temporaryPath = $inboxPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    [System.IO.File]::WriteAllText($temporaryPath, $serializedRequest, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::Move($temporaryPath, $inboxPath)
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

if ($WaitSeconds -le 0) {
    [PSCustomObject]@{
        ok = $true
        state = 'queued'
        id = $requestId
        operation = $request.operation
        result_path = $resultPath
    }
    return
}

$deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
while ([DateTime]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath $resultPath) {
        Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        return
    }
    Start-Sleep -Milliseconds 200
}

[PSCustomObject]@{
    ok = $true
    state = 'queued'
    id = $requestId
    operation = $request.operation
    message = 'Command is queued but did not complete during the wait period.'
    result_path = $resultPath
}
