#include "MaxLoudApo.h"

#include <ShlObj.h>
#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstring>
#include <strsafe.h>

#pragma comment(lib, "audioeng.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "uuid.lib")

const CLSID CLSID_MaxLoudApo =
{ 0x7cb491f3, 0xe5c2, 0x4f34, { 0xab, 0x14, 0x08, 0xad, 0x93, 0x3e, 0xc7, 0x7d } };

const GUID GUID_MaxLoudEffect =
{ 0x4ca86cd8, 0x33b7, 0x4473, { 0xa1, 0x07, 0x2e, 0xb2, 0x52, 0x3a, 0xe9, 0xd6 } };

namespace MaxLoudApo
{
    #pragma warning(push)
    #pragma warning(disable: 4815)
    const AVRT_DATA CRegAPOProperties<1> CMaxLoudApo::RegistrationProperties(
        CLSID_MaxLoudApo,
        L"MaxLoud Endpoint Effect",
        L"MaxLoud",
        1,
        0,
        __uuidof(IAudioProcessingObject),
        APO_FLAG_DEFAULT);
    #pragma warning(pop)

    CMaxLoudApo::CMaxLoudApo()
        : CBaseAudioProcessingObject(RegistrationProperties)
        , _configurationStopEvent(nullptr)
        , _configurationThread(nullptr)
        , _stateFile(INVALID_HANDLE_VALUE)
        , _stateMapping(nullptr)
        , _runtimeState(nullptr)
        , _activeConfigIndex(0)
        , _filterStates(nullptr)
        , _frameScratch(nullptr)
        , _channels(0)
        , _sampleRate(0.0f)
        , _compressorEnvelope(0.0f)
        , _levelerEnvelope(0.0f)
        , _levelerGain(1.0f)
        , _lastProcessingFlags(0)
        , _processCalls(0)
        , _validCalls(0)
        , _silentCalls(0)
        , _invalidCalls(0)
        , _processedFrames(0)
        , _lastLoggedProcessCalls(0)
        , _lastLoggedValidCalls(0)
        , _lastLoggedSilentCalls(0)
        , _lastLoggedInvalidCalls(0)
        , _lastLoggedProcessedFrames(0)
        , _lastStatisticsLogTick(0)
        , _runtimeStateUnavailableLogged(0)
    {
        _configs[0] = nullptr;
        _configs[1] = nullptr;
        _configReaders[0] = 0;
        _configReaders[1] = 0;

        WriteDiagnosticLog(L"CMaxLoudApo constructed instance=%p", this);
    }

    CMaxLoudApo::~CMaxLoudApo()
    {
        WriteDiagnosticLog(L"CMaxLoudApo destructor BEGIN instance=%p", this);
        StopConfigurationWorker();
        CloseRuntimeState();

        if (_filterStates != nullptr)
        {
            AERT_Free(_filterStates);
            _filterStates = nullptr;
        }

        if (_frameScratch != nullptr)
        {
            AERT_Free(_frameScratch);
            _frameScratch = nullptr;
        }

        if (_configs[0] != nullptr)
        {
            AERT_Free(_configs[0]);
            _configs[0] = nullptr;
        }

        if (_configs[1] != nullptr)
        {
            AERT_Free(_configs[1]);
            _configs[1] = nullptr;
        }

        WriteDiagnosticLog(L"CMaxLoudApo destructor END instance=%p", this);
    }

    void CMaxLoudApo::WriteDiagnosticLog(
        const wchar_t* format,
        ...)
    {
        wchar_t programData[MAX_PATH] = {};
        if (FAILED(SHGetFolderPathW(
            nullptr,
            CSIDL_COMMON_APPDATA,
            nullptr,
            SHGFP_TYPE_CURRENT,
            programData)))
        {
            return;
        }

        wchar_t maxLoudDirectory[MAX_PATH] = {};
        wchar_t logDirectory[MAX_PATH] = {};
        wchar_t logPath[MAX_PATH] = {};

        if (FAILED(StringCchPrintfW(
                maxLoudDirectory,
                ARRAYSIZE(maxLoudDirectory),
                L"%s\\MaxLoud",
                programData)) ||
            FAILED(StringCchPrintfW(
                logDirectory,
                ARRAYSIZE(logDirectory),
                L"%s\\logs",
                maxLoudDirectory)))
        {
            return;
        }

        CreateDirectoryW(maxLoudDirectory, nullptr);
        CreateDirectoryW(logDirectory, nullptr);

        SYSTEMTIME time = {};
        GetLocalTime(&time);

        if (FAILED(StringCchPrintfW(
            logPath,
            ARRAYSIZE(logPath),
            L"%s\\MaxLoudApo-%04u%02u%02u.log",
            logDirectory,
            time.wYear,
            time.wMonth,
            time.wDay)))
        {
            return;
        }

        wchar_t message[2048] = {};
        va_list arguments;
        va_start(arguments, format);
        const auto formatResult = StringCchVPrintfW(
            message,
            ARRAYSIZE(message),
            format,
            arguments);
        va_end(arguments);

        if (FAILED(formatResult))
        {
            return;
        }

        wchar_t line[2600] = {};
        if (FAILED(StringCchPrintfW(
            line,
            ARRAYSIZE(line),
            L"%04u-%02u-%02u %02u:%02u:%02u.%03u [pid=%lu tid=%lu] %s\r\n",
            time.wYear,
            time.wMonth,
            time.wDay,
            time.wHour,
            time.wMinute,
            time.wSecond,
            time.wMilliseconds,
            GetCurrentProcessId(),
            GetCurrentThreadId(),
            message)))
        {
            return;
        }

        char utf8Line[8192] = {};
        const auto utf8Length = WideCharToMultiByte(
            CP_UTF8,
            0,
            line,
            -1,
            utf8Line,
            ARRAYSIZE(utf8Line),
            nullptr,
            nullptr);

        if (utf8Length <= 1)
        {
            return;
        }

        const auto file = CreateFileW(
            logPath,
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);

        if (file == INVALID_HANDLE_VALUE)
        {
            return;
        }

        DWORD bytesWritten = 0;
        WriteFile(
            file,
            utf8Line,
            static_cast<DWORD>(utf8Length - 1),
            &bytesWritten,
            nullptr);

        CloseHandle(file);
    }

