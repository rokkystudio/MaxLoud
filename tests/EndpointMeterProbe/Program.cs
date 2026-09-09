using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    private static readonly Guid MmDeviceEnumeratorClsid =
        new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private static readonly Guid AudioMeterInterfaceId =
        new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    private static readonly Guid AudioEndpointVolumeInterfaceId =
        new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

    private const uint DeviceStateActive = 0x00000001;
    private const uint ClsCtxAll = 23;
    private const uint SndAsync = 0x0001;
    private const uint SndFilename = 0x00020000;

    private static int Main()
    {
        var enumeratorType = Type.GetTypeFromCLSID(
            MmDeviceEnumeratorClsid,
            true);

        var enumerator =
            (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType);

        IMMDevice defaultDevice = null;
        IAudioMeterInformation meter = null;
        IAudioEndpointVolume endpointVolume = null;

        try
        {
            enumerator.GetDefaultAudioEndpoint(
                EDataFlow.Render,
                ERole.Multimedia,
                out defaultDevice);

            defaultDevice.GetId(out var id);
            Console.WriteLine("Default render endpoint: " + id);

            var iid = AudioMeterInterfaceId;
            defaultDevice.Activate(
                ref iid,
                ClsCtxAll,
                IntPtr.Zero,
                out var meterObject);

            meter = (IAudioMeterInformation)meterObject;

            var volumeIid = AudioEndpointVolumeInterfaceId;
            defaultDevice.Activate(
                ref volumeIid,
                ClsCtxAll,
                IntPtr.Zero,
                out var volumeObject);

            endpointVolume = (IAudioEndpointVolume)volumeObject;
            endpointVolume.GetMasterVolumeLevelScalar(out var volumeScalar);
            endpointVolume.GetMute(out var muted);
            Console.WriteLine("Master volume: " + (volumeScalar * 100.0f).ToString("0.0") + "%");
            Console.WriteLine("Muted: " + muted);

            var wav = @"C:\Windows\Media\Windows Notify System Generic.wav";
            if (!PlaySound(wav, IntPtr.Zero, SndFilename | SndAsync))
            {
                Console.Error.WriteLine("PlaySound failed.");
                return 2;
            }

            var maximum = 0.0f;
            for (var index = 0; index < 80; index++)
            {
                meter.GetPeakValue(out var peak);
                if (peak > maximum)
                {
                    maximum = peak;
                }

                Thread.Sleep(25);
            }

            Console.WriteLine("Endpoint peak: " + maximum.ToString("0.000000"));
            return maximum > 0.0001f ? 0 : 3;
        }
        finally
        {
            ReleaseComObject(endpointVolume);
            ReleaseComObject(meter);
            ReleaseComObject(defaultDevice);
            ReleaseComObject(enumerator);
        }
    }

    private static void ReleaseComObject(object value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(
        string soundName,
        IntPtr module,
        uint flags);
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

    void OpenPropertyStore(uint storageMode, out IntPtr propertyStore);

    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string endpointId);

    void GetState(out uint deviceState);
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

    void GetDefaultAudioEndpoint(
        EDataFlow dataFlow,
        ERole role,
        out IMMDevice endpoint);

    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string endpointId, out IMMDevice endpoint);

    void RegisterEndpointNotificationCallback(IntPtr client);

    void UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IntPtr notify);
    void UnregisterControlChangeNotify(IntPtr notify);
    void GetChannelCount(out uint channelCount);
    void SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
    void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    void GetMasterVolumeLevel(out float levelDb);
    void GetMasterVolumeLevelScalar(out float level);
    void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
    void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    void GetChannelVolumeLevel(uint channel, out float levelDb);
    void GetChannelVolumeLevelScalar(uint channel, out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    void GetVolumeStepInfo(out uint step, out uint stepCount);
    void VolumeStepUp(ref Guid eventContext);
    void VolumeStepDown(ref Guid eventContext);
    void QueryHardwareSupport(out uint hardwareSupportMask);
    void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

[ComImport]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    void GetPeakValue(out float peak);

    void GetMeteringChannelCount(out uint channelCount);

    void GetChannelsPeakValues(
        uint channelCount,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] peakValues);

    void QueryHardwareSupport(out uint hardwareSupportMask);
}
