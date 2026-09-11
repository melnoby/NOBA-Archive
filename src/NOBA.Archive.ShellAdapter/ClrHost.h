// DROP 08 Phase 5 - hosting the CLR inside the surrogate, and only inside the surrogate.
//
// THE MEASURED FACTS THIS IS BUILT ON. Gate 1 measured the round trip; E1 proved the CLR lands in
// dllhost.exe rather than in the client; E2 proved the same for the PACKAGED com:SurrogateServer
// path, with the surrogate running as the standard user (DELTA section 18). None of that is assumed
// here - it is why B1 was chosen over B2, which would have put this runtime into explorer.exe.
//
// NO nethost DEPENDENCY. hostfxr is loaded by name and its three entry points fetched with
// GetProcAddress, exactly as the G.1 host and the E1/E2 probes did. Linking nethost.lib would add a
// build-time dependency on an SDK component for the sake of one path lookup, and the sparse package
// puts the payload at an external location the loader already has to be told about.
//
// THE COLD START IS THE RISK, AND THE PLATFORM HAS THE ANSWER. GetState receives fOkToBeSlow; when it
// is FALSE and the runtime is not up, the documented return is E_PENDING and the shell re-asks on a
// background thread (plan section 0.2). So this exposes "is it warm" separately from "answer this",
// and the warm-up runs on the thread the shell provides for it.

#pragma once

#include <string>

namespace noba {

// The single ABI entry point (plan section B.3): UTF-8 in, UTF-8 out, allocated managed-side.
using NobaShellQueryFn = int (*)(const char* requestJson, char** responseJson, int* responseLength);
using NobaShellFreeFn = void (*)(char* responseJson);
using NobaShellWarmFn = int (*)();

// The runtime and the managed bridge, started once per surrogate process.
class ClrHost {
 public:
    // True when a query can be answered without starting anything.
    static bool IsWarm();

    // Starts the runtime and resolves the bridge's exports. Safe to call from more than one thread
    // and safe to call repeatedly; the shell calls GetState on several threads and two of them racing
    // to start a runtime inside a right-click is not a failure anybody would reproduce on purpose.
    static bool Warm();

    // Answers one query. Returns false when the bridge could not be reached or refused to answer, in
    // which case response is untouched. Plan section E.3: on any bridge failure the adapter starts
    // nothing.
    static bool Query(const std::string& requestJson, std::string& response);

 private:
    static bool Initialise();
};

}  // namespace noba
