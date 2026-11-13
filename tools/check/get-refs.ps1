# Unity 참조용 어셈블리를 tools/check/.cache 에 내려받는다 (형식 검사용. 저장소에는 넣지 않는다).
# 출처: BepInEx NuGet 피드의 UnityEngine.Modules (참조 전용 어셈블리), Unity.InputSystem.
# NuGet 복원은 이 피드에서 멈추는 일이 있어 curl 로 직접 받는다.
$ErrorActionPreference = 'Stop'
$cache = Join-Path $PSScriptRoot '.cache'
New-Item -ItemType Directory -Force $cache | Out-Null

$packages = @(
    @{ id = 'unityengine.modules'; version = '6000.3.26'; dir = 'um' },
    @{ id = 'unity.inputsystem';   version = '1.5.0';     dir = 'is' }
)

foreach ($p in $packages) {
    $target = Join-Path $cache $p.dir
    if (Test-Path (Join-Path $target 'lib\netstandard2.0')) { Write-Host "이미 있음: $($p.id) $($p.version)"; continue }
    $zip = Join-Path $cache "$($p.dir).zip"
    $url = "https://nuget.bepinex.dev/v3/package/$($p.id)/$($p.version)/$($p.id).$($p.version).nupkg"
    Write-Host "내려받는 중: $url"
    & curl.exe -sS -L --fail --max-time 300 -o $zip $url
    if ($LASTEXITCODE -ne 0) { throw "내려받지 못했습니다: $url" }
    Expand-Archive $zip -DestinationPath $target -Force
}
Write-Host '준비됨.'
