#include "NobaSubCommand.h"

#include <windows.h>
#include <shlwapi.h>

#include "ClrHost.h"
#include "Json.h"

namespace noba {
namespace {

// Win32 has no API that takes an argument ARRAY. CreateProcessW takes one string, and so does
// ShellExecuteEx. So the array the bridge produced has to be serialised - and the only correct way to
// do that is the documented inverse of CommandLineToArgvW, which is what every .NET and C runtime
// uses to parse it back.
//
// This is stated plainly rather than described as "no command string": the plan's requirement is
// that no SHELL is involved and that the adapter never composes a command from parts it understood.
// It does neither. It quotes opaque strings by a documented rule, and noba-ar.exe parses them back
// by the matching one. cmd.exe is never in the picture, so a path containing & or | is a path.
std::wstring QuoteArgument(const std::wstring& argument) {
    if (!argument.empty() &&
        argument.find_first_of(L" \t\n\v\"") == std::wstring::npos) {
        return argument;
    }

    std::wstring quoted;
    quoted.push_back(L'"');
    for (auto it = argument.begin();; ++it) {
        size_t backslashes = 0;
        while (it != argument.end() && *it == L'\\') {
            ++it;
            ++backslashes;
        }

        if (it == argument.end()) {
            // Backslashes immediately before the closing quote must be doubled, or they escape it.
            quoted.append(backslashes * 2, L'\\');
            break;
        }

        if (*it == L'"') {
            quoted.append(backslashes * 2 + 1, L'\\');
        } else {
            quoted.append(backslashes, L'\\');
        }
        quoted.push_back(*it);
    }
    quoted.push_back(L'"');
    return quoted;
}

// The executable, resolved beside this DLL. The bridge names a file, never a path: deciding where
// the product is installed is not the adapter's business, and an absolute path from the managed side
// would have to survive the sparse package's external location.
std::wstring ResolveExecutable(const std::wstring& fileName) {
    HMODULE self = nullptr;
    if (!::GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&ResolveExecutable),
            &self)) {
        return std::wstring();
    }

    wchar_t path[MAX_PATH] = {};
    const DWORD length = ::GetModuleFileNameW(self, path, MAX_PATH);
    if (length == 0 || length == MAX_PATH) {
        return std::wstring();
    }

    std::wstring full(path, length);
    const size_t slash = full.find_last_of(L'\\');
    if (slash == std::wstring::npos) {
        return std::wstring();
    }
    return full.substr(0, slash + 1) + fileName;
}

HRESULT StartPlan(const std::wstring& executable, const std::vector<std::wstring>& arguments) {
    const std::wstring path = ResolveExecutable(executable);
    if (path.empty() || ::GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) {
        return E_FAIL;
    }

    std::wstring commandLine = QuoteArgument(path);
    for (const std::wstring& argument : arguments) {
        commandLine.push_back(L' ');
        commandLine.append(QuoteArgument(argument));
    }

    STARTUPINFOW startup = {};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};

    // No elevation moniker, no RunAs, no shell. The surrogate already runs as the standard user
    // (plan section L.4, proven by E2 criterion 3), and the child inherits that. Elevating here would
    // make the whole elevation boundary a convention instead of a property.
    std::wstring mutableCommandLine = commandLine;
    if (!::CreateProcessW(
            path.c_str(),
            &mutableCommandLine[0],
            nullptr,
            nullptr,
            FALSE,
            CREATE_UNICODE_ENVIRONMENT,
            nullptr,
            nullptr,
            &startup,
            &process)) {
        return HRESULT_FROM_WIN32(::GetLastError());
    }

    ::CloseHandle(process.hThread);
    ::CloseHandle(process.hProcess);
    return S_OK;
}

}  // namespace

NobaSubCommand::NobaSubCommand(const MenuEntry& entry, const std::vector<SelectedItem>& selection)
    : entry_(entry), selection_(selection) {}

IFACEMETHODIMP NobaSubCommand::QueryInterface(REFIID riid, void** ppv) {
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

IFACEMETHODIMP_(ULONG) NobaSubCommand::AddRef() {
    return static_cast<ULONG>(::InterlockedIncrement(&references_));
}

IFACEMETHODIMP_(ULONG) NobaSubCommand::Release() {
    const LONG remaining = ::InterlockedDecrement(&references_);
    if (remaining == 0) {
        delete this;
    }
    return static_cast<ULONG>(remaining);
}

IFACEMETHODIMP NobaSubCommand::GetTitle(IShellItemArray*, LPWSTR* name) {
    if (name == nullptr) {
        return E_POINTER;
    }
    return ::SHStrDupW(entry_.title.c_str(), name);
}

IFACEMETHODIMP NobaSubCommand::GetIcon(IShellItemArray*, LPWSTR* icon) {
    if (icon == nullptr) {
        return E_POINTER;
    }
    *icon = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP NobaSubCommand::GetToolTip(IShellItemArray*, LPWSTR* infoTip) {
    if (infoTip == nullptr) {
        return E_POINTER;
    }
    // Whether the shell consults this for a DISABLED command is unverified and is measured by
    // section G.5. Until it is answered, nothing is claimed here: the blocker reason crosses the ABI
    // and is available the moment there is a surface that shows it.
    *infoTip = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP NobaSubCommand::GetCanonicalName(GUID* guid) {
    if (guid == nullptr) {
        return E_POINTER;
    }
    *guid = GUID_NULL;
    return S_OK;
}

IFACEMETHODIMP NobaSubCommand::GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) {
    if (state == nullptr) {
        return E_POINTER;
    }
    // Answered from the snapshot the root already paid for. No E_PENDING here: by the time a child is
    // asked, the runtime is warm - the root's GetState is what handled the cold case.
    *state = ToCommandState(entry_.state);
    return S_OK;
}

IFACEMETHODIMP NobaSubCommand::Invoke(IShellItemArray* items, IBindCtx*) {
    // Re-asked, not trusted. The snapshot describes the moment the menu was drawn; between then and
    // the click the selection can change, a file can be deleted, a provider can fail to load.
    const std::vector<SelectedItem> current = (items != nullptr) ? ReadSelection(items) : selection_;

    std::string response;
    if (!ClrHost::Query(BuildInvokeRequest(entry_.id, current, CurrentUiLanguage()), response)) {
        return E_FAIL;
    }

    JsonObject invoke;
    if (!ObjectAt(response, "invoke", invoke)) {
        // The bridge refused: the entry is not available for this selection any more. Nothing is
        // started, and the failure is the shell's to report.
        return E_ACCESSDENIED;
    }

    std::string executable;
    if (!ObjectString(invoke, "executable", executable) || executable.empty()) {
        return E_FAIL;
    }

    std::vector<std::wstring> arguments;
    for (const std::string& argument : ObjectStringArray(invoke, "arguments")) {
        arguments.push_back(ToUtf16(argument));
    }

    return StartPlan(ToUtf16(executable), arguments);
}

IFACEMETHODIMP NobaSubCommand::GetFlags(EXPCMDFLAGS* flags) {
    if (flags == nullptr) {
        return E_POINTER;
    }
    *flags = ECF_DEFAULT;
    return S_OK;
}

IFACEMETHODIMP NobaSubCommand::EnumSubCommands(IEnumExplorerCommand** enumCommands) {
    if (enumCommands == nullptr) {
        return E_POINTER;
    }
    *enumCommands = nullptr;
    return E_NOTIMPL;
}

}  // namespace noba
