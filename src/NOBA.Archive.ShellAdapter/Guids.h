// DROP 08 Phase 5 - identity for the modern context-menu handler.
//
// SHELL-065: identity has ONE home, and it is ShellRegistration.cs. The value below is a copy, and
// it is allowed to exist only because ShellAdapterIdentityTests asserts it equals
// ShellRegistration.ModernHandlerClsid on every build. That is the same arrangement SHELL-031 uses
// for the interim labels: two copies are tolerable exactly as long as a test refuses to let them
// drift.
//
// A CLSID typed twice and mistyped once is a handler that registers cleanly and never activates. The
// symptom is a menu that silently does not appear, on somebody else's machine, with nothing in any
// log - which is why this file is three lines and a test rather than a convention.

#pragma once

#include <guiddef.h>

// {0C5B9F1E-9B4A-4F2D-B3E6-2A7D5C8E1F40}
DEFINE_GUID(CLSID_NobaExplorerCommand,
    0x0c5b9f1e, 0x9b4a, 0x4f2d, 0xb3, 0xe6, 0x2a, 0x7d, 0x5c, 0x8e, 0x1f, 0x40);

// The same value as text, for logging and for the identity test to compare against. Braced, because
// that is the form the manifest and the registry both use.
#define NOBA_MODERN_HANDLER_CLSID L"{0C5B9F1E-9B4A-4F2D-B3E6-2A7D5C8E1F40}"
