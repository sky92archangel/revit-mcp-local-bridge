[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$PackageDirectory,
    [string]$InstallDirectory,
    [string]$AddinsDirectory,
    [ValidateSet('none', 'codex', 'workbuddy', 'deepseek', 'generic-mcp', 'rest')]
    [string]$Connector = 'none'
)

& (Join-Path $PSScriptRoot 'install-revit.ps1') @PSBoundParameters -RevitVersion '2020'
