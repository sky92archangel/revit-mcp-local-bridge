# 构建管道方案（Revit 2020–2026）

## 设计目标

- 每个 Revit 版本有独立的编译入口和符号配置
- 单一 `.csproj` + `dotnet build` 作为统一编译器，按配置矩阵分发
- 共享源码（`src/`）被所有版本引用
- 版本差异（API、编译符号）集中在 `.csproj` 属性组中声明
- R20–R24 使用 .NET Framework 4.8，R25–R26 使用 .NET 8 Windows

---

## 1. 版本矩阵总览

| Revit | .NET 运行时 | 编译工具 | Nice3point 包版本 | 条件编译符号 |
|-------|------------|---------|-------------------|-------------|
| 2020 | .NET Framework 4.8 | `dotnet build` | 2020.* | _(无)_ |
| 2021 | .NET Framework 4.8 | `dotnet build` | 2021.* | _(无)_ |
| 2022 | .NET Framework 4.8 | `dotnet build` | 2022.* | `REVIT2022_OR_GREATER` |
| 2023 | .NET Framework 4.8 | `dotnet build` | 2023.* | `+REVIT2023_OR_GREATER` |
| 2024 | .NET Framework 4.8 | `dotnet build` | 2024.* | `+REVIT2024_OR_GREATER` |
| 2025 | .NET 8.0 Windows | `dotnet build` | 2025.* | `+REVIT2025_OR_GREATER` |
| 2026 | .NET 8.0 Windows | `dotnet build` | 2026.* | `+REVIT2025_OR_GREATER` |

---

## 2. 目录结构

```
revit-mcp-local-bridge/
│
├── src/                              ← ★ 单一事实源（全部 .cs，全部版本共享）
│   ├── PlanCommandExecutor.cs
│   ├── RevitPlanCreations.cs
│   └── ...
│   └── Adapter/                      ← 版本特定入口（R20–R27）
│       ├── AdapterEntry20.cs
│       └── ...
│
├── build/                            ← 版本清单
│   └── version-manifest.json
│
├── RevitCommandBridge.csproj         ← ★ 统一项目文件，14 个配置
├── RevitCommandBridge.slnx           ← .slnx 新格式解决方案
│
├── build.ps1                         ← 单版本编译（调用 dotnet build）
├── build-all.ps1                     ← 全版本批量编译
├── build-installer.ps1               ← 安装器打包
├── install-revit.ps1                 ← 安装/检测
│
├── plans/
│   └── BUILD-PIPELINE.md             ← 本文档
└── VERSION-SUPPORT.md                ← 版本支持文档
```

---

## 3. 项目文件驱动编译（RevitCommandBridge.csproj）

`RevitCommandBridge.csproj` 是构建管道的**单一数据源**，14 个配置（Debug/Release × R20–R26）。

### 3.1 框架选择

```xml
<!-- Revit 2020–2024: .NET Framework 4.8 -->
<PropertyGroup Condition="$(Configuration.Contains('R20')) Or $(Configuration.Contains('R21')) Or $(Configuration.Contains('R22')) Or $(Configuration.Contains('R23')) Or $(Configuration.Contains('R24'))">
    <TargetFramework>net48</TargetFramework>
</PropertyGroup>

<!-- Revit 2025–2026: .NET 8 Windows -->
<PropertyGroup Condition="$(Configuration.Contains('R25')) Or $(Configuration.Contains('R26'))">
    <TargetFramework>net8.0-windows</TargetFramework>
</PropertyGroup>
```

### 3.2 条件编译符号

```xml
<PropertyGroup Condition="$(Configuration.Contains('R22'))">
    <DefineConstants>$(DefineConstants);REVIT2022_OR_GREATER</DefineConstants>
</PropertyGroup>
<PropertyGroup Condition="$(Configuration.Contains('R25')) Or $(Configuration.Contains('R26'))">
    <DefineConstants>$(DefineConstants);REVIT2022_OR_GREATER;REVIT2023_OR_GREATER;REVIT2024_OR_GREATER;REVIT2025_OR_GREATER</DefineConstants>
</PropertyGroup>
```

### 3.3 版本适配入口

每个配置编译仅对应的 AdapterEntry，通过 condition 筛选：

```xml
<ItemGroup Condition="$(Configuration.Contains('R20'))">
    <Compile Include="src\Adapter\AdapterEntry20.cs" />
</ItemGroup>
```

### 3.4 NuGet 引用

通过 Nice3point.Revit 包自动获取对应版本的 Revit API DLL：

```xml
<PackageReference Include="Nice3point.Revit.Build.Tasks" Version="2.*" />
<PackageReference Include="Nice3point.Revit.Api.RevitAPI" Version="$(RevitVersion).*" />
<PackageReference Include="Nice3point.Revit.Api.RevitAPIUI" Version="$(RevitVersion).*" />
```

Nice3point.Revit.Build.Tasks 自动处理 `Private=false`、生成 `.addin`、部署到 Revit Addins 等外围工作。

---

## 4. 构建命令

### 4.1 单版本编译

```powershell
# 编译 Revit 2026 版本（Debug）
dotnet build -c "Debug R26"

# 编译 Revit 2026 版本（Release）
dotnet build -c "Release R26"

# 使用 build.ps1 包装（含安装器打包和版本清单校验）
.\build.ps1 -RevitVersion 2026
```

### 4.2 批量编译

```powershell
# 编译所有版本清单中定义的版本
.\build-all.ps1

# 仅编译指定版本
.\build-all.ps1 -RevitVersions 2020,2026
```

### 4.3 产物目录

```text
bin\
├── R20\RevitCommandBridge.dll
├── R21\RevitCommandBridge.dll
├── R22\RevitCommandBridge.dll
├── R23\RevitCommandBridge.dll
├── R24\RevitCommandBridge.dll
├── R25\RevitCommandBridge.dll
└── R26\RevitCommandBridge.dll

dist\
├── RevitCommandBridge-2020\
├── RevitCommandBridge-2021\
├── ...
└── RevitCommandBridge-2026\
    ├── RevitCommandBridge.dll
    ├── RevitCommandBridge.pdb
    ├── bridge.config.json
    └── ...
```

---

## 5. 安装器打包

```powershell
# 打包单个版本
.\build-installer.ps1

# 打包多个版本
.\build-installer.ps1 -RevitVersion 2026,2027

# 自定义输出文件名
.\build-installer.ps1 -OutputPath "dist\RevitCommandBridgeSetup-2026.exe"
```

---

## 6. 验收标准

| 验收项 | 验证方式 | 通过条件 |
|--------|---------|---------|
| 2020 编译 | `dotnet build -c "Debug R20"` | 输出 `bin\R20\RevitCommandBridge.dll` |
| 2024 编译 | `dotnet build -c "Debug R24"` | 输出含 `REVIT2024_OR_GREATER` 符号的 DLL |
| 2026 编译 | `dotnet build -c "Debug R26"` | 输出 .NET 8 DLL，含 `REVIT2025_OR_GREATER` |
| 批量编译 | `build-all.ps1` | 全部已定义版本依次编译成功 |
| 部分失败不影响整体 | `build-all.ps1` 中一个版本失败 | 继续编译后续版本，最终汇总报告 |
| 清单驱动 | `build/version-manifest.json` | 新增版本只需改清单和 `.csproj`，`build.ps1` 不动 |
