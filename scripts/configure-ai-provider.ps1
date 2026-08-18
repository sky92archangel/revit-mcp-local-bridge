[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,
    [ValidateSet('openai-compatible')]
    [string]$ProviderKind = 'openai-compatible',
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https?://')]
    [string]$BaseUrl,
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Model,
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$ProfileName = 'default',
    [string]$ProfileDirectory,
    [SecureString]$ApiKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

function Get-ProfileDirectory {
    param([string]$Directory, [string]$Version)
    if (-not [string]::IsNullOrWhiteSpace($Directory)) {
        return [System.IO.Path]::GetFullPath($Directory)
    }
    return Join-Path $env:LOCALAPPDATA "RevitCommandBridge\$Version\ai-providers"
}

function Get-ProfileEntropy {
    param([string]$Version)
    return [System.Text.Encoding]::UTF8.GetBytes("RevitCommandBridge:ai-provider:1:$Version")
}

$normalizedBaseUrl = $BaseUrl.Trim().TrimEnd('/')
$normalizedModel = $Model.Trim()
$targetDirectory = Get-ProfileDirectory -Directory $ProfileDirectory -Version $RevitVersion
$profilePath = Join-Path $targetDirectory "$ProfileName.json"

if (-not $PSCmdlet.ShouldProcess($profilePath, "Save $ProviderKind model profile protected by Windows DPAPI")) {
    [PSCustomObject]@{
        State = 'preview'
        ProviderKind = $ProviderKind
        RevitVersion = $RevitVersion
        BaseUrl = $normalizedBaseUrl
        Model = $normalizedModel
        ProfilePath = $profilePath
        CredentialStorage = 'Windows DPAPI (CurrentUser)'
    }
    return
}

if ($null -eq $ApiKey) {
    $ApiKey = Read-Host -Prompt 'Model API Key (stored with Windows DPAPI for this user only)' -AsSecureString
}

$keyPointer = [IntPtr]::Zero
$plainApiKey = $null
try {
    $keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ApiKey)
    $plainApiKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)
    if ([string]::IsNullOrWhiteSpace($plainApiKey)) {
        throw 'API key is empty.'
    }

    $secretBytes = [System.Text.Encoding]::UTF8.GetBytes($plainApiKey)
    try {
        $protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
            $secretBytes,
            (Get-ProfileEntropy -Version $RevitVersion),
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    }
    finally {
        [Array]::Clear($secretBytes, 0, $secretBytes.Length)
    }

    $profile = [ordered]@{
        schema_version = 1
        provider_kind = $ProviderKind
        revit_version = $RevitVersion
        base_url = $normalizedBaseUrl
        model = $normalizedModel
        api_key_protected = [Convert]::ToBase64String($protectedBytes)
        credential_scheme = 'dpapi-current-user-v1'
        updated_utc = [DateTime]::UtcNow.ToString('o')
    }
    New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
    [System.IO.File]::WriteAllText(
        $profilePath,
        (($profile | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object System.Text.UTF8Encoding($false)))

    [PSCustomObject]@{
        State = 'saved'
        ProviderKind = $ProviderKind
        RevitVersion = $RevitVersion
        BaseUrl = $normalizedBaseUrl
        Model = $normalizedModel
        ProfilePath = $profilePath
        CredentialStorage = 'Windows DPAPI (CurrentUser)'
    }
}
finally {
    if ($keyPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer)
    }
}
