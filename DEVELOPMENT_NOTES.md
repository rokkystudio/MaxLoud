# MaxLoud — инженерные заметки и правила безопасной разработки

Этот файл нужно читать **до изменения аудиотракта, APO-регистрации, CompositeFX, `Enable audio enhancements`, PnP-состояния Realtek или tray/UI-интеграции**.

Он хранит не пользовательское описание, а накопленные ограничения Windows Audio, найденные причины прошлых поломок и безопасные способы диагностики. Цель — чтобы новая ветка разработки или новый чат не повторяли уже пройденные ошибки.

---

## 1. Базовые принципы

1. Не менять audio graph вслепую. Сначала снять состояние endpoint, `FxProperties`, служб, `audiodg.exe` и логи.
2. Наличие CLSID в EFX не доказывает, что APO реально обрабатывает звук. Нужен lifecycle `CreateInstance -> Initialize -> LockForProcess -> APOProcess`.
3. Standalone smoke test не заменяет проверку в реальном `audiodg.exe`.
4. Не удалять и не заменять vendor APO ради теста MaxLoud. MaxLoud добавляется рядом с Realtek/vendor EFX.
5. При аварии сначала вернуть безопасный baseline, потом продолжать разработку.
6. В `APOProcess` запрещены файлы, Registry, COM, heap allocation, блокирующие mutex и тяжёлая математика.
7. Для системного tray-меню не использовать WPF `ContextMenu`: глобальные WPF стили уже приводили к тёмному тексту на тёмном фоне. Текущий правильный подход — WinForms `ContextMenuStrip` с отдельным renderer, как в Network Diagram.
8. Общие UI-паттерны (endpoint selector, темы, языки, иконки) брать из `D:\PROJECTS\Network Diagram`, а не делать альтернативные версии.

---

## 2. Архитектура

Обычный shared-mode путь:

```text
Applications
  -> Windows Audio Engine / mixer
  -> SFX / MFX / EFX
  -> hardware driver
  -> speakers
```

Компоненты:

- `MaxLoud.exe` — C# WPF UI, tray, endpoint control, настройки и dev-интеграция APO;
- `MaxLoudApo.dll` — native x64 C++ Endpoint Effect APO;
- `%PROGRAMDATA%\MaxLoud\state.bin` — runtime DSP state;
- `%LOCALAPPDATA%\MaxLoud\settings.json` — пользовательские настройки.

Не возвращаться к архитектуре `WasapiCapture -> processing -> WasapiOut`, NAudio loopback или виртуальному кабелю без отдельного решения о смене всей архитектуры.

---

## 3. Текущий Realtek endpoint

Рабочий render endpoint:

```text
Name: Динамики (Realtek(R) Audio)
MMDevice ID: {0.0.0.00000000}.{504972e6-2e54-4b60-8589-ca21ef5a469f}
Endpoint GUID: {504972E6-2E54-4B60-8589-CA21EF5A469F}
Hardware: HDAUDIO\FUNC_01&VEN_10EC&DEV_0887&SUBSYS_1458A182&REV_1003\5&5E7EDB7&0&0001
```

Realtek CompositeFX:

```text
SFX property 13: {DA2C9ECE-7418-4906-B4FA-0A00B3EB88AA}
MFX property 14: {A296D363-EE83-4AF9-9BE7-729C1296150A}
EFX property 15: {A29EB043-6CE2-4EE2-B38C-F58719E0D88F}
```

Все три Realtek CLSID указывают на `RltkAPOU64.dll` из DriverStore.

MaxLoud APO CLSID:

```text
{7CB491F3-E5C2-4F34-AB14-08AD933EC77D}
```

Effect GUID:

```text
{4CA86CD8-33B7-4473-A107-2EB2523AE9D6}
```

---

## 4. CompositeFX и безопасное изменение цепочки

Windows 10 1803+ CompositeFX позволяет `REG_MULTI_SZ` несколько APO CLSID на одной стадии.

Ключевые свойства:

```text
13 = SFX
14 = MFX
15 = EFX
```

MaxLoud должен добавляться к EFX, сохраняя Realtek и порядок существующих узлов.