    void CMaxLoudApo::LogAudioFormat(
        const wchar_t* label,
        const UNCOMPRESSEDAUDIOFORMAT& format)
    {
        wchar_t formatGuid[64] = {};
        StringFromGUID2(
            format.guidFormatType,
            formatGuid,
            ARRAYSIZE(formatGuid));

        WriteDiagnosticLog(
            L"%s: rate=%.3f Hz channels=%lu bytesPerSample=%lu validBits=%lu format=%s",
            label,
            static_cast<double>(format.fFramesPerSecond),
            format.dwSamplesPerFrame,
            format.dwBytesPerSampleContainer,
            format.dwValidBitsPerSample,
            formatGuid);
    }

    void CMaxLoudApo::LogProcessStatistics(bool force)
    {
        const auto now = GetTickCount64();
        if (!force &&
            _lastStatisticsLogTick != 0 &&
            now - _lastStatisticsLogTick < 1000)
        {
            return;
        }

        _lastStatisticsLogTick = now;

        const auto processCalls = InterlockedCompareExchange64(&_processCalls, 0, 0);
        const auto validCalls = InterlockedCompareExchange64(&_validCalls, 0, 0);
        const auto silentCalls = InterlockedCompareExchange64(&_silentCalls, 0, 0);
        const auto invalidCalls = InterlockedCompareExchange64(&_invalidCalls, 0, 0);
        const auto processedFrames = InterlockedCompareExchange64(&_processedFrames, 0, 0);

        if (!force &&
            processCalls == _lastLoggedProcessCalls &&
            validCalls == _lastLoggedValidCalls &&
            silentCalls == _lastLoggedSilentCalls &&
            invalidCalls == _lastLoggedInvalidCalls &&
            processedFrames == _lastLoggedProcessedFrames)
        {
            return;
        }

        WriteDiagnosticLog(
            L"APOProcess stats: calls=%lld valid=%lld silent=%lld invalid=%lld frames=%lld activeBank=%ld readers=[%ld,%ld]",
            processCalls,
            validCalls,
            silentCalls,
            invalidCalls,
            processedFrames,
            InterlockedCompareExchange(&_activeConfigIndex, 0, 0),
            InterlockedCompareExchange(&_configReaders[0], 0, 0),
            InterlockedCompareExchange(&_configReaders[1], 0, 0));

        _lastLoggedProcessCalls = processCalls;
        _lastLoggedValidCalls = validCalls;
        _lastLoggedSilentCalls = silentCalls;
        _lastLoggedInvalidCalls = invalidCalls;
        _lastLoggedProcessedFrames = processedFrames;
    }

    HRESULT __fastcall CMaxLoudApo::ValidateDefaultAPOFormat(
        UNCOMPRESSEDAUDIOFORMAT& audioFormat,
        bool isInput)
    {
        LogAudioFormat(
            isInput ? L"ValidateDefaultAPOFormat input" : L"ValidateDefaultAPOFormat output",
            audioFormat);

        if (!IsEqualGUID(
            audioFormat.guidFormatType,
            KSDATAFORMAT_SUBTYPE_IEEE_FLOAT))
        {
            WriteDiagnosticLog(L"ValidateDefaultAPOFormat rejected: not IEEE float32");
            return APOERR_FORMAT_NOT_SUPPORTED;
        }

        if (audioFormat.dwBytesPerSampleContainer != sizeof(float) ||
            audioFormat.dwValidBitsPerSample != 32 ||
            audioFormat.dwSamplesPerFrame == 0 ||
            audioFormat.fFramesPerSecond <= 0.0f)
        {
            WriteDiagnosticLog(L"ValidateDefaultAPOFormat rejected: invalid float container/bit/channel/rate fields");
            return APOERR_FORMAT_NOT_SUPPORTED;
        }

        WriteDiagnosticLog(L"ValidateDefaultAPOFormat accepted");
        return S_OK;
    }

    STDMETHODIMP CMaxLoudApo::IsInputFormatSupported(
        IAudioMediaType* outputFormat,
        IAudioMediaType* requestedInputFormat,
        IAudioMediaType** supportedInputFormat)
    {
        ASSERT_NONREALTIME();

        if (requestedInputFormat == nullptr)
        {
            return CBaseAudioProcessingObject::IsInputFormatSupported(
                outputFormat,
                requestedInputFormat,
                supportedInputFormat);
        }

        UNCOMPRESSEDAUDIOFORMAT format = {};
        const auto result = requestedInputFormat->GetUncompressedAudioFormat(
            &format);

        if (FAILED(result))
        {
            WriteDiagnosticLog(L"IsInputFormatSupported GetUncompressedAudioFormat failed hr=0x%08lX", static_cast<ULONG>(result));
            return APOERR_FORMAT_NOT_SUPPORTED;
        }

        LogAudioFormat(L"IsInputFormatSupported requested", format);

        if (IsEqualGUID(
            format.guidFormatType,
            KSDATAFORMAT_SUBTYPE_IEEE_FLOAT))
        {
            const auto baseResult = CBaseAudioProcessingObject::IsInputFormatSupported(
                outputFormat,
                requestedInputFormat,
                supportedInputFormat);

            WriteDiagnosticLog(L"IsInputFormatSupported float base result=0x%08lX", static_cast<ULONG>(baseResult));
            return baseResult;
        }

        const auto suggestionResult = SuggestFloatFormat(
            requestedInputFormat,
            supportedInputFormat);

        WriteDiagnosticLog(L"IsInputFormatSupported PCM suggestion result=0x%08lX", static_cast<ULONG>(suggestionResult));
        return suggestionResult;
    }

