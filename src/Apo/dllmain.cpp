#include "MaxLoudApo.h"

#include <new>
#include <strsafe.h>

class CMaxLoudApoModule :
    public ATL::CAtlDllModuleT<CMaxLoudApoModule>
{
};

CMaxLoudApoModule _AtlModule;

namespace
{
    /// <summary>
    /// Appends one lightweight non-real-time COM loader diagnostic line.
    /// This intentionally avoids DllMain because file I/O while the loader lock is held is unsafe.
    /// </summary>
    void WriteLoaderDiagnostic(
        const wchar_t* operation,
        REFCLSID classId,
        REFIID interfaceId,
        HRESULT result)
    {
        wchar_t classIdText[64] = {};
        wchar_t interfaceIdText[64] = {};
        StringFromGUID2(classId, classIdText, ARRAYSIZE(classIdText));
        StringFromGUID2(interfaceId, interfaceIdText, ARRAYSIZE(interfaceIdText));

        SYSTEMTIME time = {};
        GetLocalTime(&time);

        wchar_t line[768] = {};
        if (FAILED(StringCchPrintfW(
                line,
                ARRAYSIZE(line),
                L"%04u-%02u-%02u %02u:%02u:%02u.%03u [pid=%lu tid=%lu] %s clsid=%s iid=%s hr=0x%08lX\r\n",
                time.wYear,
                time.wMonth,
                time.wDay,
                time.wHour,
                time.wMinute,
                time.wSecond,
                time.wMilliseconds,
                GetCurrentProcessId(),
                GetCurrentThreadId(),
                operation,
                classIdText,
                interfaceIdText,
                static_cast<ULONG>(result))))
        {
            return;
        }

        const auto file = CreateFileW(
            L"C:\\ProgramData\\MaxLoud\\logs\\MaxLoudApo-loader.log",
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
            line,
            static_cast<DWORD>(wcslen(line) * sizeof(wchar_t)),
            &bytesWritten,
            nullptr);

        CloseHandle(file);
    }

    /// <summary>
    /// Wraps ATL's real class factory so MaxLoud can see the exact CreateInstance request and HRESULT
    /// issued by audiodg.exe before an APO object constructor is reached.
    /// </summary>
    class CLoggingClassFactory final : public IClassFactory
    {
    public:
        CLoggingClassFactory(
            REFCLSID classId,
            IClassFactory* innerFactory)
            : _referenceCount(1)
            , _classId(classId)
            , _innerFactory(innerFactory)
        {
        }

        ~CLoggingClassFactory()
        {
            if (_innerFactory != nullptr)
            {
                _innerFactory->Release();
                _innerFactory = nullptr;
            }
        }

        STDMETHOD(QueryInterface)(
            REFIID interfaceId,
            void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;

            if (interfaceId == IID_IUnknown ||
                interfaceId == IID_IClassFactory)
            {
                *object = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        STDMETHOD_(ULONG, AddRef)() override
        {
            return static_cast<ULONG>(
                InterlockedIncrement(&_referenceCount));
        }

        STDMETHOD_(ULONG, Release)() override
        {
            const auto referenceCount =
                InterlockedDecrement(&_referenceCount);

            if (referenceCount == 0)
            {
                delete this;
                return 0;
            }

            return static_cast<ULONG>(referenceCount);
        }

        STDMETHOD(CreateInstance)(
            IUnknown* outerUnknown,
            REFIID interfaceId,
            void** object) override
        {
            const auto result =
                _innerFactory->CreateInstance(
                    outerUnknown,
                    interfaceId,
                    object);

            WriteLoaderDiagnostic(
                L"IClassFactory::CreateInstance",
                _classId,
                interfaceId,
                result);

            return result;
        }

        STDMETHOD(LockServer)(BOOL lock) override
        {
            const auto result =
                _innerFactory->LockServer(lock);

            WriteLoaderDiagnostic(
                lock
                    ? L"IClassFactory::LockServer(TRUE)"
                    : L"IClassFactory::LockServer(FALSE)",
                _classId,
                IID_IClassFactory,
                result);

            return result;
        }

    private:
        volatile LONG _referenceCount;
        CLSID _classId;
        IClassFactory* _innerFactory;
    };
}

/// <summary>
/// Initializes and terminates the ATL in-process COM module used by Windows Audio Engine.
/// </summary>
extern "C" BOOL WINAPI DllMain(
    HINSTANCE instance,
    DWORD reason,
    LPVOID reserved)
{
    UNREFERENCED_PARAMETER(instance);

    return _AtlModule.DllMain(
        reason,
        reserved);
}

/// <summary>
/// Returns whether the MaxLoud APO COM module can be unloaded.
/// </summary>
STDAPI DllCanUnloadNow(void)
{
    return _AtlModule.DllCanUnloadNow();
}

/// <summary>
/// Returns the COM class factory for CLSID_MaxLoudApo and records both factory acquisition and CreateInstance.
/// </summary>
STDAPI DllGetClassObject(
    REFCLSID classId,
    REFIID interfaceId,
    LPVOID* object)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }

    *object = nullptr;

    if (interfaceId == IID_IClassFactory)
    {
        IClassFactory* innerFactory = nullptr;
        const auto result =
            _AtlModule.DllGetClassObject(
                classId,
                IID_IClassFactory,
                reinterpret_cast<void**>(&innerFactory));

        WriteLoaderDiagnostic(
            L"DllGetClassObject",
            classId,
            interfaceId,
            result);

        if (FAILED(result))
        {
            return result;
        }

        auto* loggingFactory =
            new (std::nothrow) CLoggingClassFactory(
                classId,
                innerFactory);

        if (loggingFactory == nullptr)
        {
            innerFactory->Release();
            return E_OUTOFMEMORY;
        }

        *object = static_cast<IClassFactory*>(loggingFactory);
        return S_OK;
    }

    const auto result =
        _AtlModule.DllGetClassObject(
            classId,
            interfaceId,
            object);

    WriteLoaderDiagnostic(
        L"DllGetClassObject(non-IClassFactory)",
        classId,
        interfaceId,
        result);

    return result;
}
