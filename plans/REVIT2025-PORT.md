# Revit 2025+（.NET 8）移植方案

## 背景

Revit 2025 起，Autodesk 将插件运行时从 **.NET Framework 4.8** 迁移至 **.NET 8**。这一变更要求插件的编译目标、引用程序集、构建工具链全部更换——项目现有 `csc.exe` + .NET Framework 4.8 的编译路径无法产出 Revit 2025+ 可加载的 DLL。

本文档给出从**现有架构**到**双运行时并存**的完整技术方案：新增一个 .NET 8 适配器项目，与现有 .NET Framework 4.8 项目共享 90%+ 的源代码，通过条件编译处理版本差异。

---

## 目录

- [1. 总体策略](#1-总体策略)
- [2. 项目结构变更](#2-项目结构变更)
- [3. 详细实现步骤](#3-详细实现步骤)
  - [3.1 新建 .NET 8 项目](#31-新建-net-8-项目)
  - [3.2 链接现有源代码](#32-链接现有源代码)
  - [3.3 处理 System.Web.Extensions 依赖](#33-处理-systemwebextensions-依赖)
  - [3.4 新增 REVIT_NET8 条件编译符号](#34-新增-revit_net8-条件编译符号)
  - [3.5 改造 build.ps1 编译分岔](#35-改造-buildps1-编译分岔)
  - [3.6 更新安装脚本](#36-更新安装脚本)
  - [3.7 更新 VERSION-SUPPORT.md](#37-更新-version-supportmd)
  - [3.8 真机回归验证](#38-真机回归验证)
- [4. 条件编译清单](#4-条件编译清单)
- [5. .NET 8 API 差异与适配](#5-net-8-api-差异与适配)
- [6. 风险与回退](#6-风险与回退)

---

## 1. 总体策略

```
┌─────────────────────────────┐      ┌─────────────────────────────┐
│  .NET Framework 4.8 路径    │      │     .NET 8 路径             │
│  (Revit 2020-2024)          │      │  (Revit 2025-2026)          │
│                             │      │                             │
│  build.ps1                  │      │  build.ps1 (分岔)           │
│    └→ csc.exe               │      │    └→ dotnet build          │
│    └→ src/*.cs              │      │    └→ src-net8/*.csproj     │
│         (源文件)            │      │         └→ src/*.cs (链接)  │
│                             │      │              (共享源码)     │
│  dist/RevitCommandBridge-20XX│     │  dist/RevitCommandBridge-20XX│
└─────────────────────────────┘      └─────────────────────────────┘
```

- **不修改**现有 `src/` 下 22 个 `.cs` 文件的核心逻辑
- **新增** `src-net8/RevitCommandBridge.Adapter25.csproj`，通过 `<Compile Link=` 引用现有源文件
- 差异点用条件编译 `#if REVIT_NET8` / `#else` 处理
- 构建脚本根据 `-RevitVersion` 自动选择编译路径

---

## 2. 项目结构变更

```
revit-mcp-local-bridge/
├── src/                           ← 不变：.NET Framework 4.8 源文件
│   ├── PlanCommandExecutor.cs
│   ├── RevitPlanCreations.cs
│   ├── RevitPlanQueries.cs
│   ├── RevitPlanMutations.cs
│   ├── RevitOutputOperations.cs
│   ├── RevitParameterAdmin.cs
│   ├── BridgeSchemas.cs
│   ├── BridgeFailurePreprocessor.cs
│   ├── BridgeFamilyLoadOptions.cs
│   ├── RevitSectionFactory.cs
│   ├── PlanValues.cs
│   ├── RevitPlanOperations.cs
│   ├── RevitLookups.cs
│   ├── BridgeBuildInfo.cs
│   ├── BridgeFileQueue.cs
│   ├── BridgeModels.cs
│   ├── BridgeRuntime.cs
│   ├── RevitCommandBridgeApp.cs
│   ├── RevitCommandExecutor.cs
│   ├── RevitFamilyOperations.cs
│   ├── RevitGeometryFactory.cs
│   ├── CommandPanelForm.cs
│   └── RevitLookups.cs
│
├── src-net8/                      ← 新增：.NET 8 项目
│   ├── RevitCommandBridge.Adapter25.csproj    ← 项目文件
│   └── Adapter25Entry.cs                      ← .NET 8 入口适配
│
├── build.ps1                      ← 修改：版本分岔 + dotnet build
├── install-revit.ps1              ← 修改：扫描 2025-2026
├── install-revit2020.ps1          ← 不变
├── plans/
│   ├── EXTENSION-PLAN.md          ← 不变
│   └── REVIT2025-PORT.md          ← 本文档
└── VERSION-SUPPORT.md             ← 修改
```

### 新增文件说明

| 文件 | 用途 |
|------|------|
| `src-net8/RevitCommandBridge.Adapter25.csproj` | .NET 8 SDK 风格项目文件，链接 `src/` 下所有 `.cs`，引用 `net8.0-windows` 目标框架 |
| `src-net8/Adapter25Entry.cs` | .NET 8 下 `IExternalApplication` 入口适配（如需要处理 `AppDomain` 差异） |

---

## 3. 详细实现步骤

### 3.1 新建 .NET 8 项目

**位置**：`src-net8/RevitCommandBridge.Adapter25.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <AssemblyName>RevitCommandBridge</AssemblyName>
    <RootNamespace>RevitCommandBridge</RootNamespace>
    <PlatformTarget>AnyCPU</PlatformTarget>
    <Optimize>true</Optimize>
    <DebugType>pdbonly</DebugType>
  </PropertyGroup>

  <!-- 条件编译符号：2025+ 必定包含两个既有符号 -->
  <PropertyGroup>
    <DefineConstants>REVIT_FORGE_UNITS;REVIT_PARAMETER_GROUPS;REVIT_NET8</DefineConstants>
  </PropertyGroup>

  <!-- 链接 src/ 下所有源文件 -->
  <ItemGroup>
    <Compile Include="..\src\*.cs" Link="src/%(Filename)%(Extension)" />
  </ItemGroup>

  <!-- 排除入口冲突 -->
  <ItemGroup>
    <Compile Remove="..\src\BridgeRuntime.cs" />
  </ItemGroup>

  <!-- 新增 .NET 8 专属源文件 -->
  <ItemGroup>
    <Compile Include="Adapter25Entry.cs" />
  </ItemGroup>

  <!-- Revit API 引用（由构建脚本传入路径） -->
  <ItemGroup>
    <Reference Include="RevitAPI">
      <HintPath Condition="Exists('$(RevitInstallDirectory)\RevitAPI.dll')">$(RevitInstallDirectory)\RevitAPI.dll</HintPath>
    </Reference>
    <Reference Include="RevitAPIUI">
      <HintPath Condition="Exists('$(RevitInstallDirectory)\RevitAPIUI.dll')">$(RevitInstallDirectory)\RevitAPIUI.dll</HintPath>
    </Reference>
  </ItemGroup>

  <!-- System.Text.Json 替代 System.Web.Extensions -->
  <ItemGroup>
    <PackageReference Include="System.Text.Json" Version="8.0.4" />
  </ItemGroup>

</Project>
```

### 3.2 链接现有源代码

**原理**：`<Compile Include="..\src\*.cs" Link=...>` 将 `src/` 下所有 `.cs` **作为链接文件**包含到 .NET 8 项目中。

**关键点**：

- 这些文件**不复制**，`src/` 下的修改立即反映到两个编译目标
- 需要在 `.NET 8` 项目中使用 `<Compile Remove>` 排除不兼容的文件（如 `BridgeRuntime.cs` 如果依赖 `AppDomain`）
- .NET 8 项目可以单独添加 `Adapter25Entry.cs` 来提供 .NET 8 专属实现

### 3.3 处理 System.Web.Extensions 依赖

现有项目引用了 `System.Web.Extensions.dll`（.NET Framework 4.8 自带）。在 .NET 8 中该程序集**不存在**。

**需要搜索并替换**此依赖的使用：

```bash
grep -rn "JavaScriptSerializer\|System.Web.Extensions\|Web.Script" src/ --include="*.cs"
```

**典型替换方案**：

```csharp
#if REVIT_NET8
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif
```

**具体替换点**（待确认）：

| 使用位置 | .NET Framework 4.8 | .NET 8 |
|---------|-------------------|--------|
| JSON 序列化 | `JavaScriptSerializer().Serialize(obj)` | `JsonSerializer.Serialize(obj)` |
| JSON 反序列化 | `JavaScriptSerializer().Deserialize<T>(str)` | `JsonSerializer.Deserialize<T>(str)` |
| 字典序列化 | `JavaScriptSerializer().Serialize(dict)` | `JsonSerializer.Serialize(dict)` |

创建一个统一工具方法文件 `src/JsonHelper.cs`（两端共享）：

```csharp
internal static class JsonHelper
{
#if REVIT_NET8
    public static string Serialize(object value) => System.Text.Json.JsonSerializer.Serialize(value);
    public static T Deserialize<T>(string json) => System.Text.Json.JsonSerializer.Deserialize<T>(json);
#else
    public static string Serialize(object value) => new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(value);
    public static T Deserialize<T>(string json) => new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<T>(json);
#endif
}
```

### 3.4 新增 REVIT_NET8 条件编译符号

**在 `build.ps1` 中自动追加**：

```powershell
if ($apiVersion.Major -ge 25) {
    $symbols += 'REVIT_NET8'
}
```

**在代码中使用**：

```csharp
#if REVIT_NET8
    // .NET 8 + Revit 2025+ API
    string path = AppContext.BaseDirectory;
#else
    // .NET Framework 4.8 + Revit 2020-2024
    string path = AppDomain.CurrentDomain.BaseDirectory;
#endif
```

**源代码中需要添加 `#if REVIT_NET8` 分支的位置**（基于代码分析）：

| 文件 | 原因 | 处理方式 |
|------|------|---------|
| `src/RevitCommandExecutor.cs` | 可能使用 `AppDomain.CurrentDomain`、`Assembly.GetExecutingAssembly().Location` | 替换为 `AppContext.BaseDirectory` / `Assembly.GetExecutingAssembly().Location`（.NET 8 仍支持后者） |
| `src/RevitCommandBridgeApp.cs` | `IExternalApplication` 入口 | 确认签名无变化（2025 API 文档），否则加 `#if` |
| `src/BridgeRuntime.cs` | 可能依赖 `AppDomain` 或 `System.Web` | 完全替换实现，.NET 8 下跳过 |
| `src/BridgeFileQueue.cs` | `FileSystemWatcher` 差异 | 确认无 API 变更 |
| `src/CommandPanelForm.cs` | WinForms 差异 | 确认 .NET 8 WinForms 兼容 |

### 3.5 改造 build.ps1 编译分岔

**修改后逻辑**（替换第 28-77 行）：

```powershell
$revitVersionNum = [int]$RevitVersion

if ($revitVersionNum -ge 2025) {
    # ──────────────────────────────────────────
    # .NET 8 编译路径
    # ──────────────────────────────────────────
    Write-Host "Detected Revit $RevitVersion — using .NET 8 build path"

    # 验证 dotnet SDK 可用
    $dotnetResult = dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "缺少 .NET 8 SDK。请安装 .NET 8 SDK 后再编译 Revit $RevitVersion。"
    }
    $dotnetVersion = $dotnetResult.Trim()
    Write-Host "dotnet SDK version: $dotnetVersion"

    # 设置 RevitAPI 引用路径作为 MSBuild 属性传入
    $projectFile = Join-Path $PSScriptRoot 'src-net8\RevitCommandBridge.Adapter25.csproj'
    if (-not (Test-Path -LiteralPath $projectFile)) {
        throw "找不到 .NET 8 项目文件: $projectFile"
    }

    # 构建
    dotnet build $projectFile `
        --configuration Release `
        -p:RevitInstallDirectory=$RevitInstallDirectory `
        -p:OutputPath=$OutputDirectory

    if ($LASTEXITCODE -ne 0) {
        throw ".NET 8 编译失败。"
    }
} else {
    # ──────────────────────────────────────────
    # .NET Framework 4.8 编译路径（原有逻辑）
    # ──────────────────────────────────────────
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $revitApi = Join-Path $RevitInstallDirectory 'RevitAPI.dll'
    $revitApiUi = Join-Path $RevitInstallDirectory 'RevitAPIUI.dll'

    foreach ($requiredPath in @($csc, $revitApi, $revitApiUi)) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw "Missing build dependency: $requiredPath"
        }
    }

    $apiAssemblyName = [Reflection.AssemblyName]::GetAssemblyName($revitApi)
    $apiVersion = $apiAssemblyName.Version

    $sourceDirectory = Join-Path $PSScriptRoot 'src'
    $sourceFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $assemblyPath = Join-Path $OutputDirectory 'RevitCommandBridge.dll'

    $symbols = @()
    if ($apiVersion.Major -ge 21) {
        $symbols += 'REVIT_FORGE_UNITS'
    }
    if ($apiVersion.Major -ge 23) {
        $symbols += 'REVIT_PARAMETER_GROUPS'
    }

    $compilerArguments = @(
        '/nologo',
        '/target:library',
        '/platform:anycpu',
        '/optimize+',
        '/debug:pdbonly',
        $(if ($symbols.Count -gt 0) { '/define:' + ($symbols -join ';') } else { $null }),
        ('/out:' + $assemblyPath),
        ('/reference:' + $revitApi),
        ('/reference:' + $revitApiUi),
        '/reference:System.Web.Extensions.dll',
        '/reference:System.Windows.Forms.dll',
        '/reference:System.Drawing.dll',
        '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll',
        '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll'
    ) + $sourceFiles | Where-Object { $null -ne $_ }

    & $csc @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "RevitCommandBridge compilation failed with exit code: $LASTEXITCODE"
    }
}

# ──────────────────────────────────────────
# 公共打包步骤（两路径共用）
# ──────────────────────────────────────────
foreach ($directoryName in @('scripts', 'examples', 'deploy', 'schemas', 'src', 'verification')) {
    $source = Join-Path $PSScriptRoot $directoryName
    if (Test-Path -LiteralPath $source) {
        $destination = Join-Path $OutputDirectory $directoryName
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $destination -Recurse -Force
    }
}

foreach ($fileName in @('README.md', 'PROTOCOL.md', 'ARCHITECTURE.md', 'ENGINEERING-RECORD.md', 'VERSION-SUPPORT.md', 'CONNECTORS.md', 'install-revit.ps1', 'uninstall-revit.ps1', 'install-revit2020.ps1', 'build-revit-adapter.ps1')) {
    $source = Join-Path $PSScriptRoot $fileName
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $OutputDirectory $fileName) -Force
    }
}

$packageMetadata = [ordered]@{
    product       = 'RevitCommandBridge'
    revit_version = $RevitVersion
    protocol      = 'revit-command-bridge/2.0'
    runtime       = if ($revitVersionNum -ge 2025) { 'net8.0-windows' } else { 'net48' }
}
$packageMetadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'bridge.config.json') -Encoding UTF8

if (-not $SkipInstaller.IsPresent) {
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -DistDirectory (Join-Path $PSScriptRoot 'dist') | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Revit AI Hub setup build failed with exit code: $LASTEXITCODE"
    }
}

Write-Host "Build completed for Revit ${RevitVersion}: $assemblyPath"
```

### 3.6 更新安装脚本

**文件**：`install-revit.ps1`

**注册表扫描范围扩展**（找到 Revit 2025/2026 的检测键）：

```powershell
# 在扫描列表中添加
$revitYears2025Plus = @(2025, 2026)
foreach ($year in $revitYears) { ... }  # 原有 2020-2024 逻辑

# Revit 2025+ 的注册表键
$hkcu2025 = "HKCU:\Software\Autodesk\Revit\2025\Product"
$hkcu2026 = "HKCU:\Software\Autodesk\Revit\2026\Product"
```

**安装路径**：Revit 2025+ 插件的 `.addin` 引用路径不变（`%APPDATA%\Autodesk\Revit\Addins\2025\`），但 `Assembly` 指向 `net8.0-windows` 编译的 DLL。

### 3.7 更新 VERSION-SUPPORT.md

修改当前支持矩阵表：

```markdown
| Revit version | Build route | Validation state | Notes |
| --- | --- | --- | --- |
| 2020–2024 | `csc.exe` .NET Framework 4.8 | [V] 2020 verified; 2021-2024 API-compiled | 不变 |
| 2025–2026 | `dotnet build` .NET 8 | [T] build path added; live regression pending | .NET 8 适配器项目, 共享 src/ 源码, REVIT_NET8 条件编译 |
```

### 3.8 真机回归验证

**验证清单**：

```
[ ] build.ps1 -RevitVersion 2025 成功编译（.NET 8 路径）
[ ] build.ps1 -RevitVersion 2024 不受影响（.NET Framework 4.8 路径）
[ ] Revit 2025 加载插件无异常
[ ] 65 个原子操作的 preview 模式均返回正常（只读）
[ ] 创建一个标高 + 一段墙 + 一个楼板 + 一个洞口（写事务）
[ ] load_family 加载 .rfa 族文件
[ ] query_parameters 枚举全部参数
[ ] query_geometry 三档返回
[ ] check_interferences 碰撞检测
[ ] transform_elements move/copy/rotate/mirror
[ ] 中文别名全部正常识别
[ ] BridgeFailurePreprocessor 在位，警告不弹框
[ ] 同一计划在 Revit 2020 和 2025 输出一致
[ ] 构建产物 bridge.config.json 中 runtime 字段正确
```

---

## 4. 条件编译清单

**已存在**（`build.ps1` 自动注入）：

| 符号 | 触发条件 | 用途 |
|------|---------|------|
| `REVIT_FORGE_UNITS` | API Major >= 21（2021+） | `Parameter.GetUnitTypeId()` vs `DisplayUnitType` |
| `REVIT_PARAMETER_GROUPS` | API Major >= 23（2023+） | `GroupTypeId` vs `BuiltInParameterGroup` |

**本次新增**（`build.ps1` 中 `Major >= 25` 注入）：

| 符号 | 触发条件 | 用途 |
|------|---------|------|
| `REVIT_NET8` | API Major >= 25（2025+） | .NET 8 专属 API 适配 |

**代码中两符号与 `REVIT_NET8` 的包含关系**：2025+ 必定包含前两个符号（2025 >= 2021，2025 >= 2023），因此 `REVIT_NET8` 分支内不需要额外判断 `REVIT_FORGE_UNITS`。

---

## 5. .NET 8 API 差异与适配

### 5.1 已知差异

| 差异项 | .NET Framework 4.8 | .NET 8 | 影响文件 |
|--------|-------------------|--------|---------|
| 编译器 | `csc.exe` | `dotnet build` | `build.ps1` |
| 引用格式 | 直接 `/reference` DLL | NuGet PackageReference + SDK 风格 | `*.csproj` |
| `System.Web.Extensions` | 内置于 GAC | 不存在 | 有引用的 `.cs` 文件 |
| `JavaScriptSerializer` | `System.Web.Script.Serialization` | 无，改用 `System.Text.Json` | `BridgeModels.cs` 等 |
| `AppDomain.CurrentDomain.BaseDirectory` | 可用 | `AppContext.BaseDirectory` | `BridgeRuntime.cs` |
| `Assembly.GetExecutingAssembly().Location` | 可用 | 仍可用 | 无需更改 |
| `FileSystemWatcher` | `System.IO.FileSystemWatcher` | 同名，API 兼容 | 无需更改 |
| WinForms (`System.Windows.Forms`) | GAC | NuGet `System.Windows.Forms` | `CommandPanelForm.cs` |
| WPF (`PresentationCore`, `WindowsBase`) | GAC | NuGet `System.Windows.Extensions` | 部分工具类 |

### 5.2 WinForms/WPF 在 .NET 8 下的处理

`.csproj` 中设置了 `<UseWindowsForms>true</UseWindowsForms>` 和 `<UseWPF>true</UseWPF>`，SDK 会自动引用对应的 NuGet 包，无需手动指定版本。

### 5.3 Revit API 2025+ 可能的弃用

已知的 Revit API 弃用趋势（需对照 2025 API 文档逐项确认）：

| API | 风险 | 替换方案 |
|-----|------|---------|
| `ParameterFilterRuleFactory.CreateEqualsRule` | 2025+ 可能弃用 | `FilterStringRule` + `FilterNumericEquals` 组合 |
| `NewShaftOpening(bottomLevel, topLevel, profile)` | 签名可能变化 | 参考 2025 API 文档 |
| `FamilyManager.AddParameter` 重载 | 可能与 ForgeTypeId 统一 | 2021+ 已用 ForgeTypeId，无需额外处理 |

这些差异可在 `src/RevitPlanCreations.cs` 中用 `#if !REVIT_NET8` / `#else` 切换。

---

## 6. 风险与回退

| 风险 | 影响 | 缓解措施 |
|------|------|---------|
| Revit 2025 API 有未预料的签名变化 | 编译失败 | .NET 8 项目先 `dotnet build` 快速失败排查，修改对应 `#if` 分支 |
| `BridgeRuntime.cs` 无法直接移植 | 入口适配 | 重新实现 `IExternalApplication`，使用 .NET 8 兼容的路径获取方式 |
| `System.Text.Json` 序列化行为与 `JavaScriptSerializer` 不一致 | 协议兼容性 | 在 `JsonHelper` 统一工具中设置 `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`，保持输出一致 |
| 2025 加载 DLL 时 Framework 版本不匹配 | 插件不可用 | `net8.0-windows` 必须精确匹配 Revit 2025 的运行时版本 |
| 维护两套编译路径 | 构建复杂度 | 共享源码策略（`<Compile Link>`）保证单一事实源，仅在必须差异化处用 `#if` |

**回退策略**：若 `dotnet build` 路径出现问题，2025+ 编译失败不应影响 2020-2024 的 `.NET Framework 4.8` 编译路径——两者的 `if/else` 分支完全独立，异常互不传染。

---

## 附录 A：移植工作流速查

```powershell
# 编译 Revit 2026（.NET 8 路径）
.\build.ps1 -RevitVersion 2026 `
    -RevitInstallDirectory "C:\Program Files\Autodesk\Revit 2026"

# 安装到 Revit 2026
.\install-revit.ps1 -RevitVersion 2026 `
    -PackageDirectory .\dist\RevitCommandBridge-2026

# 编译 Revit 2020（.NET Framework 4.8 路径，不受影响）
.\build.ps1 -RevitVersion 2020 `
    -RevitInstallDirectory "C:\Program Files\Autodesk\Revit 2020"
```

## 附录 B：验收标准

| 标准 | 命令 | 预期 |
|------|------|------|
| .NET 8 编译成功 | `dotnet build src-net8` | 无错误，输出 `RevitCommandBridge.dll` |
| 版本分岔正确 | `build.ps1 -RevitVersion 2025` 成功，`2024` 也成功 | 两个独立输出目录 |
| 时间线回归 | `build.ps1 -RevitVersion 2020` | 原有 `csc.exe` 路径仍正常工作 |
| 安装检测 | `install-revit.ps1 -ListDetected` | Revit 2025/2026 出现在扫描结果中 |
| 真机加载 | Revit 2025 启动 → 加载插件（无模态错误） | .addin 正确指向 `net8.0-windows` DLL |
| 65 原子预览 | MCP 发送 `preview: true` 计划 | 65 个操作全部正常返回 preview data |
| 65 原子执行 | MCP 发送简单建模计划 | 模型正确创建，事务正常提交 |
