# AI 提供者配置器 —— 使用 Windows DPAPI 加密保存模型 API Key 和相关配置
# AI provider configurator — encrypts and saves model API Key and related configuration using Windows DPAPI
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

# 启用严格模式与错误停止
# Enable strict mode and stop-on-error
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 设置控制台为 UTF-8 输出
# Set console output to UTF-8
try {
    $utf8OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    [Console]::OutputEncoding = $utf8OutputEncoding
    $OutputEncoding = $utf8OutputEncoding
}
catch { }
# 加载 System.Security 以使用 DPAPI
# Load System.Security for DPAPI support
Add-Type -AssemblyName System.Security

# 获取配置文件存储目录
# Get the profile storage directory
function Get-ProfileDirectory {
    param([string]$Directory, [string]$Version)
    if (-not [string]::IsNullOrWhiteSpace($Directory)) {
        return [System.IO.Path]::GetFullPath($Directory)
    }
    return Join-Path $env:LOCALAPPDATA "RevitCommandBridge\$Version\ai-providers"
}

# 生成 DPAPI 熵值（附加的字节数据用于增强加密）
# Generate DPAPI entropy (additional byte data to strengthen encryption)
function Get-ProfileEntropy {
    param([string]$Version)
    return [System.Text.Encoding]::UTF8.GetBytes("RevitCommandBridge:ai-provider:1:$Version")
}

# 标准化 URL 和模型名，确定配置文件路径
# Normalize URL and model name, determine profile path
$normalizedBaseUrl = $BaseUrl.Trim().TrimEnd('/')
$normalizedModel = $Model.Trim()
$targetDirectory = Get-ProfileDirectory -Directory $ProfileDirectory -Version $RevitVersion
$profilePath = Join-Path $targetDirectory "$ProfileName.json"

# -WhatIf 仅预览不写入
# -WhatIf: preview only, no write
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

# 交互式输入 API Key（如果未通过参数提供）
# Prompt for API Key interactively (if not provided via parameter)
if ($null -eq $ApiKey) {
    $ApiKey = Read-Host -Prompt 'Model API Key (stored with Windows DPAPI for this user only)' -AsSecureString
}

# 使用 Windows DPAPI 加密 API Key 并写入配置文件
# Encrypt API Key using Windows DPAPI and write the configuration file
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
