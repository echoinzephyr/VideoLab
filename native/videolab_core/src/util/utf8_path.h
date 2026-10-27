#pragma once
#include <filesystem>
#include <string>
#include <string_view>

namespace videolab::core {
// The public ABI always uses UTF-8; filesystem's narrow Windows constructor
// instead uses the active ANSI codepage. Keep conversions explicit in both ways.
inline std::filesystem::path Utf8Path(std::string_view value) {
  return std::filesystem::path(std::u8string(value.begin(), value.end()));
}
inline std::string Utf8PathString(const std::filesystem::path& value) {
  const auto utf8 = value.u8string();
  return std::string(utf8.begin(), utf8.end());
}
}
