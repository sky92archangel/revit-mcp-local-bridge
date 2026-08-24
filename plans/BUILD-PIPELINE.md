# 单版本构建管道方案（Revit 2020-2024）

## 设计目标

- 每个 Revit 版本有独立的编译入口和符号配置
- 单一 `build.ps1` 作为统一调度器，按版本矩阵分发
- 共享源码（`src/`）被所有版本引用
- 版本差异（API、编译符号）集中在版本清单中声明
- 仅支持 .NET Framework 4.8 + csc.exe 编译管道

---

## 1. 版本矩阵总览

| Revit | .NET 运行时 | 编译工具 | 编译符号 |
|-------|------------|---------|---------|
| 2020 | .NET Framework 4.8 | `csc.exe` | _(无)_ |
| 2021 | .NET Framework 4.8 | `csc.exe` | `REVIT_FORGE_UNITS` |
| 2022 | .NET Framework 4.8 | `csc.exe` | `REVIT_FORGE_UNITS` |
| 2023 | .NET Framework 4.8 | `csc.exe` | `REVIT_FORGE_UNITS` + `REVIT_PARAMETER_GROUPS` |
| 2024 | .NET Framework 4.8 | `csc.exe` | `REVIT_FORGE_UNITS` + `REVIT_PARAMETER_GROUPS` |

---

## 2. 目录结构

```
revit-mcp-local-bridge/
│
├── src/                              ← ★ 单一事实源（全部 .cs，全部版本共享）
│   ├── PlanCommandExecutor.cs
│   ├── RevitPlanCreations.cs
│   └── ...
│
├── build/                            ← ★ 版本构建配置目录
│   └── version-manifest.json         ←    版本矩阵声明文件
│
├── build.ps1                         ← 单版本编译
├── build-all.ps1                     ← 全版本批量编译
├── build-installer.ps1               ← 安装器打包
├── install-revit.ps1                 ← 安装/检测
│
├── plans/
│   └── BUILD-PIPELINE.md             ← 本文档
└── VERSION-SUPPORT.md                ← 版本支持文档
```

---

## 3. 版本清单文件

`build/version-manifest.json` 是构建管道的**单一数据源**，声明每个 Revit 版本的元数据：

```json
{
  "schema_version": 1,
  "versions": [
    {
      "year": 2020,
      "runtime": "net48",
      "compiler": "csc",
      "define_symbols": [],
      "framework_references": [
        "System.Web.Extensions.dll",
        "System.Windows.Forms.dll",
        "System.Drawing.dll"
      ],
      "wpf_references": [
        "WindowsBase.dll",
        "PresentationCore.dll"
      ]
    },
    {
      "year": 2021,
      "runtime": "net48",
      "compiler": "csc",
      "define_symbols": ["REVIT_FORGE_UNITS"],
      "inherits_from": 2020
    },
    {
      "year": 2022,
      "runtime": "net48",
      "compiler": "csc",
      "define_symbols": ["REVIT_FORGE_UNITS"],
      "inherits_from": 2020
    },
    {
      "year": 2023,
      "runtime": "net48",
      "compiler": "csc",
      "define_symbols": ["REVIT_FORGE_UNITS", "REVIT_PARAMETER_GROUPS"],
      "inherits_from": 2020
    },
    {
      "year": 2024,
      "runtime": "net48",
      "compiler": "csc",
      "define_symbols": ["REVIT_FORGE_UNITS", "REVIT_PARAMETER_GROUPS"],
      "inherits_from": 2020
    }
  ]
}
```

---

## 4. csc.exe 编译管道

2020-2024 全部使用 .NET Framework 编译器 (`csc.exe`)，无 `.csproj` 文件。

`build.ps1` 从版本清单读取符号和引用列表，构造 csc 命令行：

```powershell
# 读取版本清单
$manifest = Get-Content (Join-Path $PSScriptRoot 'build\version-manifest.json') | ConvertFrom-Json
$versionConfig = $manifest.versions | Where-Object { $_.year -eq [int]$RevitVersion }

# 使用清单中的符号和引用
$symbols = $versionConfig.define_symbols
$refArgs = $versionConfig.framework_references | ForEach-Object { "/reference:$_" }
```

### 编译命令结构

