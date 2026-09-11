// =============================================================================================
// DROP 08 Phase 5 — GATE 2 / E2 STUB. NOT PRODUCT CODE. NOT THE NOBA SHELL ADAPTER.
//
// WHAT THIS ANSWERS
//   E2: when Explorer activates a context-menu handler registered through a sparse package's
//       com:SurrogateServer, does the DLL — and the CLR it hosts — land in dllhost.exe rather than
//       in explorer.exe, running as the standard user?
//
// THE SEVEN BINDING CONSTRAINTS (DROP08_PHASE5_DELTA.md §2), and where each is met:
//   1. constant harmless title ............ GetTitle returns one literal, always
//   2. deterministic harmless state ....... GetState returns ECS_ENABLED, always, unconditionally
//   3. Invoke does nothing meaningful ..... Invoke logs and returns S_OK; it starts no process
//   4. no archive business logic .......... there is none in this file
//   5. no capability logic ................ there is none in this file
//   6. no format names .................... there are none in this file
//   7. no production NOBA action .......... nothing here is reachable from the product
//
// ONE JUDGEMENT CALL, STATED SO IT CAN BE OVERRULED
//   The stub DOES host the CLR and DOES call NobaShellWarm. Without that, E2 would only prove that a
//   C++ DLL loads into dllhost.exe — and pass criteria 2 and 3 are written about coreclr.dll and
//   hostfxr.dll specifically, so a stub that never starts the runtime could not satisfy them.
//
//   NobaShellWarm builds the DI container and the provider registry and returns an int. It does NOT
//   call ShellMenuBuilder. The menu title and state below are literals and are NOT derived from it —
//   nothing the bridge returns reaches the menu. That is the line "do not connect the real adapter
//   to ShellMenuBuilder" draws, and this stays on the safe side of it. If you read that instruction
//   more strictly, say so and the CLR hosting comes out — at the cost of E2 no longer being able to
//   answer criteria 2 and 3.
// =============================================================================================

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <cstdio>
#include <cstdint>
#include <cstdarg>
#include <string>
#include <new>

// {17019B66-1E78-459B-92AE-63A440E7C5EA}
static const GUID CLSID_NobaE2Stub =
    { 0x17019B66, 0x1E78, 0x459B, { 0x92, 0xAE, 0x63, 0xA4, 0x40, 0xE7, 0xC5, 0xEA } };

// Constraint 1. One literal, no resource lookup, no localisation. Production localisation is
// explicitly out of scope for this gate.
static const wchar_t* const kTitle = L"NOBA Phase 5 isolation probe (harness)";

static LONG g_objects = 0;
static HMODULE g_module = nullptr;

static std::wstring ModuleDir()
{
    wchar_t path[MAX_PATH] = {};
    ::GetModuleFileNameW(g_module, path, MAX_PATH);
    std::wstring s(path);
    const size_t slash = s.find_last_of(L'\\');
    return (slash == std::wstring::npos) ? std::wstring() : s.substr(0, slash);
}

// A handler inside dllhost.exe has nowhere to print, and a silent surrogate is indistinguishable
// from one that was never activated. Everything goes to a log beside the DLL, and the run script
// reads it back.
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
    const std::wstring file = ModuleDir() + L"\\noba_e2_stub.log";
    FILE* f = OpenFileW(file.c_str(), L"a");
    if (!f) { return; }
    va_list args; va_start(args, fmt);
    std::fprintf(f, "[pid %lu] ", ::GetCurrentProcessId());
    std::vfprintf(f, fmt, args);
    std::fprintf(f, "\n");
    va_end(args);
    std::fclose(f);
}

// --- CLR hosting, for pass criteria 2 and 3 only -----------------------------------------------
typedef void* hostfxr_handle;
typedef int32_t (*hostfxr_initialize_for_runtime_config_fn)(const wchar_t*, const void*, hostfxr_handle*);
typedef int32_t (*hostfxr_get_runtime_delegate_fn)(hostfxr_handle, int32_t, void**);
typedef int32_t (*load_assembly_and_get_function_pointer_fn)(
    const wchar_t*, const wchar_t*, const wchar_t*, const wchar_t*, void*, void**);
typedef int32_t (*noba_warm_fn)();

static const int32_t hdt_load_assembly_and_get_function_pointer = 5;
static const wchar_t* UNMANAGED_CALLERS_ONLY = (const wchar_t*)(-1);
static bool g_clrTried = false;

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

