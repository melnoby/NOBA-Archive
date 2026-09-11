// =============================================================================================
// DROP 08 Phase 5 — GATE 2 / E1 CLIENT. NOT PRODUCT CODE.
//
// Activates the probe through the classic out-of-process COM surrogate and checks the four E1 pass
// criteria that were fixed in DROP08_PHASE5_DELTA.md §2 BEFORE any of this existed:
//
//   1. this process's module list contains NO coreclr.dll and NO hostfxr.dll
//   2. a dllhost.exe exists whose module list contains BOTH, plus the NOBA bridge assembly
//   3. faulting the managed side kills the surrogate and THIS PROCESS SURVIVES
//   4. NobaShellQuery round-trips across the process boundary and returns the real menu
//
// Anything less is FAIL, not "partial". The criteria are checked in that spirit: each prints its own
// verdict, and the exit code is non-zero unless all four pass.
//
// ORDER MATTERS. The CLR is started in the surrogate (criterion 4) BEFORE either module list is
// read. Reading them earlier would let criterion 1 pass for the trivial reason that nothing had
// loaded the runtime anywhere yet — which would be a test that could not fail.
// =============================================================================================

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <oleauto.h>
#include <tlhelp32.h>
#include <cstdio>
#include <cstdint>
#include <cctype>
#include <string>
#include <vector>
#include <algorithm>

// {DD5D1E41-3276-4680-97B4-85B59D8D4986}
static const GUID CLSID_NobaE1Probe =
    { 0xDD5D1E41, 0x3276, 0x4680, { 0x97, 0xB4, 0x85, 0xB5, 0x9D, 0x8D, 0x49, 0x86 } };

static int g_pass = 0, g_fail = 0;

static void Verdict(bool ok, const char* criterion, const char* detail)
{
    std::printf("  [%s] %s\n         %s\n", ok ? "PASS" : "FAIL", criterion, detail);
    if (ok) { ++g_pass; } else { ++g_fail; }
}

// Returns the lowercased module file names loaded in `pid`, or an empty vector on failure.
// `ok` distinguishes "no modules matched" from "could not look", which must never be conflated:
// an unreadable module list is not evidence of absence.
static std::vector<std::string> Modules(DWORD pid, bool* ok)
{
    std::vector<std::string> names;
    *ok = false;

    HANDLE snap = INVALID_HANDLE_VALUE;
    for (int attempt = 0; attempt < 8; ++attempt)
    {
        snap = ::CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, pid);
        if (snap != INVALID_HANDLE_VALUE) { break; }
        if (::GetLastError() != ERROR_BAD_LENGTH) { break; }  // documented transient case
        ::Sleep(50);
    }
    if (snap == INVALID_HANDLE_VALUE) { return names; }

    MODULEENTRY32 me = {};
    me.dwSize = sizeof(me);
    if (::Module32First(snap, &me))
    {
        do
        {
            std::string n(me.szModule);
            std::transform(n.begin(), n.end(), n.begin(),
                           [](unsigned char c) { return (char)::tolower(c); });
            names.push_back(n);
        } while (::Module32Next(snap, &me));
        *ok = true;
    }
    ::CloseHandle(snap);
    return names;
}

static bool Has(const std::vector<std::string>& mods, const char* needle)
{
    return std::any_of(mods.begin(), mods.end(),
                       [&](const std::string& m) { return m.find(needle) != std::string::npos; });
}

static HRESULT Call(IDispatch* d, DISPID id, VARIANT* out)
{
    DISPPARAMS noArgs = { nullptr, nullptr, 0, 0 };
    if (out) { ::VariantInit(out); }
    return d->Invoke(id, IID_NULL, LOCALE_USER_DEFAULT, DISPATCH_METHOD,
                     &noArgs, out, nullptr, nullptr);
}

