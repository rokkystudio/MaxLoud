using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MaxLoud
{
    /// <summary>
    /// Describes one active Windows render endpoint and its persistent endpoint GUID.
    /// </summary>
    internal sealed class AudioEndpointInfo
    {
        public string Id { get; }
        public Guid EndpointGuid { get; }
        public string Name { get; }
        public bool IsDefault { get; }

        /// <summary>
        /// Creates an immutable render endpoint description.
        /// </summary>
        public AudioEndpointInfo(
            string id,
            Guid endpointGuid,
            string name,
            bool isDefault)
        {
            Id = id;
            EndpointGuid = endpointGuid;
            Name = name;
            IsDefault = isDefault;
        }

        /// <summary>
        /// Returns the endpoint name and marks the current default render device.
        /// </summary>
        public override string ToString()
        {
            return IsDefault
                ? Name + "  [default]"
                : Name;
        }
    }

    /// <summary>
    /// Uses Windows Core Audio to enumerate render endpoints and Windows 10 PolicyConfig to
    /// read/write PKEY_AudioEndpoint_Disable_SysFx in the endpoint effects store.
    /// </summary>
    internal sealed class AudioEndpointManager : IDisposable
    {
        private readonly IMMDeviceEnumerator _enumerator;

        /// <summary>
        /// Creates the Windows MMDevice enumerator used by MaxLoud.
        /// </summary>
        public AudioEndpointManager()
        {
            var enumeratorType = Type.GetTypeFromCLSID(
                CoreAudioInterop.MmDeviceEnumeratorClsid,
                true);

            _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType);
        }

        /// <summary>
        /// Returns all active render endpoints and identifies the multimedia default endpoint.
        /// </summary>
        public IReadOnlyList<AudioEndpointInfo> GetActiveRenderEndpoints()
        {
            var defaultEndpointId = GetDefaultRenderEndpointId();
            IMMDeviceCollection collection = null;

            try
            {
                _enumerator.EnumAudioEndpoints(
                    EDataFlow.Render,
                    CoreAudioInterop.DeviceStateActive,
                    out collection);

                collection.GetCount(out var count);
                var result = new List<AudioEndpointInfo>((int)count);

                for (var index = 0u; index < count; index++)
                {
                    IMMDevice device = null;

                    try
                    {
                        collection.Item(index, out device);
                        result.Add(ReadEndpoint(
                            device,
                            string.Equals(
                                GetEndpointId(device),
                                defaultEndpointId,
                                StringComparison.OrdinalIgnoreCase)));
                    }
                    finally
                    {
                        CoreAudioInterop.ReleaseComObject(device);
                    }
                }

                return result;
            }
            finally
            {
                CoreAudioInterop.ReleaseComObject(collection);
            }
        }

        /// <summary>
        /// Returns whether Windows system audio enhancements are enabled for the selected endpoint.
        /// The canonical value is read from the endpoint effects store through IPolicyConfig with bFxStore=true.
        /// </summary>
        public bool GetSystemEnhancementsEnabled(
            AudioEndpointInfo endpoint)
        {
            var policyConfig = CreatePolicyConfig();

            try
            {
                var key = CoreAudioInterop.AudioEndpointDisableSysFx;
                policyConfig.GetPropertyValue(
                    endpoint.Id,
                    true,
                    ref key,
                    out var value);

                try
                {
                    return value.GetUInt32() == 0;
                }
                finally
                {
                    CoreAudioInterop.ClearPropVariant(ref value);
                }
            }
            finally
            {
                CoreAudioInterop.ReleaseComObject(policyConfig);
            }
        }

        /// <summary>
        /// Sets PKEY_AudioEndpoint_Disable_SysFx through the Windows 10 PolicyConfig effects store.
        /// IMMDevice::OpenPropertyStore exposes the normal endpoint store and returns VT_EMPTY for this
        /// effects-store property on this Windows 10 endpoint, so it must not be used as the source of truth.
        /// </summary>
        public void SetSystemEnhancementsEnabled(
            AudioEndpointInfo endpoint,
            bool enabled)
        {
            var policyConfig = CreatePolicyConfig();

            try
            {
                var key = CoreAudioInterop.AudioEndpointDisableSysFx;
                var value = PropVariant.FromUInt32(enabled ? 0u : 1u);

                policyConfig.SetPropertyValue(
                    endpoint.Id,
                    true,
                    ref key,
                    ref value);
            }
            finally
            {
                CoreAudioInterop.ReleaseComObject(policyConfig);
            }
        }

        /// <summary>
        /// Creates the Windows 10 PolicyConfig COM object used by the Sound Control Panel effects store.
        /// </summary>
        private static IPolicyConfig CreatePolicyConfig()
        {
            var type = Type.GetTypeFromCLSID(
                CoreAudioInterop.PolicyConfigClsid,
                true);

            return (IPolicyConfig)Activator.CreateInstance(type);
        }

        /// <summary>
        /// Releases the Windows MMDevice enumerator COM object.
        /// </summary>
        public void Dispose()
        {
            CoreAudioInterop.ReleaseComObject(_enumerator);
        }

        /// <summary>
        /// Reads endpoint ID, friendly name and PKEY_AudioEndpoint_GUID from one IMMDevice.
        /// </summary>
        private static AudioEndpointInfo ReadEndpoint(
            IMMDevice device,
            bool isDefault)
        {
            var endpointId = GetEndpointId(device);
            IPropertyStore propertyStore = null;

            try
            {
                device.OpenPropertyStore(
                    CoreAudioInterop.StgmRead,
                    out propertyStore);

                var name = ReadString(
                    propertyStore,
                    CoreAudioInterop.DeviceFriendlyName);

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = ReadString(
                        propertyStore,
                        CoreAudioInterop.DeviceDescription);
                }

                var endpointGuidText = ReadString(
                    propertyStore,
                    CoreAudioInterop.AudioEndpointGuid);

                if (!Guid.TryParse(endpointGuidText, out var endpointGuid))
                {
                    throw new InvalidOperationException(
                        "Endpoint " + endpointId +
                        " РЅРµ СЃРѕРґРµСЂР¶РёС‚ РєРѕСЂСЂРµРєС‚РЅС‹Р№ PKEY_AudioEndpoint_GUID: " +
                        endpointGuidText);
                }

                return new AudioEndpointInfo(
                    endpointId,
                    endpointGuid,
                    name ?? endpointId,
                    isDefault);
            }
            finally
            {
                CoreAudioInterop.ReleaseComObject(propertyStore);
            }
        }

        /// <summary>
        /// Returns the multimedia default render endpoint ID.
        /// </summary>
        private string GetDefaultRenderEndpointId()
        {
            IMMDevice device = null;

            try
            {
                _enumerator.GetDefaultAudioEndpoint(
                    EDataFlow.Render,
                    ERole.Multimedia,
                    out device);

                return GetEndpointId(device);
            }
            finally
            {
                CoreAudioInterop.ReleaseComObject(device);
            }
        }

        /// <summary>
        /// Reads the persistent MMDevice endpoint identifier.
        /// </summary>
        private static string GetEndpointId(IMMDevice device)
        {
            device.GetId(out var endpointId);
            return endpointId;
        }

        /// <summary>
        /// Reads one VT_LPWSTR property from an endpoint property store.
        /// </summary>
        private static string ReadString(
            IPropertyStore propertyStore,
            PropertyKey propertyKey)
        {
            var key = propertyKey;
            propertyStore.GetValue(ref key, out var value);

            try
            {
                return value.GetString();
            }
            finally
            {
                CoreAudioInterop.ClearPropVariant(ref value);
            }
        }
    }
}

