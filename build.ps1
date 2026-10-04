# DualSenseBridge — сборка одного EXE (Windows 10/11 x64).
# Требуется .NET SDK 10+ (https://dotnet.microsoft.com/download) и доступ в интернет
# (NuGet-restore). Запуск: powershell -ExecutionPolicy Bypass -File build.ps1
#
# Что делает скрипт:
#   1. restore + build (Release) всего решения;
#   2. запускает юнит-тесты протокола (золотые векторы);
#   3. публикует ОДИН самодостаточный файл release\DualSenseBridge.exe
#      (self-contained, single-file, сжатие, самораспаковка нативных библиотек).
#
# Итоговый EXE не требует установленного .NET и содержит всё, кроме драйверов:
# usbip-win2 (vadimgrn/usbip-win2), VIIPER (hbashton) и HidHide ставятся
# при первом запуске командой: DualSenseBridge.exe --install-deps

param(
    [switch]$SkipTests,   # пропустить тесты
    [switch]$FrameworkDependent  # маленький EXE (~5 МБ), но требует .NET Desktop Runtime
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$outDir = Join-Path $root 'release'

Write-Host '==> Сборка DualSenseBridge (Release)...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'DualSenseBridge.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }

if (-not $SkipTests) {
    Write-Host '==> Юнит-тесты протокола...' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests\DualSenseBridge.Tests\DualSenseBridge.Tests.csproj') -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли.' }
}

Write-Host '==> Публикация single-file EXE...' -ForegroundColor Cyan
$publishArgs = @(
    'publish', (Join-Path $root 'src\DualSenseBridge\DualSenseBridge.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $outDir,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true'
)
if ($FrameworkDependent) {
    # Компактный вариант: требует .NET 10 Desktop Runtime на машине пользователя.
    $publishArgs += @('--self-contained', 'false', '-p:EnableCompressionInSingleFile=true')
} else {
    # Полностью автономный EXE (~52 МБ): ничего дополнительно ставить не нужно.
    $publishArgs += @('--self-contained', 'true', '-p:EnableCompressionInSingleFile=true')
}

dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Публикация не удалась.' }

Get-ChildItem $outDir -Filter *.pdb | Remove-Item -ErrorAction SilentlyContinue
$exe = Join-Path $outDir 'DualSenseBridge.exe'
Write-Host ''
Write-Host 'Готово:' -ForegroundColor Green
Write-Host ("  " + $exe + "  (" + [math]::Round((Get-Item $exe).Length / 1MB, 1) + " МБ)")
Write-Host ''
Write-Host 'Дальнейшие шаги на целевой машине:'
Write-Host '  1) DualSenseBridge.exe --check-deps    — проверить usbip-win2/VIIPER/HidHide'
Write-Host '  2) DualSenseBridge.exe --install-deps — установить (запросит UAC, возможен ребут для usbip)'
Write-Host '  3) Подключить DualSense по Bluetooth и запустить DualSenseBridge.exe'
