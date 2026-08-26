# 连接器配置工具 —— 为指定 AI 客户端或平台生成 MCP / REST / AI 配置文件
# Connector configurator — generates MCP / REST / AI profile files for a specified client or platform
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('codex', 'workbuddy', 'deepseek', 'function-api', 'openai-compatible', 'generic-mcp', 'rest')]
    [string]$Provider,
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,
    [string]$RootDirectory,
    [string]$OutputDirectory,
    [string]$NodePath
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

# 转义 TOML 字符串中的特殊字符
# Escape special characters in TOML strings
function Escape-TomlString {
    param([string]$Value)
    return ($Value -replace '\\', '\\' -replace '"', '\\"')
}

# 读取安装元数据（bridge.config.json），获取 Revit 版本等信息
# Read installation metadata (bridge.config.json) to get Revit version, etc.
$installDirectory = Split-Path -Parent $PSScriptRoot
$metadataPath = Join-Path $installDirectory 'bridge.config.json'
$metadata = $null
if (Test-Path -LiteralPath $metadataPath) {
    $metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
if ([string]::IsNullOrWhiteSpace($RevitVersion)) {
    if ($null -eq $metadata -or [string]::IsNullOrWhiteSpace([string]$metadata.revit_version)) {
        throw 'RevitVersion was not supplied and bridge.config.json has no version.'
    }
    $RevitVersion = [string]$metadata.revit_version
}
if ([string]::IsNullOrWhiteSpace($RootDirectory)) {
    $RootDirectory = Join-Path (Join-Path $env:LOCALAPPDATA 'RevitCommandBridge') $RevitVersion
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $installDirectory 'connections'
}
if ([string]::IsNullOrWhiteSpace($NodePath)) {
    $bundledNodePath = Join-Path $RootDirectory 'runtime\node.exe'
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    $NodePath = if (Test-Path -LiteralPath $bundledNodePath -PathType Leaf) {
        $bundledNodePath
    }
    elseif ($null -ne $node) {
        $node.Source
    }
    else {
        throw 'Node.js 运行环境缺失。请使用包含内置运行环境的完整安装包重新安装。'
    }
}

if (-not (Test-Path -LiteralPath $NodePath -PathType Leaf)) {
    throw "Node.js 运行环境不存在: $NodePath"
}

$mcpScript = Join-Path $PSScriptRoot 'revit-mcp-server.mjs'
$httpScript = Join-Path $PSScriptRoot 'revit-http-gateway.mjs'
foreach ($requiredPath in @($mcpScript, $httpScript)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Bridge script is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$serverName = "revit_$RevitVersion"
$restPort = 8765 + (([int]$RevitVersion * 31) % 1000)
$mcpServer = [ordered]@{
    command = $NodePath
    args = @($mcpScript)
    env = [ordered]@{
        REVIT_COMMAND_BRIDGE_ROOT = $RootDirectory
        REVIT_COMMAND_BRIDGE_VERSION = $RevitVersion
    }
}
$restProfile = [ordered]@{
    base_url = "http://127.0.0.1:$restPort"
    health_url = "http://127.0.0.1:$restPort/health"
    command_url = "http://127.0.0.1:$restPort/commands?wait_seconds=60"
    port = $restPort
    start_command = @($NodePath, $httpScript)
    environment = $mcpServer.env
}
$aiProviderProfilePath = Join-Path $RootDirectory 'ai-providers\default.json'
$aiLauncher = Join-Path $PSScriptRoot 'start-openai-compatible-chat.ps1'
$aiProvider = [ordered]@{
    kind = 'openai-compatible-chat-completions'
    profile_path = $aiProviderProfilePath
    credential_storage = 'Windows DPAPI (CurrentUser)'
    launcher = [ordered]@{
        command = 'powershell.exe'
        args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $aiLauncher, '-RevitVersion', $RevitVersion)
    }
}

$profile = [ordered]@{
    provider = $Provider
    revit_version = $RevitVersion
    protocol = 'revit-command-bridge/2.0'
    bridge_root = $RootDirectory
    mcp_server = $mcpServer
    rest = $restProfile
    ai_provider = $aiProvider
    notes = @(
        'Start Revit and open a project first.',
        'Use preview=true before write operations.',
        'External applications must use revit_execute_plan or revit_command only.'
    )
}
$profilePath = Join-Path $OutputDirectory "$Provider-$RevitVersion.connection.json"
$profile | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $profilePath -Encoding UTF8

$generated = @($profilePath)
switch ($Provider) {
    'codex' {
        $tomlPath = Join-Path $OutputDirectory "codex-revit-$RevitVersion.toml"
        $toml = @(
            "[mcp_servers.$serverName]",
            ('command = "' + (Escape-TomlString $NodePath) + '"'),
            ('args = ["' + (Escape-TomlString $mcpScript) + '"]'),
            ('env = { REVIT_COMMAND_BRIDGE_ROOT = "' + (Escape-TomlString $RootDirectory) + '", REVIT_COMMAND_BRIDGE_VERSION = "' + (Escape-TomlString $RevitVersion) + '" }')
        ) -join [Environment]::NewLine
        [System.IO.File]::WriteAllText($tomlPath, $toml + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
        $generated += $tomlPath
    }
    { $_ -in @('workbuddy', 'generic-mcp') } {
        $mcpPath = Join-Path $OutputDirectory "$Provider-revit-$RevitVersion.mcp.json"
        $mcpServers = [ordered]@{}
        $mcpServers[$serverName] = $mcpServer
        [ordered]@{ mcpServers = $mcpServers } |
            ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath $mcpPath -Encoding UTF8
        $generated += $mcpPath
    }
    'openai-compatible' {
        $aiPath = Join-Path $OutputDirectory "openai-compatible-revit-$RevitVersion.ai.json"
        [ordered]@{
            provider = 'openai-compatible'
            revit_version = $RevitVersion
            protocol = 'revit-command-bridge/2.0'
            ai_provider = $aiProvider
            rest = $restProfile
            notes = @(
                'This profile contains no API key.',
                'Use the installer or configure-ai-provider.ps1 to save an API key protected by Windows DPAPI.',
                'Any provider with OpenAI-compatible Chat Completions and tool calling can use this launcher.'
            )
        } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $aiPath -Encoding UTF8
        $generated += $aiPath
    }
    { $_ -in @('deepseek', 'function-api', 'rest') } {
        $restPath = Join-Path $OutputDirectory "$Provider-revit-$RevitVersion.rest.json"
        $restProfile | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $restPath -Encoding UTF8
        $generated += $restPath
    }
}

$nextStep = switch ($Provider) {
    'openai-compatible' {
        'Configure an API key through Revit AI Hub Setup or configure-ai-provider.ps1, then start the local assistant with: powershell -NoProfile -ExecutionPolicy Bypass -File "' + $aiLauncher + '" -RevitVersion ' + $RevitVersion
        break
    }
    { $_ -in @('deepseek', 'function-api', 'rest') } {
        'Start the local REST gateway with: "' + $NodePath + '" "' + $httpScript + '"'
        break
    }
    default {
        "Import the generated MCP configuration into $Provider, then restart that application."
        break
    }
}

[PSCustomObject]@{
    Provider = $Provider
    RevitVersion = $RevitVersion
    GeneratedFiles = $generated
    BridgeRoot = $RootDirectory
    NextStep = $nextStep
}