    STDMETHODIMP CMaxLoudApo::IsOutputFormatSupported(
        IAudioMediaType* inputFormat,
        IAudioMediaType* requestedOutputFormat,
        IAudioMediaType** supportedOutputFormat)
    {
        ASSERT_NONREALTIME();

        if (requestedOutputFormat == nullptr)
        {
            return CBaseAudioProcessingObject::IsOutputFormatSupported(
                inputFormat,
                requestedOutputFormat,
                supportedOutputFormat);
        }

        UNCOMPRESSEDAUDIOFORMAT format = {};
        const auto result = requestedOutputFormat->GetUncompressedAudioFormat(
            &format);

        if (FAILED(result))
        {
            WriteDiagnosticLog(L"IsOutputFormatSupported GetUncompressedAudioFormat failed hr=0x%08lX", static_cast<ULONG>(result));
            return APOERR_FORMAT_NOT_SUPPORTED;
        }

        LogAudioFormat(L"IsOutputFormatSupported requested", format);

        if (IsEqualGUID(
            format.guidFormatType,
            KSDATAFORMAT_SUBTYPE_IEEE_FLOAT))
        {
            const auto baseResult = CBaseAudioProcessingObject::IsOutputFormatSupported(
                inputFormat,
                requestedOutputFormat,
                supportedOutputFormat);

            WriteDiagnosticLog(L"IsOutputFormatSupported float base result=0x%08lX", static_cast<ULONG>(baseResult));
            return baseResult;
        }

        const auto suggestionResult = SuggestFloatFormat(
            requestedOutputFormat,
            supportedOutputFormat);

        WriteDiagnosticLog(L"IsOutputFormatSupported PCM suggestion result=0x%08lX", static_cast<ULONG>(suggestionResult));
        return suggestionResult;
    }

    HRESULT CMaxLoudApo::SuggestFloatFormat(
        IAudioMediaType* requestedFormat,
        IAudioMediaType** supportedFormat)
    {
        ASSERT_NONREALTIME();

        if (requestedFormat == nullptr)
        {
            return E_POINTER;
        }

        if (supportedFormat != nullptr)
        {
            *supportedFormat = nullptr;
        }

        UNCOMPRESSEDAUDIOFORMAT format = {};
        auto result = requestedFormat->GetUncompressedAudioFormat(
            &format);

        if (FAILED(result) ||
            format.dwSamplesPerFrame == 0 ||
            format.fFramesPerSecond <= 0.0f)
        {
            return APOERR_FORMAT_NOT_SUPPORTED;
        }

        format.guidFormatType = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        format.dwBytesPerSampleContainer = sizeof(float);
        format.dwValidBitsPerSample = 32;
        LogAudioFormat(L"SuggestFloatFormat result", format);

        if (supportedFormat != nullptr)
        {
            result = CreateAudioMediaTypeFromUncompressedAudioFormat(
                &format,
                supportedFormat);

            if (FAILED(result))
            {
                return result;
            }
        }

        return S_FALSE;
    }

    STDMETHODIMP CMaxLoudApo::LockForProcess(
        UINT32 inputConnectionCount,
        APO_CONNECTION_DESCRIPTOR** inputConnections,
        UINT32 outputConnectionCount,
        APO_CONNECTION_DESCRIPTOR** outputConnections)
    {
        ASSERT_NONREALTIME();

        WriteDiagnosticLog(
            L"LockForProcess BEGIN inputs=%u outputs=%u",
            inputConnectionCount,
            outputConnectionCount);

        auto result = CBaseAudioProcessingObject::LockForProcess(
            inputConnectionCount,
            inputConnections,
            outputConnectionCount,
            outputConnections);

        if (FAILED(result))
        {
            WriteDiagnosticLog(L"LockForProcess base failed hr=0x%08lX", static_cast<ULONG>(result));
            return result;
        }

        _channels = GetSamplesPerFrame();
        _sampleRate = GetFramesPerSecond();

        WriteDiagnosticLog(
            L"LockForProcess negotiated rate=%.3f Hz channels=%u",
            static_cast<double>(_sampleRate),
            _channels);

        if (_channels == 0 || _sampleRate <= 0.0f)
        {
            CBaseAudioProcessingObject::UnlockForProcess();
            return E_INVALIDARG;
        }

        const auto stateCount = static_cast<size_t>(_channels) * 5;

        result = AERT_Allocate(
            sizeof(BiquadState) * stateCount,
            reinterpret_cast<void**>(&_filterStates));

        if (FAILED(result))
        {
            WriteDiagnosticLog(
                L"LockForProcess AERT_Allocate filterStates failed bytes=%zu hr=0x%08lX",
                sizeof(BiquadState) * stateCount,
                static_cast<ULONG>(result));
            CBaseAudioProcessingObject::UnlockForProcess();
            return result;
        }

        result = AERT_Allocate(
            sizeof(float) * _channels,
            reinterpret_cast<void**>(&_frameScratch));

        if (FAILED(result))
        {
            WriteDiagnosticLog(
                L"LockForProcess AERT_Allocate frameScratch failed bytes=%zu hr=0x%08lX",
                sizeof(float) * static_cast<size_t>(_channels),
                static_cast<ULONG>(result));
            AERT_Free(_filterStates);
            _filterStates = nullptr;
            CBaseAudioProcessingObject::UnlockForProcess();
            return result;
        }

        for (auto index = 0u; index < 2; index++)
        {
            result = AERT_Allocate(
                sizeof(ProcessingConfig),
                reinterpret_cast<void**>(&_configs[index]));

            if (FAILED(result))
            {
                WriteDiagnosticLog(
                    L"LockForProcess AERT_Allocate config[%u] failed bytes=%zu hr=0x%08lX",
                    index,
                    sizeof(ProcessingConfig),
                    static_cast<ULONG>(result));
                if (_configs[0] != nullptr)
                {
                    AERT_Free(_configs[0]);
                    _configs[0] = nullptr;
                }

                if (_configs[1] != nullptr)
                {
                    AERT_Free(_configs[1]);
                    _configs[1] = nullptr;
                }

                AERT_Free(_frameScratch);
                _frameScratch = nullptr;

                AERT_Free(_filterStates);
                _filterStates = nullptr;

                CBaseAudioProcessingObject::UnlockForProcess();
                return result;
            }
        }

        WriteDiagnosticLog(
            L"LockForProcess RT allocations ready: stateBytes=%zu scratchBytes=%zu configBytes=%zu x2",
            sizeof(BiquadState) * stateCount,
            sizeof(float) * static_cast<size_t>(_channels),
            sizeof(ProcessingConfig));

        std::memset(
            _filterStates,
            0,
            sizeof(BiquadState) * stateCount);

        std::memset(
            _frameScratch,
            0,
            sizeof(float) * _channels);

        RuntimeState initialState = {};
        initialState.Magic = StateMagic;
        initialState.Version = StateVersion;
        initialState.Flags = 0;
        initialState.ThresholdDb = -20.0f;
        initialState.Ratio = 4.0f;
        initialState.AttackMs = 10.0f;
        initialState.ReleaseMs = 350.0f;
        initialState.VolumeLeveling = 0.0f;
        initialState.InputGainDb = 4.0f;
        initialState.LimiterCeilingDb = -1.0f;

        BuildProcessingConfig(
            initialState,
            *_configs[0]);

        *_configs[1] = *_configs[0];
        InterlockedExchange(&_activeConfigIndex, 0);
        InterlockedExchange(&_configReaders[0], 0);
        InterlockedExchange(&_configReaders[1], 0);
        _lastProcessingFlags = 0;

        result = StartConfigurationWorker();
        if (FAILED(result))
        {
            UnlockForProcess();
            return result;
        }

        ResetProcessingState();
        WriteDiagnosticLog(L"LockForProcess END success");
        return S_OK;
    }

