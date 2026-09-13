using System;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace MaxLoud
{
    /// <summary>
    /// Provides narrowly scoped access to one endpoint FxProperties key.
    /// Windows grants Administrators QueryValue/SetValue on these keys but does not necessarily grant
    /// CreateSubKey or full KEY_WRITE, so MaxLoud must request only the rights it actually needs.
    /// </summary>
    internal static class EndpointFxRegistry
    {
        private const string EndpointRoot =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

        private const string DisableSystemEffectsValueName =
            "{1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E},5";

        /// <summary>
        /// Opens FxProperties for normal read operations.
        /// </summary>
        public static RegistryKey OpenRead(AudioEndpointInfo endpoint)
        {
            var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);

            try
            {
                return baseKey.OpenSubKey(
                    GetFxPropertiesPath(endpoint),
                    false);
            }
            finally
            {
                baseKey.Dispose();
            }
        }

        /// <summary>
        /// Opens FxProperties with only QueryValues and SetValue rights.
        /// Requesting the broader writable:true access mask fails on normal Windows endpoint ACLs
        /// even for an elevated Administrator because those ACLs intentionally omit CreateSubKey/Delete.
        /// </summary>
        public static RegistryKey OpenUpdate(AudioEndpointInfo endpoint)
        {
            var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);

            try
            {
                return baseKey.OpenSubKey(
                    GetFxPropertiesPath(endpoint),
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.QueryValues |
                    RegistryRights.SetValue);
            }
            finally
            {
                baseKey.Dispose();
            }
        }

        /// <summary>
        /// Returns whether the endpoint master system-effects switch is enabled.
        /// An absent PKEY_AudioEndpoint_Disable_SysFx value is treated as enabled.
        /// </summary>
        public static bool GetSystemEnhancementsEnabled(
            AudioEndpointInfo endpoint)
        {
            using (var fxKey = OpenRead(endpoint))
            {
                if (fxKey == null)
                {
                    return true;
                }

                var value = fxKey.GetValue(
                    DisableSystemEffectsValueName);

                return
                    value == null ||
                    Convert.ToUInt32(value) == 0;
            }
        }

        /// <summary>
        /// Writes PKEY_AudioEndpoint_Disable_SysFx directly to the endpoint FxProperties store.
        /// Value 0 enables system effects; value 1 disables them.
        /// </summary>
        public static void SetSystemEnhancementsEnabled(
            AudioEndpointInfo endpoint,
            bool enabled)
        {
            using (var fxKey = OpenUpdate(endpoint))
            {
                if (fxKey == null)
                {
                    throw new InvalidOperationException(
                        "FxProperties отсутствует у endpoint " + endpoint.Name + ".");
                }

                fxKey.SetValue(
                    DisableSystemEffectsValueName,
                    enabled ? 0 : 1,
                    RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// Returns the machine registry path of the selected endpoint FxProperties key.
        /// </summary>
        public static string GetFxPropertiesPath(
            AudioEndpointInfo endpoint)
        {
            return EndpointRoot + "\\" +
                endpoint.EndpointGuid.ToString("B").ToUpperInvariant() +
                "\\FxProperties";
        }
    }
}
