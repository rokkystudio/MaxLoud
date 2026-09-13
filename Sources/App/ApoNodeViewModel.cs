namespace MaxLoud
{
    /// <summary>
    /// Presents one immutable APO-chain node to the WPF grouped list.
    /// </summary>
    public sealed class ApoNodeViewModel
    {
        internal ApoNodeInfo Node { get; }
        public string StageTitle { get; }
        public string DisplayName { get; }
        public string StateText { get; }
        public string Details { get; }
        public bool IsAttached { get; }
        public bool CanDetach { get; }

        /// <summary>
        /// Creates a WPF presentation value without changing the underlying APO-chain model.
        /// </summary>
        internal ApoNodeViewModel(ApoNodeInfo node)
        {
            Node = node;
            StageTitle = GetStageTitle(node.Stage);
            DisplayName = node.IsMaxLoud
                ? node.Name + "  [Windows EFX integration]"
                : node.Name;

            StateText = node.CanDetach
                ? node.IsMaxLoud
                    ? node.Attached
                        ? "Интегрирован"
                        : "Не интегрирован"
                    : node.Attached
                        ? "Подключён"
                        : "Отключён"
                : "Legacy / read-only";

            Details =
                node.Clsid.ToString("B").ToUpperInvariant() +
                "  |  " +
                (node.DllPath ?? "InprocServer32 not found");

            IsAttached = node.Attached;
            CanDetach = node.CanDetach;
        }

        /// <summary>
        /// Returns the user-facing Windows Audio graph stage name used as the WPF group header.
        /// </summary>
        private static string GetStageTitle(ApoStage stage)
        {
            switch (stage)
            {
                case ApoStage.Sfx:
                    return "SFX — Stream effects";
                case ApoStage.Mfx:
                    return "MFX — Mode effects";
                case ApoStage.Efx:
                    return "EFX — Endpoint effects";
                default:
                    return stage.ToString();
            }
        }
    }
}