    STDMETHODIMP CMaxLoudApo::UnlockForProcess()
    {
        ASSERT_NONREALTIME();

        WriteDiagnosticLog(L"UnlockForProcess BEGIN");
        LogProcessStatistics(true);
        StopConfigurationWorker();
        CloseRuntimeState();

        if (_filterStates != nullptr)
        {
            AERT_Free(_filterStates);
            _filterStates = nullptr;
        }

        if (_frameScratch != nullptr)
        {
            AERT_Free(_frameScratch);
            _frameScratch = nullptr;
        }

        for (auto index = 0u; index < 2; index++)
        {
            if (_configs[index] != nullptr)
            {
                AERT_Free(_configs[index]);
                _configs[index] = nullptr;
            }
        }

        _channels = 0;
        _sampleRate = 0.0f;

        const auto result = CBaseAudioProcessingObject::UnlockForProcess();
        WriteDiagnosticLog(L"UnlockForProcess END hr=0x%08lX", static_cast<ULONG>(result));
        return result;
    }

    STDMETHODIMP CMaxLoudApo::Initialize(
        UINT32 dataSize,
        BYTE* data)
    {
        ASSERT_NONREALTIME();

        WriteDiagnosticLog(
            L"Initialize BEGIN dataSize=%u data=%p",
            dataSize,
            data);

        if ((data == nullptr && dataSize != 0) ||
            (data != nullptr && dataSize == 0))
        {
            WriteDiagnosticLog(L"Initialize rejected: inconsistent data pointer/size");
            return E_INVALIDARG;
        }

        if (data == nullptr && dataSize == 0)
        {
            const auto result =
                CBaseAudioProcessingObject::Initialize(0, nullptr);

            WriteDiagnosticLog(
                L"Initialize END base/no-data hr=0x%08lX",
                static_cast<ULONG>(result));
            return result;
        }

        if (dataSize < sizeof(APOInitBaseStruct))
        {
            WriteDiagnosticLog(L"Initialize rejected: payload smaller than APOInitBaseStruct");
            return E_INVALIDARG;
        }

        const auto* base =
            reinterpret_cast<const APOInitBaseStruct*>(data);

        wchar_t initClsid[64] = {};
        StringFromGUID2(
            base->clsid,
            initClsid,
            ARRAYSIZE(initClsid));

        WriteDiagnosticLog(
            L"Initialize APOInitBaseStruct cbSize=%u clsid=%s",
            base->cbSize,
            initClsid);

        if (!IsEqualGUID(base->clsid, CLSID_MaxLoudApo))
        {
            WriteDiagnosticLog(L"Initialize rejected: CLSID mismatch");
            return E_INVALIDARG;
        }

        if (base->cbSize < sizeof(APOInitBaseStruct) ||
            base->cbSize > dataSize)
        {
            WriteDiagnosticLog(
                L"Initialize rejected: invalid embedded cbSize=%u for payload=%u",
                base->cbSize,
                dataSize);
            return E_INVALIDARG;
        }

        if (dataSize == sizeof(APOInitSystemEffects2))
        {
            const auto* systemEffects =
                reinterpret_cast<const APOInitSystemEffects2*>(data);

            wchar_t mode[64] = {};
            StringFromGUID2(
                systemEffects->AudioProcessingMode,
                mode,
                ARRAYSIZE(mode));

            WriteDiagnosticLog(
                L"Initialize payload=APOInitSystemEffects2 mode=%s discoveryOnly=%d",
                mode,
                systemEffects->InitializeForDiscoveryOnly ? 1 : 0);
        }
        else if (dataSize == sizeof(APOInitSystemEffects))
        {
            WriteDiagnosticLog(L"Initialize payload=APOInitSystemEffects");
        }
        else
        {
            WriteDiagnosticLog(
                L"Initialize payload size=%u accepted as system-effects extension with valid base header",
                dataSize);
        }

        // CBaseAudioProcessingObject's default Initialize implementation expects no custom
        // initialization payload. Windows system-effects APOs, however, are initialized with
        // APOInitSystemEffects* structures. We validate that system payload above, then let the
        // base class initialize its own state using the documented no-custom-data path.
        const auto result =
            CBaseAudioProcessingObject::Initialize(0, nullptr);

        WriteDiagnosticLog(
            L"Initialize END hr=0x%08lX",
            static_cast<ULONG>(result));
        return result;
    }

#pragma AVRT_CODE_BEGIN

