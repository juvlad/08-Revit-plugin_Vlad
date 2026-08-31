# Сборка VladTools и установка в папки надстроек Revit.
# Использование:  .\build.ps1                        (Release + установка, все годы)
#                 .\build.ps1 -Configuration Debug
#                 .\build.ps1 -NoDeploy              (только собрать)
#                 .\build.ps1 -RevitVersion 2025     (один год)

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('2022', '2024', '2025')]
    [string[]]$RevitVersion = @('2022', '2024', '2025'),
    [switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\VladTools\VladTools.csproj'

if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    Write-Warning 'Revit запущен — он держит VladTools.dll, копирование в папку надстроек не пройдёт. Закройте Revit.'
}

$deploy = if ($NoDeploy) { 'false' } else { 'true' }
$built = @()

foreach ($year in $RevitVersion) {
    # Без установленного Revit нет RevitAPI.dll по HintPath — год пропускаем,
    # а не роняем всю сборку: на машине может стоять не каждая версия.
    if (-not (Test-Path "C:\Program Files\Autodesk\Revit $year")) {
        Write-Warning "Revit $year не установлен — год пропущен."
        continue
    }

    Write-Host "── Revit $year ──" -ForegroundColor Cyan
    dotnet build $project -c $Configuration -p:RevitVersion=$year -p:DeployToRevit=$deploy
    if ($LASTEXITCODE -ne 0) { throw "Сборка под Revit $year не удалась (код $LASTEXITCODE)." }
    $built += $year
}

if ($built.Count -eq 0) { throw 'Ни один год не собран: установленных версий Revit не найдено.' }

if (-not $NoDeploy) {
    Write-Host "Готово: Revit $($built -join ', '). Перезапустите Revit — вкладка «Vlad Tools» появится на ленте." -ForegroundColor Green
}
