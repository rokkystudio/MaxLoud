using System;
using System.Runtime.InteropServices;

namespace MaxLoud
{
    /// <summary>
    /// Contains the Core Audio COM declarations required to enumerate render endpoints and edit endpoint properties.
    /// </summary>
    internal static class CoreAudioInterop
    {
        public const uint DeviceStateActive = 0x00000001;
        public const uint StgmRead = 0x00000000;
        public const uint StgmReadWrite = 0x00000002;

        public static readonly Guid MmDeviceEnumeratorClsid =
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        public static readonly Guid PolicyConfigClsid =
            new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

        public static readonly PropertyKey DeviceFriendlyName =
            new PropertyKey(
                new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                14);

        public static readonly PropertyKey DeviceDescription =
            new PropertyKey(
                new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                2);

        public static readonly PropertyKey AudioEndpointGuid =
            new PropertyKey(
                new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"),
                4);

        public static readonly PropertyKey AudioEndpointDisableSysFx =
            new PropertyKey(
                new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"),
                5);

        /// <summary>
        /// Releases a COM object when the runtime created an RCW for it.
        /// </summary>
        public static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        /// <summary>
        /// Clears resources owned by a PROPVARIANT returned from a COM property store.
        /// </summary>
        public static void ClearPropVariant(ref PropVariant value)
        {
            PropVariantClear(ref value);
        }
    }

    internal enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    internal enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;

        /// <summary>
        /// Creates a PROPERTYKEY from its property-set GUID and numeric property identifier.
        /// </summary>
        public PropertyKey(Guid formatId, int propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        private const ushort VtEmpty = 0;
        private const ushort VtI4 = 3;
        private const ushort VtUi4 = 19;
        private const ushort VtLpwstr = 31;

        [FieldOffset(0)]
        private ushort _valueType;

        [FieldOffset(2)]
        private ushort _reserved1;

        [FieldOffset(4)]
        private ushort _reserved2;

        [FieldOffset(6)]
        private ushort _reserved3;

        [FieldOffset(8)]
        private int _intValue;

        [FieldOffset(8)]
        private uint _uintValue;

        [FieldOffset(8)]
        private IntPtr _pointerValue;

        /// <summary>
        /// Creates an unsigned 32-bit PROPVARIANT suitable for IPropertyStore.SetValue.
        /// </summary>
        public static PropVariant FromUInt32(uint value)
        {
            return new PropVariant
            {
                _valueType = VtUi4,
                _uintValue = value
            };
        }

        /// <summary>
        /// Returns an integer property stored as VT_UI4 or VT_I4.
        /// </summary>
        public uint GetUInt32()
        {
            switch (_valueType)
            {
                case VtEmpty:
                    return 0;
                case VtUi4:
                    return _uintValue;
                case VtI4:
                    return unchecked((uint)_intValue);
                default:
                    throw new InvalidOperationException(
                        "РћР¶РёРґР°Р»СЃСЏ VT_UI4/VT_I4, РїРѕР»СѓС‡РµРЅ VARTYPE " + _valueType + ".");
            }
        }

        /// <summary>
        /// Returns the string stored in a VT_LPWSTR PROPVARIANT.
        /// </summary>
        public string GetString()
        {
            if (_valueType == VtEmpty)
            {
                return null;
            }

            if (_valueType != VtLpwstr)
            {
                throw new InvalidOperationException(
                    "РћР¶РёРґР°Р»СЃСЏ VT_LPWSTR, РїРѕР»СѓС‡РµРЅ VARTYPE " + _valueType + ".");
            }

            return _pointerValue == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(_pointerValue);
        }

    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        void GetCount(out uint deviceCount);

        void Item(
            uint deviceIndex,
            out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        void Activate(
            ref Guid interfaceId,
            uint classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

        void OpenPropertyStore(
            uint storageMode,
            out IPropertyStore propertyStore);

        void GetId(
            [MarshalAs(UnmanagedType.LPWStr)] out string endpointId);

        void GetState(out uint deviceState);
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(
            EDataFlow dataFlow,
            uint stateMask,
            out IMMDeviceCollection devices);

        void GetDefaultAudioEndpoint(
            EDataFlow dataFlow,
            ERole role,
            out IMMDevice endpoint);

        void GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string endpointId,
            out IMMDevice endpoint);

        void RegisterEndpointNotificationCallback(IntPtr client);

        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        void GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        void GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out IntPtr format);
        void ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        void GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out long defaultPeriod, out long minimumPeriod);
        void SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        void GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        void SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        void GetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Bool)] bool effectsStore,
            ref PropertyKey key,
            out PropVariant value);
        void SetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Bool)] bool effectsStore,
            ref PropertyKey key,
            ref PropVariant value);
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        void SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        void GetCount(out uint propertyCount);

        void GetAt(
            uint propertyIndex,
            out PropertyKey key);

        void GetValue(
            ref PropertyKey key,
            out PropVariant value);

        void SetValue(
            ref PropertyKey key,
            ref PropVariant value);

        void Commit();
    }
}