    STDMETHODIMP_(void) CMaxLoudApo::APOProcess(
        UINT32 inputConnectionCount,
        APO_CONNECTION_PROPERTY** inputConnections,
        UINT32 outputConnectionCount,
        APO_CONNECTION_PROPERTY** outputConnections)
    {
        UNREFERENCED_PARAMETER(inputConnectionCount);
        UNREFERENCED_PARAMETER(outputConnectionCount);

        InterlockedIncrement64(&_processCalls);

        auto* input = inputConnections[0];
        auto* output = outputConnections[0];

        if (input->u32BufferFlags == BUFFER_INVALID)
        {
            InterlockedIncrement64(&_invalidCalls);
            output->u32BufferFlags = BUFFER_INVALID;
            output->u32ValidFrameCount = 0;
            return;
        }

        const auto frameCount = input->u32ValidFrameCount;
        InterlockedExchangeAdd64(
            &_processedFrames,
            static_cast<LONG64>(frameCount));

        auto* inputSamples = reinterpret_cast<float*>(input->pBuffer);
        auto* outputSamples = reinterpret_cast<float*>(output->pBuffer);

        if (input->u32BufferFlags == BUFFER_SILENT)
        {
            InterlockedIncrement64(&_silentCalls);

            if (outputSamples != inputSamples)
            {
                const auto sampleCount =
                    frameCount * _channels;

                for (auto sampleIndex = 0u;
                     sampleIndex < sampleCount;
                     sampleIndex++)
                {
                    outputSamples[sampleIndex] = 0.0f;
                }
            }

            output->u32BufferFlags = BUFFER_SILENT;
            output->u32ValidFrameCount = frameCount;
            return;
        }

        InterlockedIncrement64(&_validCalls);

        ULONG configIndex;

        for (;;)
        {
            configIndex =
                static_cast<ULONG>(InterlockedCompareExchange(
                    &_activeConfigIndex,
                    0,
                    0));

            InterlockedIncrement(
                &_configReaders[configIndex]);

            const auto confirmedIndex =
                static_cast<ULONG>(InterlockedCompareExchange(
                    &_activeConfigIndex,
                    0,
                    0));

            if (configIndex == confirmedIndex)
            {
                break;
            }

            InterlockedDecrement(
                &_configReaders[configIndex]);
        }

        const auto* config = _configs[configIndex];
        const auto processingFlags =
            config->Flags &
            (FlagMaxLoudEnabled |
             FlagCompressorEnabled |
             FlagEqualizerEnabled |
             FlagLimiterEnabled);

        const auto processingEnabled =
            (processingFlags & FlagMaxLoudEnabled) != 0;

        if (processingFlags != _lastProcessingFlags)
        {
            ResetProcessingState();
            _lastProcessingFlags = processingFlags;
        }

        if (!processingEnabled)
        {
            if (outputSamples != inputSamples)
            {
                const auto sampleCount =
                    frameCount * _channels;

                for (auto sampleIndex = 0u;
                     sampleIndex < sampleCount;
                     sampleIndex++)
                {
                    outputSamples[sampleIndex] =
                        inputSamples[sampleIndex];
                }
            }

            output->u32BufferFlags = BUFFER_VALID;
            output->u32ValidFrameCount = frameCount;

            InterlockedDecrement(
                &_configReaders[configIndex]);

            return;
        }

        const auto compressorEnabled =
            (config->Flags & FlagCompressorEnabled) != 0;

        const auto equalizerEnabled =
            (config->Flags & FlagEqualizerEnabled) != 0;

        const auto limiterEnabled =
            (config->Flags & FlagLimiterEnabled) != 0;

        for (auto frame = 0u; frame < frameCount; frame++)
        {
            auto rawPower = 0.0f;

            for (auto channel = 0u; channel < _channels; channel++)
            {
                const auto sample =
                    inputSamples[frame * _channels + channel];

                rawPower += sample * sample;
            }

            rawPower /= static_cast<float>(_channels);

            const auto envelopeCoefficient =
                rawPower > _levelerEnvelope
                    ? config->LevelerEnvelopeAttackCoefficient
                    : config->LevelerEnvelopeReleaseCoefficient;

            // Keep the RMS detector warm even when Strength is 0%. Enabling the leveler then
            // starts from the real current loudness instead of waiting for a stale 0 dB envelope.
            _levelerEnvelope =
                envelopeCoefficient * _levelerEnvelope +
                (1.0f - envelopeCoefficient) * rawPower;

            auto levelerGain = 1.0f;

            if (config->LevelerAmount > 0.0001f)
            {
                auto levelerRms =
                    static_cast<float>(
                        std::sqrt(
                            std::max(
                                static_cast<double>(_levelerEnvelope),
                                0.0)));

                if (levelerRms > 1.0f)
                {
                    levelerRms = 1.0f;
                }

                const auto tablePosition =
                    levelerRms * 2048.0f;

                auto tableIndex =
                    static_cast<UINT32>(tablePosition);

                if (tableIndex >= 2048)
                {
                    tableIndex = 2047;
                }

                const auto fraction =
                    tablePosition -
                    static_cast<float>(tableIndex);

                const auto gain0 =
                    config->LevelerGainTable[tableIndex];

                const auto gain1 =
                    config->LevelerGainTable[tableIndex + 1];

                const auto desiredLevelerGain =
                    gain0 +
                    (gain1 - gain0) * fraction;

                const auto levelerGainCoefficient =
                    desiredLevelerGain > _levelerGain
                        ? config->LevelerGainRiseCoefficient
                        : config->LevelerGainFallCoefficient;

                _levelerGain =
                    levelerGainCoefficient * _levelerGain +
                    (1.0f - levelerGainCoefficient) * desiredLevelerGain;

                levelerGain = _levelerGain;
            }
            else
            {
                _levelerGain = 1.0f;
            }

            auto peak = 0.0f;

            for (auto channel = 0u; channel < _channels; channel++)
            {
                auto sample =
                    inputSamples[frame * _channels + channel] *
                    levelerGain;

                if (compressorEnabled)
                {
                    sample *= config->InputGainLinear;
                }

                if (equalizerEnabled)
                {
                    for (auto band = 0u; band < 5; band++)
                    {
                        auto& state =
                            _filterStates[channel * 5 + band];

                        const auto& coefficients =
                            config->Eq[band];

                        const auto filtered =
                            coefficients.B0 * sample +
                            state.Z1;

                        state.Z1 =
                            coefficients.B1 * sample -
                            coefficients.A1 * filtered +
                            state.Z2;

                        state.Z2 =
                            coefficients.B2 * sample -
                            coefficients.A2 * filtered;

                        sample = filtered;
                    }
                }

                _frameScratch[channel] = sample;

                const auto absoluteSample =
                    sample < 0.0f ? -sample : sample;

                if (absoluteSample > peak)
                {
                    peak = absoluteSample;
                }
            }

            auto gain = 1.0f;

            if (compressorEnabled)
            {
                const auto detectorCoefficient =
                    peak > _compressorEnvelope
                        ? config->AttackCoefficient
                        : config->ReleaseCoefficient;

                _compressorEnvelope =
                    detectorCoefficient * _compressorEnvelope +
                    (1.0f - detectorCoefficient) * peak;

                auto envelope =
                    _compressorEnvelope;

                if (envelope < 0.0f)
                {
                    envelope = 0.0f;
                }
                else if (envelope > 1.0f)
                {
                    envelope = 1.0f;
                }

                const auto tablePosition =
                    envelope * 2048.0f;

                auto tableIndex =
                    static_cast<UINT32>(tablePosition);

                if (tableIndex >= 2048)
                {
                    tableIndex = 2047;
                }

                const auto fraction =
                    tablePosition -
                    static_cast<float>(tableIndex);

                const auto gain0 =
                    config->CompressorGainTable[tableIndex];

                const auto gain1 =
                    config->CompressorGainTable[tableIndex + 1];

                gain =
                    gain0 +
                    (gain1 - gain0) * fraction;
            }

            if (limiterEnabled)
            {
                const auto processedPeak =
                    peak * gain;

                if (processedPeak >
                    config->LimiterCeilingLinear &&
                    processedPeak > 0.0f)
                {
                    gain *=
                        config->LimiterCeilingLinear /
                        processedPeak;
                }
            }

            for (auto channel = 0u; channel < _channels; channel++)
            {
                outputSamples[frame * _channels + channel] =
                    _frameScratch[channel] * gain;
            }
        }

        output->u32BufferFlags = BUFFER_VALID;
        output->u32ValidFrameCount = frameCount;

        InterlockedDecrement(
            &_configReaders[configIndex]);
    }

#pragma AVRT_CODE_END

