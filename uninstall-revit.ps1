[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,
    [string]$InstallDirectory,
    [string]$AddinsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $utf8OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    [Console]::OutputEncoding = $utf8OutputEncoding
    $OutputEncoding = $utf8OutputEncoding
}
catch { }

if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    throw '请先关闭所有 Revit 窗口，再卸载命令桥。'
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $env:LOCALAPPDATA "RevitCommandBridge\$RevitVersion"
}
if ([string]::IsNullOrWhiteSpace($AddinsDirectory)) {
    $AddinsDirectory = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
}

$manifestPath = Join-Path $AddinsDirectory 'RevitCommandBridge.addin'
$serverName = 'revit_' + $RevitVersion
$removed = New-Object System.Collections.Generic.List[string]

function Backup-File {
    param([string]$Path)
    $backup = $Path + '.revitaibhub-uninstall-backup-' + (Get-Date -Format 'yyyyMMddHHmmss')
    Copy-Item -LiteralPath $Path -Destination $backup -Force
    return $backup
}

function Remove-JsonMcpEntry {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try {
        $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
        $document = $raw | ConvertFrom-Json -AsHashtable
        if ($null -eq $document -or $null -eq $document['mcpServers'] -or -not $document['mcpServers'].ContainsKey($serverName)) { return }
        Backup-File $Path | Out-Null
        $document['mcpServers'].Remove($serverName)
        $document | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
        $script:removed.Add('AI 连接：' + $Path)
    }
    catch {
        Write-Warning ('未自动清理 AI 连接配置：' + $Path + '。' + $_.Exception.Message)
    }
}

if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
    if ($manifest -match [regex]::Escape('RevitCommandBridge.dll')) {
        if ($PSCmdlet.ShouldProcess($manifestPath, '删除 Revit 命令桥加载项清单')) {
            Remove-Item -LiteralPath $manifestPath -Force
            $removed.Add('Revit 加载项：' + $manifestPath)
        }
    }
    else {
        Write-Warning '同名加载项清单不属于命令桥，未删除。'
    }
}

$codexConfig = Join-Path $env:USERPROFILE '.codex\config.toml'
if (Test-Path -LiteralPath $codexConfig) {
    $raw = Get-Content -LiteralPath $codexConfig -Raw -Encoding UTF8
    $pattern = '(?ms)^\[mcp_servers\.' + [regex]::Escape($serverName) + '\][\s\S]*?(?=^\[|\z)'
    if ([regex]::IsMatch($raw, $pattern)) {
        if ($PSCmdlet.ShouldProcess($codexConfig, '移除 Codex 中的命令桥连接')) {
            Backup-File $codexConfig | Out-Null
            [regex]::Replace($raw, $pattern, '').TrimEnd() + [Environment]::NewLine | Set-Content -LiteralPath $codexConfig -Encoding UTF8
            $removed.Add('Codex 连接：' + $codexConfig)
        }
    }
}

foreach ($path in @(
    (Join-Path $env:USERPROFILE '.workbuddy\mcp.json'),
    (Join-Path $env:USERPROFILE '.workbuddy\.mcp.json'),
    (Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'),
    (Join-Path $env:USERPROFILE '.cursor\mcp.json'),
    (Join-Path $env:USERPROFILE '.codeium\windsurf\mcp_config.json'),
    (Join-Path $env:APPDATA 'Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json'),
    (Join-Path $env:APPDATA 'Code\User\globalStorage\rooveterinaryinc.roo-cline\settings\mcp_settings.json')
)) {
    Remove-JsonMcpEntry $path
}

if (Test-Path -LiteralPath $InstallDirectory) {
    $resolvedInstall = (Resolve-Path -LiteralPath $InstallDirectory).Path
    $expectedInstall = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "RevitCommandBridge\$RevitVersion")).TrimEnd('\')
    if (-not [string]::Equals($resolvedInstall.TrimEnd('\'), $expectedInstall, [StringComparison]::OrdinalIgnoreCase)) {
        throw '安装目录与命令桥默认目录不一致，拒绝删除：' + $resolvedInstall
    }
    if ($PSCmdlet.ShouldProcess($resolvedInstall, '删除命令桥本地文件与连接配置')) {
        Remove-Item -LiteralPath $resolvedInstall -Recurse -Force
        $removed.Add('命令桥文件：' + $resolvedInstall)
    }
}

if ($removed.Count -eq 0) {
    Write-Output ('Revit ' + $RevitVersion + ' 未发现可卸载的命令桥组件。')
}
else {
    Write-Output ('已卸载 Revit ' + $RevitVersion + ' 命令桥：')
    $removed | ForEach-Object { Write-Output ('- ' + $_) }
}
