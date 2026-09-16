#pragma once

#include <Windows.h>
#include <atlbase.h>
#include <atlcom.h>
#include <audioenginebaseapo.h>
#include <baseaudioprocessingobject.h>

extern const CLSID CLSID_MaxLoudApo;
extern const GUID GUID_MaxLoudEffect;

namespace MaxLoudApo
{
    struct BiquadCoefficients
    {
        float B0;
        float B1;
        float B2;
        float A1;
        float A2;
    };

    struct BiquadState
    {
        float Z1;
        float Z2;
    };

    struct RuntimeState
    {
        ULONG Magic;
        ULONG Version;
        volatile LONG Sequence;
        ULONG Flags;

        float ThresholdDb;
        float Ratio;
        float AttackMs;
        float ReleaseMs;
        float InputGainDb;
        float LimiterCeilingDb;

        float Eq80Db;
        float Eq250Db;
        float Eq1000Db;
        float Eq3000Db;
        float Eq8000Db;

        float VolumeLeveling;
    };

    struct ProcessingConfig
    {
        ULONG Flags;
        float AttackCoefficient;
        float ReleaseCoefficient;
        float InputGainLinear;
        float LimiterCeilingLinear;
        float LevelerAmount;
        float LevelerTargetLinear;
        float LevelerMaximumGainLinear;
        float LevelerEnvelopeAttackCoefficient;
        float LevelerEnvelopeReleaseCoefficient;
        float LevelerGainRiseCoefficient;
        float LevelerGainFallCoefficient;
        BiquadCoefficients Eq[5];
        float LevelerGainTable[2049];
        float CompressorGainTable[2049];
    };