Для записи `FxProperties` использовать минимальные Registry rights (`QueryValues`, `SetValue`). Не открывать ключ с избыточными правами.

Не придумывать универсальный per-node bypass для чужих APO: Windows не предоставляет общего свойства, подходящего для любого vendor APO.

---

## 5. Enable audio enhancements

Системный master switch соответствует:

```text
PKEY_AudioEndpoint_Disable_SysFx
{1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E},5

0 = effects enabled
1 = effects disabled
```

### Важная ошибка, которую нельзя повторять

Обычный `IMMDevice::OpenPropertyStore` на текущем Windows 10 читает normal endpoint store и для этого effects-store свойства может вернуть `VT_EMPTY`. Из-за этого UI раньше всегда показывал `Enable audio enhancements = ON`.

Правильный текущий backend — `IPolicyConfig::Get/SetPropertyValue(..., bFxStore=TRUE, PKEY_AudioEndpoint_Disable_SysFx)`.

Не использовать raw Registry как канонический источник состояния переключателя: Windows может переписать значение при перестроении graph.

Обычный click по checkbox не должен без необходимости делать `Restart AudioSrv`. Сначала применять property через PolicyConfig и позволять Windows обработать изменение.

---

## 6. APO COM aggregation

Критическая найденная ошибка:

```cpp
DECLARE_NOT_AGGREGATABLE(CMaxLoudApo)
```

ломала создание объекта в реальном Windows Audio Engine. `audiodg.exe` создаёт APO aggregated и запрашивает `IUnknown`; результат был `CLASS_E_NOAGGREGATION (0x80040110)`.

Правильно:

```cpp
DECLARE_AGGREGATABLE(CMaxLoudApo)
```

При подозрении на COM-проблему смотреть loader log и проверять реальный `IClassFactory::CreateInstance`.

---

## 7. Initialize и real audiodg lifecycle

Успешная интеграция должна подтверждаться строками примерно такого вида:

```text
CMaxLoudApo constructed
Initialize BEGIN
Initialize payload=APOInitSystemEffects2
Initialize END hr=0x00000000
IsOutputFormatSupported ... accepted
LockForProcess BEGIN
LockForProcess negotiated ...
LockForProcess END success
Runtime state applied
APOProcess stats ... valid=...
```

`Initialize` должен корректно принимать `APOInitSystemEffects2`.

Не считать `DllGetClassObject` или наличие DLL в `audiodg.exe` достаточным доказательством работы DSP.

---

## 8. APO registration flags и interface

Primary interface — реальный `IAudioProcessingObject`:

```text
{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}
```

Не подменять его случайным GUID.

Текущая регистрация рассчитана на endpoint effect и in-place processing. При изменении flags обязательно проверять реальный `audiodg.exe`.

---

## 9. Realtime DSP и AERT

`APOProcess` должен оставаться RT-safe.

Нельзя:

- читать файлы;
- читать Registry;
- вызывать COM;
- логировать на диск;
- выделять heap memory;
- выполнять блокирующие операции.

Runtime config готовится вне realtime callback и публикуется через заранее выделенные banks.

### Найденный лимит конфигурации

После добавления Volume Leveling две lookup-table по `4097 float` каждая раздули RT-конфигурацию так, что `LockForProcess` перестал завершаться успешно. Windows многократно создавал/уничтожал APO, а звук исчезал.

После уменьшения таблиц до `2049` элементов `LockForProcess END success` и `APOProcess` восстановились.

Не увеличивать размер RT-config без проверки AERT allocation HRESULT и реального `LockForProcess`.

---

## 10. Runtime state

Файл:

```text
%PROGRAMDATA%\MaxLoud\state.bin
```

Используется MMF/фиксированный binary layout с odd/even sequence protocol.

Флаги:

```text
bit0 MaxLoud master
bit1 Compressor
bit2 EQ
bit3 Limiter
```

APO process работает под `audiodg.exe`/служебной учётной записью, поэтому runtime state и каталоги должны иметь достаточные права чтения для audio service context.

---

## 11. Versioned APO DLL

Не пытаться перезаписывать загруженную `MaxLoudApo.dll` на месте.

