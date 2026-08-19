[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][ValidatePattern('^20\d{2}$')][string]$RevitVersion,
  [Parameter(Mandatory=$true)][string]$RootDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $utf8OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    [Console]::OutputEncoding = $utf8OutputEncoding
    $OutputEncoding = $utf8OutputEncoding
}
catch { }
function Backup-Config([string]$Path) {
  if (Test-Path -LiteralPath $Path -PathType Leaf) {
    $backupPath = $Path + '.revitaibhub-backup-' + (Get-Date -Format 'yyyyMMddHHmmss')
    Copy-Item -LiteralPath $Path -Destination $backupPath -Force
    Write-Output ('备份: ' + $backupPath)
  }
}
function Escape-Toml([string]$Value) {
  return $Value.Replace('\', '\\').Replace('"', '\"')
}
$bundledNodePath = Join-Path $RootDirectory 'runtime\node.exe'
$nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
$nodePath = if (Test-Path -LiteralPath $bundledNodePath -PathType Leaf) {
  $bundledNodePath
} elseif ($null -ne $nodeCommand) {
  $nodeCommand.Source
} else {
  throw 'Node.js 运行环境缺失。请使用包含内置运行环境的完整安装包重新安装。'
}
$nodeVersion = (& $nodePath --version).Trim()
Write-Output ('Node.js 运行环境: ' + $nodeVersion + ' / ' + $nodePath)
$mcpScript = Join-Path $RootDirectory 'scripts\revit-mcp-server.mjs'
$serverName = 'revit_' + $RevitVersion
$server = [ordered]@{ command=$nodePath; args=@($mcpScript); env=[ordered]@{ REVIT_COMMAND_BRIDGE_ROOT=$RootDirectory; REVIT_COMMAND_BRIDGE_VERSION=$RevitVersion } }
$connections = Join-Path $RootDirectory 'connections'
New-Item -ItemType Directory -Force -Path $connections | Out-Null
$configuredNames = New-Object System.Collections.Generic.List[string]
$genericPath = Join-Path $connections ('generic-mcp-revit-' + $RevitVersion + '.mcp.json')
[ordered]@{ mcpServers=[ordered]@{ $serverName=$server } } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $genericPath -Encoding UTF8
Write-Output ('通用 MCP 配置: ' + $genericPath)
$guidePath = Join-Path $connections ('README-导入AI-' + $RevitVersion + '.txt')
@"
Revit 命令桥 MCP 配置（Revit $RevitVersion）

1. 已安装 Revit 命令桥并打开 Revit 项目。
2. 将 generic-mcp-revit-$RevitVersion.mcp.json 的内容导入 Codex、WorkBuddy 或其它 MCP 客户端。
3. 如果客户端支持自动配置，重启客户端后即可看到 revit_$RevitVersion。
4. 先让 AI 查询项目和标高，再预览计划，确认后才执行写入。

通用配置文件：$genericPath
"@ | Set-Content -LiteralPath $guidePath -Encoding UTF8
Write-Output ('MCP 使用说明: ' + $guidePath)

$codexConfig = Join-Path $env:USERPROFILE '.codex\config.toml'
$codexCommand = Get-Command codex.exe -ErrorAction SilentlyContinue
if ($null -ne $codexCommand -or (Test-Path -LiteralPath (Split-Path -Parent $codexConfig) -PathType Container)) {
  Backup-Config $codexConfig
  if ($null -ne $codexCommand) {
    & $codexCommand.Source mcp get $serverName *> $null
    if ($LASTEXITCODE -ne 0) {
      & $codexCommand.Source mcp add $serverName --env ('REVIT_COMMAND_BRIDGE_ROOT=' + $RootDirectory) --env ('REVIT_COMMAND_BRIDGE_VERSION=' + $RevitVersion) -- $nodePath $mcpScript | Out-Null
    }
    Write-Output ('Codex 已配置: ' + $serverName)
    $configuredNames.Add('Codex')
  } else {
    $existing = if (Test-Path -LiteralPath $codexConfig) { Get-Content -LiteralPath $codexConfig -Raw -Encoding UTF8 } else { '' }
    if ($existing -notmatch [regex]::Escape('[mcp_servers.' + $serverName + ']')) {
      $nl = [Environment]::NewLine
      $block = $nl + '[mcp_servers.' + $serverName + ']' + $nl +
        'command = "' + (Escape-Toml $nodePath) + '"' + $nl +
        'args = ["' + (Escape-Toml $mcpScript) + '"]' + $nl +
        'env = { REVIT_COMMAND_BRIDGE_ROOT = "' + (Escape-Toml $RootDirectory) + '", REVIT_COMMAND_BRIDGE_VERSION = "' + $RevitVersion + '" }' + $nl
      Add-Content -LiteralPath $codexConfig -Value $block -Encoding UTF8
    }
    Write-Output ('Codex 配置已写入: ' + $codexConfig)
    $configuredNames.Add('Codex')
  }
}
$jsonClients = @(
  @{ Name='WorkBuddy'; Path=(Join-Path $env:USERPROFILE '.workbuddy\mcp.json') },
  @{ Name='WorkBuddyUser'; Path=(Join-Path $env:USERPROFILE '.workbuddy\.mcp.json') },
  @{ Name='ClaudeDesktop'; Path=(Join-Path $env:APPDATA 'Claude\claude_desktop_config.json') },
  @{ Name='Cursor'; Path=(Join-Path $env:USERPROFILE '.cursor\mcp.json') },
  @{ Name='Windsurf'; Path=(Join-Path $env:USERPROFILE '.codeium\windsurf\mcp_config.json') },
  @{ Name='Cline'; Path=(Join-Path $env:APPDATA 'Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json') },
  @{ Name='RooCode'; Path=(Join-Path $env:APPDATA 'Code\User\globalStorage\rooveterinaryinc.roo-cline\settings\mcp_settings.json') }
)
$configuredCount = 0
foreach ($client in $jsonClients) {
  $path = [string]$client.Path
  $parent = Split-Path -Parent $path
  if (-not (Test-Path -LiteralPath $path -PathType Leaf) -and -not (Test-Path -LiteralPath $parent -PathType Container)) { continue }
  try {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $document = [ordered]@{}
    if (Test-Path -LiteralPath $path -PathType Leaf) {
      $parsed = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
      if ($parsed -is [pscustomobject]) { $parsed.psobject.Properties | ForEach-Object { $document[$_.Name] = $_.Value } }
      Backup-Config $path
    }
    if (-not $document.Contains('mcpServers') -or $null -eq $document.mcpServers) {
      $document['mcpServers'] = [ordered]@{}
    }
    $servers = $document['mcpServers']
    if ($servers -is [System.Collections.IDictionary]) {
      $servers[$serverName] = $server
    } else {
      $servers | Add-Member -NotePropertyName $serverName -NotePropertyValue $server -Force
    }
    $document | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Output ($client.Name + ' 已配置: ' + $path)
    $configuredCount++
    $configuredNames.Add([string]$client.Name)
  } catch { Write-Output ($client.Name + ' 跳过: ' + $_.Exception.Message) }
}
[ordered]@{ schema_version=1; revit_version=$RevitVersion; node_path=$nodePath; node_version=$nodeVersion; detected_clients=$configuredNames.Count; configured_client_names=@($configuredNames | Select-Object -Unique); generic_mcp=$genericPath; protocols=@('stdio-mcp','json-mcp','rest','cli') } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $connections ('detected-clients-' + $RevitVersion + '.json')) -Encoding UTF8
Write-Output ('识别并配置客户端数量: ' + $configuredCount)
Write-Output '未知客户端使用 connections\generic-mcp-revit-<版本>.mcp.json'
