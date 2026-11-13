# Unity 없이 새 파일을 만들었을 때, 빠진 .meta 파일을 만들어 준다.
# Unity 는 .meta 가 없으면 스스로 만들지만, 그러면 PC마다 guid 가 달라져 씬이 스크립트를 찾지 못할 수 있다.
# 이미 있는 .meta 는 건드리지 않는다.
#
# 사용:  powershell -File tools/check/add-meta.ps1
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..\..\client\Assets' | Resolve-Path
$utf8 = New-Object System.Text.UTF8Encoding $false
$made = 0
Get-ChildItem -Path $root -Recurse -File -Include *.cs, *.unity, *.asmdef, *.json, *.txt | ForEach-Object {
    $meta = $_.FullName + '.meta'
    if (Test-Path $meta) { return }
    $guid = [guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($meta, "fileFormatVersion: 2`nguid: $guid`n", $utf8)
    Write-Output ("meta 생성: " + $_.FullName.Substring($root.Path.Length + 1) + " -> " + $guid)
    $made++
}
# 폴더에도 .meta 가 있어야 한다.
Get-ChildItem -Path $root -Recurse -Directory | ForEach-Object {
    $meta = $_.FullName + '.meta'
    if (Test-Path $meta) { return }
    $guid = [guid]::NewGuid().ToString('N')
    $text = "fileFormatVersion: 2`nguid: $guid`nfolderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
    [IO.File]::WriteAllText($meta, $text, $utf8)
    Write-Output ("meta 생성(폴더): " + $_.FullName.Substring($root.Path.Length + 1) + " -> " + $guid)
    $made++
}
Write-Output "새로 만든 .meta: $made 개"
