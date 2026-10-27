// Quality flags per hardware encoder.
//
// Every vendor spells "constant quality" differently, and two of them use a
// 0-255 quantizer instead of the 0-63 CRF scale. The mapping is the one the Dart
// implementation already used, so switching to the core cannot change the
// encoder settings a user gets.
#ifndef VIDEOLAB_CORE_FFMPEG_GPU_QUALITY_ARGS_H_
#define VIDEOLAB_CORE_FFMPEG_GPU_QUALITY_ARGS_H_

#include <string>
#include <string_view>
#include <vector>

namespace videolab::core {

/// Quality arguments for `encoder` at the requested CRF/quantizer value.
std::vector<std::string> GpuQualityArgs(std::string_view encoder, int crf);

}  // namespace videolab::core

#endif  // VIDEOLAB_CORE_FFMPEG_GPU_QUALITY_ARGS_H_
