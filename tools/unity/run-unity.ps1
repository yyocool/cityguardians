<#
.SYNOPSIS
  Unity 에디터를 명령행(batchmode)으로 실행하고 끝날 때까지 기다린다.

.NOTES
  종료 코드: Unity의 종료 코드를 그대로 돌려준다. 3 = 같은 프로젝트를 이미 다른 Unity가 열고 있어 시작하지 않음.

.EXAMPLE
  .\tools\unity\run-unity.ps1 -LogName open -UnityArgs '-quit'
  .\tools\unity\run-unity.ps1 -LogName setup -UnityArgs '-quit','-executeMethod','CityGuardians.EditorTools.ProjectSetup.Run'
  .\tools\unity\run-unity.ps1 -LogName editmode -UnityArgs '-runTests','-testPlatform','EditMode','-testResults','Logs\editmode.xml'
#>
param(
    [Parameter(Mandatory = $true)][string]$LogName,
    [string[]]$UnityArgs = @(),
    [string]$UnityVersion = '6000.3.28f1',
    [switch]$Graphics
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repoRoot 'client'
$unity = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe"
if (-not (Test-Path $unity)) { throw "Unity $UnityVersion 을(를) 찾을 수 없습니다: $unity" }

# `powershell -File` 로 부르면 배열 인자가 "-quit,-executeMethod,X" 처럼 한 덩어리로 들어온다.
# 그대로 넘기면 Unity가 -quit 도 -executeMethod 도 알아보지 못하고 끝없이 대기하므로 여기서 나눈다.
$UnityArgs = @($UnityArgs | ForEach-Object { $_ -split ',' } | Where-Object { $_ -ne '' })

# 같은 프로젝트를 이미 열고 있는 Unity가 있으면 시작하지 않는다 (종료시키지도 않는다).
$projectPattern = [regex]::Escape($project)
$running = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" |
    Where-Object { $_.CommandLine -match $projectPattern -and $_.CommandLine -notmatch 'AssetImportWorker' }
if ($running) {
    $ids = ($running | ForEach-Object { "PID $($_.ProcessId) (시작 $($_.CreationDate))" }) -join ', '
    Write-Output "UNITY_ALREADY_RUNNING: 이 프로젝트를 이미 열고 있는 Unity가 있습니다: $ids"
    exit 3
}

$logDir = Join-Path $project 'Logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir "$LogName.log"
if (Test-Path $log) { Remove-Item $log -Force }

$argList = @('-batchmode', '-projectPath', "`"$project`"", '-logFile', "`"$log`"") + $UnityArgs
if (-not $Graphics) { $argList += '-nographics' }

Write-Output "UNITY_ARGS: $($UnityArgs -join ' | ')"
$started = Get-Date
$process = Start-Process -FilePath $unity -ArgumentList $argList -PassThru -Wait
$seconds = [int]((Get-Date) - $started).TotalSeconds

Write-Output "UNITY_EXIT_CODE=$($process.ExitCode) (${seconds}s) log=$log"
if (Test-Path $log) {
    $problems = Select-String -Path $log -Pattern 'error CS\d+|^Assets.*: error|Compilation failed|Scripts have compiler errors|Project has invalid dependencies|An error occurred while resolving packages|Exception:|Aborting batchmode' |
        Select-Object -First 40
    if ($problems) {
        Write-Output '--- 로그에서 찾은 문제 ---'
        $problems | ForEach-Object { $_.Line }
    }
}
exit $process.ExitCode