Текущий механизм копирует DLL под versioned/hash именем, например:

```text
C:\Program Files\MaxLoud\MaxLoudApo.<HASH>.dll
```

COM registration переводится на новый файл.

Это обход блокировки DLL, уже загруженной `audiodg.exe`.

CLI:

```text
MaxLoud.exe --update-apo
```

---

## 12. Protected AudioDG

Development APO пока unsigned. Для dev-интеграции может использоваться:

```text
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio\DisableProtectedAudioDG=1
```

Обязательно сохранять исходное состояние и восстанавливать его при recovery/remove. Не оставлять системную настройку изменённой без необходимости.

Production должен использовать подписанный driver/APO package.

---

## 13. Аварийное восстановление

CLI:

```text
MaxLoud.exe --recover-audio
```

Recovery должен:

1. работать с точно выбранным endpoint;
2. удалить только MaxLoud CLSID из EFX;
3. не трогать Realtek/vendor CLSID;
4. выключить MaxLoud DSP state;
5. восстановить protected-audio setting;
6. при необходимости пересоздать Windows Audio graph;
7. записать диагностический snapshot до/после.

---

## 14. Realtek PnP: опасная операция

**Не использовать `pnputil /restart-device` для текущего `Realtek(R) Audio`.**

После такого вызова драйвер вернул `3010` (reboot required), а после перезагрузки codec остался присутствующим, но отключённым:

```text
ProblemCode = 22
CM_PROB_DISABLED
```

Из-за этого `Динамики (Realtek(R) Audio)` полностью исчезли из render endpoints.

Правильное восстановление в том конкретном случае:

```text
pnputil /enable-device <exact Realtek devnode>
pnputil /scan-devices
```

После этого:

```text
ProblemCode = 0
Status = OK
```

Не перезапускать parent HDA bus без крайней необходимости.

---

## 15. Диагностика «нет звука»

Проверять слоями, а не менять всё сразу:

```text
PnP
-> MMDevice enumeration
-> default endpoint
-> master volume / mute
-> system effects state
-> CompositeFX chain
-> COM activation
-> Initialize
-> format negotiation
-> LockForProcess
-> runtime state
-> APOProcess
-> endpoint peak meter
-> physical speakers/headphones
```

Если endpoint peak meter показывает ненулевой сигнал, а физически тишина, проблема может быть ниже Windows mixer (driver/hardware/внешняя громкость). Был реальный случай, когда колонки были просто убавлены вручную — не делать вывод о поломке APO только по отсутствию слышимого звука.

---

## 16. Volume Leveling — целевая семантика

Пользовательская задача необычно агрессивная: при высокой Strength изменение player volume в широком диапазоне должно мало менять субъективную громкость.

Желаемый ориентир:

```text
player 100% -> громко
player 75%  -> почти тот же output
player 50%  -> почти тот же output
player 25%  -> всё ещё близко
ниже ~25%   -> падение становится заметнее
```

### Что уже не сработало

Peak-envelope вариант:

- слишком поздно снимал boost при росте player volume;
- давал громкий overshoot;
- быстро поднимал тихие хвосты;
- плохо компенсировал 50% player volume.

Двусторонняя RMS-нормализация тоже была неправильной для этой задачи: на 100% player volume Leveling мог делать сигнал тише bypass.

### Текущий принцип

Leveling — RMS-based **upward-only AGC**:

- `gain >= 1.0` всегда;
- никогда не делать источник тише bypass;
- тихий материал усиливать;
- пики оставлять limiter;
- при росте входного уровня очень быстро снимать boost;
- после громкого участка медленно возвращать boost;
- detector держать прогретым даже при Strength=0.

Текущие ориентиры:

```text
fast boost removal ~ 0.25 ms
RMS/envelope release ~ 1200 ms
boost recovery ~ 1800 ms
full-strength max boost ~ +40 dB
```

При limiter ceiling около `-1 dBFS` target RMS после последнего тюнинга расположен примерно около `-7 dBFS`.

Не лечить Leveling параметрами compressor Attack/Release — это отдельный DSP-блок.

Тестировать на одном непрерывном треке:

