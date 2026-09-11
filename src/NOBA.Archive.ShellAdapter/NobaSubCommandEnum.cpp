#include "NobaSubCommandEnum.h"

#include <windows.h>

#include <new>

#include "NobaSubCommand.h"

namespace noba {

NobaSubCommandEnum::NobaSubCommandEnum(
    const std::vector<MenuEntry>& entries,
    const std::vector<SelectedItem>& selection)
    : entries_(entries), selection_(selection) {}

IFACEMETHODIMP NobaSubCommandEnum::QueryInterface(REFIID riid, void** ppv) {
    if (ppv == nullptr) {
        return E_POINTER;
    }
    if (riid == IID_IUnknown || riid == IID_IEnumExplorerCommand) {
        *ppv = static_cast<IEnumExplorerCommand*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

IFACEMETHODIMP_(ULONG) NobaSubCommandEnum::AddRef() {
    return static_cast<ULONG>(::InterlockedIncrement(&references_));
}

IFACEMETHODIMP_(ULONG) NobaSubCommandEnum::Release() {
    const LONG remaining = ::InterlockedDecrement(&references_);
    if (remaining == 0) {
        delete this;
    }
    return static_cast<ULONG>(remaining);
}

IFACEMETHODIMP NobaSubCommandEnum::Next(ULONG celt, IExplorerCommand** commands, ULONG* fetched) {
    if (commands == nullptr) {
        return E_POINTER;
    }

    ULONG produced = 0;
    while (produced < celt && position_ < entries_.size()) {
        // A hidden entry is skipped rather than handed over with ECS_HIDDEN. Both draw nothing, but
        // an object the shell never needs is an object nobody has to keep correct.
        const MenuEntry& entry = entries_[position_++];
        if (entry.state == EntryState::Hidden) {
            continue;
        }

        auto* command = new (std::nothrow) NobaSubCommand(entry, selection_);
        if (command == nullptr) {
            break;
        }
        commands[produced++] = command;
    }

    if (fetched != nullptr) {
        *fetched = produced;
    }
    return (produced == celt) ? S_OK : S_FALSE;
}

IFACEMETHODIMP NobaSubCommandEnum::Skip(ULONG celt) {
    position_ += celt;
    if (position_ > entries_.size()) {
        position_ = entries_.size();
        return S_FALSE;
    }
    return S_OK;
}

IFACEMETHODIMP NobaSubCommandEnum::Reset() {
    position_ = 0;
    return S_OK;
}

IFACEMETHODIMP NobaSubCommandEnum::Clone(IEnumExplorerCommand** copy) {
    if (copy == nullptr) {
        return E_POINTER;
    }
    *copy = nullptr;
    return E_NOTIMPL;
}

}  // namespace noba
