// Platform primitive behind ProcessRunner.
//
// Implemented once per OS family:
//   - platform/windows/process_windows.cpp  (CreateProcessW + Job Object)
//   - platform/posix/process_posix.cpp      (posix_spawn + process group)
//
// Linux and macOS share the POSIX implementation on purpose: duplicating an
// identical file per OS would add no isolation and two places to fix bugs.
#ifndef VIDEOLAB_CORE_PLATFORM_PROCESS_PLATFORM_H_
#define VIDEOLAB_CORE_PLATFORM_PROCESS_PLATFORM_H_

#include "process/process_runner.h"

namespace videolab::core::platform {

ProcessOutcome SpawnAndCapture(const ProcessRequest& request);

}  // namespace videolab::core::platform

#endif  // VIDEOLAB_CORE_PLATFORM_PROCESS_PLATFORM_H_