```text
100 -> 75 -> 50 -> 25 -> 10%
```

Слушать overshoot, pumping, хвосты, noise floor и степень компенсации.

---

## 17. Compressor / EQ / Limiter

Цепочка:

```text
Volume Leveling -> Input Gain -> EQ -> Compressor -> Limiter
```

Compressor linked между каналами, чтобы не менять stereo balance.

`Ratio=1.0` означает фактически отсутствие gain reduction компрессором. Для слышимого теста использовать, например, 4:1.

Limiter — последний защитный stage.

---

## 18. UI и Network Diagram parity

Источник эталонных UI-паттернов:

```text
D:\PROJECTS\Network Diagram
```

### Audio endpoint selector

Текущий selector повторяет Network Diagram:

- это не стандартный ComboBox;
- button + dropdown ContextMenu;
- стрелка справа;
- иконка слева;
- выбранный пункт отмечен галочкой.

### Theme

Поддерживаются:

```text
Light
Dark
```

Базовая авторская палитра больше не копируется между проектами. Канонический source-модуль:

```text
D:\PROJECTS\Shared\NeoUI\NeoThemePalette.cs
```

MaxLoud, Network Diagram и HIDEME подключают этот файл как linked source и адаптируют его к своему WPF/WinForms UI. `light.png`/`dark.png` и остальные проектные assets остаются локальными.

### Language

Поддерживаются:

```text
auto
en
ru
```

`auto` использует `CurrentUICulture` и выбирает Русский для `ru`, иначе English.

Иконки-флаги также взяты из Network Diagram и используются на кнопке языка в title bar.

В выпадающем language menu использовать **простые строковые Header** (`System / English / Русский`), а не `StackPanel` с флагом внутри Header. Сложный WPF Header конфликтовал с общим `MenuItem` template и приводил к пустым пунктам меню.

Настройки хранятся в `settings.json`:

```json
"Theme": "Light",
"Language": "auto"
```

### Tray context menu

После нескольких неудачных попыток WPF `ContextMenu` признан неправильным вариантом для tray. Причина: глобальный `TextBlock` style/foreground протекал внутрь header presenter, из-за чего в light theme получался тёмный текст на фиксированном тёмном фоне.

Текущий правильный механизм:

```text
System.Windows.Forms.ContextMenuStrip
+ D:\PROJECTS\Shared\NeoUI\NeoTrayMenuStyle.cs
+ фиксированные dark palette colors
```

MaxLoud и Network Diagram используют один и тот же linked-source renderer/layout. Не возвращать WPF tray menu и не создавать локальную копию `NeoTrayMenuStyle`.

Для popup, открываемого вручную из native `Shell_NotifyIcon` callback, перед `Show()` нужен foreground-owner contract (`SetForegroundWindow`), иначе меню может не закрываться при клике по рабочему столу/другому окну. Общий `NeoTrayMenuStyle.Show()` делает это и после закрытия отправляет benign `WM_NULL` owner-окну.

Важно: если `ToolStripMenuItem.Text` заполняется уже после создания меню, нельзя оставлять размеры, рассчитанные при пустом тексте. При каждом открытии tray menu нужно измерять актуальные локализованные строки через `TextRenderer.MeasureText`, задавать общую ширину пунктов и только затем вызывать `Show()`. Иначе `AutoSize=false` оставляет пункты шириной порядка 40 px, renderer получает нулевую область текста и меню выглядит пустым.

---

## 19. Single instance

Нормальный запуск использует named semaphore/event:

```text
Local\RokkyStudio.MaxLoud.SingleInstance
Local\RokkyStudio.MaxLoud.Activate
```

Второй запуск:

- не создаёт второй tray instance;
- сигналит primary process;
- primary показывает и активирует окно;
- secondary завершает работу с кодом 0.

Maintenance modes `--update-apo` и `--recover-audio` обходят interactive single-instance lock намеренно.

---

## 20. Tray icon

Native tray icon использует `Shell_NotifyIconW`.

