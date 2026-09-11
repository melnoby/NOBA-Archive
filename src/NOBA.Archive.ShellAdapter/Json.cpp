#include "Json.h"

#include <windows.h>

#include <cstdio>

namespace noba {
namespace {

const char* SkipWhitespace(const char* p, const char* end) {
    while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) {
        ++p;
    }
    return p;
}

// Reads a JSON string starting at the opening quote. Returns the position after the closing quote,
// or nullptr if the string never terminates.
//
// The escape handling is the whole reason this is a function rather than a scan for the next quote:
// a path is "C:\\d\\a.txt" on the wire, and a scanner that stops at the first quote after a
// backslash truncates every Windows path it sees.
const char* ReadString(const char* p, const char* end, std::string* out) {
    if (p >= end || *p != '"') {
        return nullptr;
    }
    ++p;
    while (p < end) {
        const char c = *p;
        if (c == '"') {
            return p + 1;
        }
        if (c == '\\') {
            if (p + 1 >= end) {
                return nullptr;
            }
            const char esc = p[1];
            if (out != nullptr) {
                switch (esc) {
                    case 'n': out->push_back('\n'); break;
                    case 'r': out->push_back('\r'); break;
                    case 't': out->push_back('\t'); break;
                    case 'b': out->push_back('\b'); break;
                    case 'f': out->push_back('\f'); break;
                    case 'u': {
                        // \uXXXX. Surrogate pairs are re-encoded through UTF-16 so an astral
                        // character in a file name survives; a lone escape is dropped rather than
                        // producing invalid UTF-8.
                        if (p + 5 >= end) {
                            return nullptr;
                        }
                        unsigned code = 0;
                        if (!ParseHex4(p + 2, end, code)) {
                            return nullptr;
                        }
                        wchar_t unit = static_cast<wchar_t>(code);
                        out->append(ToUtf8(std::wstring(1, unit)));
                        p += 4;
                        break;
                    }
                    default: out->push_back(esc); break;
                }
            } else if (esc == 'u') {
                // Validated even when the value is discarded. A skip that accepted what a read
                // rejects would make SkipValue and ReadString disagree about which documents are
                // well formed, and the disagreement would surface as a menu built from half a
                // response.
                if (p + 5 >= end) {
                    return nullptr;
                }
                unsigned ignored = 0;
                if (!ParseHex4(p + 2, end, ignored)) {
                    return nullptr;
                }
                p += 4;
            }
            p += 2;
            continue;
        }
        if (out != nullptr) {
            out->push_back(c);
        }
        ++p;
    }
    return nullptr;
}

// Skips one JSON value of any kind, so key search can step over what it is not looking for.
const char* SkipValue(const char* p, const char* end) {
    p = SkipWhitespace(p, end);
    if (p >= end) {
        return nullptr;
    }
    if (*p == '"') {
        return ReadString(p, end, nullptr);
    }
    if (*p == '{' || *p == '[') {
        const char open = *p;
        const char close = (open == '{') ? '}' : ']';
        int depth = 0;
        while (p < end) {
            if (*p == '"') {
                p = ReadString(p, end, nullptr);
                if (p == nullptr) {
                    return nullptr;
                }
                continue;
            }
            if (*p == open) {
                ++depth;
            } else if (*p == close) {
                --depth;
                if (depth == 0) {
                    return p + 1;
                }
            }
            ++p;
        }
        return nullptr;
    }
    // A bare literal: number, true, false, null.
    while (p < end && *p != ',' && *p != '}' && *p != ']' &&
           *p != ' ' && *p != '\t' && *p != '\r' && *p != '\n') {
        ++p;
    }
    return p;
}

// Finds the value for key at the top level of the object spanning [begin, end).
const char* FindValue(const char* begin, const char* end, const char* key) {
    const char* p = SkipWhitespace(begin, end);
    if (p >= end || *p != '{') {
        return nullptr;
    }
    ++p;
    const size_t keyLength = std::char_traits<char>::length(key);
    while (p < end) {
        p = SkipWhitespace(p, end);
        if (p >= end || *p == '}') {
            return nullptr;
        }
        std::string name;
        const char* afterName = ReadString(p, end, &name);
        if (afterName == nullptr) {
            return nullptr;
        }
        p = SkipWhitespace(afterName, end);
        if (p >= end || *p != ':') {
            return nullptr;
        }
        ++p;
        p = SkipWhitespace(p, end);
        if (name.size() == keyLength && name.compare(key) == 0) {
            return p;
        }
        p = SkipValue(p, end);
        if (p == nullptr) {
            return nullptr;
        }
        p = SkipWhitespace(p, end);
        if (p < end && *p == ',') {
            ++p;
        }
    }
    return nullptr;
}

}  // namespace

