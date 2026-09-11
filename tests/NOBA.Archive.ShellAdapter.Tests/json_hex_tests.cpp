// DROP 08 Phase 5 - regression tests for the \uXXXX escape in the adapter's JSON reader.
//
// WHY THIS FILE EXISTS. Json.cpp used std::sscanf(p + 2, "%4x", &code) to read the four hex digits
// of a \uXXXX escape. MSVC deprecates sscanf (C4996) and the project treats warnings as errors, so
// the VM build failed on it. The warning turned out to be the smaller half of the problem: "%4x"
// accepts input that JSON does not, and the caller advanced the cursor by four characters whatever
// had actually matched - so a short escape swallowed the closing quote and desynchronised the parse.
//
// The replacement is noba::ParseHex4, which reads exactly four hex digits or refuses. These are its
// tests, and the last section documents - by running both - exactly which inputs changed answer.
//
// WHY A PLAIN main() AND NOT THE MANAGED TEST SUITE. ParseHex4 is header-only and touches no Windows
// API precisely so it can be tested anywhere. The rest of the adapter cannot: Json.cpp needs
// <windows.h> for WideCharToMultiByte. A test that could only run on the Windows VM would not have
// run before this was reported, which is the situation this file exists to end.
//
// BUILD AND RUN (any machine with a C++17 compiler):
//
//     g++ -std=c++17 -Wall -Wextra -Wpedantic -Werror
//         tests/NOBA.Archive.ShellAdapter.Tests/json_hex_tests.cpp -o /tmp/json_hex_tests
//     /tmp/json_hex_tests
//
// (one command; the two lines above are split for width, not continued with a backslash - a
// backslash at the end of a // comment continues the comment onto the next line, which -Wcomment
// reports and -Werror then rejects.)
//
// Exit code 0 means every case passed; non-zero prints the failures.

#include "../../src/NOBA.Archive.ShellAdapter/Json.h"

#include <cstdio>
#include <string>