MaxLoud принимает зарегистрированное сообщение `TaskbarCreated` на HWND главного WPF-окна и после перезапуска Explorer повторно выполняет `NIM_ADD` для последнего `HICON` и tooltip. Так как MaxLoud elevated, сообщение от обычного Explorer явно разрешается через `ChangeWindowMessageFilterEx(..., MSGFLT_ALLOW, ...)`.

После каждого успешного `NIM_ADD` устанавливается `NOTIFYICON_VERSION_4` через `NIM_SETVERSION`. При этой версии native mouse notification берётся из младшего слова `lParam`; нельзя снова сравнивать весь `lParam` с `WM_LBUTTONUP/WM_RBUTTONUP`, иначе tray clicks перестанут распознаваться.

Если немедленный `NIM_ADD` после `TaskbarCreated` не удался, сервис оставляет icon как не зарегистрированную, а следующий обычный status tick повторяет `SetIcon` и регистрацию.

Ранее была ошибка P/Invoke с неправильным именем `ShellNotifyIcon`; не возвращать её.

Левый click — DSP master toggle с задержкой для различения double click.

Native double-click sequence для tray приходит примерно как `WM_LBUTTONUP -> WM_LBUTTONDBLCLK -> WM_LBUTTONUP`. После `WM_LBUTTONDBLCLK` обязательно подавлять следующий `WM_LBUTTONUP`, иначе он заново запускает single-click timer и после открытия окна переключает DSP.

Double click — открыть настройки и **не** менять DSP master.

Right click — открыть WinForms ContextMenuStrip.

Tray menu и tooltip значка полностью локализуются через `LocalizationService`: `English` / `Русский`, а `auto` выбирает один из них по `CurrentUICulture`. Не оставлять hardcoded `ON/OFF`, русские status-строки или подписи пунктов непосредственно в `UpdateTrayState()`.

### Автозапуск

Пользовательская настройка `RunOnStartup` хранится в `%LOCALAPPDATA%\MaxLoud\settings.json` и управляется одной галочкой в основном окне.

