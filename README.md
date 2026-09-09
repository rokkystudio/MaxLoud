# MaxLoud

MaxLoud — системный регулятор громкости и динамики для Windows 10. Он предназначен прежде всего для фильмов, видео, музыки и других источников, у которых сильно отличается субъективная громкость.

Вместо виртуального аудиоустройства MaxLoud подключается к штатной Windows Audio Engine как Endpoint Effect APO и обрабатывает звук непосредственно в системной цепочке эффектов выбранного устройства вывода.

## Возможности

- автоматическое выравнивание громкости `Volume Leveling`;
- предварительное усиление `Input Gain`;
- динамический компрессор;
- 5-полосный эквалайзер;
- peak limiter;
- работа с системным `Enable audio enhancements`;
- просмотр SFX / MFX / EFX цепочки выбранного Windows-устройства;
- подключение и отключение собственного MaxLoud EFX;
- системный tray с быстрым включением и выключением DSP и восстановлением значка после перезапуска Explorer;
- запуск вместе с Windows через elevated Scheduled Task;
- selector Audio endpoint в стиле Network Diagram с выпадающим WPF-меню;
- светлая и тёмная тема с мгновенным переключением;
- языки интерфейса `English`, `Русский` и `Системный` с автоопределением;
- live-применение настроек без кнопки Apply;
- диагностические логи и сбор отчёта о состоянии аудиосистемы.

## Как проходит звук

Для обычного shared-mode аудио схема выглядит так:

```text
Приложения
  -> Windows Audio Engine / mixer
  -> SFX / MFX / EFX
  -> драйвер устройства
  -> динамики / наушники
```

MaxLoud состоит из двух частей:

- `MaxLoud.exe` — C# WPF-приложение, tray, настройки и управление интеграцией с Windows;
- `MaxLoudApo.dll` — нативный x64 C++ Audio Processing Object, который выполняет DSP внутри Windows Audio Engine.

MaxLoud не использует VB-CABLE, NAudio loopback, WasapiCapture/WasapiOut или повторный вывод уже захваченного системного звука.

## Volume Leveling

`Volume Leveling` автоматически компенсирует различия средней громкости.

Это RMS-based upward AGC: тихий сигнал получает дополнительное усиление, но сам `Volume Leveling` никогда не делает исходный сигнал тише bypass. При росте входного уровня автоматический boost снимается быстро и возвращается значительно медленнее, чтобы уменьшить громкие выбросы и pumping на хвостах звуков.

Параметр `Strength` задаёт глубину выравнивания:

- `0%` — Leveling не изменяет громкость;
- средние значения — умеренно сближают тихие и громкие источники;
- `100%` — максимально агрессивное выравнивание с большим доступным диапазоном автоматического gain.

Limiter остаётся последним этапом цепочки и ограничивает пики после остальных DSP-блоков.

## Compressor + Input Gain

Компрессор уменьшает динамический диапазон: громкие участки становятся ближе к тихим.

Доступны:

- `Input Gain` — предварительное усиление;
- `Threshold` — уровень начала компрессии;
- `Ratio` — степень сжатия;
- `Attack` — скорость реакции на рост уровня;
- `Release` — скорость возврата gain reduction.

Компрессор связан между каналами, поэтому одинаковое gain reduction применяется ко всему stereo frame и не сдвигает стереобаланс.

## Equalizer

Пять peaking-полос:

- `80 Hz` — низ и гул;
- `250 Hz` — бубнение и мутность;
- `1 kHz` — середина;
- `3 kHz` — присутствие и разборчивость речи;
- `8 kHz` — яркость и резкость.

Каждая полоса регулируется в диапазоне `-12 ... +12 dB`.

## Limiter

Limiter ограничивает максимальный peak после Leveling, Input Gain, EQ и Compressor.

Он особенно полезен при сильном предварительном усилении и агрессивном выравнивании громкости.

## Окно настроек

В главном окне можно:

- выбрать Windows render endpoint через компактный dropdown-selector;
- переключить светлую/тёмную тему кнопкой в title bar;
- выбрать `English`, `Русский` или `Системный` язык интерфейса;
- включить или выключить запуск MaxLoud вместе с Windows;
- включить или выключить `Enable audio enhancements`;
- увидеть реальную SFX / MFX / EFX цепочку;
- подключить или отключить MaxLoud EFX;
- настроить весь DSP;
- открыть Windows Sound Settings, классическую Sound Control Panel и Volume Mixer;
- открыть диагностические логи;
- скопировать диагностический отчёт.

