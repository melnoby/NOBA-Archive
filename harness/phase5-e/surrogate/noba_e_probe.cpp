// =============================================================================================
// DROP 08 Phase 5 — GATE 2 / E1 FEASIBILITY PROBE. NOT PRODUCT CODE.
//
// WHAT THIS ANSWERS
//   E1: when a DLL that hosts .NET is activated through an out-of-process COM surrogate, does the
//       CLR land in the SURROGATE and not in the CLIENT, and does faulting the managed side leave
//       the client alive?
//
// WHAT THIS IS NOT
//   Not an IExplorerCommand. Not a context menu. Not a package. Not signed. E1 uses the CLASSIC
//   surrogate route — AppID + DllSurrogate under HKCU — which needs neither MSIX nor a certificate.
//   That is exactly why it can run before E2.
//
//   E1 PASSING DOES NOT MEAN E2 PASSES. They are different activation paths: HKCR/HKCU versus the
//   packaged COM catalogue. Only E2 exercises the one production uses.
//
// WHY IDispatch
//   A custom IUnknown-derived interface cannot cross a process boundary without a proxy/stub or a
//   registered type library. IID_IDispatch has a system-registered proxy, so an IDispatch-based
//   probe marshals with no extra machinery at all. The probe therefore exposes three fixed DISPIDs
//   and no type information — GetIDsOfNames is not used and says so.
//
//     DISPID 1  → the surrogate's own process id, as VT_I4
//     DISPID 2  → the real menu JSON from the managed bridge, as VT_BSTR
//     DISPID 3  → deliberately fault this process (the E1 pass criterion 3 test)
//
// WHY THE hostfxr CODE IS DUPLICATED FROM g1host.cpp
//   Because g1host.cpp is hash-locked to the Gate 1 transcript: its SHA-256 is printed in that
//   transcript as proof of provenance, and refactoring it into a shared header would invalidate
//   that proof. Duplication in a throwaway harness is the cheaper of the two costs.
// =============================================================================================

#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#include <windows.h>
#include <objbase.h>
#include <oleauto.h>
#include <cstdio>
#include <cstring>
#include <cstdint>
#include <cstdarg>
#include <string>
#include <new>

// --- identity ---------------------------------------------------------------------------------
// {DD5D1E41-3276-4680-97B4-85B59D8D4986}
static const GUID CLSID_NobaE1Probe =
    { 0xDD5D1E41, 0x3276, 0x4680, { 0x97, 0xB4, 0x85, 0xB5, 0x9D, 0x8D, 0x49, 0x86 } };

static LONG g_objects = 0;
static LONG g_locks   = 0;
static HMODULE g_module = nullptr;

// --- diagnostics -------------------------------------------------------------------------------
// A DLL inside dllhost.exe has nowhere to print. Everything interesting therefore goes to a log
// beside the DLL, and the run script prints it. Silence in a surrogate is indistinguishable from
// success, which is the failure mode this avoids.
static std::wstring ModuleDir()
{
    wchar_t path[MAX_PATH] = {};
    ::GetModuleFileNameW(g_module, path, MAX_PATH);
    std::wstring s(path);
    const size_t slash = s.find_last_of(L'\\');
    return (slash == std::wstring::npos) ? std::wstring() : s.substr(0, slash);
}

// MSVC deprecates _wfopen (C4996) in favour of _wfopen_s; MinGW has no _wfopen_s at all. Rather than
// define _CRT_SECURE_NO_WARNINGS — which is warning suppression, and this project does not suppress
// warnings — the secure call is used where it exists. The warning was real: it appeared in the first
// MSVC build of this harness.
static FILE* OpenFileW(const wchar_t* path, const wchar_t* mode)
{
#ifdef _MSC_VER
    FILE* f = nullptr;
    return (::_wfopen_s(&f, path, mode) == 0) ? f : nullptr;
#else
    return ::_wfopen(path, mode);
#endif
}

static void Log(const char* fmt, ...)
{
    const std::wstring file = ModuleDir() + L"\\noba_e_probe.log";
    FILE* f = OpenFileW(file.c_str(), L"a");
    if (!f) { return; }
    va_list args;
    va_start(args, fmt);
    std::fprintf(f, "[pid %lu] ", ::GetCurrentProcessId());
    std::vfprintf(f, fmt, args);
    std::fprintf(f, "\n");
    va_end(args);
    std::fclose(f);
}

// --- hostfxr surface, declared rather than included --------------------------------------------
typedef void* hostfxr_handle;
typedef int32_t (*hostfxr_initialize_for_runtime_config_fn)(
    const wchar_t*, const void*, hostfxr_handle*);
