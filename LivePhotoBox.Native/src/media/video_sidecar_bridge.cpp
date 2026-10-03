#include "media/video_sidecar_bridge.h"

#include "foundation/internal.h"
#include "media/video_sidecar_loader.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>

namespace lpb::media
{
namespace
{

struct callback_state
{
    lpb_context* context{};
    const lpb::random_access_reader* input{};
    windows_owned_output* output{};
    uint64_t input_position{};
    uint64_t output_position{};
};

void set_reason(video_sidecar_bridge_result& result, const char* format, int32_t status, int32_t library_error) noexcept
{
    if (format == nullptr) return;
    static_cast<void>(_snprintf_s(result.reason.data(), result.reason.size(), _TRUNCATE, format,
        static_cast<int>(status), static_cast<int>(library_error)));
}

bool seek_position(uint64_t current, uint64_t size, int64_t offset, int32_t whence, uint64_t& next) noexcept
{
    if (size > static_cast<uint64_t>((std::numeric_limits<int64_t>::max)()) || current > size) return false;
    int64_t base = 0;
    switch (whence)
    {
    case 0: base = 0; break; // SEEK_SET
    case 1: base = static_cast<int64_t>(current); break; // SEEK_CUR
    case 2: base = static_cast<int64_t>(size); break; // SEEK_END
    default: return false;
    }
    if ((offset > 0 && base > (std::numeric_limits<int64_t>::max)() - offset) ||
        (offset < 0 && base < (std::numeric_limits<int64_t>::min)() - offset)) return false;
    const int64_t candidate = base + offset;
    if (candidate < 0 || static_cast<uint64_t>(candidate) > size) return false;
    next = static_cast<uint64_t>(candidate);
    return true;
}

int32_t LPB_VIDEO_SIDECAR_CALL input_read(void* opaque, uint8_t* destination,
    uint32_t capacity, uint32_t* bytes_read) noexcept
{
    auto* state = static_cast<callback_state*>(opaque);
    if (bytes_read != nullptr) *bytes_read = 0U;
    if (state == nullptr || state->context == nullptr || state->input == nullptr ||
        destination == nullptr || bytes_read == nullptr || capacity == 0U) return LPB_VIDEO_SIDECAR_IO_ERROR;
    if (lpb_context_check_cancelled(state->context) == LPB_RESULT_CANCELLED) return LPB_VIDEO_SIDECAR_IO_CANCELLED;
    if (state->input_position > state->input->length) return LPB_VIDEO_SIDECAR_IO_ERROR;
    const uint64_t remaining = state->input->length - state->input_position;
    if (remaining == 0U) return LPB_VIDEO_SIDECAR_IO_EOF;
    const uint32_t count = static_cast<uint32_t>(std::min<uint64_t>(remaining, capacity));
    if (!state->input->read_exact(state->input_position,
            std::span<uint8_t>(destination, static_cast<size_t>(count)))) return LPB_VIDEO_SIDECAR_IO_ERROR;
    state->input_position += count;
    *bytes_read = count;
    return LPB_VIDEO_SIDECAR_IO_OK;
}

int32_t LPB_VIDEO_SIDECAR_CALL input_seek(void* opaque, int64_t offset,
    int32_t whence, int64_t* position) noexcept
{
    auto* state = static_cast<callback_state*>(opaque);
    if (state == nullptr || state->context == nullptr || state->input == nullptr || position == nullptr) return LPB_VIDEO_SIDECAR_IO_ERROR;
    if (lpb_context_check_cancelled(state->context) == LPB_RESULT_CANCELLED) return LPB_VIDEO_SIDECAR_IO_CANCELLED;
    uint64_t next = 0;
    if (!seek_position(state->input_position, state->input->length, offset, whence, next)) return LPB_VIDEO_SIDECAR_IO_ERROR;
    state->input_position = next;
    *position = static_cast<int64_t>(next);
    return LPB_VIDEO_SIDECAR_IO_OK;
}

int32_t LPB_VIDEO_SIDECAR_CALL input_size(void* opaque, uint64_t* size) noexcept
{
    auto* state = static_cast<callback_state*>(opaque);
    if (state == nullptr || state->input == nullptr || size == nullptr) return LPB_VIDEO_SIDECAR_IO_ERROR;
    *size = state->input->length;
    return LPB_VIDEO_SIDECAR_IO_OK;
}

int32_t LPB_VIDEO_SIDECAR_CALL output_write(void* opaque, const uint8_t* source,
    uint32_t size, uint32_t* bytes_written) noexcept
{
    auto* state = static_cast<callback_state*>(opaque);
    if (bytes_written != nullptr) *bytes_written = 0U;
    if (state == nullptr || state->context == nullptr || state->output == nullptr ||
        source == nullptr || bytes_written == nullptr) return LPB_VIDEO_SIDECAR_IO_ERROR;
    if (lpb_context_check_cancelled(state->context) == LPB_RESULT_CANCELLED) return LPB_VIDEO_SIDECAR_IO_CANCELLED;
    if (size == 0U)
    {
        *bytes_written = 0U;
        return LPB_VIDEO_SIDECAR_IO_OK;
    }
    if (size > static_cast<uint64_t>((std::numeric_limits<int64_t>::max)()) - state->output_position ||
        !state->output->write_at(state->output_position,
            std::span<const uint8_t>(source, static_cast<size_t>(size)))) return LPB_VIDEO_SIDECAR_IO_ERROR;
    state->output_position += size;
    *bytes_written = size;
    return LPB_VIDEO_SIDECAR_IO_OK;
}

int32_t LPB_VIDEO_SIDECAR_CALL output_seek(void* opaque, int64_t offset,
    int32_t whence, int64_t* position) noexcept
{
    auto* state = static_cast<callback_state*>(opaque);
    if (state == nullptr || state->context == nullptr || state->output == nullptr || position == nullptr) return LPB_VIDEO_SIDECAR_IO_ERROR;
    if (lpb_context_check_cancelled(state->context) == LPB_RESULT_CANCELLED) return LPB_VIDEO_SIDECAR_IO_CANCELLED;
    uint64_t size = 0;
    uint64_t next = 0;
    if (!state->output->size(size) || !seek_position(state->output_position, size, offset, whence, next)) return LPB_VIDEO_SIDECAR_IO_ERROR;
    state->output_position = next;
    *position = static_cast<int64_t>(next);
    return LPB_VIDEO_SIDECAR_IO_OK;
}

int32_t LPB_VIDEO_SIDECAR_CALL output_size(void* opaque, uint64_t* size) noexcept
{
    auto* state = static_cast<callback_state*>(opaque);
    if (state == nullptr || state->output == nullptr || size == nullptr) return LPB_VIDEO_SIDECAR_IO_ERROR;
    return state->output->size(*size) ? LPB_VIDEO_SIDECAR_IO_OK : LPB_VIDEO_SIDECAR_IO_ERROR;
}

int32_t LPB_VIDEO_SIDECAR_CALL is_cancelled(void* opaque, uint32_t* cancelled) noexcept
{
    auto* state = static_cast<callback_state*>(opaque);
    if (state == nullptr || state->context == nullptr || cancelled == nullptr) return LPB_VIDEO_SIDECAR_IO_ERROR;
    *cancelled = lpb_context_check_cancelled(state->context) == LPB_RESULT_CANCELLED ? 1U : 0U;
    return LPB_VIDEO_SIDECAR_IO_OK;
}

bool valid_string(const char* value, size_t capacity) noexcept
{
    return value != nullptr && std::memchr(value, '\0', capacity) != nullptr && value[0] != '\0';
}

lpb_result map_status(int32_t status) noexcept
{
    if (status == LPB_VIDEO_SIDECAR_CANCELLED) return LPB_RESULT_CANCELLED;
    if (status == LPB_VIDEO_SIDECAR_INVALID_ARGUMENT || status == LPB_VIDEO_SIDECAR_UNSUPPORTED) return LPB_RESULT_INVALID_ARGUMENT;
    return LPB_RESULT_INTERNAL_ERROR;
}

} // namespace

video_sidecar_bridge_result run_video_sidecar_transcode(
    lpb_context* context,
    const lpb::random_access_reader& input,
    windows_owned_output& output,
    lpb_video_container target_container,
    lpb_video_codec target_codec,
    int32_t crf,
    bool native_transplants_mebx) noexcept
{
    video_sidecar_bridge_result outcome{};
    if (context == nullptr || input.length == 0U || input.read_at == nullptr ||
        crf < 0 || crf > 51 ||
        (target_container != LPB_VIDEO_CONTAINER_MOV && target_container != LPB_VIDEO_CONTAINER_MP4) ||
        (target_codec != LPB_VIDEO_CODEC_H264 && target_codec != LPB_VIDEO_CODEC_HEVC))
    {
        outcome.native_result = LPB_RESULT_INVALID_ARGUMENT;
        set_reason(outcome, "The Native-to-sidecar request was invalid (status=%d, library_error=%d).", LPB_VIDEO_SIDECAR_INVALID_ARGUMENT, 0);
        return outcome;
    }

    VideoSidecarModule sidecar;
    if (!sidecar.load() || sidecar.transcode() == nullptr || sidecar.info() == nullptr)
    {
        outcome.native_result = LPB_RESULT_INTERNAL_ERROR;
        static_cast<void>(_snprintf_s(outcome.reason.data(), outcome.reason.size(), _TRUNCATE,
            "BackendUnavailable: %s; no Media Foundation retry.", sidecar.failure_reason()));
        return outcome;
    }

    callback_state state{context, &input, &output, 0U, 0U};
    lpb_video_sidecar_io_v1 io{};
    io.struct_size = sizeof(io);
    io.input_context = &state;
    io.input_read = input_read;
    io.input_seek = input_seek;
    io.input_size = input_size;
    io.output_context = &state;
    io.output_write = output_write;
    io.output_seek = output_seek;
    io.output_size = output_size;
    io.is_cancelled = is_cancelled;
    io.cancellation_context = &state;

    lpb_video_sidecar_request_v1 request{};
    request.struct_size = sizeof(request);
    request.target_codec = target_codec == LPB_VIDEO_CODEC_HEVC
        ? LPB_VIDEO_SIDECAR_CODEC_HEVC
        : LPB_VIDEO_SIDECAR_CODEC_H264;
    request.target_container = target_container == LPB_VIDEO_CONTAINER_MOV
        ? LPB_VIDEO_SIDECAR_CONTAINER_MOV
        : LPB_VIDEO_SIDECAR_CONTAINER_MP4;
    request.crf = crf;
    request.flags = native_transplants_mebx
        ? LPB_VIDEO_SIDECAR_REQUEST_NATIVE_TRANSPLANTS_MEBX
        : 0U;

    outcome.sidecar_result.struct_size = sizeof(outcome.sidecar_result);
    const int32_t status = sidecar.transcode()(&request, &io, &outcome.sidecar_result);
    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
        outcome.native_result = LPB_RESULT_CANCELLED;
        set_reason(outcome, "Video sidecar conversion was cancelled (status=%d, library_error=%d).",
            LPB_VIDEO_SIDECAR_CANCELLED, outcome.sidecar_result.library_error);
        return outcome;
    }
    if (status != outcome.sidecar_result.status || status != LPB_VIDEO_SIDECAR_OK ||
        outcome.sidecar_result.struct_size != static_cast<uint32_t>(sizeof(outcome.sidecar_result)))
    {
        outcome.native_result = map_status(status);
        if (status == LPB_VIDEO_SIDECAR_UNSUPPORTED) {
            set_reason(outcome, "Unsupported: minimal-libav cannot preserve the selected video request (status=%d, library_error=%d); no Media Foundation retry.",
                status, outcome.sidecar_result.library_error);
        } else {
            set_reason(outcome, "Minimal-libav sidecar conversion failed (status=%d, library_error=%d); no Media Foundation retry.",
                status, outcome.sidecar_result.library_error);
        }
        return outcome;
    }