Все DSP-ползунки применяются сразу во время перетаскивания.

## Tray

MaxLoud работает из системного трея.

- один левый клик переключает только MaxLoud DSP master;
- двойной левый клик открывает окно настроек;
- правый клик открывает контекстное меню.

Повторный запуск `MaxLoud.exe` не создаёт второй экземпляр. Уже запущенное окно восстанавливается из скрытого/minimized состояния и выводится на передний план.

После перезапуска `explorer.exe` MaxLoud принимает системное сообщение `TaskbarCreated` и повторно регистрирует текущую tray icon без перезапуска приложения.

Галочка `Run with Windows` / `Запускать вместе с Windows` управляет задачей `RokkyStudio.MaxLoud` в Windows Task Scheduler. Задача запускается при входе пользователя с `RunLevel=Highest` в интерактивной пользовательской сессии, потому что development-версия MaxLoud требует административный токен и должна владеть tray icon; обычный `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` для такого процесса не используется.

## Enable audio enhancements

Этот переключатель соответствует системному Windows `Enable audio enhancements` для выбранного устройства.

Если он выключен, Windows обходит системную цепочку эффектов endpoint, поэтому не работают как MaxLoud, так и vendor APO, находящиеся в этой цепочке.

Переключение MaxLoud DSP master — отдельная операция: она не выключает Realtek, Dolby и другие сторонние APO.

## APO chain

MaxLoud показывает системные стадии:

- `SFX` — Stream Effects;
- `MFX` — Mode Effects;
- `EFX` — Endpoint Effects.

Собственный MaxLoud APO подключается как EFX и может работать рядом с уже установленным endpoint APO производителя.

## Настройки и runtime state

Пользовательские настройки:

```text
%LOCALAPPDATA%\MaxLoud\settings.json
```

В том же `settings.json` хранится `RunOnStartup`; при включённой настройке MaxLoud поддерживает актуальную elevated-задачу `RokkyStudio.MaxLoud` для входа текущего пользователя.

Текущая конфигурация DSP, которую читает нативный APO:

```text
%PROGRAMDATA%\MaxLoud\state.bin
```

## Логи

Лог приложения:

```text
%LOCALAPPDATA%\MaxLoud\logs\MaxLoud-YYYYMMDD.log
```

Лог нативного APO:

```text
%PROGRAMDATA%\MaxLoud\logs\MaxLoudApo-YYYYMMDD.log
```

В окне доступны кнопки `Open Logs` и `Copy Diagnostics`.

## Установка APO

Текущая версия проекта использует локальную development-установку APO.

При отсутствии или изменении `MaxLoudApo.dll` приложение может установить/обновить локальную интеграцию. DLL копируется в `C:\Program Files\MaxLoud`, регистрируется как Windows Audio Processing Object и добавляется в EFX выбранного endpoint.

Для development-сборки приложение запускается с правами администратора.

Production-версия должна использовать подписанный Windows audio driver/APO package вместо development-механизма.

## Аварийное восстановление

Для безопасного отключения MaxLoud от выбранного endpoint предусмотрен режим:

```text
MaxLoud.exe --recover-audio
```

Он удаляет только MaxLoud из EFX, не удаляя vendor APO устройства, выключает MaxLoud DSP и пересоздаёт Windows Audio graph.

## Сборка

Проект рассчитан на x64.

Конфигурации:

```text
Debug|x64
Release|x64
```

Используемая на текущей машине сборочная среда:

- Visual Studio 2019 / MSVC v142;
- Windows SDK 10.0.19041.0;
- .NET SDK 5.0.

Пример Release-сборки:

```powershell
cd D:\PROJECTS\MaxLoud
& 'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe' .\MaxLoud.sln /p:Configuration=Release /p:Platform=x64 /m /nodeReuse:false
```

Результаты:

```text
bin\Debug\MaxLoud.exe
bin\Debug\apo\MaxLoudApo.dll

bin\Release\MaxLoud.exe
bin\Release\apo\MaxLoudApo.dll
```

## Структура проекта

```text
MaxLoud\
├── MaxLoud.sln
├── MaxLoud.csproj
├── README.md
├── DEVELOPMENT_NOTES.md
├── src\
│   ├── App\
│   └── Apo\
├── tests\
├── driver\
├── res\
├── bin\
└── obj\
```

Инженерные ограничения, найденные ошибки, правила безопасной работы с Windows Audio и заметки для дальнейшей разработки собраны отдельно в `DEVELOPMENT_NOTES.md`.
