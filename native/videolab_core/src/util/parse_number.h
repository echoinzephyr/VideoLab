// Dart-shaped number parsing.
//
// The Dart implementation used `int.tryParse` / `double.tryParse`, which accept
// surrounding whitespace, allow a leading sign, and require the whole string to
// be consumed. The core has to agree, otherwise a value the UI accepted would be
// rejected by the builder.
#ifndef VIDEOLAB_CORE_UTIL_PARSE_NUMBER_H_
#define VIDEOLAB_CORE_UTIL_PARSE_NUMBER_H_

#include <string_view>

namespace videolab::core {

/// Full-string double parse. Non-finite values ("Infinity", "NaN") parse
/// successfully, exactly like Dart; callers that care check `IsFinite`.
bool TryParseDouble(std::string_view text, double& out_value);

/// Full-string integer parse. Rejects fractions, exponents and overflow, like
/// Dart's `int.tryParse`.
bool TryParseInt(std::string_view text, int& out_value);

/// True for a finite value.
bool IsFiniteNumber(double value);

}  // namespace videolab::core

#endif  // VIDEOLAB_CORE_UTIL_PARSE_NUMBER_H_
