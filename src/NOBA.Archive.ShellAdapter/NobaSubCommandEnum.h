// DROP 08 Phase 5 - the submenu's children, handed to the shell one at a time.
//
// A snapshot, not a live view. The entries were decided when the root was asked; enumerating them
// again against a changed selection would produce a menu whose lines disagree with its own root.

#pragma once

#include <shobjidl.h>

#include <vector>

#include "MenuModel.h"
#include "Selection.h"

namespace noba {

class NobaSubCommandEnum : public IEnumExplorerCommand {
 public:
    NobaSubCommandEnum(const std::vector<MenuEntry>& entries, const std::vector<SelectedItem>& selection);

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    // IEnumExplorerCommand
    IFACEMETHODIMP Next(ULONG celt, IExplorerCommand** commands, ULONG* fetched) override;
    IFACEMETHODIMP Skip(ULONG celt) override;
    IFACEMETHODIMP Reset() override;
    IFACEMETHODIMP Clone(IEnumExplorerCommand** copy) override;

 private:
    virtual ~NobaSubCommandEnum() = default;

    LONG references_ = 1;
    std::vector<MenuEntry> entries_;
    std::vector<SelectedItem> selection_;
    size_t position_ = 0;
};

}  // namespace noba
