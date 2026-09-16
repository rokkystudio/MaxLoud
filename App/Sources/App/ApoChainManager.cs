using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace MaxLoud
{
    internal enum ApoStage
    {
        Sfx,
        Mfx,
        Efx
    }

    /// <summary>
    /// Describes one APO registered in the selected endpoint's SFX, MFX or EFX stage.
    /// </summary>
    internal sealed class ApoNodeInfo
    {
        public ApoStage Stage { get; }
        public Guid Clsid { get; }
        public string Name { get; }
        public string DllPath { get; }
        public bool Attached { get; }
        public bool CanDetach { get; }
        public int Position { get; }
        public bool IsMaxLoud { get; }

        /// <summary>
        /// Creates an immutable APO-chain node resolved from endpoint FxProperties and COM registration.
        /// </summary>
        public ApoNodeInfo(
            ApoStage stage,
            Guid clsid,
            string name,
            string dllPath,
            bool attached,
            bool canDetach,
            int position,
            bool isMaxLoud)
        {
            Stage = stage;
            Clsid = clsid;
            Name = name;
            DllPath = dllPath;
            Attached = attached;
            CanDetach = canDetach;
            Position = position;
            IsMaxLoud = isMaxLoud;
        }
    }

    /// <summary>
    /// Reads and edits endpoint CompositeFX registration while preserving detached node positions in MaxLoud state.
    /// </summary>
    internal sealed class ApoChainManager
    {
        public static readonly Guid MaxLoudApoClsid =
            new Guid("7CB491F3-E5C2-4F34-AB14-08AD933EC77D");

        private const string FxPropertySet =
            "{D04E05A6-594B-4FB6-A80D-01AF5EED7D1D}";

        private const string DetachedRoot =
            @"SOFTWARE\MaxLoud\DetachedApo";

        /// <summary>
        /// Reads the selected endpoint's SFX, MFX and EFX nodes.
        /// CompositeFX nodes are editable; legacy single-value nodes are reported read-only.
        /// </summary>
        public IReadOnlyList<ApoNodeInfo> ReadChain(
            AudioEndpointInfo endpoint)
        {
            var result = new List<ApoNodeInfo>();

            using (var fxKey = EndpointFxRegistry.OpenRead(endpoint))
            {
                if (fxKey == null)
                {
                    return result;
                }

                ReadStage(
                    result,
                    endpoint,
                    fxKey,
                    ApoStage.Sfx,
                    13,
                    5);

                ReadStage(
                    result,
                    endpoint,
                    fxKey,
                    ApoStage.Mfx,
                    14,
                    6);

                ReadStage(
                    result,
                    endpoint,
                    fxKey,
                    ApoStage.Efx,
                    15,
                    7);
            }

            return result;
        }

        /// <summary>
        /// Attaches or detaches one editable CompositeFX node.
        /// When a CompositeFX value does not exist yet, attaching MaxLoud creates it and seeds the list
        /// with the endpoint's legacy single-stage APO so the existing vendor effect remains first.
        /// Detach stores the original list position so a later attach can restore the same order.
        /// </summary>
        public void SetAttached(
            AudioEndpointInfo endpoint,
            ApoNodeInfo node,
            bool attached)
        {
            DiagnosticLogger.Info(
                "SetAttached BEGIN: endpoint=" + endpoint.Name +
                ", stage=" + node.Stage +
                ", clsid=" + node.Clsid.ToString("B") +
                ", requested=" + attached);

            if (!node.CanDetach)
            {
                throw new InvalidOperationException(
                    "Узел " + node.Name +
                    " зарегистрирован через legacy FX property и не является CompositeFX-списком.");
            }

            var valueName = GetCompositeValueName(node.Stage);

            using (var fxKey = EndpointFxRegistry.OpenUpdate(endpoint))
            {
                if (fxKey == null)
                {
                    throw new InvalidOperationException(
                        "FxProperties отсутствует у endpoint " + endpoint.Name + ".");
                }

                var valueNames = fxKey.GetValueNames();
                var compositeExists = valueNames.Contains(
                    valueName,
                    StringComparer.OrdinalIgnoreCase);

                List<string> current;

                if (compositeExists)
                {
                    if (fxKey.GetValueKind(valueName) != RegistryValueKind.MultiString)
                    {
                        throw new InvalidOperationException(
                            "Ожидался REG_MULTI_SZ для " + valueName + ".");
                    }

                    current = ((string[])fxKey.GetValue(
                        valueName,
                        Array.Empty<string>())).ToList();

                    DiagnosticLogger.Info(
                        "CompositeFX before " + node.Stage + ": [" +
                        string.Join(", ", current) + "]");
                }
                else
                {
                    if (!attached)
                    {
                        return;
                    }

                    current = ReadLegacyStageSeed(
                        fxKey,
                        node.Stage);
                }

                var clsidText = FormatClsid(node.Clsid);
                var currentIndex = current.FindIndex(
                    value => string.Equals(
                        value,
                        clsidText,
                        StringComparison.OrdinalIgnoreCase));

                if (attached)
                {
                    if (currentIndex >= 0)
                    {
                        RemoveDetachedRecord(
                            endpoint,
                            node.Stage,
                            node.Clsid);
                        return;
                    }

                    var restoredIndex = ReadDetachedPosition(
                        endpoint,
                        node.Stage,
                        node.Clsid);

                    if (restoredIndex < 0 ||
                        restoredIndex == int.MaxValue)
                    {
                        restoredIndex = current.Count;
                    }

                    restoredIndex = Math.Min(
                        restoredIndex,
                        current.Count);

                    current.Insert(restoredIndex, clsidText);

                    fxKey.SetValue(
                        valueName,
                        current.ToArray(),
                        RegistryValueKind.MultiString);

                    RemoveDetachedRecord(
                        endpoint,
                        node.Stage,
                        node.Clsid);

                    DiagnosticLogger.Info(
                        "SetAttached END (attached): " + valueName + " = [" +
                        string.Join(", ", current) + "]");

                    return;
                }

                if (currentIndex < 0)
                {
                    return;
                }

                SaveDetachedPosition(
                    endpoint,
                    node.Stage,
                    node.Clsid,
                    currentIndex);

                current.RemoveAt(currentIndex);

                fxKey.SetValue(
                    valueName,
                    current.ToArray(),
                    RegistryValueKind.MultiString);

                DiagnosticLogger.Info(
                    "SetAttached END (detached): " + valueName + " = [" +
                    string.Join(", ", current) + "]");
            }
        }

        /// <summary>
        /// Attaches or detaches the known MaxLoud EFX CLSID while preserving its CompositeFX list position.
        /// </summary>
        public void SetMaxLoudAttached(
            AudioEndpointInfo endpoint,
            bool attached)
        {
            var node = ReadChain(endpoint).FirstOrDefault(
                item =>
                    item.Stage == ApoStage.Efx &&
                    item.IsMaxLoud);

            if (node == null)
            {
                node = CreateNode(
                    ApoStage.Efx,
                    MaxLoudApoClsid,
                    false,
                    true,
                    int.MaxValue);
            }

            SetAttached(
                endpoint,
                node,
                attached);
        }

        /// <summary>
        /// Returns whether MaxLoudApo.dll is currently present in the endpoint EFX list.
        /// </summary>
        public bool IsMaxLoudAttached(AudioEndpointInfo endpoint)
        {
            return ReadChain(endpoint).Any(
                node =>
                    node.Stage == ApoStage.Efx &&
                    node.IsMaxLoud &&
                    node.Attached);
        }

        /// <summary>
        /// Reads one stage from CompositeFX when available and falls back to the legacy single CLSID property.
        /// </summary>
        private void ReadStage(
            List<ApoNodeInfo> result,
            AudioEndpointInfo endpoint,
            RegistryKey fxKey,
            ApoStage stage,
            int compositePropertyId,
            int legacyPropertyId)
        {
            var compositeValueName =
                FxPropertySet + "," + compositePropertyId;

            if (fxKey.GetValueNames().Contains(
                compositeValueName,
                StringComparer.OrdinalIgnoreCase))
            {
                if (fxKey.GetValueKind(compositeValueName) !=
                    RegistryValueKind.MultiString)
                {
                    throw new InvalidDataException(
                        compositeValueName +
                        " должен иметь тип REG_MULTI_SZ.");
                }

                var values = (string[])fxKey.GetValue(
                    compositeValueName,
                    Array.Empty<string>());

                for (var index = 0; index < values.Length; index++)
                {
                    if (!Guid.TryParse(values[index], out var clsid))
                    {
                        throw new InvalidDataException(
                            "Некорректный APO CLSID в " +
                            compositeValueName + ": " + values[index]);
                    }

                    result.Add(CreateNode(
                        stage,
                        clsid,
                        true,
                        true,
                        index));
                }

                AppendDetachedNodes(
                    result,
                    endpoint,
                    stage);

                return;
            }

            var legacyValueName =
                FxPropertySet + "," + legacyPropertyId;

            if (!fxKey.GetValueNames().Contains(
                legacyValueName,
                StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            var value = fxKey.GetValue(legacyValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!Guid.TryParse(value, out var legacyClsid))
            {
                throw new InvalidDataException(
                    "Некорректный legacy APO CLSID в " +
                    legacyValueName + ": " + value);
            }

            result.Add(CreateNode(
                stage,
                legacyClsid,
                true,
                false,
                0));
        }

        /// <summary>
        /// Returns the legacy single APO CLSID for a stage as the initial CompositeFX list.
        /// An absent legacy property produces an empty list; malformed legacy data is reported explicitly.
        /// </summary>
        private static List<string> ReadLegacyStageSeed(
            RegistryKey fxKey,
            ApoStage stage)
        {
            var valueName = GetLegacyValueName(stage);

            if (!fxKey.GetValueNames().Contains(
                valueName,
                StringComparer.OrdinalIgnoreCase))
            {
                return new List<string>();
            }

            if (fxKey.GetValueKind(valueName) != RegistryValueKind.String)
            {
                throw new InvalidDataException(
                    valueName +
                    " должен иметь тип REG_SZ.");
            }

            var value = fxKey.GetValue(valueName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            if (!Guid.TryParse(value, out var clsid))
            {
                throw new InvalidDataException(
                    "Некорректный legacy APO CLSID в " +
                    valueName + ": " + value);
            }

            return new List<string>
            {
                FormatClsid(clsid)
            };
        }

        /// <summary>
        /// Adds nodes previously detached by MaxLoud so they remain visible and can be restored.
        /// </summary>
        private void AppendDetachedNodes(
            List<ApoNodeInfo> result,
            AudioEndpointInfo endpoint,
            ApoStage stage)
        {
            var path = GetDetachedStagePath(
                endpoint,
                stage);

            using (var key = Registry.LocalMachine.OpenSubKey(
                path,
                false))
            {
                if (key == null)
                {
                    return;
                }

                foreach (var valueName in key.GetValueNames())
                {
                    if (!Guid.TryParse(
                        valueName,
                        out var clsid))
                    {
                        continue;
                    }

                    if (result.Any(
                        node =>
                            node.Stage == stage &&
                            node.Clsid == clsid &&
                            node.Attached))
                    {
                        continue;
                    }

                    var position = Convert.ToInt32(
                        key.GetValue(valueName, int.MaxValue));

                    result.Add(CreateNode(
                        stage,
                        clsid,
                        false,
                        true,
                        position));
                }
            }
        }

        /// <summary>
        /// Resolves COM friendly name and InprocServer32 path for one APO CLSID.
        /// </summary>
        private static ApoNodeInfo CreateNode(
            ApoStage stage,
            Guid clsid,
            bool attached,
            bool canDetach,
            int position)
        {
            var clsidPath =
                @"CLSID\" + FormatClsid(clsid);

            string name = null;
            string dllPath = null;

            using (var clsidKey =
                Registry.ClassesRoot.OpenSubKey(
                    clsidPath,
                    false))
            {
                if (clsidKey != null)
                {
                    name = clsidKey.GetValue(null) as string;

                    using (var serverKey =
                        clsidKey.OpenSubKey(
                            "InprocServer32",
                            false))
                    {
                        dllPath =
                            serverKey?.GetValue(null) as string;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(dllPath))
            {
                dllPath =
                    Environment.ExpandEnvironmentVariables(
                        dllPath);
            }

            return new ApoNodeInfo(
                stage,
                clsid,
                string.IsNullOrWhiteSpace(name)
                    ? FormatClsid(clsid)
                    : name,
                dllPath,
                attached,
                canDetach,
                position,
                clsid == MaxLoudApoClsid);
        }

        /// <summary>
        /// Stores the position of a node before removing it from the endpoint's CompositeFX list.
        /// </summary>
        private static void SaveDetachedPosition(
            AudioEndpointInfo endpoint,
            ApoStage stage,
            Guid clsid,
            int position)
        {
            using (var key = Registry.LocalMachine.CreateSubKey(
                GetDetachedStagePath(endpoint, stage),
                true))
            {
                key.SetValue(
                    FormatClsid(clsid),
                    position,
                    RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// Returns the saved CompositeFX list position for a detached node or -1 when no record exists.
        /// </summary>
        private static int ReadDetachedPosition(
            AudioEndpointInfo endpoint,
            ApoStage stage,
            Guid clsid)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(
                GetDetachedStagePath(endpoint, stage),
                false))
            {
                if (key == null)
                {
                    return -1;
                }

                var value = key.GetValue(
                    FormatClsid(clsid));

                return value == null
                    ? -1
                    : Convert.ToInt32(value);
            }
        }

        /// <summary>
        /// Removes MaxLoud's detached-node bookkeeping after a node is restored.
        /// </summary>
        private static void RemoveDetachedRecord(
            AudioEndpointInfo endpoint,
            ApoStage stage,
            Guid clsid)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(
                GetDetachedStagePath(endpoint, stage),
                true))
            {
                key?.DeleteValue(
                    FormatClsid(clsid),
                    false);
            }
        }

        /// <summary>
        /// Returns MaxLoud's machine-level bookkeeping path for one endpoint and one APO stage.
        /// </summary>
        private static string GetDetachedStagePath(
            AudioEndpointInfo endpoint,
            ApoStage stage)
        {
            return DetachedRoot + "\\" +
                endpoint.EndpointGuid.ToString("D") +
                "\\" + stage;
        }

        /// <summary>
        /// Returns the legacy single-effect property name associated with one APO stage.
        /// </summary>
        private static string GetLegacyValueName(
            ApoStage stage)
        {
            switch (stage)
            {
                case ApoStage.Sfx:
                    return FxPropertySet + ",5";
                case ApoStage.Mfx:
                    return FxPropertySet + ",6";
                case ApoStage.Efx:
                    return FxPropertySet + ",7";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(stage));
            }
        }

        /// <summary>
        /// Returns the CompositeFX property name associated with one APO stage.
        /// </summary>
        private static string GetCompositeValueName(
            ApoStage stage)
        {
            switch (stage)
            {
                case ApoStage.Sfx:
                    return FxPropertySet + ",13";
                case ApoStage.Mfx:
                    return FxPropertySet + ",14";
                case ApoStage.Efx:
                    return FxPropertySet + ",15";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(stage));
            }
        }

        /// <summary>
        /// Formats a CLSID exactly as the Windows audio registry stores it.
        /// </summary>
        private static string FormatClsid(Guid clsid)
        {
            return clsid.ToString("B").ToUpperInvariant();
        }
    }
}
