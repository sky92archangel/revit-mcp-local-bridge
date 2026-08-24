[CmdletBinding()]
param(
    [string[]]$RevitVersions,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$manifestPath = Join-Path $PSScriptRoot 'build\version-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "未找到版本清单: $manifestPath"
}
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

if ($null -eq $manifest.versions -or $manifest.versions.Count -eq 0) {
    throw "版本清单为空"
}

$targets = if ($RevitVersions) {
    $manifest.versions | Where-Object { $_.year -in ($RevitVersions | ForEach-Object { [int]$_ }) }
} else {
    $manifest.versions
}

if ($null -eq $targets -or $targets.Count -eq 0) {
    $allYears = ($manifest.versions | ForEach-Object { $_.year }) -join ', '
    throw "未找到匹配的版本。可用版本: $allYears"
}

$results = @()
foreach ($version in $targets) {
    Write-Host "`n═══════════════════════════════════════════"
    Write-Host "  编译 Revit $($version.year)  ($($version.runtime))"
    Write-Host "═══════════════════════════════════════════`n"

    $started = Get-Date
    try {
        & (Join-Path $PSScriptRoot 'build.ps1') -RevitVersion $version.year -SkipInstaller:$SkipInstaller.IsPresent
        $elapsed = (Get-Date) - $started
        Write-Host "[OK] Revit $($version.year) 完成 ($($elapsed.TotalSeconds.ToString('F1'))s)" -ForegroundColor Green
        $results += @{ year = $version.year; status = 'ok'; elapsed = $elapsed.TotalSeconds }
    } catch {
        Write-Host "[FAIL] Revit $($version.year): $($_.Exception.Message)" -ForegroundColor Red
        $results += @{ year = $version.year; status = 'fail'; error = $_.Exception.Message }
    }
}

Write-Host "`n═══════════════════════════════════════════"
Write-Host "  编译汇总"
Write-Host "═══════════════════════════════════════════"
$ok   = @($results | Where-Object { $_.status -eq 'ok' })
$fail = @($results | Where-Object { $_.status -eq 'fail' })
Write-Host "成功: $($ok.Count)  |  失败: $($fail.Count)"
foreach ($r in $ok)   { Write-Host "  [OK]    Revit $($r.year)  ($($r.elapsed.ToString('F1'))s)" }
foreach ($r in $fail) { Write-Host "  [FAIL]  Revit $($r.year): $($r.error)" -ForegroundColor Red }

if ($fail.Count -gt 0) { exit 1 }
