// DROP 08 Phase 5 - the DLL's two exports, and nothing else.
//
// NO SELF-REGISTRATION. There is no DllRegisterServer here and there must not be: the handler is
// registered by the sparse package's com:SurrogateServer declaration (ADR-0006), which is what E2
// proved. A DLL that could also register itself would give a machine two ways to acquire the handler
// and one way to remove it.

#include <windows.h>

#include <new>
#include <unknwn.h>

#include "ClassFactory.h"

// initguid.h re-defines DEFINE_GUID to the form that ALLOCATES the value rather than declaring it,
// so it must be included in exactly one translation unit and immediately before the header that
// declares the GUID. Without it the CLSID is a declaration nobody defines, and the DLL fails to link
// - which is a far better failure than the alternative, where each translation unit gets its own.
#include <initguid.h>

#include "Guids.h"

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        // No loader-lock work. Starting a runtime here would run under the loader lock, in a process
        // Explorer owns; the runtime is started from GetState instead, on a thread the shell provides.
        ::DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    *ppv = nullptr;

    if (clsid != CLSID_NobaExplorerCommand) {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) noba::ClassFactory();
    if (factory == nullptr) {
        return E_OUTOFMEMORY;
    }

    const HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow() {
    return (noba::g_objects == 0 && noba::g_locks == 0) ? S_OK : S_FALSE;
}
