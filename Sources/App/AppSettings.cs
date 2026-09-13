namespace MaxLoud
{
    /// <summary>
    /// Stores the selected render endpoint and the MaxLoud DSP parameters controlled by the tray application.
    /// </summary>
    internal sealed class AppSettings
    {
        public string RenderEndpointId { get; set; }
        public string Theme { get; set; } = UiThemeService.LightTheme;
        public string Language { get; set; } = LocalizationService.AutomaticLanguage;
        public bool RunOnStartup { get; set; } = true;

        public bool MaxLoudEnabled { get; set; } = true;
        public bool CompressorEnabled { get; set; } = true;
        public bool EqualizerEnabled { get; set; } = true;
        public bool LimiterEnabled { get; set; } = true;

        public float ThresholdDb { get; set; } = -20.0f;
        public float Ratio { get; set; } = 4.0f;
        public float AttackMs { get; set; } = 10.0f;
        public float ReleaseMs { get; set; } = 350.0f;
        public float VolumeLeveling { get; set; }
        public float InputGainDb { get; set; } = 4.0f;
        public float LimiterCeilingDb { get; set; } = -1.0f;

        public float Eq80Db { get; set; }
        public float Eq250Db { get; set; }
        public float Eq1000Db { get; set; }
        public float Eq3000Db { get; set; }
        public float Eq8000Db { get; set; }

        /// <summary>
        /// Creates an independent settings value for UI editing and runtime state publication.
        /// </summary>
        public AppSettings Clone()
        {
            return new AppSettings
            {
                RenderEndpointId = RenderEndpointId,
                Theme = Theme,
                Language = Language,
                RunOnStartup = RunOnStartup,
                MaxLoudEnabled = MaxLoudEnabled,
                CompressorEnabled = CompressorEnabled,
                EqualizerEnabled = EqualizerEnabled,
                LimiterEnabled = LimiterEnabled,
                ThresholdDb = ThresholdDb,
                Ratio = Ratio,
                AttackMs = AttackMs,
                ReleaseMs = ReleaseMs,
                VolumeLeveling = VolumeLeveling,
                InputGainDb = InputGainDb,
                LimiterCeilingDb = LimiterCeilingDb,
                Eq80Db = Eq80Db,
                Eq250Db = Eq250Db,
                Eq1000Db = Eq1000Db,
                Eq3000Db = Eq3000Db,
                Eq8000Db = Eq8000Db
            };
        }
    }
}