std::string ToUtf8(const std::wstring& value) {
    if (value.empty()) {
        return std::string();
    }
    const int needed = ::WideCharToMultiByte(
        CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (needed <= 0) {
        return std::string();
    }
    std::string out(static_cast<size_t>(needed), '\0');
    ::WideCharToMultiByte(
        CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), &out[0], needed, nullptr, nullptr);
    return out;
}

std::wstring ToUtf16(const std::string& value) {
    if (value.empty()) {
        return std::wstring();
    }
    const int needed = ::MultiByteToWideChar(
        CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0);
    if (needed <= 0) {
        return std::wstring();
    }
    std::wstring out(static_cast<size_t>(needed), L'\0');
    ::MultiByteToWideChar(
        CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), &out[0], needed);
    return out;
}

void AppendJsonString(std::string& out, const std::string& value) {
    out.push_back('"');
    for (const char c : value) {
        switch (c) {
            case '"': out.append("\\\""); break;
            case '\\': out.append("\\\\"); break;
            case '\n': out.append("\\n"); break;
            case '\r': out.append("\\r"); break;
            case '\t': out.append("\\t"); break;
            case '\b': out.append("\\b"); break;
            case '\f': out.append("\\f"); break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) {
                    char buffer[7];
                    std::snprintf(buffer, sizeof(buffer), "\\u%04x", static_cast<unsigned char>(c));
                    out.append(buffer);
                } else {
                    out.push_back(c);
                }
                break;
        }
    }
    out.push_back('"');
}

bool ObjectString(const JsonObject& object, const char* key, std::string& out) {
    if (object.begin == nullptr) {
        return false;
    }
    const char* value = FindValue(object.begin, object.end, key);
    if (value == nullptr || value >= object.end || *value != '"') {
        return false;
    }
    std::string parsed;
    if (ReadString(value, object.end, &parsed) == nullptr) {
        return false;
    }
    out = parsed;
    return true;
}

bool ObjectBool(const JsonObject& object, const char* key, bool& out) {
    if (object.begin == nullptr) {
        return false;
    }
    const char* value = FindValue(object.begin, object.end, key);
    if (value == nullptr) {
        return false;
    }
    const size_t remaining = static_cast<size_t>(object.end - value);
    if (remaining >= 4 && std::char_traits<char>::compare(value, "true", 4) == 0) {
        out = true;
        return true;
    }
    if (remaining >= 5 && std::char_traits<char>::compare(value, "false", 5) == 0) {
        out = false;
        return true;
    }
    return false;
}

bool ObjectAt(const std::string& document, const char* key, JsonObject& out) {
    const char* begin = document.c_str();
    const char* end = begin + document.size();
    const char* value = FindValue(begin, end, key);
    if (value == nullptr || value >= end || *value != '{') {
        return false;
    }
    const char* after = SkipValue(value, end);
    if (after == nullptr) {
        return false;
    }
    out.begin = value;
    out.end = after;
    return true;
}

std::vector<JsonObject> ArrayOfObjects(const std::string& document, const char* key) {
    std::vector<JsonObject> items;
    const char* begin = document.c_str();
    const char* end = begin + document.size();
    const char* value = FindValue(begin, end, key);
    if (value == nullptr || value >= end || *value != '[') {
        return items;
    }
    const char* p = value + 1;
    while (p < end) {
        p = SkipWhitespace(p, end);
        if (p >= end || *p == ']') {
            break;
        }
        if (*p == '{') {
            const char* after = SkipValue(p, end);
            if (after == nullptr) {
                break;
            }
            JsonObject object;
            object.begin = p;
            object.end = after;
            items.push_back(object);
            p = after;
        } else {
            p = SkipValue(p, end);
            if (p == nullptr) {
                break;
            }
        }
        p = SkipWhitespace(p, end);
        if (p < end && *p == ',') {
            ++p;
        }
    }
    return items;
}

std::vector<std::string> ObjectStringArray(const JsonObject& object, const char* key) {
    std::vector<std::string> values;
    if (object.begin == nullptr) {
        return values;
    }
    const char* value = FindValue(object.begin, object.end, key);
    if (value == nullptr || value >= object.end || *value != '[') {
        return values;
    }
    const char* p = value + 1;
    while (p < object.end) {
        p = SkipWhitespace(p, object.end);
        if (p >= object.end || *p == ']') {
            break;
        }
        if (*p == '"') {
            std::string parsed;
            const char* after = ReadString(p, object.end, &parsed);
            if (after == nullptr) {
                break;
            }
            values.push_back(parsed);
            p = after;
        } else {
            p = SkipValue(p, object.end);
            if (p == nullptr) {
                break;
            }
        }
        p = SkipWhitespace(p, object.end);
        if (p < object.end && *p == ',') {
            ++p;
        }
    }
    return values;
}

}  // namespace noba
