// Splits a byte stream into lines for the live consumers of a child process.
//
// Both platform process implementations use this, so line semantics (including
// the final line without a trailing newline) are identical on Windows and POSIX
// and can be tested without spawning anything.
#ifndef VIDEOLAB_CORE_PROCESS_LINE_BUFFER_H_
#define VIDEOLAB_CORE_PROCESS_LINE_BUFFER_H_

#include <cstddef>
#include <string>

#include "process/process_runner.h"

namespace videolab::core {

/// Appends `size` bytes and hands every complete line to `sink`.
///
/// Lines are split on '\n'; a trailing '\r' is removed so CRLF output behaves
/// like LF. `pending` must be a per-stream buffer that the caller keeps between
/// calls and finally passes to FlushPendingLine().
void FeedLines(std::string& pending, const ProcessLineSink& sink,
               ProcessStream stream, const char* data, std::size_t size);

/// Emits a last line that was not terminated by a newline, if any.
void FlushPendingLine(std::string& pending, const ProcessLineSink& sink,
                      ProcessStream stream);

}  // namespace videolab::core

#endif  // VIDEOLAB_CORE_PROCESS_LINE_BUFFER_H_