typedef int32_t (*hostfxr_get_runtime_delegate_fn)(hostfxr_handle, int32_t, void**);
typedef int32_t (*load_assembly_and_get_function_pointer_fn)(
    const wchar_t*, const wchar_t*, const wchar_t*, const wchar_t*, void*, void**);

static const int32_t hdt_load_assembly_and_get_function_pointer = 5;
static const wchar_t* UNMANAGED_CALLERS_ONLY = (const wchar_t*)(-1);

typedef int32_t (*noba_query_fn)(const char*, char**, int32_t*);
typedef void    (*noba_free_fn)(char*);

static noba_query_fn g_query = nullptr;
static noba_free_fn  g_free  = nullptr;
static bool          g_clrTried = false;

// The script writes this file next to the DLL. A path handed over explicitly beats a path guessed
// by enumerating the dotnet install: when it is wrong, it is wrong loudly and in one place.
static std::wstring ReadHostfxrPath()
{
    const std::wstring file = ModuleDir() + L"\\hostfxr.path";
    FILE* f = OpenFileW(file.c_str(), L"r");
    if (!f) { Log("hostfxr.path not found beside the DLL"); return std::wstring(); }
    char buf[1024] = {};
    if (!std::fgets(buf, sizeof(buf), f)) { std::fclose(f); return std::wstring(); }
    std::fclose(f);
    std::string s(buf);
    while (!s.empty() && (s.back() == '\n' || s.back() == '\r' || s.back() == ' ')) { s.pop_back(); }
    const int n = ::MultiByteToWideChar(CP_ACP, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w((size_t)n, L'\0');
    ::MultiByteToWideChar(CP_ACP, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

// GetProcAddress returns FARPROC, and casting that straight to a typed function pointer is what
// -Wcast-function-type objects to. Going through void* is the standard-sanctioned two-step and is
// the same shape g1host.cpp already uses via noba_sym. Not a suppression - the cast is genuinely
// better written this way.
template <typename T>
static T ProcAs(HMODULE h, const char* name)
{
    return reinterpret_cast<T>(reinterpret_cast<void*>(::GetProcAddress(h, name)));
}

// Starts the CLR **in this process**. That this happens inside dllhost.exe rather than inside the
// client is the entire point of E1.
static bool EnsureClr()
{
    if (g_query) { return true; }
    if (g_clrTried) { return false; }
    g_clrTried = true;

    const std::wstring fxr = ReadHostfxrPath();
    if (fxr.empty()) { return false; }

    HMODULE h = ::LoadLibraryW(fxr.c_str());
    if (!h) { Log("LoadLibraryW(hostfxr) failed, gle=%lu", ::GetLastError()); return false; }

    auto init = ProcAs<hostfxr_initialize_for_runtime_config_fn>(h, "hostfxr_initialize_for_runtime_config");
    auto getd = ProcAs<hostfxr_get_runtime_delegate_fn>(h, "hostfxr_get_runtime_delegate");
    if (!init || !getd) { Log("hostfxr exports missing"); return false; }

    const std::wstring dir    = ModuleDir();
    const std::wstring config = dir + L"\\NOBA.Phase5.G1.Bridge.runtimeconfig.json";
    const std::wstring asmPath = dir + L"\\NOBA.Phase5.G1.Bridge.dll";

    hostfxr_handle ctx = nullptr;
    int32_t rc = init(config.c_str(), nullptr, &ctx);
    if (rc > 2 || !ctx) { Log("hostfxr_initialize rc=0x%x", rc); return false; }

    void* raw = nullptr;
    rc = getd(ctx, hdt_load_assembly_and_get_function_pointer, &raw);
    if (rc != 0 || !raw) { Log("get_runtime_delegate rc=0x%x", rc); return false; }

    auto loadFn = (load_assembly_and_get_function_pointer_fn)raw;
    rc = loadFn(asmPath.c_str(),
                L"NOBA.Phase5.G1.BridgeExports, NOBA.Phase5.G1.Bridge",
                L"NobaShellQuery", UNMANAGED_CALLERS_ONLY, nullptr, (void**)&g_query);
    if (rc != 0 || !g_query) { Log("resolve NobaShellQuery rc=0x%x", rc); return false; }

    loadFn(asmPath.c_str(), L"NOBA.Phase5.G1.BridgeExports, NOBA.Phase5.G1.Bridge",
           L"NobaShellFree", UNMANAGED_CALLERS_ONLY, nullptr, (void**)&g_free);

    Log("CLR started and bridge resolved INSIDE this process");
    return true;
}

// --- the probe object ---------------------------------------------------------------------------
class Probe : public IDispatch
{
public:
    Probe() : m_ref(1) { ::InterlockedIncrement(&g_objects); }
    virtual ~Probe() { ::InterlockedDecrement(&g_objects); }

    // IUnknown
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) { return E_POINTER; }
        if (::IsEqualIID(riid, IID_IUnknown) || ::IsEqualIID(riid, IID_IDispatch))
        {
            *ppv = static_cast<IDispatch*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return (ULONG)::InterlockedIncrement(&m_ref); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        const LONG n = ::InterlockedDecrement(&m_ref);
        if (n == 0) { delete this; }
        return (ULONG)n;
    }

    // IDispatch — no type information on purpose. The client calls fixed DISPIDs, so there is
    // nothing to describe and nothing that could drift out of step with a description.
    HRESULT STDMETHODCALLTYPE GetTypeInfoCount(UINT* pctinfo) override
    {
        if (pctinfo) { *pctinfo = 0; }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetTypeInfo(UINT, LCID, ITypeInfo** ppTInfo) override
    {
        if (ppTInfo) { *ppTInfo = nullptr; }
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetIDsOfNames(REFIID, LPOLESTR*, UINT, LCID, DISPID*) override
    {
        return DISP_E_UNKNOWNNAME;
    }

    HRESULT STDMETHODCALLTYPE Invoke(DISPID id, REFIID, LCID, WORD, DISPPARAMS*,
                                     VARIANT* pResult, EXCEPINFO*, UINT*) override
    {
        switch (id)
        {
        case 1: // which process am I actually running in?
            if (pResult) { ::VariantInit(pResult); pResult->vt = VT_I4;
                           pResult->lVal = (LONG)::GetCurrentProcessId(); }
            Log("DISPID 1 -> pid reported");
            return S_OK;

        case 2: // the real menu, through the real bridge, from inside this process
        {
            if (!EnsureClr()) { return E_FAIL; }
            const char* request =
                "{\"items\":[{\"path\":\"C:\\\\D\\\\a.txt\",\"isDirectory\":false},"
                            "{\"path\":\"C:\\\\D\\\\b.txt\",\"isDirectory\":false},"
                            "{\"path\":\"C:\\\\D\\\\c.txt\",\"isDirectory\":false}]}";
            char* response = nullptr; int32_t len = 0;
            const int32_t rc = g_query(request, &response, &len);
            if (rc != 0 || !response)
            {
                Log("bridge query rc=%d", rc);
                if (response && g_free) { g_free(response); }
                return E_FAIL;
            }
            const int wn = ::MultiByteToWideChar(CP_UTF8, 0, response, len, nullptr, 0);
            BSTR bstr = ::SysAllocStringLen(nullptr, (UINT)wn);
            ::MultiByteToWideChar(CP_UTF8, 0, response, len, bstr, wn);
            if (g_free) { g_free(response); }
            if (pResult) { ::VariantInit(pResult); pResult->vt = VT_BSTR; pResult->bstrVal = bstr; }
            else { ::SysFreeString(bstr); }
            Log("DISPID 2 -> menu returned (%d bytes)", len);
            return S_OK;
        }

        case 3: // fault this process on purpose — pass criterion 3
            Log("DISPID 3 -> faulting this process deliberately");
            *(volatile int*)nullptr = 0;
            return S_OK; // not reached

        default:
            return DISP_E_MEMBERNOTFOUND;
        }
    }

private:
    LONG m_ref;
};

// --- class factory --------------------------------------------------------------------------------
class Factory : public IClassFactory
{
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) { return E_POINTER; }
        if (::IsEqualIID(riid, IID_IUnknown) || ::IsEqualIID(riid, IID_IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }   // static, never freed
    ULONG STDMETHODCALLTYPE Release() override { return 1; }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) { return CLASS_E_NOAGGREGATION; }
        Probe* p = new (std::nothrow) Probe();
        if (!p) { return E_OUTOFMEMORY; }
        const HRESULT hr = p->QueryInterface(riid, ppv);
        p->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        if (lock) { ::InterlockedIncrement(&g_locks); } else { ::InterlockedDecrement(&g_locks); }
        return S_OK;
    }
};

static Factory g_factory;

// --- exports ----------------------------------------------------------------------------------------
extern "C" BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = (HMODULE)inst; ::DisableThreadLibraryCalls(inst); }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!::IsEqualCLSID(rclsid, CLSID_NobaE1Probe)) { return CLASS_E_CLASSNOTAVAILABLE; }
    Log("DllGetClassObject — loaded into this process");
    return g_factory.QueryInterface(riid, ppv);
}

extern "C" HRESULT __stdcall DllCanUnloadNow(void)
{
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}
