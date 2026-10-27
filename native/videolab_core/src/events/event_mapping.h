// Copies a queued event into the ABI struct. String pointers are filled by
// the caller, because their lifetime differs per delivery path (scratch
// storage for polling, the queued event for listener dispatch).
#ifndef VIDEOLAB_CORE_EVENTS_EVENT_MAPPING_H_
#define VIDEOLAB_CORE_EVENTS_EVENT_MAPPING_H_

#include "events/event_queue.h"
#include "videolab_core.h"

namespace videolab::core {

inline void FillEventFields(const QueuedEvent& source, VDEvent* destination) {
  destination->type = static_cast<uint32_t>(source.type);
  destination->task_id = source.task_id;
  destination->flags = source.flags;
  destination->level = source.level;
  destination->fraction = source.fraction;
  destination->bytes_downloaded = source.bytes_downloaded;
  destination->bytes_total = source.bytes_total;
  destination->speed_bps = source.speed_bps;
  destination->eta_seconds = source.eta_seconds;
  destination->exit_code = source.exit_code;
  destination->reserved0 = 0;
  destination->message = nullptr;
  destination->detail_json = nullptr;
}

}  // namespace videolab::core

#endif  // VIDEOLAB_CORE_EVENTS_EVENT_MAPPING_H_
