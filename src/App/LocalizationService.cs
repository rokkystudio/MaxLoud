using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace MaxLoud
{
    /// <summary>
    /// Provides immediate English/Russian UI localization with optional system-language detection.
    /// </summary>
    internal static class LocalizationService
    {
        public const string AutomaticLanguage = "auto";
        public const string EnglishLanguage = "en";
        public const string RussianLanguage = "ru";

        private static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            { "LocTrayDsp", "MaxLoud DSP" },
            { "LocTrayOpen", "Open window" },
            { "LocTrayExit", "Exit" },
            { "LocTrayStateDspOff", "DSP OFF" },
            { "LocTrayStateDeviceUnavailable", "device unavailable" },
            { "LocTrayStateApoNotInstalled", "APO not installed" },
            { "LocTrayStateEfxDetached", "EFX not attached" },
            { "LocTrayStateEnhancementsOff", "audio enhancements OFF" },
            { "LocTrayStateDspOn", "DSP ON" },
            { "LocTrayStateError", "error: {0}" },
            { "LocOn", "ON" },
            { "LocOff", "OFF" },
            { "LocHideToTray", "Hide to system tray" },
            { "LocSubtitle", "Windows Audio APO Controller" },
            { "LocIntro", "MaxLoud controls the system APO chain of the selected render endpoint. A single click on the tray icon toggles only MaxLoud DSP; Windows/vendor APO remain in their current state." },
            { "LocRunOnStartup", "Run with Windows" },
            { "LocEndpointSection", "Windows Audio endpoint" },
            { "LocOutputEndpoint", "Output endpoint" },
            { "LocRefresh", "Refresh" },
            { "LocWindowsSoundSettings", "Windows Sound Settings" },
            { "LocSoundControlPanel", "Sound Control Panel" },
            { "LocVolumeMixer", "Windows Volume Mixer" },
            { "LocOpenLogs", "Open Logs" },
            { "LocCopyDiagnostics", "Copy Diagnostics" },
            { "LocInstallApo", "Install / update MaxLoud APO" },
            { "LocDetachApo", "Detach MaxLoud EFX" },
            { "LocRemoveApo", "Remove MaxLoud APO" },
            { "LocRestartAudio", "Restart Windows Audio" },
            { "LocEnableEnhancements", "Enable audio enhancements" },
            { "LocApoChain", "APO chain" },
            { "LocApoChainDescription", "SFX is the stream stage before mixing; MFX is the mode stage; EFX is the endpoint stage after the mixer. Headers are groups only; checkboxes belong only to real APO nodes." },
            { "LocApoDetachHint", "Unchecking a CompositeFX node temporarily removes its CLSID from the corresponding list. MaxLoud remembers its position and restores the node to the same place when enabled again." },
            { "LocDspSection", "MaxLoud DSP" },
            { "LocSignalChain", "Signal chain: Volume Leveling → Input Gain → EQ → Compressor → Limiter. All DSP parameters are applied immediately; there is no Apply button." },
            { "LocDspMaster", "MaxLoud processing (master)" },
            { "LocVolumeLeveling", "Volume leveling" },
            { "LocVolumeLevelingDescription", "RMS upward leveling: boosts quiet material but never makes the source quieter than bypass. 0% does nothing; 50% compensates about 6 dB; 100% aggressively tries to keep roughly 25–100% player volume close in loudness. Reaction to rising level is fast, boost recovery is slow." },
            { "LocStrength", "Strength" },
            { "LocCompressorInputGain", "Compressor + Input Gain" },
            { "LocInputGain", "Input gain" },
            { "LocThreshold", "Threshold" },
            { "LocRatio", "Ratio" },
            { "LocAttack", "Attack" },
            { "LocRelease", "Release" },
            { "LocLimiter", "Limiter" },
            { "LocCeiling", "Ceiling" },
            { "LocEqualizer", "5-band EQ" },
            { "LocStatus", "Status" },
            { "LocTooltipLanguage", "Interface language" },
            { "LocTooltipTheme", "Switch light / dark theme" },
            { "LocSystemLanguage", "System (auto: {0})" },
            { "LocDefaultSuffix", "default" },
            { "LocDspBypassed", "DSP bypassed. Turn this master ON before changing Volume Leveling, Input Gain, EQ, Compressor or Limiter." },
            { "LocDspActive", "DSP is active. Changes are published immediately." },
            { "LocEnabling", "Enabling…" },
            { "LocDisabling", "Disabling…" }
        };

        private static readonly Dictionary<string, string> Russian = new Dictionary<string, string>
        {
            { "LocTrayDsp", "MaxLoud DSP" },
            { "LocTrayOpen", "Открыть окно" },
            { "LocTrayExit", "Выход" },
            { "LocTrayStateDspOff", "DSP ВЫКЛ" },
            { "LocTrayStateDeviceUnavailable", "устройство недоступно" },
            { "LocTrayStateApoNotInstalled", "APO не установлен" },
            { "LocTrayStateEfxDetached", "EFX не подключён" },
            { "LocTrayStateEnhancementsOff", "улучшения звука ВЫКЛ" },
            { "LocTrayStateDspOn", "DSP ВКЛ" },
            { "LocTrayStateError", "ошибка: {0}" },
            { "LocOn", "ВКЛ" },
            { "LocOff", "ВЫКЛ" },
            { "LocHideToTray", "Скрыть в системный трей" },
            { "LocSubtitle", "Контроллер Windows Audio APO" },
            { "LocIntro", "MaxLoud управляет системной APO-цепочкой выбранного устройства вывода. Одиночный клик по значку в системном трее переключает только DSP MaxLoud; APO Windows и производителя остаются в текущем состоянии." },
            { "LocRunOnStartup", "Запускать вместе с Windows" },
            { "LocEndpointSection", "Устройство вывода Windows Audio" },
            { "LocOutputEndpoint", "Устройство вывода" },
            { "LocRefresh", "Обновить" },
            { "LocWindowsSoundSettings", "Параметры звука Windows" },
            { "LocSoundControlPanel", "Панель управления звуком" },
            { "LocVolumeMixer", "Микшер громкости Windows" },
            { "LocOpenLogs", "Открыть логи" },
            { "LocCopyDiagnostics", "Копировать диагностику" },
            { "LocInstallApo", "Установить / обновить MaxLoud APO" },
            { "LocDetachApo", "Отключить MaxLoud EFX" },
            { "LocRemoveApo", "Удалить MaxLoud APO" },
            { "LocRestartAudio", "Перезапустить Windows Audio" },
            { "LocEnableEnhancements", "Включить улучшения звука" },
            { "LocApoChain", "Цепочка APO" },
            { "LocApoChainDescription", "SFX — потоковый этап до микширования; MFX — этап режима; EFX — endpoint-этап после микшера. Заголовки являются только группами, галочки есть исключительно у реальных APO-узлов." },
            { "LocApoDetachHint", "Снятие галочки CompositeFX временно удаляет его CLSID из соответствующего списка. MaxLoud сохраняет позицию и возвращает узел туда же при повторном включении." },
            { "LocDspSection", "MaxLoud DSP" },
            { "LocSignalChain", "Цепочка: Выравнивание громкости → Входное усиление → EQ → Компрессор → Лимитер. Все DSP-параметры применяются сразу, отдельной кнопки применения нет." },
            { "LocDspMaster", "Обработка MaxLoud (master)" },
            { "LocVolumeLeveling", "Выравнивание громкости" },
            { "LocVolumeLevelingDescription", "RMS-выравнивание вверх: усиливает тихий материал, но никогда не делает исходный сигнал тише режима без обработки. 0% — не вмешивается; 50% компенсирует около 6 dB; 100% агрессивно стремится удерживать примерно 25–100% громкости плеера близко по уровню. Реакция на рост громкости быстрая, возврат усиления медленный." },
            { "LocStrength", "Сила" },
            { "LocCompressorInputGain", "Компрессор + входное усиление" },
            { "LocInputGain", "Входное усиление" },
            { "LocThreshold", "Порог" },
            { "LocRatio", "Соотношение" },
            { "LocAttack", "Атака" },
            { "LocRelease", "Восстановление" },
            { "LocLimiter", "Лимитер" },
            { "LocCeiling", "Предел" },
            { "LocEqualizer", "5-полосный EQ" },
            { "LocStatus", "Состояние" },
            { "LocTooltipLanguage", "Язык интерфейса" },
            { "LocTooltipTheme", "Сменить светлую / тёмную тему" },
            { "LocSystemLanguage", "Системный (авто: {0})" },
            { "LocDefaultSuffix", "по умолчанию" },
            { "LocDspBypassed", "DSP выключен. Включите основную обработку перед настройкой выравнивания громкости, входного усиления, EQ, компрессора или лимитера." },
            { "LocDspActive", "DSP активен. Изменения применяются сразу." },
            { "LocEnabling", "Включение…" },
            { "LocDisabling", "Отключение…" }
        };

        public static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language) ||
                string.Equals(language, AutomaticLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return AutomaticLanguage;
            }

            return string.Equals(language, RussianLanguage, StringComparison.OrdinalIgnoreCase)
                ? RussianLanguage
                : EnglishLanguage;
        }

        public static string ResolveLanguage(string language)
        {
            var normalized = NormalizeLanguage(language);
            if (!string.Equals(normalized, AutomaticLanguage, StringComparison.Ordinal))
            {
                return normalized;
            }

            return string.Equals(
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                RussianLanguage,
                StringComparison.OrdinalIgnoreCase)
                ? RussianLanguage
                : EnglishLanguage;
        }

        public static string LanguageDisplayName(string language)
        {
            return string.Equals(ResolveLanguage(language), RussianLanguage, StringComparison.Ordinal)
                ? "Русский"
                : "English";
        }

        public static string Text(string key, string selectedLanguage)
        {
            var dictionary = string.Equals(
                ResolveLanguage(selectedLanguage),
                RussianLanguage,
                StringComparison.Ordinal)
                ? Russian
                : English;

            return dictionary.TryGetValue(key, out var value) ? value : key;
        }

        public static string SystemLanguageDisplayText(string selectedLanguage)
        {
            var format = Text("LocSystemLanguage", selectedLanguage);
            return string.Format(format, LanguageDisplayName(AutomaticLanguage));
        }

        public static void Apply(Application application, string selectedLanguage)
        {
            if (application == null)
            {
                return;
            }

            var dictionary = string.Equals(
                ResolveLanguage(selectedLanguage),
                RussianLanguage,
                StringComparison.Ordinal)
                ? Russian
                : English;

            foreach (var pair in dictionary)
            {
                application.Resources[pair.Key] = pair.Value;
            }
        }
    }
}