    /// <summary>
    /// Implements the MaxLoud endpoint-effect APO.
    /// Configuration math and shared-state polling run on a worker thread while APOProcess
    /// reads only locked memory prepared before entering the real-time audio path.
    /// </summary>
    class ATL_NO_VTABLE CMaxLoudApo :
        public ATL::CComObjectRootEx<ATL::CComMultiThreadModel>,
        public ATL::CComCoClass<CMaxLoudApo, &CLSID_MaxLoudApo>,
        public CBaseAudioProcessingObject,
        public IAudioSystemEffects2
    {
    public:
        CMaxLoudApo();
        ~CMaxLoudApo();

        DECLARE_NO_REGISTRY()
        DECLARE_AGGREGATABLE(CMaxLoudApo)

        BEGIN_COM_MAP(CMaxLoudApo)
            COM_INTERFACE_ENTRY(IAudioProcessingObject)
            COM_INTERFACE_ENTRY(IAudioProcessingObjectRT)
            COM_INTERFACE_ENTRY(IAudioProcessingObjectConfiguration)
            COM_INTERFACE_ENTRY(IAudioSystemEffects)
            COM_INTERFACE_ENTRY(IAudioSystemEffects2)
        END_COM_MAP()

        /// <summary>
        /// Processes one block of interleaved floating-point audio in the Windows Audio Engine real-time thread.
        /// </summary>
        STDMETHOD_(void, APOProcess)(
            UINT32 inputConnectionCount,
            APO_CONNECTION_PROPERTY** inputConnections,
            UINT32 outputConnectionCount,
            APO_CONNECTION_PROPERTY** outputConnections) override;

        /// <summary>
        /// Locks the negotiated stream format, allocates real-time state and starts the non-real-time configuration worker.
        /// </summary>
        STDMETHOD(LockForProcess)(
            UINT32 inputConnectionCount,
            APO_CONNECTION_DESCRIPTOR** inputConnections,
            UINT32 outputConnectionCount,
            APO_CONNECTION_DESCRIPTOR** outputConnections) override;

        /// <summary>
        /// Stops the configuration worker, releases mapped configuration and frees real-time state.
        /// </summary>
        STDMETHOD(UnlockForProcess)() override;

        /// <summary>
        /// Accepts Windows system-effects initialization payloads (APOInitSystemEffects/APOInitSystemEffects2)
        /// and then initializes the CBaseAudioProcessingObject state without custom immutable parameters.
        /// </summary>
        STDMETHOD(Initialize)(
            UINT32 dataSize,
            BYTE* data) override;

        /// <summary>
        /// Negotiates input formats and suggests float32 with the requested sample rate/channel layout when needed.
        /// </summary>
        STDMETHOD(IsInputFormatSupported)(
            IAudioMediaType* outputFormat,
            IAudioMediaType* requestedInputFormat,
            IAudioMediaType** supportedInputFormat) override;

        /// <summary>
        /// Negotiates output formats and suggests float32 with the requested sample rate/channel layout when needed.
        /// </summary>
        STDMETHOD(IsOutputFormatSupported)(
            IAudioMediaType* inputFormat,
            IAudioMediaType* requestedOutputFormat,
            IAudioMediaType** supportedOutputFormat) override;

        /// <summary>
        /// Reports the MaxLoud system-effect identifier exposed by this APO.
        /// </summary>
        STDMETHOD(GetEffectsList)(
            LPGUID* effectIds,
            UINT* effectCount,
            HANDLE eventHandle) override;

        static const CRegAPOProperties<1> RegistrationProperties;

    protected:
        /// <summary>
        /// Accepts only interleaved 32-bit IEEE floating-point streams because APOProcess operates directly on float samples.
        /// </summary>
        HRESULT __fastcall ValidateDefaultAPOFormat(
            UNCOMPRESSEDAUDIOFORMAT& audioFormat,
            bool isInput) override;

    private:
        static const ULONG StateMagic = 0x53444c4d;
        static const ULONG StateVersion = 1;

        static const ULONG FlagMaxLoudEnabled = 1 << 0;
        static const ULONG FlagCompressorEnabled = 1 << 1;
        static const ULONG FlagEqualizerEnabled = 1 << 2;
        static const ULONG FlagLimiterEnabled = 1 << 3;

        static DWORD WINAPI ConfigurationThreadProc(void* context);

        /// <summary>
        /// Appends one non-real-time UTF-8 diagnostic record to ProgramData\\MaxLoud\\logs.
        /// Never call this method from APOProcess.
        /// </summary>
        static void WriteDiagnosticLog(
            const wchar_t* format,
            ...);

        /// <summary>
        /// Writes one negotiated media-format description outside the real-time processing path.
        /// </summary>
        static void LogAudioFormat(
            const wchar_t* label,
            const UNCOMPRESSEDAUDIOFORMAT& format);

        /// <summary>
        /// Flushes atomically collected APOProcess counters from the configuration worker.
        /// </summary>
        void LogProcessStatistics(bool force);

        /// <summary>
        /// Polls shared state, waits until the inactive configuration bank has no real-time readers,
        /// then rebuilds and publishes that bank atomically.
        /// </summary>
        void ConfigurationThreadLoop();

        /// <summary>
        /// Opens and maps the ProgramData runtime-state file when it is available.
        /// </summary>
        bool OpenRuntimeState();

        /// <summary>
        /// Closes the ProgramData runtime-state mapping.
        /// </summary>
        void CloseRuntimeState();

        /// <summary>
        /// Starts the configuration worker and publishes an initial pass-through configuration.
        /// </summary>
        HRESULT StartConfigurationWorker();

        /// <summary>
        /// Stops the configuration worker and closes its synchronization handles.
        /// </summary>
        void StopConfigurationWorker();

        /// <summary>
        /// Copies one coherent odd/even-sequenced runtime-state snapshot.
        /// </summary>
        bool ReadRuntimeState(RuntimeState& state);

        /// <summary>
        /// Returns S_FALSE with an equivalent float32 media type so Windows Audio Engine can insert format conversion.
        /// </summary>
        HRESULT SuggestFloatFormat(
            IAudioMediaType* requestedFormat,
            IAudioMediaType** supportedFormat);

        /// <summary>
        /// Calculates input gain, compressor tables and five peaking-EQ coefficient sets outside APOProcess.
        /// </summary>
        void BuildProcessingConfig(
            const RuntimeState& state,
            ProcessingConfig& config);

        /// <summary>
        /// Calculates one RBJ peaking filter for the current stream sample rate.
        /// </summary>
        void BuildPeakingFilter(
            float frequency,
            float gainDb,
            BiquadCoefficients& coefficients);

        /// <summary>
        /// Clears compressor and EQ history whenever the active processing-mode flags change.
        /// </summary>
        void ResetProcessingState();

        HANDLE _configurationStopEvent;
        HANDLE _configurationThread;

        HANDLE _stateFile;
        HANDLE _stateMapping;
        RuntimeState* _runtimeState;

        ProcessingConfig* _configs[2];
        volatile LONG _activeConfigIndex;
        volatile LONG _configReaders[2];

        BiquadState* _filterStates;
        float* _frameScratch;

        UINT32 _channels;
        float _sampleRate;
        float _compressorEnvelope;
        float _levelerEnvelope;
        float _levelerGain;
        ULONG _lastProcessingFlags;

        volatile LONG64 _processCalls;
        volatile LONG64 _validCalls;
        volatile LONG64 _silentCalls;
        volatile LONG64 _invalidCalls;
        volatile LONG64 _processedFrames;

        LONG64 _lastLoggedProcessCalls;
        LONG64 _lastLoggedValidCalls;
        LONG64 _lastLoggedSilentCalls;
        LONG64 _lastLoggedInvalidCalls;
        LONG64 _lastLoggedProcessedFrames;
        ULONGLONG _lastStatisticsLogTick;
        volatile LONG _runtimeStateUnavailableLogged;
    };

    OBJECT_ENTRY_AUTO(CLSID_MaxLoudApo, CMaxLoudApo)
}
