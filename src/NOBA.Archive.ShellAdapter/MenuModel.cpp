#include "MenuModel.h"

#include "ClrHost.h"
#include "Json.h"

namespace noba {
namespace {

// The three wire names, compared literally. They are a contract with the managed side, which is why
// an unrecognised value maps to Hidden rather than to something visible: a state nobody agreed on
// must never become an enabled menu item.
EntryState StateFromWire(const std::string& value) {
    if (value == "enabled") {
        return EntryState::Enabled;
    }
    if (value == "disabled") {
        return EntryState::Disabled;
    }
    return EntryState::Hidden;
}

}  // namespace

MenuSnapshot FetchMenu(const std::vector<SelectedItem>& selection) {
    MenuSnapshot snapshot;

    std::string response;
    if (!ClrHost::Query(BuildMenuRequest(selection, CurrentUiLanguage()), response)) {
        return snapshot;
    }

    // "ok" is asked before anything is read. A refusal and an empty menu are different answers, and
    // the bridge is explicit about which it is giving - drawing a submenu for a query that was never
    // answered is the failure this check exists to prevent.
    JsonObject menu;
    if (!ObjectAt(response, "menu", menu)) {
        return snapshot;
    }

    std::string rootTitle;
    std::string rootState;
    if (!ObjectString(menu, "rootTitle", rootTitle) || !ObjectString(menu, "rootState", rootState)) {
        return snapshot;
    }

    snapshot.rootTitle = ToUtf16(rootTitle);
    snapshot.rootState = StateFromWire(rootState);

    const std::string menuDocument(menu.begin, static_cast<size_t>(menu.end - menu.begin));
    for (const JsonObject& item : ArrayOfObjects(menuDocument, "entries")) {
        std::string id;
        std::string title;
        std::string state;
        if (!ObjectString(item, "id", id) ||
            !ObjectString(item, "title", title) ||
            !ObjectString(item, "state", state)) {
            // One malformed entry invalidates the draw rather than being skipped. A menu missing a
            // line it should have had is worse than no menu: it looks like the product decided.
            return MenuSnapshot();
        }

        MenuEntry entry;
        entry.id = ToUtf16(id);
        entry.title = ToUtf16(title);
        entry.state = StateFromWire(state);
        snapshot.entries.push_back(entry);
    }

    snapshot.valid = true;
    return snapshot;
}

EXPCMDSTATE ToCommandState(EntryState state) {
    switch (state) {
        case EntryState::Enabled:
            return ECS_ENABLED;
        case EntryState::Disabled:
            // SHELL-L9. The classic surface had no third state, so a line the model could only
            // describe as "shown and greyed" had to be omitted. This is where that finally arrives.
            return ECS_DISABLED;
        default:
            return ECS_HIDDEN;
    }
}

}  // namespace noba
