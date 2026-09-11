#include "Selection.h"

#include <windows.h>
#include <shlwapi.h>

#include "Json.h"

namespace noba {
namespace {

void AppendItems(std::string& out, const std::vector<SelectedItem>& items) {
    out.append("\"items\":[");
    bool first = true;
    for (const SelectedItem& item : items) {
        if (!first) {
            out.push_back(',');
        }
        first = false;
        out.append("{\"path\":");
        AppendJsonString(out, ToUtf8(item.path));
        out.append(",\"isDirectory\":");
        out.append(item.isDirectory ? "true" : "false");
        out.push_back('}');
    }
    out.push_back(']');
}

}  // namespace

std::vector<SelectedItem> ReadSelection(IShellItemArray* items) {
    std::vector<SelectedItem> selection;
    if (items == nullptr) {
        return selection;
    }

    DWORD count = 0;
    if (FAILED(items->GetCount(&count))) {
        return selection;
    }

    for (DWORD i = 0; i < count; ++i) {
        IShellItem* item = nullptr;
        if (FAILED(items->GetItemAt(i, &item)) || item == nullptr) {
            continue;
        }

        LPWSTR path = nullptr;
        // SIGDN_FILESYSPATH fails for anything without one - a library, a search result, a device.
        // Skipping such an item is correct: the engine takes paths, and inventing one would be the
        // adapter deciding something.
        if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path != nullptr) {
            SelectedItem entry;
            entry.path = path;

            SFGAOF attributes = 0;
            if (SUCCEEDED(item->GetAttributes(SFGAO_FOLDER, &attributes))) {
                entry.isDirectory = (attributes & SFGAO_FOLDER) != 0;
            }

            selection.push_back(entry);
            ::CoTaskMemFree(path);
        }

        item->Release();
    }

    return selection;
}

std::string BuildMenuRequest(const std::vector<SelectedItem>& items, const std::wstring& language) {
    std::string out;
    out.append("{\"kind\":\"menu\",\"language\":");
    AppendJsonString(out, ToUtf8(language));
    out.push_back(',');
    AppendItems(out, items);
    out.push_back('}');
    return out;
}

std::string BuildInvokeRequest(
    const std::wstring& entryId,
    const std::vector<SelectedItem>& items,
    const std::wstring& language) {
    std::string out;
    out.append("{\"kind\":\"invoke\",\"entryId\":");
    AppendJsonString(out, ToUtf8(entryId));
    out.append(",\"language\":");
    AppendJsonString(out, ToUtf8(language));
    out.push_back(',');
    AppendItems(out, items);
    out.push_back('}');
    return out;
}

std::wstring CurrentUiLanguage() {
    wchar_t name[LOCALE_NAME_MAX_LENGTH] = {};
    if (::GetUserDefaultLocaleName(name, LOCALE_NAME_MAX_LENGTH) > 0) {
        return std::wstring(name);
    }
    return std::wstring();
}

}  // namespace noba
