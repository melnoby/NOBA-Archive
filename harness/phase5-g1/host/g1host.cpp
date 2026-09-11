// =============================================================================================
// DROP 08 Phase 5 - G.1 FEASIBILITY HARNESS. NOT PRODUCT CODE. NOT A SHELL EXTENSION.
//
// This is a plain executable, deliberately. G.1 is a feasibility gate, not permission to build the
// adapter, so there is no IExplorerCommand here, no COM, no registration and no packaging. It does
// exactly one thing: host the .NET runtime the way a com:SurrogateServer DLL would, call the managed
// bridge, and time it.
//
// WHAT IT MEASURES
//   A  can the native side activate the managed bridge at all
//   B  cold start - process start through the first menu, broken into its parts
//   C  warm - the steady-state cost of one menu
//   F  what a managed failure does to the host
//   G  what a slow managed call does, and who is responsible for the timeout
//
// WHAT IT CANNOT MEASURE, AND WILL NOT PRETEND TO
//   D  whether GetState must return E_PENDING - derived from B and C, not observed
//   E  isolation from explorer.exe - that is a property of com:SurrogateServer registration, which
//      is out of G.1 scope and can only be shown with the real package
//
// Builds on Linux (g++) and Windows (MSVC). The hostfxr contract is identical on both; the runtime
// declarations below are written out rather than included so the harness needs no SDK header paths
// on either platform.
// =============================================================================================

#include <cstdio>
#include <cstring>
#include <cstdint>
#include <string>
#include <vector>
#include <chrono>
#include <algorithm>
#include <thread>
#include <atomic>

#ifdef _WIN32
#  include <windows.h>
#  define NOBA_CHAR wchar_t
#  define NOBA_STR(s) L##s
   typedef HMODULE noba_lib;
   static noba_lib noba_load(const char* p) { return ::LoadLibraryA(p); }
   static void* noba_sym(noba_lib h, const char* n) { return (void*)::GetProcAddress(h, n); }

   // Widen through the ACP rather than std::wstring(s.begin(), s.end()). The naive form copies each
   // byte into a wchar_t, which is correct only while every byte is ASCII: a path under a user
   // folder such as C:\Users\محمد\... would silently become mojibake and hostfxr would report a
   // missing file for a file that exists. This harness is expected to be run from paths it did not
   // choose, so it does the conversion properly.
   static std::wstring noba_widen(const std::string& s) {
       if (s.empty()) { return std::wstring(); }
       const int n = ::MultiByteToWideChar(CP_ACP, 0, s.c_str(), (int)s.size(), nullptr, 0);
       std::wstring w((size_t)n, L'\0');
       ::MultiByteToWideChar(CP_ACP, 0, s.c_str(), (int)s.size(), &w[0], n);
       return w;
   }
#else
#  include <dlfcn.h>
#  define NOBA_CHAR char
#  define NOBA_STR(s) s
   typedef void* noba_lib;
   static noba_lib noba_load(const char* p) { return ::dlopen(p, RTLD_LAZY | RTLD_LOCAL); }
   static void* noba_sym(noba_lib h, const char* n) { return ::dlsym(h, n); }
#endif

// --- hostfxr / coreclr delegate surface, declared rather than included -----------------------
typedef void* hostfxr_handle;
struct hostfxr_initialize_parameters { size_t size; const NOBA_CHAR* host_path; const NOBA_CHAR* dotnet_root; };

typedef int32_t (*hostfxr_initialize_for_runtime_config_fn)(
    const NOBA_CHAR* runtime_config_path, const hostfxr_initialize_parameters* params, hostfxr_handle* host_context_handle);
typedef int32_t (*hostfxr_get_runtime_delegate_fn)(hostfxr_handle host_context_handle, int32_t type, void** delegate);
typedef int32_t (*hostfxr_close_fn)(hostfxr_handle host_context_handle);

typedef int32_t (*load_assembly_and_get_function_pointer_fn)(
    const NOBA_CHAR* assembly_path, const NOBA_CHAR* type_name, const NOBA_CHAR* method_name,
    const NOBA_CHAR* delegate_type_name, void* reserved, void** delegate);

