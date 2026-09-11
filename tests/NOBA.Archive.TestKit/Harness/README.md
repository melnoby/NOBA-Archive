# Offline test harness

xUnit v3 is the project's test framework (ARCHITECTURE.md §2) and is what runs on the Windows build
machine. This folder contains a small, dependency-free stand-in that is compiled **only** when
`NobaOfflineBuild=true`, so the suite can also be executed in an environment with no access to
nuget.org.

The shims deliberately mirror the xUnit surface the tests use (`[Fact]`, `[Theory]`, `[InlineData]`,
`Assert`), so **the test source files are identical in both modes** - there is no `#if` in any test.
Switching between the two is a build-property change, not an edit.
