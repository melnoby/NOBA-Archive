// DROP 08 Phase 5 - turning what Explorer selected into what the bridge is asked.
//
// This is the entire vocabulary the native side is allowed (plan section B.1): a path, a folder flag,
// an opaque entry id, and JSON. SHELL-063 forbids a format name, an action name or a capability from
// appearing anywhere in this project outside a comment, and the build fails if one does.
//
// The folder flag is ASKED OF THE SHELL, never inferred from the name. A directory called
// Backups.zip is a directory, and a handler that guessed from the extension would offer to unpack a
// folder - an offer the engine cannot honour.

#pragma once

#include <shobjidl.h>

#include <string>
#include <vector>

namespace noba {

struct SelectedItem {
    std::wstring path;
    bool isDirectory = false;
};

// Reads every item Explorer passed, in Explorer's order.
//
// Order is preserved deliberately: a multi-selection becomes ONE request carrying all of it, and the
// first item decides where the result is written. Re-ordering here would move somebody's output.
std::vector<SelectedItem> ReadSelection(IShellItemArray* items);

// {"kind":"menu","language":"...","items":[...]}
std::string BuildMenuRequest(const std::vector<SelectedItem>& items, const std::wstring& language);

// {"kind":"invoke","entryId":"...","language":"...","items":[...]}
std::string BuildInvokeRequest(
    const std::wstring& entryId,
    const std::vector<SelectedItem>& items,
    const std::wstring& language);

// The UI language tag for this process, for the bridge to resolve labels against. The adapter reads
// the tag and does nothing with it - the string table is managed-side (plan section E.1).
std::wstring CurrentUiLanguage();

}  // namespace noba