    STDMETHODIMP CMaxLoudApo::GetEffectsList(
        LPGUID* effectIds,
        UINT* effectCount,
        HANDLE eventHandle)
    {
        ASSERT_NONREALTIME();
        UNREFERENCED_PARAMETER(eventHandle);

        if (effectIds == nullptr ||
            effectCount == nullptr)
        {
            return E_POINTER;
        }

        *effectIds = static_cast<LPGUID>(
            CoTaskMemAlloc(sizeof(GUID)));

        if (*effectIds == nullptr)
        {
            *effectCount = 0;
            return E_OUTOFMEMORY;
        }

        (*effectIds)[0] = GUID_MaxLoudEffect;
        *effectCount = 1;
        return S_OK;
    }

    DWORD WINAPI CMaxLoudApo::ConfigurationThreadProc(
        void* context)
    {
        static_cast<CMaxLoudApo*>(context)->
            ConfigurationThreadLoop();

        return 0;
    }

    void CMaxLoudApo::ConfigurationThreadLoop()
    {
        LONG lastSequence = -1;

        WriteDiagnosticLog(L"Configuration worker START");

        for (;;)
        {
            if (WaitForSingleObject(
                _configurationStopEvent,
                50) == WAIT_OBJECT_0)
            {
                LogProcessStatistics(true);
                WriteDiagnosticLog(L"Configuration worker STOP requested");
                return;
            }

            LogProcessStatistics(false);

            if (_runtimeState == nullptr)
            {
                OpenRuntimeState();
            }

            RuntimeState state = {};
            if (!ReadRuntimeState(state))
            {
                continue;
            }

            if (state.Sequence == lastSequence)
            {
                continue;
            }

            lastSequence = state.Sequence;

            WriteDiagnosticLog(
                L"Runtime state applied: sequence=%ld flags=0x%08lX leveling=%.1f threshold=%.2f ratio=%.2f attack=%.2f release=%.2f inputGain=%.2f limiter=%.2f eq=[%.2f,%.2f,%.2f,%.2f,%.2f]",
                state.Sequence,
                state.Flags,
                static_cast<double>(state.VolumeLeveling),
                static_cast<double>(state.ThresholdDb),
                static_cast<double>(state.Ratio),
                static_cast<double>(state.AttackMs),
                static_cast<double>(state.ReleaseMs),
                static_cast<double>(state.InputGainDb),
                static_cast<double>(state.LimiterCeilingDb),
                static_cast<double>(state.Eq80Db),
                static_cast<double>(state.Eq250Db),
                static_cast<double>(state.Eq1000Db),
                static_cast<double>(state.Eq3000Db),
                static_cast<double>(state.Eq8000Db));

            const auto activeIndex =
                InterlockedCompareExchange(
                    &_activeConfigIndex,
                    0,
                    0);

            const auto inactiveIndex =
                activeIndex == 0 ? 1 : 0;

            while (InterlockedCompareExchange(
                &_configReaders[inactiveIndex],
                0,
                0) != 0)
            {
                if (WaitForSingleObject(
                    _configurationStopEvent,
                    1) == WAIT_OBJECT_0)
                {
                    return;
                }
            }

            BuildProcessingConfig(
                state,
                *_configs[inactiveIndex]);

            InterlockedExchange(
                &_activeConfigIndex,
                inactiveIndex);
        }
    }

