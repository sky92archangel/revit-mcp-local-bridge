# 修复所有剩余构建错误
# 使用显式 UTF-8 编码读取和写入文件

function Fix-File {
    param($path, $old, $new)
    $content = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    if ($content.Contains($old)) {
        $content = $content.Replace($old, $new)
        [System.IO.File]::WriteAllText($path, $content, [System.Text.Encoding]::UTF8)
        Write-Host "  Fixed: $old -> $new"
        return $true
    }
    return $false
}

# 1. 修复 CommandPanelForm.cs - Color 歧义
Write-Host "=== Fixing CommandPanelForm.cs ==="
$cpf = "src/CommandPanelForm.cs"
$content = [System.IO.File]::ReadAllText($cpf, [System.Text.Encoding]::UTF8)

# 替换 Color.White -> System.Drawing.Color.White
$content = $content.Replace("Color.White", "System.Drawing.Color.White")
# 替换 Color.FromArgb -> System.Drawing.Color.FromArgb
$content = $content.Replace("Color.FromArgb", "System.Drawing.Color.FromArgb")
# 替换 Panel -> System.Windows.Forms.Panel (但保留 CommandPanelForm 和 CommandPanelManager)
$content = $content.Replace("var body = new Panel", "var body = new System.Windows.Forms.Panel")
# 替换 TextBox -> System.Windows.Forms.TextBox (但保留 _command 字段声明已修复)
$content = $content.Replace("new TextBox", "new System.Windows.Forms.TextBox")

[System.IO.File]::WriteAllText($cpf, $content, [System.Text.Encoding]::UTF8)
Write-Host "  Fixed Color/Panel/TextBox ambiguity"

# 2. 修复 RevitParameterAdmin.cs - PG_HVAC
Write-Host "=== Fixing RevitParameterAdmin.cs ==="
$rpa = "src/RevitParameterAdmin.cs"
$content = [System.IO.File]::ReadAllText($rpa, [System.Text.Encoding]::UTF8)
# PG_HVAC 在 R20 中不存在，使用 PG_MECHANICAL
$content = $content.Replace("BuiltInParameterGroup.PG_HVAC", "BuiltInParameterGroup.PG_MECHANICAL")
[System.IO.File]::WriteAllText($rpa, $content, [System.Text.Encoding]::UTF8)
Write-Host "  Fixed PG_HVAC -> PG_MECHANICAL"

# 3. 修复 RevitPlanCreations.cs - Floor.Create
Write-Host "=== Fixing RevitPlanCreations.cs ==="
$rpc = "src/RevitPlanCreations.cs"
$content = [System.IO.File]::ReadAllText($rpc, [System.Text.Encoding]::UTF8)
# R20 使用 Document.Create.NewFloor 而不是 Floor.Create
# 查找 Floor.Create( 并替换
$content = $content.Replace("Floor.Create(", "Document.Create.NewFloor(")
[System.IO.File]::WriteAllText($rpc, $content, [System.Text.Encoding]::UTF8)
Write-Host "  Fixed Floor.Create -> Document.Create.NewFloor"

# 4. 修复 RevitLookups.cs - Parameter.GetUnitTypeId()
Write-Hook "=== Fixing RevitLookups.cs ==="
$rl = "src/RevitLookups.cs"
$content = [System.IO.File]::ReadAllText($rl, [System.Text.Encoding]::UTF8)
# R20 使用 Parameter.Definition.ParameterType 而不是 Parameter.GetUnitTypeId()
$content = $content.Replace("parameter.GetUnitTypeId()", "parameter.Definition.ParameterType")
[System.IO.File]::WriteAllText($rl, $content, [System.Text.Encoding]::UTF8)
Write-Host "  Fixed parameter.GetUnitTypeId() -> parameter.Definition.ParameterType"

# 5. 修复 RevitOutputOperations.cs - ParameterFilterRuleFactory 字符串重载
Write-Host "=== Fixing RevitOutputOperations.cs ==="
$roo = "src/RevitOutputOperations.cs"
$content = [System.IO.File]::ReadAllText($roo, [System.Text.Encoding]::UTF8)
# R20 的 ParameterFilterRuleFactory.CreateEqualsRule 不接受 string 参数
# 需要替换为 ElementId 版本
# 查找 pattern: ParameterFilterRuleFactory.CreateEqualsRule(someId, someString)
# 替换为: ParameterFilterRuleFactory.CreateEqualsRule(someId, new ElementId(BuiltInCategory.OST_...))
# 但更简单的方式是使用 ParameterValueProvider 和 FilterStringRule
# 实际上，对于字符串过滤，R20 使用 FilterStringRule
# 先看看具体代码
Write-Host "  Note: ParameterFilterRuleFactory string overload needs manual review"

# 6. 全面修复所有剩余 .Value -> .GetValue()
Write-Host "=== Fixing remaining .Value -> .GetValue() ==="
$files = @(
    "src/RevitPlanQueries.cs",
    "src/RevitPlanCreations.cs",
    "src/RevitOutputOperations.cs",
    "src/RevitCommandExecutor.cs",
    "src/RevitFamilyOperations.cs",
    "src/RevitLookups.cs",
    "src/PlanCommandExecutor.cs",
    "src/RevitPlanMutations.cs"
)

foreach ($f in $files) {
    $content = [System.IO.File]::ReadAllText($f, [System.Text.Encoding]::UTF8)
    $changed = $false
    
    # 替换 .Value 但排除 .ValueTuple, .ValueType, .Values, .Value 作为完整单词
    # 只替换 ElementId 上的 .Value
    # 模式: 变量名以 Id 结尾 + .Value
    # 使用正则: (\w+Id)\.Value\b 但排除 InvalidElementId
    
    # 简单替换所有 .Value 为 .GetValue() 但排除特殊情况
    # 先备份
    $backup = $content
    
    # 替换各种模式
    $patterns = @(
        @{old = 'id\.Value'; new = 'id.GetValue()'},
        @{old = '\.Value\b'; new = '.GetValue()'}  # 这个太宽泛，需要更精确
    )
    
    # 更精确的方法：只替换 ElementId 变量上的 .Value
    # 匹配 pattern: 字母数字(可能以Id结尾).Value 但不在字符串中
    $content = [System.Text.RegularExpressions.Regex]::Replace($content, '(\w+Id)\.Value\b', '$1.GetValue()')
    
    if ($content -ne $backup) {
        [System.IO.File]::WriteAllText($f, $content, [System.Text.Encoding]::UTF8)
        Write-Host "  Fixed .Value -> .GetValue() in $f"
    } else {
        Write-Host "  No changes in $f"
    }
}

Write-Host ""
Write-Host "=== Fix complete ==="
