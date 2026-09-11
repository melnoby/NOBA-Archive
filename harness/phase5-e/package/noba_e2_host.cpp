// DROP 08 Phase 5 — GATE 2 / E2. A do-nothing executable.
//
// A sparse package's <Application> element requires an Executable, and the file must exist in the
// external location or Add-AppxPackage refuses the package. Nothing ever launches this: the manifest
// sets AppListEntry="none", and the only thing the package is for is the COM server declared beside
// it. It exists so the manifest is valid, and it is deliberately the smallest program that can be.
int main() { return 0; }