    bool CMaxLoudApo::OpenRuntimeState()
    {
        ASSERT_NONREALTIME();

        if (_runtimeState != nullptr)
        {
            return true;
        }

        const auto logOpenFailure = [this](const wchar_t* step, DWORD error)
        {
            if (InterlockedCompareExchange(
                &_runtimeStateUnavailableLogged,
                1,
                0) == 0)
            {
                WriteDiagnosticLog(
                    L"Runtime state open failed at %s, Win32=%lu",
                    step,
                    error);
            }
        };

        wchar_t programData[MAX_PATH] = {};
        if (FAILED(SHGetFolderPathW(
            nullptr,
            CSIDL_COMMON_APPDATA,
            nullptr,
            SHGFP_TYPE_CURRENT,
            programData)))
        {
            logOpenFailure(L"SHGetFolderPathW", GetLastError());
            return false;
        }

        wchar_t statePath[MAX_PATH] = {};
        if (FAILED(StringCchPrintfW(
            statePath,
            ARRAYSIZE(statePath),
            L"%s\\MaxLoud\\state.bin",
            programData)))
        {
            return false;
        }

        _stateFile = CreateFileW(
            statePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);

        if (_stateFile == INVALID_HANDLE_VALUE)
        {
            logOpenFailure(L"CreateFileW", GetLastError());
            return false;
        }

        _stateMapping = CreateFileMappingW(
            _stateFile,
            nullptr,
            PAGE_READONLY,
            0,
            sizeof(RuntimeState),
            nullptr);

        if (_stateMapping == nullptr)
        {
            const auto error = GetLastError();
            logOpenFailure(L"CreateFileMappingW", error);
            CloseHandle(_stateFile);
            _stateFile = INVALID_HANDLE_VALUE;
            return false;
        }

        _runtimeState =
            static_cast<RuntimeState*>(
                MapViewOfFile(
                    _stateMapping,
                    FILE_MAP_READ,
                    0,
                    0,
                    sizeof(RuntimeState)));

        if (_runtimeState == nullptr)
        {
            const auto error = GetLastError();
            logOpenFailure(L"MapViewOfFile", error);
            CloseHandle(_stateMapping);
            _stateMapping = nullptr;

            CloseHandle(_stateFile);
            _stateFile = INVALID_HANDLE_VALUE;

            return false;
        }

        InterlockedExchange(&_runtimeStateUnavailableLogged, 0);
        WriteDiagnosticLog(L"Runtime state mapped successfully: %s", statePath);
        return true;
    }

    void CMaxLoudApo::CloseRuntimeState()
    {
        ASSERT_NONREALTIME();

        if (_runtimeState != nullptr)
        {
            UnmapViewOfFile(_runtimeState);
            _runtimeState = nullptr;
        }

        if (_stateMapping != nullptr)
        {
            CloseHandle(_stateMapping);
            _stateMapping = nullptr;
        }

        if (_stateFile != INVALID_HANDLE_VALUE)
        {
            CloseHandle(_stateFile);
            _stateFile = INVALID_HANDLE_VALUE;
        }
    }

    HRESULT CMaxLoudApo::StartConfigurationWorker()
    {
        ASSERT_NONREALTIME();
        WriteDiagnosticLog(L"StartConfigurationWorker BEGIN");

        _configurationStopEvent =
            CreateEventW(
                nullptr,
                TRUE,
                FALSE,
                nullptr);

        if (_configurationStopEvent == nullptr)
        {
            const auto error = GetLastError();
            WriteDiagnosticLog(L"StartConfigurationWorker CreateEventW failed Win32=%lu", error);
            return HRESULT_FROM_WIN32(error);
        }

        OpenRuntimeState();

        _configurationThread =
            CreateThread(
                nullptr,
                0,
                ConfigurationThreadProc,
                this,
                0,
                nullptr);

        if (_configurationThread == nullptr)
        {
            const auto error = GetLastError();
            WriteDiagnosticLog(L"StartConfigurationWorker CreateThread failed Win32=%lu", error);

            CloseHandle(
                _configurationStopEvent);

            _configurationStopEvent = nullptr;

            return HRESULT_FROM_WIN32(error);
        }

        WriteDiagnosticLog(L"StartConfigurationWorker END success thread=%p", _configurationThread);
        return S_OK;
    }

    void CMaxLoudApo::StopConfigurationWorker()
    {
        ASSERT_NONREALTIME();

        if (_configurationThread != nullptr ||
            _configurationStopEvent != nullptr)
        {
            WriteDiagnosticLog(L"StopConfigurationWorker BEGIN");
        }

        if (_configurationStopEvent != nullptr)
        {
            SetEvent(_configurationStopEvent);
        }

        if (_configurationThread != nullptr)
        {
            WaitForSingleObject(
                _configurationThread,
                INFINITE);

            CloseHandle(_configurationThread);
            _configurationThread = nullptr;
        }

        if (_configurationStopEvent != nullptr)
        {
            CloseHandle(_configurationStopEvent);
            _configurationStopEvent = nullptr;
        }

        WriteDiagnosticLog(L"StopConfigurationWorker END");
    }

    bool CMaxLoudApo::ReadRuntimeState(
        RuntimeState& state)
    {
        if (_runtimeState == nullptr)
        {
            return false;
        }

        const auto sequenceBefore =
            _runtimeState->Sequence;

        if ((sequenceBefore & 1) != 0)
        {
            return false;
        }

        MemoryBarrier();
        std::memcpy(
            &state,
            _runtimeState,
            sizeof(RuntimeState));
        MemoryBarrier();

        const auto sequenceAfter =
            _runtimeState->Sequence;

        if (sequenceBefore != sequenceAfter ||
            (sequenceAfter & 1) != 0)
        {
            return false;
        }

        if (state.Magic != StateMagic ||
            state.Version != StateVersion)
        {
            return false;
        }

        return true;
    }