    const uint32_t required_codec_capability = target_codec == LPB_VIDEO_CODEC_HEVC
        ? LPB_VIDEO_SIDECAR_CAP_HEVC
        : LPB_VIDEO_SIDECAR_CAP_H264;
    const uint32_t required_depth_capability = target_codec == LPB_VIDEO_CODEC_HEVC
        ? LPB_VIDEO_SIDECAR_CAP_HEVC_10BIT
        : LPB_VIDEO_SIDECAR_CAP_H264_10BIT;
    const auto* sidecar_info = sidecar.info();
    const bool reserved_zero = std::all_of(std::begin(outcome.sidecar_result.reserved),
        std::end(outcome.sidecar_result.reserved), [](uint32_t value) { return value == 0U; });
    if (sidecar_info == nullptr ||
        (sidecar_info->capability_flags & (required_codec_capability | required_depth_capability)) !=
            (required_codec_capability | required_depth_capability) ||
        outcome.sidecar_result.selected_capability != (required_codec_capability | required_depth_capability) ||
        outcome.sidecar_result.target_codec != request.target_codec ||
        outcome.sidecar_result.output_width == 0U || outcome.sidecar_result.output_height == 0U ||
        outcome.sidecar_result.output_pixel_depth <= 8U || outcome.sidecar_result.video_frames == 0U ||
        outcome.sidecar_result.output_bytes == 0U ||
        outcome.sidecar_result.time_base_numerator <= 0 || outcome.sidecar_result.time_base_denominator <= 0 ||
        !reserved_zero || !valid_string(outcome.sidecar_result.encoder_name, sizeof(outcome.sidecar_result.encoder_name)) ||
        std::strncmp(outcome.sidecar_result.library_version, "9.0.1", sizeof(outcome.sidecar_result.library_version)) != 0)
    {
        outcome.native_result = LPB_RESULT_INTERNAL_ERROR;
        set_reason(outcome, "Minimal-libav sidecar returned an incomplete or inconsistent success record (status=%d, library_error=%d).",
            LPB_VIDEO_SIDECAR_INTERNAL_ERROR, outcome.sidecar_result.library_error);
        return outcome;
    }

    outcome.native_result = LPB_RESULT_OK;
    static_cast<void>(_snprintf_s(outcome.reason.data(), outcome.reason.size(), _TRUNCATE,
        "Minimal-libav sidecar completed with %s using FFmpeg %s; software-forced, no fallback.",
        outcome.sidecar_result.encoder_name, outcome.sidecar_result.library_version));
    return outcome;
}

} // namespace lpb::media