// Starts the runtime in whatever process this DLL was loaded into. Which process that turns out to
// be is the entire question E2 exists to answer.
static void WarmClrOnce()
{
    if (g_clrTried) { return; }
    g_clrTried = true;

    const std::wstring fxr = ReadHostfxrPath();
    if (fxr.empty()) { return; }
    HMODULE h = ::LoadLibraryW(fxr.c_str());
    if (!h) { Log("LoadLibraryW(hostfxr) failed gle=%lu", ::GetLastError()); return; }

    auto init = ProcAs<hostfxr_initialize_for_runtime_config_fn>(h, "hostfxr_initialize_for_runtime_config");
    auto getd = ProcAs<hostfxr_get_runtime_delegate_fn>(h, "hostfxr_get_runtime_delegate");
    if (!init || !getd) { Log("hostfxr exports missing"); return; }

    const std::wstring dir = ModuleDir();
    hostfxr_handle ctx = nullptr;
    int32_t rc = init((dir + L"\\NOBA.Phase5.G1.Bridge.runtimeconfig.json").c_str(), nullptr, &ctx);
    if (rc > 2 || !ctx) { Log("hostfxr_initialize rc=0x%x", rc); return; }

    void* raw = nullptr;
    rc = getd(ctx, hdt_load_assembly_and_get_function_pointer, &raw);
    if (rc != 0 || !raw) { Log("get_runtime_delegate rc=0x%x", rc); return; }

    noba_warm_fn warm = nullptr;
    rc = ((load_assembly_and_get_function_pointer_fn)raw)(
        (dir + L"\\NOBA.Phase5.G1.Bridge.dll").c_str(),
        L"NOBA.Phase5.G1.BridgeExports, NOBA.Phase5.G1.Bridge",
        L"NobaShellWarm", UNMANAGED_CALLERS_ONLY, nullptr, (void**)&warm);
    if (rc != 0 || !warm) { Log("resolve NobaShellWarm rc=0x%x", rc); return; }

    // DI container and provider registry only. This does not call ShellMenuBuilder and its result
    // does not reach the menu.
    Log("NobaShellWarm returned %d - the CLR is running IN THIS PROCESS", warm());
}

// --- the stub command ---------------------------------------------------------------------------
class StubCommand : public IExplorerCommand
{
public:
    StubCommand() : m_ref(1) { ::InterlockedIncrement(&g_objects); }
    virtual ~StubCommand() { ::InterlockedDecrement(&g_objects); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) { return E_POINTER; }
        if (::IsEqualIID(riid, IID_IUnknown) || ::IsEqualIID(riid, IID_IExplorerCommand))
        {
            *ppv = static_cast<IExplorerCommand*>(this);
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

    // Constraint 1: a constant. Drawing the menu is also the moment the runtime gets started, which
    // is what makes a plain right-click sufficient evidence - the user need not click the item.
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, LPWSTR* ppszName) override
    {
        if (!ppszName) { return E_POINTER; }
        Log("GetTitle - Explorer activated this handler here");
        WarmClrOnce();
        return ::SHStrDupW(kTitle, ppszName);
    }

    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, LPWSTR* ppszIcon) override
    {
        if (ppszIcon) { *ppszIcon = nullptr; }
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, LPWSTR* ppszInfotip) override
    {
        if (ppszInfotip) { *ppszInfotip = nullptr; }
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* pguidCommandName) override
    {
        if (pguidCommandName) { *pguidCommandName = GUID_NULL; }
        return S_OK;
    }

    // Constraint 2: deterministic and unconditional. No selection is inspected, so there is nothing
    // for a capability rule to be smuggled into.
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray*, BOOL, EXPCMDSTATE* pCmdState) override
    {
        if (!pCmdState) { return E_POINTER; }
        *pCmdState = ECS_ENABLED;
        return S_OK;
    }

    // Constraint 3: nothing happens. No process is started, nothing is written, nothing is read.
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray*, IBindCtx*) override
    {
        Log("Invoke - deliberately does nothing");
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* pFlags) override
    {
        if (pFlags) { *pFlags = ECF_DEFAULT; }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** ppEnum) override
    {
        if (ppEnum) { *ppEnum = nullptr; }
        return E_NOTIMPL;   // flat entry; submenus are Phase 5 implementation, not this gate
    }

private:
    LONG m_ref;
};

class Factory : public IClassFactory
{
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) { return E_POINTER; }
        if (::IsEqualIID(riid, IID_IUnknown) || ::IsEqualIID(riid, IID_IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) { return CLASS_E_NOAGGREGATION; }
        StubCommand* c = new (std::nothrow) StubCommand();
        if (!c) { return E_OUTOFMEMORY; }
        const HRESULT hr = c->QueryInterface(riid, ppv);
        c->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }
};

static Factory g_factory;

extern "C" BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = (HMODULE)inst; ::DisableThreadLibraryCalls(inst); }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!::IsEqualCLSID(rclsid, CLSID_NobaE2Stub)) { return CLASS_E_CLASSNOTAVAILABLE; }
    Log("DllGetClassObject - loaded into this process");
    return g_factory.QueryInterface(riid, ppv);
}

extern "C" HRESULT __stdcall DllCanUnloadNow(void)
{
    return (g_objects == 0) ? S_OK : S_FALSE;
}
