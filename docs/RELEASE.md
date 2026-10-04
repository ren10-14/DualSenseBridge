# ЭТАП 5 — Финальный EXE

## Результат

`release/DualSenseBridge.exe` — **один самодостаточный файл** (self-contained, single-file,
сжатие, самораспаковка нативных библиотек), Windows 10/11 x64. Установленный .NET не требуется.

- Размер: ~52 МБ (self-contained) или ~5 МБ с `build.ps1 -FrameworkDependent`
  (тогда нужна .NET 10 Desktop Runtime).
- Иконка и манифест (asInvoker, PerMonitorV2) встроены.
- Сборка кросс-платформенная: проверена сборка из Linux (`EnableWindowsTargeting=true`);
  на Windows — обычным `build.ps1`.

## Сборка

```powershell
# Требуется: .NET SDK 10+ (dotnet.microsoft.com), интернет (NuGet)
powershell -ExecutionPolicy Bypass -File build.ps1
```

Скрипт: build → 36 юнит-тестов → publish в `release\DualSenseBridge.exe`.

Эквивалент напрямую:

```
dotnet publish src/DualSenseBridge/DualSenseBridge.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -o release
```

## Установка на машине пользователя (первый запуск)

1. `DualSenseBridge.exe --check-deps` — статус usbip-win2 / viiper.exe / HidHide.
2. `DualSenseBridge.exe --install-deps` — установка (UAC):
   - **usbip-win2** — BSD-2, WHLK/attestation-подписан Microsoft; возможна перезагрузка;
   - **VIIPER (hbashton)** — GPL-3.0, отдельный процесс, устанавливается в пользовательский каталог;
   - **HidHide** — MIT.
   Все три — свободные лицензии, распространение разрешено.
3. Подключить DualSense по Bluetooth, запустить `DualSenseBridge.exe` —
   мост стартует автоматически (трей), физический контроллер скрывается через HidHide,
   игре доступен виртуальный DualSense.

## Что входит в EXE, а что нет

| В EXE | Отдельно (ставится `--install-deps`) |
|---|---|
| Ядро протокола, мост, трей/GUI, CLI, установка драйверов | usbip-win2 (драйвер, подписан MS) |
| Клиент VIIPER (TCP) | viiper.exe (GPL, отдельный процесс) |
| Интеграция HidHide | HidHide (драйвер-фильтр) |

EXE не содержит чужих бинарников — только наш код + BCL .NET + HidSharp (Apache-2.0),
что лицензионно чисто.

## Контроль качества перед релизом

- [x] `dotnet build` — 0 ошибок, 0 предупреждений.
- [x] 36/36 юнит-тестов (золотые векторы CRC/отчётов/триггеров/V5/трансляции).
- [x] Single-file publish win-x64 выполняется (проверено кросс-сборкой).
- [ ] Проверка на реальном железе (DualSense BT + игра): вибрация, триггеры (Steam Input:
      GTA V/ASTRO по Steam), подсветка, реконнекты — требуется Windows-машина с адаптером.
- [ ] Проверка `--install-deps` на чистой Windows 11.

Последние два пункта невозможно выполнить в среде разработки без контроллера —
перед распространением прогнать по чек-листу в `docs/IMPLEMENTATION.md` (раздел «Диагностика»).
