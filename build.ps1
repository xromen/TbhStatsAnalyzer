param(
    [switch]$SkipNative,
    [switch]$Test,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $SkipNative) {
    & .\native\build-native.cmd
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать нативный сборщик.' }
}
dotnet build TbhStatsAnalyzer.sln -c Release -m:1 --nologo "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать WPF-приложение.' }
if ($Test) {
    $taskChecks = Join-Path $PSScriptRoot 'tests\TbhStatsAnalyzer.Checks\bin\Release\net8.0-windows\TbhStatsAnalyzer.Checks.exe'
    & $taskChecks
    if ($LASTEXITCODE -ne 0) { throw 'Проверки не прошли.' }
    foreach ($taskMode in '--ui-deaths', '--ui-filters') {
        & $taskChecks $taskMode
        if ($LASTEXITCODE -ne 0) { throw "Проверка $taskMode не прошла." }
    }
}
$taskOutput = Join-Path $PSScriptRoot 'artifacts\TbhStatsAnalyzer'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
Copy-Item -Path 'src\TbhStatsAnalyzer\bin\Release\net8.0-windows\*' -Exclude 'TbhObserver.dll','observer' -Destination $taskOutput -Recurse -Force
Copy-Item -LiteralPath 'native\vendor\minhook\LICENSE.txt' -Destination (Join-Path $taskOutput 'MinHook-LICENSE.txt') -Force
Copy-Item -LiteralPath 'README.md' -Destination $taskOutput -Force
$taskScreenshots = Join-Path $taskOutput 'docs\screenshots'
New-Item -ItemType Directory -Path $taskScreenshots -Force | Out-Null
Copy-Item -LiteralPath 'docs\screenshots\main-window.png' -Destination $taskScreenshots -Force
Write-Host "Готово: $taskOutput\TbhStatsAnalyzer.exe"
