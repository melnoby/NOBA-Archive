#include "NobaExplorerCommand.h"

#include <windows.h>

#include <new>
#include <shlwapi.h>

#include "ClrHost.h"
#include "NobaSubCommandEnum.h"

namespace noba {

bool NobaExplorerCommand::EnsureMenu(IShellItemArray* items) {
    if (fetched_) {
        return menu_.valid;
    }
    fetched_ = true;
    selection_ = ReadSelection(items);
    menu_ = FetchMenu(selection_);
    return menu_.valid;
}

IFACEMETHODIMP NobaExplorerCommand::QueryInterface(REFIID riid, void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    if (riid == IID_IUnknown || riid == IID_IExplorerCommand) {
        *ppv = static_cast<IExplorerCommand*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

IFACEMETHODIMP_(ULONG) NobaExplorerCommand::AddRef() {
    return static_cast<ULONG>(::InterlockedIncrement(&references_));
}

IFACEMETHODIMP_(ULONG) NobaExplorerCommand::Release() {
    const LONG remaining = ::InterlockedDecrement(&references_);
    if (remaining == 0) {
        delete this;
    }
    return static_cast<ULONG>(remaining);
}

IFACEMETHODIMP NobaExplorerCommand::GetTitle(IShellItemArray* items, LPWSTR* name) {
    if (name == nullptr) {
        return E_POINTER;
    }
    if (!EnsureMenu(items)) {
        return E_FAIL;
    }
    return ::SHStrDupW(menu_.rootTitle.c_str(), name);
}

IFACEMETHODIMP NobaExplorerCommand::GetIcon(IShellItemArray*, LPWSTR* icon) {
    if (icon == nullptr) {
        return E_POINTER;
    }
    *icon = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP NobaExplorerCommand::GetToolTip(IShellItemArray*, LPWSTR* infoTip) {
    if (infoTip == nullptr) {
        return E_POINTER;
    }
    *infoTip = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP NobaExplorerCommand::GetCanonicalName(GUID* guid) {
    if (guid == nullptr) {
        return E_POINTER;
    }
    *guid = GUID_NULL;
    return S_OK;
}

IFACEMETHODIMP NobaExplorerCommand::GetState(IShellItemArray* items, BOOL okToBeSlow, EXPCMDSTATE* state) {
    if (state == nullptr) {
        return E_POINTER;
    }

    // The cold path, and the reason B1 is viable at all. Starting a runtime here would block the
    // menu; E_PENDING hands the work to the thread the shell provides for exactly this.
    if (!ClrHost::IsWarm() && !okToBeSlow) {
        *state = ECS_HIDDEN;
        return E_PENDING;
    }

    if (!EnsureMenu(items)) {
        // A bridge that could not answer shows nothing. It does not show an empty NOBA submenu, and
        // it does not fail the whole context menu.
        *state = ECS_HIDDEN;
        return S_OK;
    }

    *state = ToCommandState(menu_.rootState);
    return S_OK;
}

IFACEMETHODIMP NobaExplorerCommand::Invoke(IShellItemArray*, IBindCtx*) {
    // The root is a container. Clicking it opens the submenu; there is nothing to run.
    return S_OK;
}

IFACEMETHODIMP NobaExplorerCommand::GetFlags(EXPCMDFLAGS* flags) {
    if (flags == nullptr) {
        return E_POINTER;
    }
    *flags = ECF_HASSUBCOMMANDS;
    return S_OK;
}

IFACEMETHODIMP NobaExplorerCommand::EnumSubCommands(IEnumExplorerCommand** enumCommands) {
    if (enumCommands == nullptr) {
        return E_POINTER;
    }
    *enumCommands = nullptr;

    if (!menu_.valid) {
        return E_FAIL;
    }

    auto* enumerator = new (std::nothrow) NobaSubCommandEnum(menu_.entries, selection_);
    if (enumerator == nullptr) {
        return E_OUTOFMEMORY;
    }

    *enumCommands = enumerator;
    return S_OK;
}

}  // namespace noba
