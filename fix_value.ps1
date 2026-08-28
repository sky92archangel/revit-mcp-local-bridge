# 全面修复所有 ElementId.Value -> ElementId.GetValue()
# 使用显式 UTF-8 编码读取和写入文件

$files = Get-ChildItem -Path src -Filter *.cs

foreach ($file in $files) {
    $content = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    $original = $content
    
    # Pattern 1: variableName.Id.Value (e.g., level.Id.Value, view.Id.Value, element.Id.Value)
    # This is the most common pattern - any variable/property access to .Id then .Value
    $content = [regex]::Replace($content, '(\.Id)\.Value\b', '$1.GetValue()')
    
    # Pattern 2: variableName.Value where variableName ends with Id (ElementId variables)
    $content = [regex]::Replace($content, '(?<!\w)(\w+Id)\.Value(?!\w)', { param($m) $m.Groups[1].Value + '.GetValue()' })
    
    # Pattern 3: standalone id.Value (id is a common ElementId variable name)
    $content = [regex]::Replace($content, '(?<!\w)id\.Value(?!\w)', 'id.GetValue()')
    
    # Pattern 4: ElementId.InvalidElementId.Value
    $content = $content -replace 'ElementId\.InvalidElementId\.Value', 'ElementId.InvalidElementId.GetValue()'
    
    # Pattern 5: new ElementId(...).Value
    $content = [regex]::Replace($content, 'new ElementId\([^)]*\)\.Value', { param($m) $m.Value -replace '\.Value$', '.GetValue()' })
    
    # Pattern 6: parameter.AsElementId().Value
    $content = $content -replace 'parameter\.AsElementId\(\)\.Value', 'parameter.AsElementId().GetValue()'
    
    # Pattern 7: .Select(id => id.Value) in lambda expressions
    $content = $content -replace '\.Select\(id => id\.Value\)', '.Select(id => id.GetValue())'
    $content = $content -replace '\.Select\(id => \(int\)id\.Value\)', '.Select(id => (int)id.GetValue())'
    
    # Pattern 8: array[index].Value where array is ElementId[]
    $content = [regex]::Replace($content, '(\w+)\[(\w+)\]\.Value', '$1[$2].GetValue()')
    
    # Pattern 9: (int)something.Id.Value or (long)something.Id.Value
    $content = [regex]::Replace($content, '\((int|long)\)(\w+\.Id)\.Value\b', '($1)$2.GetValue()')
    
    # Pattern 10: (int)id.Value
    $content = [regex]::Replace($content, '\((int|long)\)id\.Value\b', '($1)id.GetValue()')
    
    if ($content -ne $original) {
        [System.IO.File]::WriteAllText($file.FullName, $content, [System.Text.Encoding]::UTF8)
        Write-Host "Modified: $($file.Name)"
    }
}
Write-Host "Done!"
