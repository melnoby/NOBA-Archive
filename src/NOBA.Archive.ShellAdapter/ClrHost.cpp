#include "ClrHost.h"

#include <windows.h>

#include <mutex>
#include <string>

#include "Json.h"

namespace noba {
namespace {

// hostfxr's contract, declared here rather than pulled in from an SDK header. The signatures are
// part of a stable ABI; declaring them keeps this project buildable with nothing but the Windows SDK.
using char_t = wchar_t;

struct hostfxr_initialize_parameters {
    size_t size;
    const char_t* host_path;
    const char_t* dotnet_root;
};

using hostfxr_handle = void*;

using hostfxr_initialize_for_runtime_config_fn = int(__cdecl*)(
    const char_t* runtime_config_path,
    const hostfxr_initialize_parameters* parameters,
    hostfxr_handle* host_context_handle);

using hostfxr_get_runtime_delegate_fn = int(__cdecl*)(
    hostfxr_handle host_context_handle,
    int type,
    void** delegate);

using hostfxr_close_fn = int(__cdecl*)(hostfxr_handle host_context_handle);

// hdt_load_assembly_and_get_function_pointer
constexpr int kLoadAssemblyAndGetFunctionPointer = 5;

using load_assembly_and_get_function_pointer_fn = int(__cdecl*)(
    const char_t* assembly_path,
    const char_t* type_name,
    const char_t* method_name,
    const char_t* delegate_type_name,
    void* reserved,
    void** delegate);

// UNMANAGEDCALLERSONLY_METHOD. The bridge's exports carry [UnmanagedCallersOnly], so the delegate
// type name must be this sentinel rather than a managed type - passing a type name here would make
// the runtime look for a matching delegate and fail at load with a message about signatures.
const char_t* const kUnmanagedCallersOnly = reinterpret_cast<const char_t*>(-1);

std::once_flag g_once;
bool g_ready = false;
NobaShellQueryFn g_query = nullptr;
NobaShellFreeFn g_free = nullptr;
NobaShellWarmFn g_warmUp = nullptr;

// A two-step cast through void*, because casting FARPROC straight to a function pointer trips
// -Wcast-function-type and the suppression would hide a real mismatch elsewhere.
template <typename T>
T ProcAs(HMODULE module, const char* name) {
    void* address = reinterpret_cast<void*>(::GetProcAddress(module, name));
    return reinterpret_cast<T>(address);
}

// The directory this DLL was loaded from. The bridge assembly and its runtimeconfig sit beside it -
// in the sparse-package layout that is the external location, which is where the unpackaged build
// already put them.
std::wstring ModuleDirectory() {
    HMODULE self = nullptr;
    if (!::GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&ModuleDirectory),
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
    return (slash == std::wstring::npos) ? std::wstring() : full.substr(0, slash);
}

}  // namespace

bool ClrHost::Initialise() {
    const std::wstring directory = ModuleDirectory();
    if (directory.empty()) {
        return false;
    }

    HMODULE hostfxr = ::LoadLibraryExW(L"hostfxr.dll", nullptr, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (hostfxr == nullptr) {
        return false;
    }

    auto initialise = ProcAs<hostfxr_initialize_for_runtime_config_fn>(
        hostfxr, "hostfxr_initialize_for_runtime_config");
    auto getDelegate = ProcAs<hostfxr_get_runtime_delegate_fn>(hostfxr, "hostfxr_get_runtime_delegate");
    auto close = ProcAs<hostfxr_close_fn>(hostfxr, "hostfxr_close");
    if (initialise == nullptr || getDelegate == nullptr || close == nullptr) {
        return false;
    }

    const std::wstring config = directory + L"\\NOBA.Archive.ShellBridge.runtimeconfig.json";
    const std::wstring assembly = directory + L"\\NOBA.Archive.ShellBridge.dll";

    hostfxr_handle context = nullptr;
    if (initialise(config.c_str(), nullptr, &context) != 0 || context == nullptr) {
        if (context != nullptr) {
            close(context);
        }
        return false;
    }

    void* raw = nullptr;
    const int delegateResult = getDelegate(context, kLoadAssemblyAndGetFunctionPointer, &raw);
    // The context is closed either way: hostfxr keeps the runtime alive independently, and holding a
    // context open for the life of a surrogate leaks a handle into a process Explorer owns.
    close(context);
    if (delegateResult != 0 || raw == nullptr) {
        return false;
    }

    auto load = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(raw);
    const char_t* const type = L"NOBA.Archive.ShellBridge.ShellBridgeExports, NOBA.Archive.ShellBridge";

    void* query = nullptr;
    void* release = nullptr;
    void* warm = nullptr;
    if (load(assembly.c_str(), type, L"NobaShellQuery", kUnmanagedCallersOnly, nullptr, &query) != 0 ||
        load(assembly.c_str(), type, L"NobaShellFree", kUnmanagedCallersOnly, nullptr, &release) != 0 ||
        load(assembly.c_str(), type, L"NobaShellWarm", kUnmanagedCallersOnly, nullptr, &warm) != 0) {
        return false;
    }

    g_query = reinterpret_cast<NobaShellQueryFn>(query);
    g_free = reinterpret_cast<NobaShellFreeFn>(release);
    g_warmUp = reinterpret_cast<NobaShellWarmFn>(warm);

    // The engine is built on this call rather than on the first menu, so the first right-click after
    // a warm-up does not pay for a provider registry.
    return g_warmUp() == 0;
}

bool ClrHost::IsWarm() {
    return g_ready;
}

bool ClrHost::Warm() {
    std::call_once(g_once, []() { g_ready = Initialise(); });
    return g_ready;
}

bool ClrHost::Query(const std::string& requestJson, std::string& response) {
    if (!Warm() || g_query == nullptr || g_free == nullptr) {
        return false;
    }

    char* raw = nullptr;
    int length = 0;
    if (g_query(requestJson.c_str(), &raw, &length) != 0 || raw == nullptr) {
        return false;
    }

    response.assign(raw, static_cast<size_t>(length));
    g_free(raw);
    return true;
}

}  // namespace noba
