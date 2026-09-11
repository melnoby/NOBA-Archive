// DROP 08 Phase 5 - one submenu line.
//
// Holds a snapshot of what the bridge said when the menu was drawn, and answers GetTitle/GetState
// from it without another round trip. Invoke does NOT trust that snapshot: the selection can change
// between the draw and the click, so availability is re-asked (plan section E.3).

#pragma once

#include <shobjidl.h>

#include <string>
#include <vector>

#include "MenuModel.h"
#include "Selection.h"

namespace noba {

class NobaSubCommand : public IExplorerCommand {
 public:
    NobaSubCommand(const MenuEntry& entry, const std::vector<SelectedItem>& selection);

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray* items, LPWSTR* name) override;
    IFACEMETHODIMP GetIcon(IShellItemArray* items, LPWSTR* icon) override;
    IFACEMETHODIMP GetToolTip(IShellItemArray* items, LPWSTR* infoTip) override;
    IFACEMETHODIMP GetCanonicalName(GUID* guid) override;
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL okToBeSlow, EXPCMDSTATE* state) override;
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx* bindContext) override;
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override;
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** enumCommands) override;

 private:
    // Virtual, because Release() does `delete this` through a base pointer. Non-virtual here is
    // undefined behaviour that happens to work until a member needs destroying - and this one holds
    // two std::wstring and a vector.
    virtual ~NobaSubCommand() = default;

    LONG references_ = 1;
    MenuEntry entry_;
    std::vector<SelectedItem> selection_;
};

}  // namespace noba
