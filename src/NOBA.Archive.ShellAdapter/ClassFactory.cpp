#include "ClassFactory.h"

#include <windows.h>

#include <new>

#include "NobaExplorerCommand.h"

namespace noba {

LONG g_objects = 0;
LONG g_locks = 0;

IFACEMETHODIMP ClassFactory::QueryInterface(REFIID riid, void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    if (riid == IID_IUnknown || riid == IID_IClassFactory) {
        *ppv = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

IFACEMETHODIMP_(ULONG) ClassFactory::AddRef() {
    return static_cast<ULONG>(::InterlockedIncrement(&references_));
}

IFACEMETHODIMP_(ULONG) ClassFactory::Release() {
    const LONG remaining = ::InterlockedDecrement(&references_);
    if (remaining == 0) {
        delete this;
    }
    return static_cast<ULONG>(remaining);
}

IFACEMETHODIMP ClassFactory::CreateInstance(IUnknown* outer, REFIID riid, void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    *ppv = nullptr;

    // Aggregation is not supported, and saying so is required rather than optional: a container that
    // aggregated this object would get its IUnknown identity wrong in ways that surface much later.
    if (outer != nullptr) {
        return CLASS_E_NOAGGREGATION;
    }

    auto* command = new (std::nothrow) NobaExplorerCommand();
    if (command == nullptr) {
        return E_OUTOFMEMORY;
    }

    ::InterlockedIncrement(&g_objects);
    const HRESULT hr = command->QueryInterface(riid, ppv);
    command->Release();
    return hr;
}

IFACEMETHODIMP ClassFactory::LockServer(BOOL lock) {
    if (lock) {
        ::InterlockedIncrement(&g_locks);
    } else {
        ::InterlockedDecrement(&g_locks);
    }
    return S_OK;
}

}  // namespace noba