namespace {

int g_failures = 0;
int g_checks = 0;

void Check(bool condition, const char* what) {
    ++g_checks;
    if (!condition) {
        ++g_failures;
        std::printf("  FAIL  %s\n", what);
    }
}

// Calls ParseHex4 over a std::string, with end at the string's end unless a shorter span is asked
// for. The span matters: the escape at the end of a truncated document must be refused rather than
// read out of the buffer.
bool Parse(const std::string& text, unsigned& value, size_t span = std::string::npos) {
    const char* p = text.c_str();
    const size_t length = (span == std::string::npos) ? text.size() : span;
    return noba::ParseHex4(p, p + length, value);
}

// ------------------------------------------------------------------------------------------------
// Accepted: exactly four hexadecimal digits
// ------------------------------------------------------------------------------------------------

void AcceptsFourHexDigits() {
    std::printf("accepts four hex digits\n");

    struct Case {
        const char* input;
        unsigned expected;
    };

    const Case cases[] = {
        {"0000", 0x0000u},        // the low bound
        {"ffff", 0xFFFFu},        // the high bound, lower case
        {"FFFF", 0xFFFFu},        // the high bound, upper case
        {"0041", 0x0041u},        // 'A'
        {"00e9", 0x00E9u},        // 'e' with an acute accent, lower case
        {"00E9", 0x00E9u},        // the same, upper case - JSON allows either
        {"aBcD", 0xABCDu},        // mixed case in one escape
        {"d83d", 0xD83Du},        // a high surrogate: valid to READ, whatever it means
        {"dc9a", 0xDC9Au},        // the matching low surrogate
        {"0009", 0x0009u},        // a leading zero run is not a short escape
        {"1234", 0x1234u},
        {"abcd", 0xABCDu},
        {"9999", 0x9999u},
    };

    for (const Case& c : cases) {
        unsigned value = 0xDEADu;
        const bool ok = Parse(c.input, value);
        Check(ok, c.input);
        Check(ok && value == c.expected, c.input);
        if (ok && value != c.expected) {
            std::printf("        %s -> 0x%04X, expected 0x%04X\n", c.input, value, c.expected);
        }
    }

    // Only the first four characters are consulted. The caller advances by four, so reading more
    // than four would be reading somebody else's document.
    unsigned value = 0;
    Check(Parse("00e9\" , \"next\"", value) && value == 0x00E9u, "stops after the fourth digit");
    Check(Parse("0041ffff", value) && value == 0x0041u, "does not run on into the next escape");
}

// ------------------------------------------------------------------------------------------------
// Refused: everything else
// ------------------------------------------------------------------------------------------------

void RefusesMalformedInput() {
    std::printf("refuses malformed input\n");

    const char* refused[] = {
        "00g9",   // a non-hex letter, the plain malformed case
        "g000",   // non-hex in the first position
        "00G9",   // upper case beyond 'F'
        "zzzz",   // nothing hex at all
        " 123",   // leading space: sscanf skipped whitespace, JSON does not have any here
        "\t123",  // the same with a tab
        "\n123",  // and with a newline
        "-123",   // a sign: sscanf read this as 0xFFFFFEDD
        "+abc",   // the other sign
        "0x12",   // an 0x prefix: sscanf accepted it and read two digits
        "0X12",   // upper case prefix
        "12\"x",  // the closing quote of a short escape - the desynchronising case
        "12 4",   // an interior space
        "1.23",   // a decimal point
        "12-4",   // an interior sign
        "1_23",   // an underscore
        "",       // nothing at all
    };

    for (const char* input : refused) {
        unsigned value = 0xDEADu;
        Check(!Parse(input, value), input);
        Check(value == 0xDEADu, "value untouched on refusal");
    }
}

// ------------------------------------------------------------------------------------------------
// The buffer bound
// ------------------------------------------------------------------------------------------------

void RefusesFewerThanFourCharacters() {
    std::printf("refuses fewer than four characters\n");

    // Short strings. "9" is the case that produced the original bug: sscanf returned 1 and the
    // caller advanced four characters anyway.
    const char* shortInputs[] = {"9", "ab", "abc", "1", "12", "123"};
    for (const char* input : shortInputs) {
        unsigned value = 0xDEADu;
        Check(!Parse(input, value), input);
        Check(value == 0xDEADu, "value untouched on a short input");
    }

    // The bound is end, not the terminator. The buffer here HAS four hex digits; the caller says
    // only three of them belong to this document, and ParseHex4 must believe the caller.
    unsigned value = 0xDEADu;
    Check(!Parse("abcd", value, 3), "three characters of a four-digit buffer");
    Check(!Parse("abcd", value, 0), "an empty span");
    Check(Parse("abcd", value, 4) && value == 0xABCDu, "exactly four characters is enough");

    // Null pointers are refused rather than dereferenced.
    unsigned nulls = 0xDEADu;
    const char* text = "abcd";
    Check(!noba::ParseHex4(nullptr, text + 4, nulls), "a null begin");
    Check(!noba::ParseHex4(text, nullptr, nulls), "a null end");
    Check(!noba::ParseHex4(text + 4, text, nulls), "end before begin");
    Check(nulls == 0xDEADu, "value untouched for null input");
}

// ------------------------------------------------------------------------------------------------
// What changed, demonstrated rather than asserted from memory
// ------------------------------------------------------------------------------------------------
//
// Compiled only where sscanf is available without a deprecation error - which is not MSVC, and is
// the whole reason the call had to go. The point of the section is the printed table: it is the
// evidence that the old call accepted these inputs, not a claim that it did.

#ifndef _MSC_VER
void DocumentsWhatSscanfAccepted() {
    std::printf("what the old sscanf call accepted and ParseHex4 refuses\n");

    const char* inputs[] = {" 123", "-123", "0x12", "+abc", "9"};

    for (const char* input : inputs) {
        unsigned old = 0;
        const int matched = std::sscanf(input, "%4x", &old);

        unsigned current = 0xDEADu;
        const bool now = noba::ParseHex4(input, input + std::char_traits<char>::length(input), current);

        std::printf("        sscanf(\"%s\", \"%%4x\") -> %d, 0x%X   |   ParseHex4 -> %s\n",
                    input, matched, matched == 1 ? old : 0u, now ? "accepted" : "refused");

        // The old call matched, the new one refuses: that is the behaviour change, and it is the
        // change that stops "\u9\"" from consuming its own closing quote.
        Check(matched == 1, "sscanf accepted this input");
        Check(!now, "ParseHex4 refuses this input");
    }
}
#endif

}  // namespace

int main() {
    std::printf("NOBA.Archive.ShellAdapter - Json.h ParseHex4\n\n");

    AcceptsFourHexDigits();
    RefusesMalformedInput();
    RefusesFewerThanFourCharacters();
#ifndef _MSC_VER
    DocumentsWhatSscanfAccepted();
#endif

    std::printf("\n%d checks, %d failure(s)\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
