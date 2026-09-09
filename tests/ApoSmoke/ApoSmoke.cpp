#include <Windows.h>
#include <audioenginebaseapo.h>
#include <audiomediatype.h>
#include <mmreg.h>

#include <cmath>
#include <iostream>
#include <vector>

namespace
{
    const CLSID ClsidMaxLoudApo =
    { 0x7cb491f3, 0xe5c2, 0x4f34, { 0xab, 0x14, 0x08, 0xad, 0x93, 0x3e, 0xc7, 0x7d } };

    using DllGetClassObjectFn = HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, LPVOID*);

    void ThrowIfFailed(HRESULT result, const char* operation)
    {
        if (FAILED(result))
        {
            std::cerr << operation << " failed: 0x" << std::hex
                      << static_cast<unsigned long>(result) << std::dec << std::endl;
            ExitProcess(1);
        }
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2)
    {
        std::wcerr << L"Usage: ApoSmoke.exe <MaxLoudApo.dll>" << std::endl;
        return 2;
    }

    ThrowIfFailed(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "CoInitializeEx");

    auto module = LoadLibraryW(argv[1]);
    if (module == nullptr)
    {
        std::wcerr << L"LoadLibraryW failed: " << GetLastError() << std::endl;
        CoUninitialize();
        return 1;
    }

    auto dllGetClassObject = reinterpret_cast<DllGetClassObjectFn>(
        GetProcAddress(module, "DllGetClassObject"));

    if (dllGetClassObject == nullptr)
    {
        std::cerr << "DllGetClassObject export is missing." << std::endl;
        FreeLibrary(module);
        CoUninitialize();
        return 1;
    }

    IClassFactory* factory = nullptr;
    ThrowIfFailed(
        dllGetClassObject(
            ClsidMaxLoudApo,
            IID_IClassFactory,
            reinterpret_cast<void**>(&factory)),
        "DllGetClassObject");

    IAudioProcessingObject* apo = nullptr;
    ThrowIfFailed(
        factory->CreateInstance(
            nullptr,
            __uuidof(IAudioProcessingObject),
            reinterpret_cast<void**>(&apo)),
        "IClassFactory::CreateInstance");

    factory->Release();

    APOInitSystemEffects2 systemEffectsInitialization = {};
    systemEffectsInitialization.APOInit.cbSize = sizeof(systemEffectsInitialization);
    systemEffectsInitialization.APOInit.clsid = ClsidMaxLoudApo;
    systemEffectsInitialization.AudioProcessingMode = GUID_NULL;
    systemEffectsInitialization.InitializeForDiscoveryOnly = FALSE;

    ThrowIfFailed(
        apo->Initialize(
            sizeof(systemEffectsInitialization),
            reinterpret_cast<BYTE*>(&systemEffectsInitialization)),
        "IAudioProcessingObject::Initialize(APOInitSystemEffects2)");

    std::cout << "System-effects initialization: APOInitSystemEffects2 accepted." << std::endl;

    IAudioSystemEffects2* effects = nullptr;
    ThrowIfFailed(
        apo->QueryInterface(
            __uuidof(IAudioSystemEffects2),
            reinterpret_cast<void**>(&effects)),
        "QueryInterface(IAudioSystemEffects2)");

    LPGUID effectIds = nullptr;
    UINT effectCount = 0;
    ThrowIfFailed(
        effects->GetEffectsList(
            &effectIds,
            &effectCount,
            nullptr),
        "IAudioSystemEffects2::GetEffectsList");

    if (effectCount != 1 || effectIds == nullptr)
    {
        std::cerr << "Unexpected effect-list size." << std::endl;
        effects->Release();
        apo->Release();
        FreeLibrary(module);
        CoUninitialize();
        return 1;
    }

    CoTaskMemFree(effectIds);
    effects->Release();

    IAudioProcessingObjectConfiguration* configuration = nullptr;
    ThrowIfFailed(
        apo->QueryInterface(
            __uuidof(IAudioProcessingObjectConfiguration),
            reinterpret_cast<void**>(&configuration)),
        "QueryInterface(IAudioProcessingObjectConfiguration)");

    IAudioProcessingObjectRT* realTime = nullptr;
    ThrowIfFailed(
        apo->QueryInterface(
            __uuidof(IAudioProcessingObjectRT),
            reinterpret_cast<void**>(&realTime)),
        "QueryInterface(IAudioProcessingObjectRT)");

    WAVEFORMATEXTENSIBLE format = {};
    format.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    format.Format.nChannels = 2;
    format.Format.nSamplesPerSec = 44100;
    format.Format.wBitsPerSample = 32;
    format.Format.nBlockAlign = 8;
    format.Format.nAvgBytesPerSec = 44100 * 8;
    format.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
    format.Samples.wValidBitsPerSample = 32;
    format.dwChannelMask = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
    format.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;

    IAudioMediaType* mediaType = nullptr;
    ThrowIfFailed(
        CreateAudioMediaType(
            &format.Format,
            sizeof(format),
            &mediaType),
        "CreateAudioMediaType(float32 44100)");

    WAVEFORMATEXTENSIBLE pcmFormat = format;
    pcmFormat.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;

    IAudioMediaType* pcmMediaType = nullptr;
    ThrowIfFailed(
        CreateAudioMediaType(
            &pcmFormat.Format,
            sizeof(pcmFormat),
            &pcmMediaType),
        "CreateAudioMediaType(PCM32 44100)");

    IAudioMediaType* suggestedFormat = nullptr;
    auto negotiationResult = apo->IsInputFormatSupported(
        mediaType,
        pcmMediaType,
        &suggestedFormat);

    if (negotiationResult != S_FALSE || suggestedFormat == nullptr)
    {
        std::cerr << "PCM32 44100 input negotiation did not suggest float32. HRESULT=0x"
                  << std::hex << static_cast<unsigned long>(negotiationResult)
                  << std::dec << std::endl;
        pcmMediaType->Release();
        mediaType->Release();
        realTime->Release();
        configuration->Release();
        apo->Release();
        FreeLibrary(module);
        CoUninitialize();
        return 1;
    }

    UNCOMPRESSEDAUDIOFORMAT suggested = {};
    ThrowIfFailed(
        suggestedFormat->GetUncompressedAudioFormat(&suggested),
        "GetUncompressedAudioFormat(suggested input)");

    if (!IsEqualGUID(suggested.guidFormatType, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) ||
        suggested.dwSamplesPerFrame != 2 ||
        suggested.dwBytesPerSampleContainer != 4 ||
        suggested.dwValidBitsPerSample != 32 ||
        std::fabs(suggested.fFramesPerSecond - 44100.0f) > 0.5f)
    {
        std::cerr << "Suggested input format is not stereo float32 44100." << std::endl;
        suggestedFormat->Release();
        pcmMediaType->Release();
        mediaType->Release();
        realTime->Release();
        configuration->Release();
        apo->Release();
        FreeLibrary(module);
        CoUninitialize();
        return 1;
    }

    suggestedFormat->Release();
    suggestedFormat = nullptr;

    negotiationResult = apo->IsOutputFormatSupported(
        mediaType,
        pcmMediaType,
        &suggestedFormat);

    if (negotiationResult != S_FALSE || suggestedFormat == nullptr)
    {
        std::cerr << "PCM32 44100 output negotiation did not suggest float32. HRESULT=0x"
                  << std::hex << static_cast<unsigned long>(negotiationResult)
                  << std::dec << std::endl;
        pcmMediaType->Release();
        mediaType->Release();
        realTime->Release();
        configuration->Release();
        apo->Release();
        FreeLibrary(module);
        CoUninitialize();
        return 1;
    }

    suggestedFormat->Release();
    pcmMediaType->Release();

    std::cout << "Format negotiation: PCM32 44100 stereo -> float32 44100 stereo." << std::endl;

    constexpr UINT32 frameCount = 256;
    std::vector<float> input(frameCount * 2);
    std::vector<float> output(frameCount * 2, -123.0f);

    for (UINT32 frame = 0; frame < frameCount; frame++)
    {
        const auto phase = static_cast<float>(frame) * 0.03125f;
        input[frame * 2] = 0.25f * std::sin(phase);
        input[frame * 2 + 1] = -0.20f * std::cos(phase);
    }

    APO_CONNECTION_DESCRIPTOR inputDescriptor = {};
    inputDescriptor.Type = APO_CONNECTION_BUFFER_TYPE_EXTERNAL;
    inputDescriptor.pBuffer = reinterpret_cast<UINT_PTR>(input.data());
    inputDescriptor.u32MaxFrameCount = frameCount;
    inputDescriptor.pFormat = mediaType;
    inputDescriptor.u32Signature = APO_CONNECTION_DESCRIPTOR_SIGNATURE;

    APO_CONNECTION_DESCRIPTOR outputDescriptor = {};
    outputDescriptor.Type = APO_CONNECTION_BUFFER_TYPE_EXTERNAL;
    outputDescriptor.pBuffer = reinterpret_cast<UINT_PTR>(output.data());
    outputDescriptor.u32MaxFrameCount = frameCount;
    outputDescriptor.pFormat = mediaType;
    outputDescriptor.u32Signature = APO_CONNECTION_DESCRIPTOR_SIGNATURE;

    APO_CONNECTION_DESCRIPTOR* inputDescriptors[] = { &inputDescriptor };
    APO_CONNECTION_DESCRIPTOR* outputDescriptors[] = { &outputDescriptor };

    ThrowIfFailed(
        configuration->LockForProcess(
            1,
            inputDescriptors,
            1,
            outputDescriptors),
        "IAudioProcessingObjectConfiguration::LockForProcess");

    APO_CONNECTION_PROPERTY inputProperty = {};
    inputProperty.pBuffer = reinterpret_cast<UINT_PTR>(input.data());
    inputProperty.u32ValidFrameCount = frameCount;
    inputProperty.u32BufferFlags = BUFFER_VALID;
    inputProperty.u32Signature = APO_CONNECTION_PROPERTY_SIGNATURE;

    APO_CONNECTION_PROPERTY outputProperty = {};
    outputProperty.pBuffer = reinterpret_cast<UINT_PTR>(output.data());
    outputProperty.u32ValidFrameCount = 0;
    outputProperty.u32BufferFlags = BUFFER_INVALID;
    outputProperty.u32Signature = APO_CONNECTION_PROPERTY_SIGNATURE;

    APO_CONNECTION_PROPERTY* inputProperties[] = { &inputProperty };
    APO_CONNECTION_PROPERTY* outputProperties[] = { &outputProperty };

    realTime->APOProcess(
        1,
        inputProperties,
        1,
        outputProperties);

    if (outputProperty.u32BufferFlags != BUFFER_VALID ||
        outputProperty.u32ValidFrameCount != frameCount)
    {
        std::cerr << "APOProcess returned invalid output metadata." << std::endl;
        configuration->UnlockForProcess();
        mediaType->Release();
        realTime->Release();
        configuration->Release();
        apo->Release();
        FreeLibrary(module);
        CoUninitialize();
        return 1;
    }

    for (size_t index = 0; index < input.size(); index++)
    {
        if (output[index] != input[index])
        {
            std::cerr << "Initial bypass output differs from input at sample "
                      << index << "." << std::endl;
            configuration->UnlockForProcess();
            mediaType->Release();
            realTime->Release();
            configuration->Release();
            apo->Release();
            FreeLibrary(module);
            CoUninitialize();
            return 1;
        }
    }

    Sleep(150);

    for (UINT32 frame = 0; frame < frameCount; frame++)
    {
        input[frame * 2] = 0.95f;
        input[frame * 2 + 1] = -0.95f;
    }

    std::fill(output.begin(), output.end(), 0.0f);
    inputProperty.u32ValidFrameCount = frameCount;
    inputProperty.u32BufferFlags = BUFFER_VALID;
    outputProperty.u32ValidFrameCount = 0;
    outputProperty.u32BufferFlags = BUFFER_INVALID;

    realTime->APOProcess(
        1,
        inputProperties,
        1,
        outputProperties);

    bool runtimeProcessingObserved = false;
    auto maximumOutput = 0.0f;

    for (size_t index = 0; index < input.size(); index++)
    {
        if (!std::isfinite(output[index]))
        {
            std::cerr << "DSP output contains a non-finite sample." << std::endl;
            configuration->UnlockForProcess();
            mediaType->Release();
            realTime->Release();
            configuration->Release();
            apo->Release();
            FreeLibrary(module);
            CoUninitialize();
            return 1;
        }

        if (output[index] != input[index])
        {
            runtimeProcessingObserved = true;
        }

        maximumOutput = (std::max)(
            maximumOutput,
            std::fabs(output[index]));
    }

    std::cout << "Runtime DSP observed: "
              << (runtimeProcessingObserved ? "yes" : "no")
              << ", max output: " << maximumOutput << std::endl;

    ThrowIfFailed(
        configuration->UnlockForProcess(),
        "IAudioProcessingObjectConfiguration::UnlockForProcess");

    mediaType->Release();
    realTime->Release();
    configuration->Release();
    apo->Release();
    FreeLibrary(module);
    CoUninitialize();

    std::cout << "MaxLoud APO smoke test passed." << std::endl;
    return 0;
}
