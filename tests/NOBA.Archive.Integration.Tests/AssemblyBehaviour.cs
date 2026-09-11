#if !NOBA_OFFLINE_TESTS
using Xunit;

// SEC-012 alone expands 512 MB, SEC-021 another 320 MB, and INT-004/INT-013 write multi-megabyte
// fixtures. xUnit v3 parallelises test classes by default; running these concurrently means several
// preallocated multi-hundred-megabyte files at once, so a machine with modest free space fails on disk
// pressure and the failure reads like a code defect. Serialising this assembly is a resource decision,
// not a correctness one - the tests are already isolated from each other by per-test temp directories.
//
// Every other suite stays parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
#endif