Не заменять её на `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, пока manifest MaxLoud требует `requireAdministrator`: Explorer не является корректным механизмом тихого elevated-autostart. Текущий механизм — задача Windows Task Scheduler `RokkyStudio.MaxLoud` с trigger `ONLOGON`, `RunLevel=Highest` и interactive-only запуском (`/IT`), чтобы elevated MaxLoud жил в пользовательской desktop/session и имел видимую tray icon. При каждом запуске с включённой настройкой задача обновляется, чтобы путь к текущему `MaxLoud.exe` оставался актуальным.

У `ContextMenuStrip` с `Padding=4` нельзя задавать `ToolStripMenuItem.Width = menuWidth - Padding.Horizontal`: WinForms прижимает item к левому краю и весь вычтенный padding визуально остаётся справа. Также нельзя пытаться дорисовать hover за `e.Item.Bounds` — renderer обрезается clipping region. Правильная схема: сохранить стабильную высоту/layout и задавать каждому `ToolStripMenuItem` полную ширину `menuWidth`. По фактической пиксельной проверке текущего renderer визуально одинаковый ~2 px зазор получается при `horizontalInset=3` и `verticalInset=1` из-за дополнительного горизонтального clipping WinForms.

Для tray menu не менять вручную внутреннюю высоту/outer padding без пересчёта ToolStrip layout: попытка убрать `ContextMenuStrip.Padding` и задать `menu.Size` по простой сумме высот вызвала встроенные scroll arrows и скрыла пункты. Рабочая схема: `Padding=4`, известные item heights, полная рассчитанная высота меню; визуальный inset hover делать только внутри renderer, не через изменение layout.

---

## 21. Логи

App log:

```text
%LOCALAPPDATA%\MaxLoud\logs\MaxLoud-YYYYMMDD.log
```

Native APO log:

```text
%PROGRAMDATA%\MaxLoud\logs\MaxLoudApo-YYYYMMDD.log
```

COM loader log:

```text
%PROGRAMDATA%\MaxLoud\logs\MaxLoudApo-loader.log
```

Loader log нужен для `DllGetClassObject`, `IClassFactory::CreateInstance` и COM activation diagnostics.

Не писать в эти файлы из realtime `APOProcess`.

---

## 22. Сборка

C# frontend:

```text
Debug|x64
Debug|x86
Release|x64
Release|x86
```

`MaxLoudApo.dll` собирается отдельно для x64 и x86; solution использует APO той же архитектуры, что и C# frontend.

Среда текущей машины:

```text
Visual Studio 2019 Community 16.11.x
MSVC v142
Windows SDK 10.0.19041.0
.NET SDK 5.0.416
```

MSBuild:

```text
C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe
```

Release publish C# frontend:

```text
Build\publish\x64\MaxLoud.exe
Build\publish\x86\MaxLoud.exe
```

Нативный APO:

```text
Build\bin\x64\Debug\apo\MaxLoudApo.dll
Build\bin\x86\Debug\apo\MaxLoudApo.dll
Build\bin\x64\Release\apo\MaxLoudApo.dll
Build\bin\x86\Release\apo\MaxLoudApo.dll
```

Если MSBuild stdout уже говорит `Build succeeded`, но managed agent job остаётся `running=true`, это может быть зависший MSBuild/node reuse. Для сборок использовать `/nodeReuse:false`, а зависший job после подтверждённого успеха останавливать.

---

## 23. Тестовые утилиты

`tests\EndpointToggleProbe` — независимый процесс для проверки system enhancements backend.

Важно: старые результаты этого probe до перехода на PolicyConfig могут относиться к прямому Registry backend и не воспроизводить поведение Sound Control Panel.

Standalone APO smoke tests полезны, но не заменяют реальный `audiodg.exe`.

---

## 24. Realtek Audio Control API

В установленном UWP-пакете Realtek найдены WinRT классы:

```text
RtkAudioCore.RApoInformation
RtkAudioCore.RApoInformationPublicUse
RtkAudioCore.AudioDevice.ApoEffect.RApoEffectBase
RtkAudioCore.AudioDevice.ApoEffect.RRtkRenderApoEffectBase
RtkAudioCore.AudioDevice.ApoEffect.RRtkRenderApoRtkEqEffect
```

Подтверждены методы/свойства типа:

```text
GetSysFxDisabled()
SetSysFxDisabled(bool)
SysFxDisabled
EqPreset
EqGain0...
```

Это потенциальный vendor-specific путь управления Realtek effects без структурного detach CLSID, но не использовать его до полного понимания WinRT activation/context requirements. Не менять private vendor state спекулятивно.

---

## 25. Что не делать в новой ветке

Не делать следующее без новых доказательств/отдельного решения:

- не возвращать NAudio/virtual cable capture-render architecture;
- не использовать raw Registry как канонический backend `Enable audio enhancements`;
- не использовать обычный `IMMDevice::OpenPropertyStore` для effects-store switch;
- не ставить `DECLARE_NOT_AGGREGATABLE` на APO;
- не считать load DLL доказательством processing;
- не увеличивать RT lookup tables без проверки AERT allocations;
- не писать на диск из `APOProcess`;
- не удалять Realtek/vendor APO ради теста;
- не использовать `pnputil /restart-device` для текущего Realtek codec;
- не путать MaxLoud DSP master с Windows system-effects master;
- не возвращать WPF tray context menu;
- не делать новый endpoint selector/theme/language UI в обход Network Diagram patterns;
- не утверждать, что физический звук отсутствует из-за APO, пока не проверены endpoint meter и внешняя громкость.

---

## 26. Рекомендуемый порядок работы после открытия новой ветки

1. Прочитать этот файл.
2. Прочитать текущий `README.md`.
3. Перед audio graph изменением снять endpoint snapshot и логи.
4. После C++ APO изменения собрать Debug/Release.
5. Обновить APO через versioned install.
6. Проверить реальный `audiodg.exe`: `LockForProcess END success` и растущий `APOProcess valid`.
7. При UI-only изменениях не перестраивать audio graph без причины.
8. После изменения tray проверять именно живой `D:\PROJECTS\MaxLoud\Build\bin\x64\Release\MaxLoud.exe`, а не старый экземпляр.
9. Обновлять этот файл, если найден новый системный нюанс или реально подтверждённая ошибка.
