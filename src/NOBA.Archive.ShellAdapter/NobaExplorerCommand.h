// DROP 08 Phase 5 - the submenu root, and the only object the shell creates for us.
//
// THIS IS WHERE THE COLD START IS HANDLED. GetState receives fOkToBeSlow. When the runtime is not up
// and the shell says it will not wait, the documented answer is E_PENDING: the shell re-asks on a
// background thread, and the warm-up runs there instead of inside the held right-click (plan
// section 0.2). Using it is the platform's own mechanism for this situation, not a workaround.
//
// ONE ROUND TRIP PER DRAW. The menu is fetched once and cached on this object; the children answer
// from that snapshot.

#pragma once

#include <shobjidl.h>

#include "MenuModel.h"
#include "Selection.h"

namespace noba {

class NobaExplorerCommand : public IExplorerCommand {
 public:
    NobaExplorerCommand() = default;

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
    virtual ~NobaExplorerCommand() = default;

    // Fetches the menu once. Returns false when the bridge could not answer, which every caller
    // turns into "show nothing" rather than into an empty submenu.
    bool EnsureMenu(IShellItemArray* items);

    LONG references_ = 1;
    MenuSnapshot menu_;
    std::vector<SelectedItem> selection_;
    bool fetched_ = false;
};

}  // namespace noba