static const int32_t hdt_load_assembly_and_get_function_pointer = 5;

// UNMANAGEDCALLERSONLY_METHOD - the sentinel that says "this method is [UnmanagedCallersOnly]".
static const NOBA_CHAR* UNMANAGED_CALLERS_ONLY = (const NOBA_CHAR*)(-1);

typedef int32_t (*noba_query_fn)(const char* requestUtf8, char** responseUtf8, int32_t* responseLength);
typedef void    (*noba_free_fn)(char* buffer);
typedef int32_t (*noba_warm_fn)();

// --- timing -----------------------------------------------------------------------------------
using clock_t_ = std::chrono::steady_clock;
static double ms_since(clock_t_::time_point from) {
    return std::chrono::duration<double, std::milli>(clock_t_::now() - from).count();
}

static void report(const char* label, double ms) {
    std::printf("  %-46s %10.2f ms\n", label, ms);
}

// --- main -------------------------------------------------------------------------------------
int main(int argc, char** argv) {
    if (argc < 4) {
        std::printf("usage: g1host <hostfxr-path> <bridge-dir> <runtimeconfig-path> [warm-iterations]\n");
        return 2;
    }

    const std::string hostfxrPath = argv[1];
    const std::string bridgeDir   = argv[2];
    const std::string configPath  = argv[3];
    // Parsed defensively. atoi("--split") is 0, which would have produced an empty sample vector and
    // then read samples.front() off the end of it — a harness that crashes while reporting how
    // reliable something is proves the opposite of what it set out to prove.
    int warmIterations = 50;
    if (argc > 4 && argv[4][0] >= '0' && argv[4][0] <= '9') { warmIterations = std::atoi(argv[4]); }
    if (warmIterations < 1) { warmIterations = 1; }

    const auto processStart = clock_t_::now();

    std::printf("==============================================================================\n");
    std::printf("DROP 08 Phase 5 - G.1 feasibility harness (NOT product code)\n");
    std::printf("==============================================================================\n\n");

    // --- A: activation ------------------------------------------------------------------------
    std::printf("A. ACTIVATION\n");

    auto t = clock_t_::now();
    noba_lib fxr = noba_load(hostfxrPath.c_str());
    if (!fxr) { std::printf("  FAIL  could not load hostfxr from %s\n", hostfxrPath.c_str()); return 1; }
    report("load hostfxr", ms_since(t));

    auto init  = (hostfxr_initialize_for_runtime_config_fn)noba_sym(fxr, "hostfxr_initialize_for_runtime_config");
    auto getd  = (hostfxr_get_runtime_delegate_fn)noba_sym(fxr, "hostfxr_get_runtime_delegate");
    auto close = (hostfxr_close_fn)noba_sym(fxr, "hostfxr_close");
    if (!init || !getd || !close) { std::printf("  FAIL  hostfxr exports missing\n"); return 1; }

#ifdef _WIN32
    std::wstring wconfig = noba_widen(configPath);
    const NOBA_CHAR* configArg = wconfig.c_str();
#else
    const NOBA_CHAR* configArg = configPath.c_str();
#endif

    t = clock_t_::now();
    hostfxr_handle ctx = nullptr;
    int32_t rc = init(configArg, nullptr, &ctx);
    if (rc > 2 || ctx == nullptr) { std::printf("  FAIL  hostfxr_initialize_for_runtime_config rc=0x%x\n", rc); return 1; }
    const double initMs = ms_since(t);
    report("hostfxr_initialize_for_runtime_config", initMs);

    t = clock_t_::now();
    void* loadFnRaw = nullptr;
    rc = getd(ctx, hdt_load_assembly_and_get_function_pointer, &loadFnRaw);
    if (rc != 0 || !loadFnRaw) { std::printf("  FAIL  get_runtime_delegate rc=0x%x\n", rc); return 1; }
    const double delegateMs = ms_since(t);
    report("hostfxr_get_runtime_delegate (starts CLR)", delegateMs);

    auto loadFn = (load_assembly_and_get_function_pointer_fn)loadFnRaw;

    const std::string asmPath = bridgeDir + "/NOBA.Phase5.G1.Bridge.dll";
#ifdef _WIN32
    std::wstring wasm = noba_widen(asmPath);
    const NOBA_CHAR* asmArg = wasm.c_str();
#else
    const NOBA_CHAR* asmArg = asmPath.c_str();
#endif

    t = clock_t_::now();
    noba_query_fn query = nullptr;
    rc = loadFn(asmArg,
                NOBA_STR("NOBA.Phase5.G1.BridgeExports, NOBA.Phase5.G1.Bridge"),
                NOBA_STR("NobaShellQuery"),
                UNMANAGED_CALLERS_ONLY, nullptr, (void**)&query);
    if (rc != 0 || !query) { std::printf("  FAIL  could not resolve NobaShellQuery rc=0x%x\n", rc); return 1; }
    const double resolveMs = ms_since(t);
    report("load assembly + resolve NobaShellQuery", resolveMs);

    noba_free_fn freeFn = nullptr;
    loadFn(asmArg, NOBA_STR("NOBA.Phase5.G1.BridgeExports, NOBA.Phase5.G1.Bridge"),
           NOBA_STR("NobaShellFree"), UNMANAGED_CALLERS_ONLY, nullptr, (void**)&freeFn);

    std::printf("  PASS  the native host activated the managed bridge\n\n");

    noba_warm_fn warmFn = nullptr;
    loadFn(asmArg, NOBA_STR("NOBA.Phase5.G1.BridgeExports, NOBA.Phase5.G1.Bridge"),
           NOBA_STR("NobaShellWarm"), UNMANAGED_CALLERS_ONLY, nullptr, (void**)&warmFn);

    // --- C3: cold race ----------------------------------------------------------------------------
    // A separate mode, because it has to be the FIRST thing that touches the engine. EnsureEngine is
    // a double-checked lock; the case that can break it is several threads arriving before the engine
    // exists, and that case is gone forever once any other section has run. So this mode reports and
    // exits rather than continuing into a run whose "cold" figures would already be warm.
    bool coldRace = false;
    for (int i = 4; i < argc; ++i) {
        if (std::strcmp(argv[i], "--race") == 0) { coldRace = true; }
    }
    if (coldRace) {
        std::printf("C3. COLD RACE - 4 threads hit an unbuilt engine simultaneously\n");
        const char* raceReq =
            "{\"items\":[{\"path\":\"C:\\\\D\\\\a.txt\",\"isDirectory\":false}]}";
        std::atomic<int> bad{0};
        std::vector<double> firsts(4, 0.0);
        std::vector<std::thread> pool;
        const auto raceStart = clock_t_::now();
        for (int ti = 0; ti < 4; ++ti) {
            pool.emplace_back([&, ti]() {
                char* r = nullptr; int32_t len = 0;
                const auto t0 = clock_t_::now();
                const int32_t crc = query(raceReq, &r, &len);
                firsts[(size_t)ti] = ms_since(t0);
                if (crc != 0) { bad.fetch_add(1); }
                if (r && freeFn) { freeFn(r); }
            });
        }
        for (auto& th : pool) { th.join(); }
        report("wall clock until all 4 returned", ms_since(raceStart));
        for (int ti = 0; ti < 4; ++ti) {
            char label[64];
            std::snprintf(label, sizeof(label), "thread %d first call", ti);
            report(label, firsts[(size_t)ti]);
        }
        std::printf("  failures: %d %s\n", bad.load(),
                    bad.load() == 0 ? "- the engine was built exactly once, safely" : "- A CONTENDED COLD CALL FAILED");
        std::printf("\n  This mode stops here on purpose: every later section would be measuring a\n");
        std::printf("  warm engine and calling it cold.\n");
        close(ctx);
        return bad.load() == 0 ? 0 : 1;
    }

    // --- B1: R2 evidence - warming that builds a MENU, not just the container ---------------------
    // Its own mode and its own process, for the same reason as the race: it has to be the first thing
    // that touches the engine.
    //
    // WHY THIS EXISTS. The split run showed the cold cost is roughly half engine construction and
    // half first-ShellMenuBuilder-call JIT. A warm-up that stops at the DI container therefore leaves
    // the LARGER half sitting in the first menu the user actually sees. R2 says the warm-up must go
    // all the way through a menu. This measures whether that claim is true and what it buys.
    //
    // Deliberately additive: it adds a mode, it does not alter A, B0, B, C, C2, C3, F, F2 or G. The
    // default invocation still reproduces the Linux baseline exactly, so the Windows numbers stay
    // comparable to it. The managed side is untouched - the synthetic warm-up is an ordinary query
    // issued by the host, which is precisely where R2 puts it.
    bool warmFull = false;
    for (int i = 4; i < argc; ++i) {
        if (std::strcmp(argv[i], "--warm-full") == 0) { warmFull = true; }
    }
    if (warmFull) {
        std::printf("B1. R2 - WARM THE REAL MENU PATH (warm-up done before any user-visible menu)\n");

        // A single file. Deliberately NOT the selection measured afterwards: the measured call is a
        // three-file multi-selection, so it still has to reach branches the warm-up did not, and the
        // figure below is the pessimistic one rather than a replay of an identical call.
        const char* syntheticWarm =
            "{\"items\":[{\"path\":\"C:\\\\D\\\\warm.bin\",\"isDirectory\":false}]}";

        t = clock_t_::now();
        int32_t wrc = warmFn ? warmFn() : -1;
        const double diMs = ms_since(t);
        report("step 1: NobaShellWarm (DI + provider registry)", diMs);
        if (wrc != 0) { std::printf("  FAIL  NobaShellWarm rc=%d\n", wrc); return 1; }

        char* wr = nullptr; int32_t wlen = 0;
        t = clock_t_::now();
        rc = query(syntheticWarm, &wr, &wlen);
        const double syntheticMs = ms_since(t);
        report("step 2: synthetic menu, result discarded", syntheticMs);
        if (rc != 0) { std::printf("  FAIL  synthetic warm query rc=%d\n", rc); return 1; }
        if (wr && freeFn) { freeFn(wr); }

        const double readyMs = ms_since(processStart);
        report("bridge READY (process start -> warmed)", readyMs);
        std::printf("\n");

        // The number R2 is actually about: what the first menu a user sees costs, once warming is
        // done. Compare against the ~54-72 ms that the same call costs when warming stopped at DI.
        const char* threeFilesWarm =
            "{\"items\":[{\"path\":\"C:\\\\D\\\\a.txt\",\"isDirectory\":false},"
                        "{\"path\":\"C:\\\\D\\\\b.txt\",\"isDirectory\":false},"
                        "{\"path\":\"C:\\\\D\\\\c.txt\",\"isDirectory\":false}]}";
        char* r2 = nullptr; int32_t l2 = 0;
        t = clock_t_::now();
        rc = query(threeFilesWarm, &r2, &l2);
        const double firstRealMs = ms_since(t);
        report("FIRST USER-VISIBLE MENU after full warm-up", firstRealMs);
        if (rc != 0) { std::printf("  FAIL  rc=%d\n", rc); return 1; }
        if (r2 && freeFn) { freeFn(r2); }

        std::printf("\n  R2 verdict: warming through a menu costs %.2f ms of startup and leaves the\n",
                    diMs + syntheticMs);
        std::printf("  first user-visible menu at %.2f ms. If that is inside the menu budget, E_PENDING\n",
                    firstRealMs);
        std::printf("  is a safety net for the window before warming finishes - not the normal path.\n");
        std::printf("\n  This mode stops here: every later section would be measuring a warm engine.\n");
        close(ctx);
        return 0;
    }

    // --- B0: where the cold cost actually is ---------------------------------------------------
    // Split before it is blamed. If the engine dominates, pre-warming solves this outright and
    // E_PENDING is a safety net rather than the design.
    // Scanned rather than read from a fixed position. The first version required --split to be
    // exactly argv[5], so passing it fourth silently produced an unsplit run that looked like a
    // successful split one. A flag that is ignored when misplaced is worse than a flag that errors.
    bool splitCold = false;
    for (int i = 4; i < argc; ++i) {
        if (std::strcmp(argv[i], "--split") == 0) { splitCold = true; }
    }
    double warmOnlyMs = -1.0;
    if (splitCold && warmFn) {
        std::printf("B0. COLD, SPLIT - engine construction measured on its own\n");
        t = clock_t_::now();
        int32_t wrc = warmFn();
        warmOnlyMs = ms_since(t);
        report("NobaShellWarm (DI + provider registry only)", warmOnlyMs);
        std::printf("  rc=%d\n\n", wrc);
    }

    // --- B: cold ------------------------------------------------------------------------------
    std::printf("B. COLD START - first menu%s\n", splitCold ? " (engine ALREADY warmed above)" : ", engine not yet built");

    const char* threeFiles =
        "{\"items\":[{\"path\":\"C:\\\\D\\\\a.txt\",\"isDirectory\":false},"
                    "{\"path\":\"C:\\\\D\\\\b.txt\",\"isDirectory\":false},"
                    "{\"path\":\"C:\\\\D\\\\c.txt\",\"isDirectory\":false}]}";

    char* response = nullptr; int32_t length = 0;
    t = clock_t_::now();
    rc = query(threeFiles, &response, &length);
    const double coldCallMs = ms_since(t);
    if (rc != 0) {
        std::printf("  FAIL  first call rc=%d\n", rc);
        if (response) { std::printf("  detail: %s\n", response); if (freeFn) freeFn(response); }
        return 1;
    }
    report("first NobaShellQuery (engine + first menu)", coldCallMs);
    std::printf("  menu: %.*s\n", (length > 220 ? 220 : length), response);
    if (freeFn) freeFn(response);

    const double coldTotalMs = ms_since(processStart);
    report("TOTAL cold: process start -> first menu", coldTotalMs);
    if (splitCold && warmOnlyMs >= 0.0) {
        report("  of which: engine construction", warmOnlyMs);
        report("  of which: first menu once warm", coldCallMs);
    }
    std::printf("\n");

    // --- C: warm ------------------------------------------------------------------------------
    std::printf("C. WARM - steady state, %d iterations\n", warmIterations);

    std::vector<double> samples;
    samples.reserve(warmIterations);
    for (int i = 0; i < warmIterations; ++i) {
        response = nullptr; length = 0;
        t = clock_t_::now();
        rc = query(threeFiles, &response, &length);
        samples.push_back(ms_since(t));
        if (rc != 0) { std::printf("  FAIL  warm call %d rc=%d\n", i, rc); return 1; }
        if (freeFn) freeFn(response);
    }

    std::sort(samples.begin(), samples.end());
    report("warm min", samples.front());
    report("warm median", samples[samples.size() / 2]);
    report("warm p95", samples[(size_t)(samples.size() * 0.95)]);
    report("warm max", samples.back());
    std::printf("\n");

    // --- C2: concurrent ---------------------------------------------------------------------------
    // A surrogate serving Explorer is not called on one thread. GetState can be asked for several
    // commands at once, and the shell re-asks on a background thread after E_PENDING - which is two
    // threads in the bridge by construction. Measuring only the single-threaded case would measure
    // the case that was never in doubt.
    std::printf("C2. CONCURRENT - 4 threads x 25 calls against one shared bridge\n");
    {
        const int threads = 4, perThread = 25;
        std::vector<std::vector<double>> perThreadSamples((size_t)threads);
        std::atomic<int> failed{0};
        std::vector<std::thread> pool;
        const auto concStart = clock_t_::now();

        for (int ti = 0; ti < threads; ++ti) {
            pool.emplace_back([&, ti]() {
                for (int i = 0; i < perThread; ++i) {
                    char* r = nullptr; int32_t len = 0;
                    const auto t0 = clock_t_::now();
                    const int32_t crc = query(threeFiles, &r, &len);
                    perThreadSamples[(size_t)ti].push_back(ms_since(t0));
                    if (crc != 0) { failed.fetch_add(1); }
                    if (r && freeFn) { freeFn(r); }
                }
            });
        }
        for (auto& th : pool) { th.join(); }
        const double wall = ms_since(concStart);

        std::vector<double> all;
        for (auto& v : perThreadSamples) { all.insert(all.end(), v.begin(), v.end()); }
        std::sort(all.begin(), all.end());

        report("wall clock for 100 concurrent calls", wall);
        report("concurrent median", all[all.size() / 2]);
        report("concurrent p95", all[(size_t)(all.size() * 0.95)]);
        report("concurrent max", all.back());
        std::printf("  failures: %d %s\n\n", failed.load(),
                    failed.load() == 0 ? "- no call failed under contention" : "- CONTENDED CALLS FAILED");
    }

    // --- F: managed failure -------------------------------------------------------------------
    std::printf("F. MANAGED FAILURE - an exception on the managed side\n");
    response = nullptr; length = 0;
    t = clock_t_::now();
    rc = query("{\"fault\":\"throw\"}", &response, &length);
    report("faulting call", ms_since(t));
    std::printf("  return code : %d   (expected -99, caught managed-side)\n", rc);
    std::printf("  response    : %s\n", response ? "non-null" : "null (correct - nothing to show)");
    if (response && freeFn) freeFn(response);

    response = nullptr; length = 0;
    rc = query(threeFiles, &response, &length);
    std::printf("  next call after the fault: rc=%d %s\n", rc,
                rc == 0 ? "- the host survived and still works" : "- THE HOST DID NOT RECOVER");
    if (response && freeFn) freeFn(response);
    std::printf("\n");

    // --- F2: malformed input ------------------------------------------------------------------
    std::printf("F2. MALFORMED INPUT\n");
    response = nullptr; length = 0;
    rc = query("{ this is not json", &response, &length);
    std::printf("  malformed json  : rc=%d %s\n", rc, rc != 0 ? "(refused)" : "(ACCEPTED - WRONG)");
    if (response && freeFn) freeFn(response);

    response = nullptr; length = 0;
    rc = query("", &response, &length);
    std::printf("  empty request   : rc=%d %s\n", rc, rc != 0 ? "(refused)" : "(ACCEPTED - WRONG)");
    if (response && freeFn) freeFn(response);
    std::printf("\n");

    // --- G: slow --------------------------------------------------------------------------------
    std::printf("G. SLOW MANAGED CALL - 2s injected\n");
    response = nullptr; length = 0;
    t = clock_t_::now();
    rc = query("{\"fault\":\"slow\"}", &response, &length);
    report("slow call (blocks the caller)", ms_since(t));
    std::printf("  rc=%d - the CALLER owns the timeout; the bridge has none of its own.\n", rc);
    std::printf("  In the real adapter this is what GetState's fOkToBeSlow / E_PENDING is for.\n");
    if (response && freeFn) freeFn(response);
    std::printf("\n");

    // --- summary --------------------------------------------------------------------------------
    std::printf("==============================================================================\n");
    std::printf("SUMMARY (this platform, this machine - not a Windows claim unless run there)\n");
    std::printf("==============================================================================\n");
    report("cold: process start -> first menu", coldTotalMs);
    report("warm median", samples[samples.size() / 2]);
    report("warm p95", samples[(size_t)(samples.size() * 0.95)]);
    std::printf("\n");
    std::printf("  D (does GetState need E_PENDING?) is DERIVED from the cold figure above,\n");
    std::printf("    not observed. E (isolation from explorer.exe) is a property of\n");
    std::printf("    com:SurrogateServer registration and is OUT OF G.1 SCOPE.\n");

    close(ctx);
    return 0;
}
