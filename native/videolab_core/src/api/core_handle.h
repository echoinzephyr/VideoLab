// Completes the opaque VDCoreHandle declared by the public ABI header.
// This is the only translation unit boundary the host can cross; keep it thin.
#ifndef VIDEOLAB_CORE_API_CORE_HANDLE_H_
#define VIDEOLAB_CORE_API_CORE_HANDLE_H_

#include "core/core_context.h"
#include "videolab_core.h"

struct VDCoreHandle {
  videolab::core::CoreContext context;
};

#endif  // VIDEOLAB_CORE_API_CORE_HANDLE_H_