int main()
{
    std::printf("==============================================================================\n");
    std::printf("DROP 08 Phase 5 - GATE 2 / E1: classic out-of-process COM surrogate\n");
    std::printf("==============================================================================\n\n");

    const DWORD selfPid = ::GetCurrentProcessId();
    std::printf("client pid : %lu\n", selfPid);

    HRESULT hr = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr)) { std::printf("FAIL  CoInitializeEx 0x%08lx\n", (unsigned long)hr); return 2; }

    // CLSCTX_LOCAL_SERVER is the whole point: it is the flag that makes COM honour DllSurrogate.
    // CLSCTX_INPROC_SERVER here would load the DLL into THIS process and the test would prove the
    // opposite of what it set out to prove.
    IDispatch* probe = nullptr;
    hr = ::CoCreateInstance(CLSID_NobaE1Probe, nullptr, CLSCTX_LOCAL_SERVER,
                            IID_IDispatch, (void**)&probe);
    if (FAILED(hr) || !probe)
    {
        std::printf("FAIL  CoCreateInstance(CLSCTX_LOCAL_SERVER) 0x%08lx\n", (unsigned long)hr);
        std::printf("      Is the probe registered? Run run-e1.ps1 rather than this exe directly.\n");
        ::CoUninitialize();
        return 2;
    }
    std::printf("activated  : CLSCTX_LOCAL_SERVER succeeded\n");

    // --- where does the object actually live? ---------------------------------------------------
    VARIANT v;
    hr = Call(probe, 1, &v);
    if (FAILED(hr) || v.vt != VT_I4)
    {
        std::printf("FAIL  could not read the surrogate pid 0x%08lx\n", (unsigned long)hr);
        probe->Release(); ::CoUninitialize(); return 2;
    }
    const DWORD hostPid = (DWORD)v.lVal;
    ::VariantClear(&v);
    std::printf("host pid   : %lu%s\n\n", hostPid,
                hostPid == selfPid ? "   <-- SAME PROCESS: NOT ISOLATED" : "   (different process)");

    // --- criterion 4 first, so the runtime is genuinely loaded before either list is read --------
    std::printf("CRITERION 4 - the bridge round-trips across the process boundary\n");
    VARIANT menu;
    hr = Call(probe, 2, &menu);
    bool c4 = SUCCEEDED(hr) && menu.vt == VT_BSTR && menu.bstrVal && ::SysStringLen(menu.bstrVal) > 0;
    std::string menuUtf8;
    if (c4)
    {
        const int n = ::WideCharToMultiByte(CP_UTF8, 0, menu.bstrVal, -1, nullptr, 0, nullptr, nullptr);
        menuUtf8.resize((size_t)(n > 0 ? n - 1 : 0));
        ::WideCharToMultiByte(CP_UTF8, 0, menu.bstrVal, -1, &menuUtf8[0], n, nullptr, nullptr);
        // The real model output, reached across a process boundary. "SingleMemberFormat" appearing
        // here from a probe that contains no format name is the same evidence G.1 relied on.
        c4 = menuUtf8.find("SingleMemberFormat") != std::string::npos;
        std::printf("  menu: %.180s\n", menuUtf8.c_str());
    }
    ::VariantClear(&menu);
    Verdict(c4, "4. NobaShellQuery round-trips and returns the real menu",
            c4 ? "the disabled gzip entry came back from the surrogate"
               : "no menu, or the menu did not contain the expected model output");

    // --- criteria 1 and 2 ------------------------------------------------------------------------
    std::printf("\nCRITERION 1 - the CLIENT did not load the runtime\n");
    bool okSelf = false;
    const std::vector<std::string> mine = Modules(selfPid, &okSelf);
    const bool selfClr = Has(mine, "coreclr") || Has(mine, "hostfxr");
    char buf[512];
    if (!okSelf)
    {
        Verdict(false, "1. no coreclr/hostfxr in the client",
                "could not read this process's module list - NOT evidence of absence");
    }
    else
    {
        std::snprintf(buf, sizeof(buf), "%zu modules loaded; coreclr/hostfxr %s",
                      mine.size(), selfClr ? "PRESENT - the DLL came in-proc" : "absent");
        Verdict(!selfClr, "1. no coreclr/hostfxr in the client", buf);
    }

    std::printf("\nCRITERION 2 - the SURROGATE did load the runtime and the bridge\n");
    bool okHost = false;
    const std::vector<std::string> theirs = Modules(hostPid, &okHost);
    const bool hasClr    = Has(theirs, "coreclr");
    const bool hasFxr    = Has(theirs, "hostfxr");
    const bool hasProbe  = Has(theirs, "noba_e_probe");
    if (!okHost)
    {
        Verdict(false, "2. coreclr + hostfxr + probe in the surrogate",
                "could not read the surrogate's module list - NOT evidence of anything");
    }
    else
    {
        std::snprintf(buf, sizeof(buf), "%zu modules; coreclr=%s hostfxr=%s probe=%s",
                      theirs.size(), hasClr ? "yes" : "NO", hasFxr ? "yes" : "NO",
                      hasProbe ? "yes" : "NO");
        Verdict(hasClr && hasFxr && hasProbe && hostPid != selfPid,
                "2. coreclr + hostfxr + probe in the surrogate", buf);
    }

    // --- criterion 3 -----------------------------------------------------------------------------
    std::printf("\nCRITERION 3 - faulting the surrogate must not take the client with it\n");
    hr = Call(probe, 3, nullptr);
    // A dead server surfaces as an RPC failure. SUCCESS here would mean the fault never happened.
    const bool rpcDied = FAILED(hr);
    std::snprintf(buf, sizeof(buf), "Invoke returned 0x%08lx; this process is still executing",
                  (unsigned long)hr);
    Verdict(rpcDied, "3. surrogate died, client survived", buf);

    probe->Release();
    ::CoUninitialize();

    std::printf("\n==============================================================================\n");
    std::printf("E1 RESULT: %d passed, %d failed  ->  %s\n", g_pass, g_fail,
                (g_fail == 0 && g_pass == 4) ? "PASS" : "FAIL");
    std::printf("==============================================================================\n");
    std::printf("E1 PASS DOES NOT IMPLY E2 PASS. This is the classic HKCU activation path, not the\n");
    std::printf("packaged COM catalogue Explorer uses. Only E2 exercises that one.\n");

    return (g_fail == 0 && g_pass == 4) ? 0 : 1;
}
