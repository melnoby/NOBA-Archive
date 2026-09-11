// DROP 08 Phase 5 - the menu as the adapter is allowed to know it.
//
// An id, a title, and one of three states. That is the adapter's entire vocabulary (plan section
// B.1), and SHELL-063 makes it enforceable: no format name, action name or capability may appear in
// this project outside a comment.
//
// ONE ROUND TRIP PER DRAW. Explorer calls GetState, then GetFlags, then EnumSubCommands, then
// GetTitle and GetState on every child. Asking the bridge each time would multiply a measured cost
// by the number of entries inside a held right-click. The snapshot is fetched once per menu object
// and every child answers from it.

#pragma once

#include <shobjidl.h>

#include <string>
#include <vector>

#include "Selection.h"

namespace noba {

enum class EntryState {
    Hidden = 0,
    Disabled,
    Enabled,
};

struct MenuEntry {
    std::wstring id;
    std::wstring title;
    EntryState state = EntryState::Hidden;
};

struct MenuSnapshot {
    bool valid = false;
    std::wstring rootTitle;
    EntryState rootState = EntryState::Hidden;
    std::vector<MenuEntry> entries;
};

// Asks the bridge for the submenu. An unreachable bridge, a refusal, or a malformed response all
// produce valid == false, which every caller treats as "show nothing" - never as an empty menu that
// happens to be drawn.
MenuSnapshot FetchMenu(const std::vector<SelectedItem>& selection);

// Maps a state to what IExplorerCommand::GetState must return.
EXPCMDSTATE ToCommandState(EntryState state);

}  // namespace noba
