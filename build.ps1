# Сборка VladTools и установка в папку надстроек Revit.
# Использование:  .\build.ps1            (Release + установка)
#                 .\build.ps1 -Configuration Debug
#                 .\build.ps1 -NoDeploy   (только собрать)

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\VladTools\VladTools.csproj'

if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    Write-Warning 'Revit запущен — он держит VladTools.dll, копирование в папку надстроек не пройдёт. Закройте Revit.'
}

$deploy = if ($NoDeploy) { 'false' } else { 'true' }
dotnet build $project -c $Configuration -p:DeployToRevit=$deploy

if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась (код $LASTEXITCODE)." }

if (-not $NoDeploy) {
    Write-Host "Готово. Перезапустите Revit 2022 — вкладка «Vlad Tools» появится на ленте." -ForegroundColor Green
}
