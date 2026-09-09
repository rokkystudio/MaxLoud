using System;
using System.IO;
using System.Text.Json;

namespace MaxLoud
{
    /// <summary>
    /// Persists tray application settings in the current user's local application data directory.
    /// </summary>
    internal sealed class SettingsStore
    {
        private readonly string _settingsPath;

        /// <summary>
        /// Creates a settings store at %LOCALAPPDATA%\MaxLoud\settings.json.
        /// </summary>
        public SettingsStore()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MaxLoud");

            Directory.CreateDirectory(directory);
            _settingsPath = Path.Combine(directory, "settings.json");
        }

        /// <summary>
        /// Loads stored settings or returns defaults when no settings file exists.
        /// Invalid JSON is reported instead of being silently replaced.
        /// </summary>
        public AppSettings Load()
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);

            if (settings == null)
            {
                throw new InvalidDataException("Файл настроек MaxLoud не содержит объект настроек.");
            }

            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;

                if (!root.TryGetProperty("InputGainDb", out _) &&
                    root.TryGetProperty("MakeupGainDb", out var previousGain) &&
                    previousGain.ValueKind == JsonValueKind.Number &&
                    previousGain.TryGetSingle(out var gainDb))
                {
                    settings.InputGainDb = gainDb;
                }
            }

            return settings;
        }

        /// <summary>
        /// Writes the complete current settings to disk.
        /// </summary>
        public void Save(AppSettings settings)
        {
            var json = JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            File.WriteAllText(_settingsPath, json);
        }
    }
}