```powershell
csc.exe /nologo /target:library /platform:anycpu /optimize+ /debug:pdbonly `
  /define:REVIT_FORGE_UNITS;REVIT_PARAMETER_GROUPS `
  /out:dist\RevitCommandBridge-2024\RevitCommandBridge.dll `
  /reference:RevitAPI.dll /reference:RevitAPIUI.dll `
  /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  /reference:C:\...\WPF\WindowsBase.dll /reference:C:\...\WPF\PresentationCore.dll `
  src/*.cs
```

---

## 5. 单版本编译入口 `build.ps1`

```powershell
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^20\d{2}$')]
    [string]$RevitVersion,
    [string]$RevitInstallDirectory,
    [string]$OutputDirectory,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── 1. 加载版本清单 ──
$manifestPath = Join-Path $PSScriptRoot 'build\version-manifest.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$versionConfig = $manifest.versions | Where-Object { $_.year -eq [int]$RevitVersion }
if ($null -eq $versionConfig) {
    throw "版本清单中未定义 Revit $RevitVersion。请在 build/version-manifest.json 中添加。"
}

# ── 2. 解析 RevitAPI 路径 ──
$revitApi = Join-Path $RevitInstallDirectory 'RevitAPI.dll'
$revitApiUi = Join-Path $RevitInstallDirectory 'RevitAPIUI.dll'
$outputDir = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $PSScriptRoot "dist\RevitCommandBridge-$RevitVersion" }
$assemblyPath = Join-Path $outputDir 'RevitCommandBridge.dll'

# ── 3. csc.exe 编译 ──
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' |
    Sort-Object Name | ForEach-Object FullName)

$refArgs = @("/reference:$revitApi", "/reference:$revitApiUi")
foreach ($refName in $versionConfig.framework_references) {
    $refArgs += "/reference:$refName"
}
foreach ($refName in $versionConfig.wpf_references) {
    $refArgs += "/reference:$wpfDir\$refName"
}
$defineArgs = if ($versionConfig.define_symbols.Count -gt 0) {
    @("/define:" + ($versionConfig.define_symbols -join ';'))
} else { @() }

$cscArgs = @('/nologo', '/target:library', '/platform:anycpu',
    '/optimize+', '/debug:pdbonly') + $defineArgs +
    @("/out:$assemblyPath") + $refArgs + $sourceFiles

& $cscPath @cscArgs

# ── 4. 公共打包与元数据输出 ──
# ── 5. 版本元数据 ──
$metadata = [ordered]@{
    product       = 'RevitCommandBridge'
    revit_version = $RevitVersion
    protocol      = 'revit-command-bridge/2.0'
    runtime       = 'net48'
    symbols       = $versionConfig.define_symbols -join ','
}
$metadata | ConvertTo-Json | Set-Content (Join-Path $outputDir 'bridge.config.json') -Encoding UTF8

Write-Host "Build completed for Revit ${RevitVersion}: $assemblyPath"
```

---

## 6. 批量编译入口 `build-all.ps1`

```powershell
[CmdletBinding()]
param(
    [string[]]$RevitVersions,
    [switch]$SkipInstaller
)

$manifest = Get-Content -Raw (Join-Path $PSScriptRoot 'build\version-manifest.json') | ConvertFrom-Json
$targets = if ($RevitVersions) {
    $manifest.versions | Where-Object { $_.year -in $RevitVersions }
} else {
    $manifest.versions
}

foreach ($version in $targets) {
    & (Join-Path $PSScriptRoot 'build.ps1') -RevitVersion $version.year -SkipInstaller:$SkipInstaller
}
```

---

## 7. 验收标准

| 验收项 | 验证方式 | 通过条件 |
|--------|---------|---------|
| 2020 编译 | `build.ps1 -RevitVersion 2020` | 输出 `dist/RevitCommandBridge-2020/RevitCommandBridge.dll` |
| 2024 编译 | `build.ps1 -RevitVersion 2024` | 输出含 `REVIT_FORGE_UNITS;REVIT_PARAMETER_GROUPS` 符号的 DLL |
| 批量编译 | `build-all.ps1` | 全部已定义版本依次编译成功 |
| 部分失败不影响整体 | `build-all.ps1` 中一个版本失败 | 继续编译后续版本，最终汇总报告 |
| 清单驱动 | `version-manifest.json` | 新增版本只需改清单，`build.ps1` 不动 |
