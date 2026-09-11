// DROP 08 Phase 5 - the smallest JSON this adapter can be correct with.
//
// WHY NOT A LIBRARY. The adapter loads into dllhost.exe next to Explorer. Every dependency is a
// thing that can fail to load, a thing to license, and a thing to keep patched in a process the user
// cannot restart casually. The ABI carries two request shapes and two response shapes, all of them
// written by code in this repository - so what is needed is a reader for known shapes, not a parser
// for arbitrary documents.
//
// WHAT THIS DELIBERATELY IS NOT. It does not validate, it does not report where a document is
// malformed, and it does not build a tree. It answers "what is the string at this key" and "walk the
// objects in this array". Anything it cannot answer comes back absent, and an absent answer is
// treated by the callers as a refusal - never as a default that does something.
//
// SHELL-063: nothing here knows a format name, an action name or a capability. It moves opaque
// strings.

#pragma once

#include <string>
#include <vector>

namespace noba {

// UTF-16 (what the shell speaks) <-> UTF-8 (what the ABI speaks).
std::string ToUtf8(const std::wstring& value);
std::wstring ToUtf16(const std::string& value);

// Appends value to out as a JSON string literal, quotes included.
//
// Escaping is not optional here: a Windows path is full of backslashes, and a file may legitimately
// be named with a quote or a control character. An unescaped path is a malformed request, and a
// malformed request that happens to parse is a menu drawn for the wrong selection.
void AppendJsonString(std::string& out, const std::string& value);

// Reads EXACTLY four hexadecimal digits - the \uXXXX escape, and nothing else.
//
// WHY THIS EXISTS RATHER THAN sscanf. MSVC deprecates sscanf (C4996), and the project forbids
// suppressing it. sscanf_s is not the answer either: it is not in namespace std, it is Annex K and
// MSVC-only, and MinGW has none of it - which would end the cross-compile check that finds this
// class of warning without a Windows machine.
//
// AND sscanf WAS WRONG HERE, MEASURED, NOT ASSUMED. "%4x" accepts leading whitespace, a + or - sign,
// an 0x prefix, and FEWER than four digits:
//
//     sscanf(" 123", "%4x")  -> 1, 0x123
//     sscanf("-123", "%4x")  -> 1, 0xFFFFFEDD
//     sscanf("0x12", "%4x")  -> 1, 0x12
//     sscanf("9",    "%4x")  -> 1, 0x9
//
// The caller then advanced the cursor by four characters whatever had actually matched, so "\u9\""
// consumed the closing quote and desynchronised the parse. JSON says four hex digits; this reads
// four hex digits or refuses.
//
// Inline and header-only on purpose: it touches no Windows API, so a test can include this header
// alone and run the cases on any machine.
inline bool ParseHex4(const char* p, const char* end, unsigned& value) {
    if (p == nullptr || end == nullptr || end - p < 4) {
        return false;
    }

    unsigned result = 0;
    for (int i = 0; i < 4; ++i) {
        const char c = p[i];
        unsigned digit;
        if (c >= '0' && c <= '9') {
            digit = static_cast<unsigned>(c - '0');
        } else if (c >= 'a' && c <= 'f') {
            digit = static_cast<unsigned>(c - 'a' + 10);
        } else if (c >= 'A' && c <= 'F') {
            digit = static_cast<unsigned>(c - 'A' + 10);
        } else {
            return false;
        }
        result = (result << 4) | digit;
    }

    // Written only on success, so a caller that ignores the return value cannot pick up a
    // half-parsed number.
    value = result;
    return true;
}

// One object inside a JSON array, as a raw slice of the document.
struct JsonObject {
    const char* begin = nullptr;
    const char* end = nullptr;
};

// The string at "key" inside one object. Returns false when the key is absent or is not a string.
bool ObjectString(const JsonObject& object, const char* key, std::string& out);

// True when "key" is present and is the literal true.
bool ObjectBool(const JsonObject& object, const char* key, bool& out);

// The objects of the array at "key" at the top level of document.
std::vector<JsonObject> ArrayOfObjects(const std::string& document, const char* key);

// The object at "key" at the top level of document.
bool ObjectAt(const std::string& document, const char* key, JsonObject& out);

// The strings of the string-array at "key" inside one object.
std::vector<std::string> ObjectStringArray(const JsonObject& object, const char* key);

}  // namespace noba
