#include <Windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <ks.h>
#include <ksmedia.h>

#include <iomanip>
#include <iostream>

namespace
{
    WAVEFORMATEXTENSIBLE MakeStereoFormat(
        DWORD sampleRate,
        const GUID& subFormat)
    {
        WAVEFORMATEXTENSIBLE format = {};
        format.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
        format.Format.nChannels = 2;
        format.Format.nSamplesPerSec = sampleRate;
        format.Format.wBitsPerSample = 32;
        format.Format.nBlockAlign = 8;
        format.Format.nAvgBytesPerSec = sampleRate * 8;
        format.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
        format.Samples.wValidBitsPerSample = 32;
        format.dwChannelMask = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
        format.SubFormat = subFormat;
        return format;
    }

    void PrintWaveFormat(
        const wchar_t* prefix,
        const WAVEFORMATEX* format)
    {
        if (format == nullptr)
        {
            std::wcout << prefix << L"<none>" << std::endl;
            return;
        }

        std::wcout << prefix
                   << format->nSamplesPerSec << L" Hz / "
                   << format->wBitsPerSample << L" bit / "
                   << format->nChannels << L" ch / tag 0x"
                   << std::hex << format->wFormatTag << std::dec
                   << std::endl;
    }

    void Probe(
        IAudioClient* audioClient,
        const wchar_t* name,
        WAVEFORMATEX* requested)
    {
        WAVEFORMATEX* closest = nullptr;
        const auto result = audioClient->IsFormatSupported(
            AUDCLNT_SHAREMODE_SHARED,
            requested,
            &closest);

        std::wcout << name << L": HRESULT=0x"
                   << std::hex << static_cast<unsigned long>(result)
                   << std::dec;

        if (result == S_OK)
        {
            std::wcout << L" (exact supported)";
        }
        else if (result == S_FALSE)
        {
            std::wcout << L" (closest format returned)";
        }
        else
        {
            std::wcout << L" (unsupported/error)";
        }

        std::wcout << std::endl;
        PrintWaveFormat(L"  requested: ", requested);
        PrintWaveFormat(L"  closest:   ", closest);

        if (closest != nullptr)
        {
            CoTaskMemFree(closest);
        }
    }

    void ProbeInitialize(
        IMMDevice* endpoint,
        const wchar_t* name,
        WAVEFORMATEX* requested,
        DWORD streamFlags)
    {
        IAudioClient* client = nullptr;
        const auto activateResult = endpoint->Activate(
            __uuidof(IAudioClient),
            CLSCTX_ALL,
            nullptr,
            reinterpret_cast<void**>(&client));

        if (FAILED(activateResult))
        {
            std::wcout << name << L": Activate failed HRESULT=0x"
                       << std::hex << static_cast<unsigned long>(activateResult)
                       << std::dec << std::endl;
            return;
        }

        const auto initializeResult = client->Initialize(
            AUDCLNT_SHAREMODE_SHARED,
            streamFlags,
            1000000,
            0,
            requested,
            nullptr);

        std::wcout << name << L": Initialize HRESULT=0x"
                   << std::hex << static_cast<unsigned long>(initializeResult)
                   << std::dec
                   << (SUCCEEDED(initializeResult) ? L" (success)" : L" (failed)")
                   << std::endl;

        client->Release();
    }
}

int wmain()
{
    const auto initializeResult = CoInitializeEx(
        nullptr,
        COINIT_MULTITHREADED);

    if (FAILED(initializeResult))
    {
        std::wcerr << L"CoInitializeEx failed: 0x"
                   << std::hex << static_cast<unsigned long>(initializeResult)
                   << std::dec << std::endl;
        return 1;
    }

    IMMDeviceEnumerator* enumerator = nullptr;
    auto result = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator),
        reinterpret_cast<void**>(&enumerator));

    if (FAILED(result))
    {
        std::wcerr << L"MMDeviceEnumerator failed: 0x"
                   << std::hex << static_cast<unsigned long>(result)
                   << std::dec << std::endl;
        CoUninitialize();
        return 1;
    }

    IMMDevice* endpoint = nullptr;
    result = enumerator->GetDefaultAudioEndpoint(
        eRender,
        eMultimedia,
        &endpoint);

    if (FAILED(result))
    {
        std::wcerr << L"GetDefaultAudioEndpoint failed: 0x"
                   << std::hex << static_cast<unsigned long>(result)
                   << std::dec << std::endl;
        enumerator->Release();
        CoUninitialize();
        return 1;
    }

    LPWSTR endpointId = nullptr;
    if (SUCCEEDED(endpoint->GetId(&endpointId)))
    {
        std::wcout << L"Default multimedia render endpoint: "
                   << endpointId << std::endl;
        CoTaskMemFree(endpointId);
    }

    IAudioClient* audioClient = nullptr;
    result = endpoint->Activate(
        __uuidof(IAudioClient),
        CLSCTX_ALL,
        nullptr,
        reinterpret_cast<void**>(&audioClient));

    if (FAILED(result))
    {
        std::wcerr << L"IAudioClient activation failed: 0x"
                   << std::hex << static_cast<unsigned long>(result)
                   << std::dec << std::endl;
        endpoint->Release();
        enumerator->Release();
        CoUninitialize();
        return 1;
    }

    WAVEFORMATEX* mixFormat = nullptr;
    if (SUCCEEDED(audioClient->GetMixFormat(&mixFormat)))
    {
        PrintWaveFormat(L"Mix format: ", mixFormat);
        CoTaskMemFree(mixFormat);
    }

    auto pcm44100 = MakeStereoFormat(
        44100,
        KSDATAFORMAT_SUBTYPE_PCM);

    auto float44100 = MakeStereoFormat(
        44100,
        KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);

    auto float48000 = MakeStereoFormat(
        48000,
        KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);

    Probe(
        audioClient,
        L"PCM32 44100 stereo",
        &pcm44100.Format);

    Probe(
        audioClient,
        L"Float32 44100 stereo",
        &float44100.Format);

    Probe(
        audioClient,
        L"Float32 48000 stereo",
        &float48000.Format);

    ProbeInitialize(
        endpoint,
        L"Initialize PCM32 44100 + AUTOCONVERTPCM",
        &pcm44100.Format,
        AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
        AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY);

    ProbeInitialize(
        endpoint,
        L"Initialize Float32 44100 + AUTOCONVERTPCM",
        &float44100.Format,
        AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
        AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY);

    audioClient->Release();
    endpoint->Release();
    enumerator->Release();
    CoUninitialize();
    return 0;
}