    void CMaxLoudApo::BuildProcessingConfig(
        const RuntimeState& state,
        ProcessingConfig& config)
    {
        ASSERT_NONREALTIME();

        config.Flags = state.Flags;

        const auto attackMs =
            std::max(state.AttackMs, 0.1f);

        const auto releaseMs =
            std::max(state.ReleaseMs, 0.1f);

        config.AttackCoefficient =
            static_cast<float>(
                std::exp(
                    -1.0 /
                    (0.001 *
                     static_cast<double>(attackMs) *
                     _sampleRate)));

        config.ReleaseCoefficient =
            static_cast<float>(
                std::exp(
                    -1.0 /
                    (0.001 *
                     static_cast<double>(releaseMs) *
                     _sampleRate)));

        config.InputGainLinear =
            static_cast<float>(
                std::pow(
                    10.0,
                    state.InputGainDb / 20.0));

        config.LevelerAmount =
            std::max(
                0.0f,
                std::min(
                    state.VolumeLeveling / 100.0f,
                    1.0f));

        const auto normalizedFloor =
            std::max(
                0.01,
                1.0 -
                static_cast<double>(config.LevelerAmount));

        const auto maximumLevelerBoostDb =
            -20.0 * std::log10(normalizedFloor);

        // Volume Leveling is intentionally upward-only. It must never make a 100% player
        // signal quieter than bypass. The compressor and limiter remain responsible for
        // controlling excessive peaks after the leveler has raised quiet material.
        const auto levelerTargetDb =
            static_cast<double>(state.LimiterCeilingDb) - 6.0;

        config.LevelerTargetLinear =
            static_cast<float>(
                std::pow(10.0, levelerTargetDb / 20.0));

        config.LevelerMaximumGainLinear =
            static_cast<float>(
                std::pow(
                    10.0,
                    maximumLevelerBoostDb / 20.0));

        // When the input becomes louder, the detector and gain reduction must react almost
        // immediately so boosted audio cannot burst through before the leveler catches up.
        config.LevelerEnvelopeAttackCoefficient =
            static_cast<float>(
                std::exp(
                    -1.0 /
                    (0.001 * 0.25 * _sampleRate)));

        // Falling loudness is intentionally much slower. This prevents quiet tails after a
        // loud passage from being boosted too quickly and producing audible pumping.
        config.LevelerEnvelopeReleaseCoefficient =
            static_cast<float>(
                std::exp(
                    -1.0 /
                    (0.001 * 1200.0 * _sampleRate)));

        config.LevelerGainRiseCoefficient =
            static_cast<float>(
                std::exp(
                    -1.0 /
                    (0.001 * 1800.0 * _sampleRate)));

        config.LevelerGainFallCoefficient =
            static_cast<float>(
                std::exp(
                    -1.0 /
                    (0.001 * 0.25 * _sampleRate)));

        config.LimiterCeilingLinear =
            static_cast<float>(
                std::pow(
                    10.0,
                    state.LimiterCeilingDb / 20.0));

        const float frequencies[5] =
        {
            80.0f,
            250.0f,
            1000.0f,
            3000.0f,
            8000.0f
        };

        const float gains[5] =
        {
            state.Eq80Db,
            state.Eq250Db,
            state.Eq1000Db,
            state.Eq3000Db,
            state.Eq8000Db
        };

        for (auto band = 0u; band < 5; band++)
        {
            BuildPeakingFilter(
                frequencies[band],
                gains[band],
                config.Eq[band]);
        }

        const auto ratio =
            std::max(state.Ratio, 1.0f);

        for (auto index = 0u; index <= 2048; index++)
        {
            const auto level =
                static_cast<double>(index) /
                2048.0;

            const auto levelDb =
                20.0 *
                std::log10(
                    std::max(
                        level,
                        1.0e-9));

            auto levelerGain = 1.0;

            if (config.LevelerAmount > 0.0001f &&
                level >= 0.0005623413251903491)
            {
                auto correctionDb =
                    levelerTargetDb - levelDb;

                correctionDb =
                    std::min(
                        maximumLevelerBoostDb,
                        std::max(
                            0.0,
                            correctionDb));

                levelerGain =
                    std::pow(
                        10.0,
                        correctionDb / 20.0);
            }

            config.LevelerGainTable[index] =
                static_cast<float>(levelerGain);

            auto gainReductionDb = 0.0;

            if (levelDb > state.ThresholdDb)
            {
                const auto compressedDb =
                    state.ThresholdDb +
                    (levelDb - state.ThresholdDb) /
                    ratio;

                gainReductionDb =
                    compressedDb - levelDb;
            }

            config.CompressorGainTable[index] =
                static_cast<float>(
                    std::pow(
                        10.0,
                        gainReductionDb / 20.0));
        }
    }

    void CMaxLoudApo::BuildPeakingFilter(
        float frequency,
        float gainDb,
        BiquadCoefficients& coefficients)
    {
        ASSERT_NONREALTIME();

        const auto nyquist =
            _sampleRate * 0.5f;

        if (frequency >= nyquist)
        {
            coefficients.B0 = 1.0f;
            coefficients.B1 = 0.0f;
            coefficients.B2 = 0.0f;
            coefficients.A1 = 0.0f;
            coefficients.A2 = 0.0f;
            return;
        }

        const auto q = 0.9;
        const auto a =
            std::pow(
                10.0,
                gainDb / 40.0);

        const auto omega =
            2.0 *
            3.14159265358979323846 *
            frequency /
            _sampleRate;

        const auto alpha =
            std::sin(omega) /
            (2.0 * q);

        const auto cosOmega =
            std::cos(omega);

        const auto b0 =
            1.0 + alpha * a;

        const auto b1 =
            -2.0 * cosOmega;

        const auto b2 =
            1.0 - alpha * a;

        const auto a0 =
            1.0 + alpha / a;

        const auto a1 =
            -2.0 * cosOmega;

        const auto a2 =
            1.0 - alpha / a;

        coefficients.B0 =
            static_cast<float>(b0 / a0);

        coefficients.B1 =
            static_cast<float>(b1 / a0);

        coefficients.B2 =
            static_cast<float>(b2 / a0);

        coefficients.A1 =
            static_cast<float>(a1 / a0);

        coefficients.A2 =
            static_cast<float>(a2 / a0);
    }

    void CMaxLoudApo::ResetProcessingState()
    {
        if (_filterStates != nullptr)
        {
            const auto stateCount =
                _channels * 5;

            for (auto stateIndex = 0u;
                 stateIndex < stateCount;
                 stateIndex++)
            {
                _filterStates[stateIndex].Z1 = 0.0f;
                _filterStates[stateIndex].Z2 = 0.0f;
            }
        }

        _compressorEnvelope = 0.0f;
        _levelerEnvelope = 0.0f;
        _levelerGain = 1.0f;
    }
}
