// Internal result type: an ABI error code plus a human readable message.
//
// The C ABI cannot carry exceptions or complex error objects, so every internal
// operation returns a Status and the API layer turns it into a VDError plus a
// thread-local message.
#ifndef VIDEOLAB_CORE_CORE_STATUS_H_
#define VIDEOLAB_CORE_CORE_STATUS_H_

#include <string>
#include <utility>

#include "videolab_core.h"

namespace videolab::core {

struct Status {
  VDError code = VD_OK;
  std::string message;

  bool ok() const { return code == VD_OK; }

  static Status Ok() { return Status{}; }

  static Status Error(VDError code, std::string message) {
    Status status;
    status.code = code;
    status.message = std::move(message);
    return status;
  }
};

}  // namespace videolab::core

#endif  // VIDEOLAB_CORE_CORE_STATUS_H_
