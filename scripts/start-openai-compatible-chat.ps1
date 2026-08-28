# OpenAI 兼容聊天启动器 —— 解密 DPAPI 配置中的 API Key，设置环境变量并启动 Node.js 聊天助手
# OpenAI-compatible chat launcher — decrypts the API Key from DPAPI config, sets environment variables, and launches the Node.js chat assistant
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$ProfileName = 'default',
    [string]$ProfilePath,
    [string]$BridgeRoot,
    [string]$NodePath,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$HarnessArguments
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
# 加载 System.Security 以使用 DPAPI 解密
# Load System.Security for DPAPI decryption
Add-Type -AssemblyName System.Security

function Get-ProfileEntropy {
    param([string]$Version)
    return [System.Text.Encoding]::UTF8.GetBytes("RevitCommandBridge:ai-provider:1:$Version")
}

# 解析配置文件路径与桥接根目录
# Resolve profile path and bridge root directory
if ([string]::IsNullOrWhiteSpace($ProfilePath)) {
    $ProfilePath = Join-Path $env:LOCALAPPDATA "RevitCommandBridge\$RevitVersion\ai-providers\$ProfileName.json"
}
$BridgeRoot = if ([string]::IsNullOrWhiteSpace($BridgeRoot)) {
    Join-Path $env:LOCALAPPDATA "RevitCommandBridge\$RevitVersion"
}
else {
    [System.IO.Path]::GetFullPath($BridgeRoot)
}
$ProfilePath = [System.IO.Path]::GetFullPath($ProfilePath)
if (-not (Test-Path -LiteralPath $ProfilePath)) {
    throw "AI provider profile was not found: $ProfilePath. Run configure-ai-provider.ps1 or Revit AI Hub Setup first."
}

# 加载并验证 AI 提供者配置文件
# Load and validate the AI provider profile
$profile = Get-Content -LiteralPath $ProfilePath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($requiredProperty in @('provider_kind', 'revit_version', 'base_url', 'model', 'api_key_protected', 'credential_scheme')) {
    if ($null -eq $profile.PSObject.Properties[$requiredProperty] -or [string]::IsNullOrWhiteSpace([string]$profile.$requiredProperty)) {
        throw "AI provider profile is missing ${requiredProperty}: $ProfilePath"
    }
}
if ([string]$profile.provider_kind -ne 'openai-compatible' -or [string]$profile.credential_scheme -ne 'dpapi-current-user-v1') {
    throw "Unsupported AI provider profile: $ProfilePath"
}
if ([string]$profile.revit_version -ne $RevitVersion) {
    throw "AI provider profile targets Revit $($profile.revit_version), not Revit $RevitVersion."
}

if ([string]::IsNullOrWhiteSpace($NodePath)) {
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.Source)) {
        throw 'Node.js 18 or newer is required for the local OpenAI-compatible assistant.'
    }
    $NodePath = $node.Source
}
$harness = Join-Path $PSScriptRoot 'revit-openai-compatible-chat.mjs'
foreach ($requiredPath in @($NodePath, $harness)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required local assistant component is missing: $requiredPath"
    }
}

$apiKeyBytes = $null
$apiKey = $null
$previousApiKey = $env:REVIT_AI_API_KEY
$previousBaseUrl = $env:REVIT_AI_BASE_URL
$previousModel = $env:REVIT_AI_MODEL
$previousRoot = $env:REVIT_COMMAND_BRIDGE_ROOT
$previousVersion = $env:REVIT_COMMAND_BRIDGE_VERSION
try {
    $apiKeyBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$profile.api_key_protected),
        (Get-ProfileEntropy -Version $RevitVersion),
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    $apiKey = [System.Text.Encoding]::UTF8.GetString($apiKeyBytes)
    if ([string]::IsNullOrWhiteSpace($apiKey)) {
        throw "AI provider profile contains an empty API key: $ProfilePath"
    }

    $env:REVIT_AI_API_KEY = $apiKey
    $env:REVIT_AI_BASE_URL = ([string]$profile.base_url).Trim().TrimEnd('/')
    $env:REVIT_AI_MODEL = ([string]$profile.model).Trim()
    $env:REVIT_COMMAND_BRIDGE_ROOT = $BridgeRoot
    $env:REVIT_COMMAND_BRIDGE_VERSION = $RevitVersion
    & $NodePath $harness @HarnessArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The local OpenAI-compatible assistant exited with code $LASTEXITCODE."
    }
}
finally {
    if ($null -ne $apiKeyBytes) {
        [Array]::Clear($apiKeyBytes, 0, $apiKeyBytes.Length)
    }
    $env:REVIT_AI_API_KEY = $previousApiKey
    $env:REVIT_AI_BASE_URL = $previousBaseUrl
    $env:REVIT_AI_MODEL = $previousModel
    $env:REVIT_COMMAND_BRIDGE_ROOT = $previousRoot
    $env:REVIT_COMMAND_BRIDGE_VERSION = $previousVersion
}
