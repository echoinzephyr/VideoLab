#include "process/process_runner.h"

#include "platform/process_platform.h"

namespace videolab::core {

ProcessOutcome ProcessRunner::RunCaptured(const ProcessRequest& request) {
  if (request.executable.empty()) {
    ProcessOutcome outcome;
    outcome.error = "no executable given";
    return outcome;
  }
  return platform::SpawnAndCapture(request);
}

}  // namespace videolab::core
