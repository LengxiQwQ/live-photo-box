#include "video_sidecar_abi.h"
#include "media/video_sei_preservation.h"

#ifndef LPB_VIDEO_SIDECAR_BUILD_VERSION
#error LPB_VIDEO_SIDECAR_BUILD_VERSION must come from the pinned build profile.
#endif

#include <algorithm>
#include <array>
#include <cstdint>
#include <climits>
#include <cstring>
#include <limits>
#include <iterator>
#include <memory>
#include <map>
#include <thread>
#include <string>
#include <utility>
#include <vector>
#include <cmath>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/display.h>
#include <libavutil/error.h>
#include <libavutil/pixdesc.h>
#include <libavutil/mastering_display_metadata.h>
#include <libswscale/swscale.h>
#include <x265.h>
}

namespace {

constexpr int kIoBufferSize = 64 * 1024;
constexpr uint32_t kMebxCodecTag = static_cast<uint32_t>('m') |
    (static_cast<uint32_t>('e') << 8U) |
    (static_cast<uint32_t>('b') << 16U) |
    (static_cast<uint32_t>('x') << 24U);

bool same_mastering(const AVMasteringDisplayMetadata& a, const AVMasteringDisplayMetadata& b) noexcept {
    if (a.has_primaries != b.has_primaries || a.has_luminance != b.has_luminance) return false;
    if (a.has_primaries) {
        for (size_t i = 0U; i < 3U; ++i) for (size_t j = 0U; j < 2U; ++j)
            if (av_cmp_q(a.display_primaries[i][j], b.display_primaries[i][j]) != 0) return false;
        for (size_t i = 0U; i < 2U; ++i) if (av_cmp_q(a.white_point[i], b.white_point[i]) != 0) return false;
    }
    return !a.has_luminance || (av_cmp_q(a.min_luminance, b.min_luminance) == 0 &&
        av_cmp_q(a.max_luminance, b.max_luminance) == 0);
}

template <typename T>
void rotate_plane_cw(const T* src, int src_stride, int src_w, int src_h,
                     T* dst, int dst_stride, int rot_cw)
{
    if (rot_cw == 90) {
        for (int y = 0; y < src_h; ++y) {
            const T* src_row = src + y * src_stride;
            for (int x = 0; x < src_w; ++x) {
                dst[x * dst_stride + (src_h - 1 - y)] = src_row[x];
            }
        }
    } else if (rot_cw == 180) {
        for (int y = 0; y < src_h; ++y) {
            const T* src_row = src + y * src_stride;
            T* dst_row = dst + (src_h - 1 - y) * dst_stride;
            for (int x = 0; x < src_w; ++x) {
                dst_row[src_w - 1 - x] = src_row[x];
            }
        }
    } else if (rot_cw == 270) {
        for (int y = 0; y < src_h; ++y) {
            const T* src_row = src + y * src_stride;
            for (int x = 0; x < src_w; ++x) {
                dst[(src_w - 1 - x) * dst_stride + y] = src_row[x];
            }
        }
    }
}

void rotate_frame(const AVFrame* src, AVFrame* dst, int rot_cw, unsigned int pixel_depth)
{
    const int num_planes = (src->format == AV_PIX_FMT_YUV420P || src->format == AV_PIX_FMT_YUV420P10LE) ? 3 : 1;
    for (int p = 0; p < num_planes; ++p) {
        const int w = (p == 0) ? src->width : (src->width / 2);
        const int h = (p == 0) ? src->height : (src->height / 2);
        if (pixel_depth > 8U) {
            const int src_stride = src->linesize[p] / static_cast<int>(sizeof(uint16_t));
            const int dst_stride = dst->linesize[p] / static_cast<int>(sizeof(uint16_t));
            rotate_plane_cw(reinterpret_cast<const uint16_t*>(src->data[p]), src_stride, w, h,
                            reinterpret_cast<uint16_t*>(dst->data[p]), dst_stride, rot_cw);
        } else {
            const int src_stride = src->linesize[p];
            const int dst_stride = dst->linesize[p];
            rotate_plane_cw(src->data[p], src_stride, w, h,
                            dst->data[p], dst_stride, rot_cw);
        }
    }
}

bool frame_hdr_matches_container(const AVFrame* frame, const AVCodecParameters* source,
    bool hevc_target, const std::array<bool, 145>& seen_sei,
    std::span<const lpb::media::preserved_sei_message> current_metadata) noexcept {
    if ((frame->flags & AV_FRAME_FLAG_INTERLACED) != 0 || frame->repeat_pict != 0) return false;
    for (int i = 0; i < frame->nb_side_data; ++i) {
        const auto* side = frame->side_data[i];
        if (side->type == AV_FRAME_DATA_DOVI_METADATA || side->type == AV_FRAME_DATA_DOVI_RPU_BUFFER) return false;
        if (side->type == AV_FRAME_DATA_DYNAMIC_HDR_PLUS || side->type == AV_FRAME_DATA_DYNAMIC_HDR_VIVID) {
            // Retain exact registered T.35 payload on its original picture.
            // H.264 is not an accepted dynamic-HDR delivery route here.
            if (!hevc_target || !std::any_of(current_metadata.begin(), current_metadata.end(), [&](const auto& item) {
                if (item.type != 4U || item.payload.empty()) return false;
                return side->type == AV_FRAME_DATA_DYNAMIC_HDR_VIVID ? item.payload[0] == 0x26U :
                    item.payload.size() >= 3U && item.payload[0] == 0xB5U && item.payload[1] == 0U && item.payload[2] == 0x3CU;
            })) return false;
            continue;
        }
        AVPacketSideDataType packet_type{};
        if (side->type == AV_FRAME_DATA_MASTERING_DISPLAY_METADATA) packet_type = AV_PKT_DATA_MASTERING_DISPLAY_METADATA;
        else if (side->type == AV_FRAME_DATA_CONTENT_LIGHT_LEVEL) packet_type = AV_PKT_DATA_CONTENT_LIGHT_LEVEL;
        else continue;
        const auto* declared = av_packet_side_data_get(source->coded_side_data, source->nb_coded_side_data, packet_type);
        if (declared == nullptr) {
            if (!seen_sei[packet_type == AV_PKT_DATA_MASTERING_DISPLAY_METADATA ? 137U : 144U]) return false;
            continue;
        }
        if (packet_type == AV_PKT_DATA_MASTERING_DISPLAY_METADATA) {
            if (side->size < sizeof(AVMasteringDisplayMetadata) || declared->size < sizeof(AVMasteringDisplayMetadata) ||
                !same_mastering(*reinterpret_cast<const AVMasteringDisplayMetadata*>(side->data),
                    *reinterpret_cast<const AVMasteringDisplayMetadata*>(declared->data))) return false;
        } else {
            if (side->size < sizeof(AVContentLightMetadata) || declared->size < sizeof(AVContentLightMetadata)) return false;
            const auto& a = *reinterpret_cast<const AVContentLightMetadata*>(side->data);
            const auto& b = *reinterpret_cast<const AVContentLightMetadata*>(declared->data);
            if (a.MaxCLL != b.MaxCLL || a.MaxFALL != b.MaxFALL) return false;
        }
    }
    return true;
}

template<size_t Capacity>
void copy_fixed_string(char (&destination)[Capacity], const char* source) noexcept
{
    if (source == nullptr)
    {
        destination[0] = '\0';
        return;
    }

    const size_t copy_size = std::min(std::strlen(source), Capacity - 1U);
    std::memcpy(destination, source, copy_size);
    destination[copy_size] = '\0';
}

struct IoState
{
    const lpb_video_sidecar_io_v1* callbacks{};
};

using AvioContext = std::unique_ptr<AVIOContext, void(*)(AVIOContext*)>;
using InputFormat = std::unique_ptr<AVFormatContext, void(*)(AVFormatContext*)>;
using OutputFormat = std::unique_ptr<AVFormatContext, void(*)(AVFormatContext*)>;
using CodecContext = std::unique_ptr<AVCodecContext, void(*)(AVCodecContext*)>;
using Frame = std::unique_ptr<AVFrame, void(*)(AVFrame*)>;
using Packet = std::unique_ptr<AVPacket, void(*)(AVPacket*)>;
using ScaleContext = std::unique_ptr<SwsContext, void(*)(SwsContext*)>;

void free_avio(AVIOContext* value) noexcept
{
    if (value != nullptr)
    {
        // avio_alloc_context takes a caller-owned buffer; libavformat may
        // replace it, so free the current buffer, not the original pointer.
        av_freep(&value->buffer);
        avio_context_free(&value);
    }
}

void close_input(AVFormatContext* value) noexcept
{
    if (value != nullptr)
    {
        avformat_close_input(&value);
    }
}

void free_output(AVFormatContext* value) noexcept
{
    if (value != nullptr)
    {
        avformat_free_context(value);
    }
}

void free_codec(AVCodecContext* value) noexcept
{
    if (value != nullptr)
    {
        avcodec_free_context(&value);
    }
}

void free_frame(AVFrame* value) noexcept
{
    if (value != nullptr)
    {
        av_frame_free(&value);
    }
}

void free_packet(AVPacket* value) noexcept
{
    if (value != nullptr)
    {
        av_packet_free(&value);
    }
}

void free_scale(SwsContext* value) noexcept
{
    if (value != nullptr)
    {
        sws_freeContext(value);
    }
}

bool query_cancelled(const lpb_video_sidecar_io_v1* io, bool& cancelled) noexcept
{
    cancelled = false;
    if (io == nullptr || io->is_cancelled == nullptr)
    {
        return false;
    }

    uint32_t value = 0;
    try
    {
        if (io->is_cancelled(io->cancellation_context, &value) != LPB_VIDEO_SIDECAR_IO_OK)
        {
            return false;
        }
    }
    catch (...)
    {
        return false;
    }
    cancelled = value != 0;
    return true;
}

int read_input(void* opaque, uint8_t* destination, int capacity) noexcept
{
    auto* state = static_cast<IoState*>(opaque);
    if (state == nullptr || state->callbacks == nullptr || destination == nullptr || capacity <= 0)
    {
        return AVERROR(EINVAL);
    }

    bool cancelled = false;
    if (!query_cancelled(state->callbacks, cancelled))
    {
        return AVERROR(EIO);
    }
    if (cancelled)
    {
        return AVERROR_EXIT;
    }

    uint32_t bytes_read = 0;
    int32_t callback_status = LPB_VIDEO_SIDECAR_IO_ERROR;
    try
    {
        callback_status = state->callbacks->input_read(
            state->callbacks->input_context,
            destination,
            static_cast<uint32_t>(capacity),
            &bytes_read);
    }
    catch (...)
    {
        return AVERROR(EIO);
    }

    if (callback_status == LPB_VIDEO_SIDECAR_IO_EOF)
    {
        return AVERROR_EOF;
    }
    if (callback_status == LPB_VIDEO_SIDECAR_IO_CANCELLED)
    {
        return AVERROR_EXIT;
    }
    if (callback_status != LPB_VIDEO_SIDECAR_IO_OK || bytes_read > static_cast<uint32_t>(capacity))
    {
        return AVERROR(EIO);
    }
    return bytes_read == 0 ? AVERROR_EOF : static_cast<int>(bytes_read);
}

int64_t seek_input(void* opaque, int64_t offset, int whence) noexcept
{
    auto* state = static_cast<IoState*>(opaque);
    if (state == nullptr || state->callbacks == nullptr)
    {
        return AVERROR(EINVAL);
    }

    bool cancelled = false;
    if (!query_cancelled(state->callbacks, cancelled))
    {
        return AVERROR(EIO);
    }
    if (cancelled)
    {
        return AVERROR_EXIT;
    }

    if ((whence & AVSEEK_SIZE) != 0)
    {
        uint64_t size = 0;
        try
        {
            if (state->callbacks->input_size(state->callbacks->input_context, &size) != LPB_VIDEO_SIDECAR_IO_OK ||
                size > static_cast<uint64_t>((std::numeric_limits<int64_t>::max)()))
            {
                return AVERROR(EIO);
            }
        }
        catch (...)
        {
            return AVERROR(EIO);
        }
        return static_cast<int64_t>(size);
    }

    int64_t position = -1;
    int32_t callback_status = LPB_VIDEO_SIDECAR_IO_ERROR;
    try
    {
        callback_status = state->callbacks->input_seek(
            state->callbacks->input_context,
            offset,
            whence & ~AVSEEK_FORCE,
            &position);
    }
    catch (...)
    {
        return AVERROR(EIO);
    }
    return callback_status == LPB_VIDEO_SIDECAR_IO_OK && position >= 0
        ? position
        : AVERROR(EIO);
}

int write_output(void* opaque, const uint8_t* source, int size) noexcept
{
    auto* state = static_cast<IoState*>(opaque);
    if (state == nullptr || state->callbacks == nullptr || source == nullptr || size < 0)
    {
        return AVERROR(EINVAL);
    }

    bool cancelled = false;
    if (!query_cancelled(state->callbacks, cancelled))
    {
        return AVERROR(EIO);
    }
    if (cancelled)
    {
        return AVERROR_EXIT;
    }

    uint32_t bytes_written = 0;
    int32_t callback_status = LPB_VIDEO_SIDECAR_IO_ERROR;
    try
    {
        callback_status = state->callbacks->output_write(
            state->callbacks->output_context,
            source,
            static_cast<uint32_t>(size),
            &bytes_written);
    }
    catch (...)
    {
        return AVERROR(EIO);
    }
    return callback_status == LPB_VIDEO_SIDECAR_IO_OK && bytes_written == static_cast<uint32_t>(size)
        ? size
        : AVERROR(EIO);
}

int64_t seek_output(void* opaque, int64_t offset, int whence) noexcept
{
    auto* state = static_cast<IoState*>(opaque);
    if (state == nullptr || state->callbacks == nullptr)
    {
        return AVERROR(EINVAL);
    }

    bool cancelled = false;
    if (!query_cancelled(state->callbacks, cancelled))
    {
        return AVERROR(EIO);
    }
    if (cancelled)
    {
        return AVERROR_EXIT;
    }

    if ((whence & AVSEEK_SIZE) != 0)
    {
        uint64_t size = 0;
        try
        {
            if (state->callbacks->output_size(state->callbacks->output_context, &size) != LPB_VIDEO_SIDECAR_IO_OK ||
                size > static_cast<uint64_t>((std::numeric_limits<int64_t>::max)()))
            {
                return AVERROR(EIO);
            }
        }
        catch (...)
        {
            return AVERROR(EIO);
        }
        return static_cast<int64_t>(size);
    }

    int64_t position = -1;
    int32_t callback_status = LPB_VIDEO_SIDECAR_IO_ERROR;
    try
    {
        callback_status = state->callbacks->output_seek(
            state->callbacks->output_context,
            offset,
            whence & ~AVSEEK_FORCE,
            &position);
    }
    catch (...)
    {
        return AVERROR(EIO);
    }
    return callback_status == LPB_VIDEO_SIDECAR_IO_OK && position >= 0
        ? position
        : AVERROR(EIO);
}

AvioContext create_input_io(IoState* state)
{
    auto* buffer = static_cast<unsigned char*>(av_malloc(kIoBufferSize));
    if (buffer == nullptr)
    {
        return AvioContext(nullptr, free_avio);
    }
    AVIOContext* context = avio_alloc_context(buffer, kIoBufferSize, 0, state, read_input, nullptr, seek_input);
    if (context == nullptr)
    {
        av_free(buffer);
    }
    return AvioContext(context, free_avio);
}

AvioContext create_output_io(IoState* state)
{
    auto* buffer = static_cast<unsigned char*>(av_malloc(kIoBufferSize));
    if (buffer == nullptr)
    {
        return AvioContext(nullptr, free_avio);
    }
    AVIOContext* context = avio_alloc_context(buffer, kIoBufferSize, 1, state, nullptr, write_output, seek_output);
    if (context == nullptr)
    {
        av_free(buffer);
        return AvioContext(nullptr, free_avio);
    }
    context->seekable = AVIO_SEEKABLE_NORMAL;
    return AvioContext(context, free_avio);
}

bool encoder_supports_pixel_format(const AVCodec* encoder, AVPixelFormat format) noexcept
{
    const void* configurations = nullptr;
    int count = 0;
    if (encoder == nullptr || avcodec_get_supported_config(
        nullptr, encoder, AV_CODEC_CONFIG_PIX_FORMAT, 0, &configurations, &count) < 0 || configurations == nullptr)
    {
        return false;
    }

    const auto* formats = static_cast<const AVPixelFormat*>(configurations);
    for (int index = 0; index < count; ++index)
    {
        if (formats[index] == format)
        {
            return true;
        }
    }
    return false;
}

unsigned int pixel_depth(AVPixelFormat format) noexcept
{
    const AVPixFmtDescriptor* descriptor = av_pix_fmt_desc_get(format);
    return descriptor != nullptr ? static_cast<unsigned int>(descriptor->comp[0].depth) : 0U;
}

const char* encoder_name_for(uint32_t target_codec) noexcept
{
    switch (target_codec)
    {
    case LPB_VIDEO_SIDECAR_CODEC_COPY:
        return "copy";
    case LPB_VIDEO_SIDECAR_CODEC_H264:
        return "libx264";
    case LPB_VIDEO_SIDECAR_CODEC_HEVC:
        return "libx265";
    default:
        return nullptr;
    }
}

int32_t map_error(int error, lpb_video_sidecar_result_v1* result) noexcept
{
    if (result != nullptr)
    {
        result->library_error = error;
    }
    if (error == AVERROR_EXIT)
    {
        return LPB_VIDEO_SIDECAR_CANCELLED;
    }
    if (error == AVERROR_DECODER_NOT_FOUND || error == AVERROR_ENCODER_NOT_FOUND || error == AVERROR(EINVAL))
    {
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
    return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
}

bool check_cancelled(const lpb_video_sidecar_io_v1* io, lpb_video_sidecar_result_v1* result) noexcept
{
    bool cancelled = false;
    if (!query_cancelled(io, cancelled))
    {
        if (result != nullptr)
        {
            result->library_error = AVERROR(EIO);
            result->status = LPB_VIDEO_SIDECAR_IO_FAILURE;
        }
        return true;
    }
    if (cancelled && result != nullptr)
    {
        result->library_error = AVERROR_EXIT;
        result->status = LPB_VIDEO_SIDECAR_CANCELLED;
    }
    return cancelled;
}

using PendingSei = std::map<int64_t, std::vector<lpb::media::preserved_sei_message>>;

int write_encoded_packets(AVCodecContext* encoder, AVFormatContext* output, AVStream* output_stream,
    uint64_t& packet_count, PendingSei& metadata)
{
    Packet packet(av_packet_alloc(), free_packet);
    if (!packet)
    {
        return AVERROR(ENOMEM);
    }

    int result = 0;
    while ((result = avcodec_receive_packet(encoder, packet.get())) >= 0)
    {
        const auto found = metadata.find(packet->pts);
        if (found != metadata.end()) {
            std::vector<uint8_t> nal;
            if (!lpb::media::make_preserved_sei_nal(found->second, encoder->codec_id == AV_CODEC_ID_HEVC, nal) ||
                packet->size < 4 || packet->data[0] != 0U || packet->data[1] != 0U ||
                !((packet->data[2] == 1U) || (packet->data[2] == 0U && packet->data[3] == 1U)) ||
                packet->size > INT_MAX - 4 || nal.size() > static_cast<size_t>(INT_MAX - packet->size - 4)) return AVERROR_INVALIDDATA;
            const int prefix_size = static_cast<int>(nal.size() + 4U);
            const int old_size = packet->size;
            const int grow = av_grow_packet(packet.get(), prefix_size);
            if (grow < 0) return grow;
            std::memmove(packet->data + prefix_size, packet->data, static_cast<size_t>(old_size));
            packet->data[0] = 0U;packet->data[1] = 0U;packet->data[2] = 0U;packet->data[3] = 1U;
            std::memcpy(packet->data + 4U, nal.data(), nal.size());
            metadata.erase(found);
        }
        av_packet_rescale_ts(packet.get(), encoder->time_base, output_stream->time_base);
        packet->stream_index = output_stream->index;
        const int write_result = av_interleaved_write_frame(output, packet.get());
        av_packet_unref(packet.get());
        if (write_result < 0)
        {
            return write_result;
        }
        ++packet_count;
    }
    return result == AVERROR(EAGAIN) || result == AVERROR_EOF ? 0 : result;
}

int encode_frame(AVCodecContext* encoder, AVFormatContext* output, AVStream* output_stream,
    AVFrame* frame, uint64_t& packet_count, PendingSei& metadata)
{
    int result = avcodec_send_frame(encoder, frame);
    if (result == AVERROR(EAGAIN))
    {
        result = write_encoded_packets(encoder, output, output_stream, packet_count, metadata);
        if (result < 0)
        {
            return result;
        }
        result = avcodec_send_frame(encoder, frame);
    }
    if (result < 0)
    {
        return result;
    }
    return write_encoded_packets(encoder, output, output_stream, packet_count, metadata);
}

int transcode_impl(
    const lpb_video_sidecar_request_v1* request,
    const lpb_video_sidecar_io_v1* callbacks,
    lpb_video_sidecar_result_v1* result_info)
{
    const char* selected_encoder_name = encoder_name_for(request->target_codec);
    if (selected_encoder_name == nullptr ||
        (request->target_container != LPB_VIDEO_SIDECAR_CONTAINER_MOV &&
            request->target_container != LPB_VIDEO_SIDECAR_CONTAINER_MP4) ||
        request->crf < 0 || request->crf > 51 ||
        (request->flags & ~(LPB_VIDEO_SIDECAR_REQUEST_NATIVE_TRANSPLANTS_MEBX |
            LPB_VIDEO_SIDECAR_REQUEST_NATIVE_SOURCE_PROFILE |
            LPB_VIDEO_SIDECAR_REQUEST_COMPATIBLE_PROFILE)) != 0U ||
        request->reserved[0] != 0U || request->reserved[1] != 0U || request->reserved[2] != 0U)
    {
        return LPB_VIDEO_SIDECAR_INVALID_ARGUMENT;
    }
    if (callbacks->struct_size < sizeof(lpb_video_sidecar_io_v1) || callbacks->reserved != 0U ||
        callbacks->input_read == nullptr || callbacks->input_seek == nullptr || callbacks->input_size == nullptr ||
        callbacks->output_write == nullptr || callbacks->output_seek == nullptr || callbacks->output_size == nullptr ||
        callbacks->is_cancelled == nullptr)
    {
        return LPB_VIDEO_SIDECAR_INVALID_ARGUMENT;
    }
    if (check_cancelled(callbacks, result_info))
    {
        return result_info->status == LPB_VIDEO_SIDECAR_CANCELLED
            ? LPB_VIDEO_SIDECAR_CANCELLED
            : LPB_VIDEO_SIDECAR_IO_FAILURE;
    }

    const bool compatible_profile = (request->flags & LPB_VIDEO_SIDECAR_REQUEST_COMPATIBLE_PROFILE) != 0U;
    const bool is_stream_copy = (request->target_codec == LPB_VIDEO_SIDECAR_CODEC_COPY);
    IoState input_io_state{callbacks};
    IoState output_io_state{callbacks};
    AvioContext input_io = create_input_io(&input_io_state);
    AvioContext output_io = create_output_io(&output_io_state);
    if (!input_io || !output_io)
    {
        return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
    }

    InputFormat input(nullptr, close_input);
    AVFormatContext* input_raw = avformat_alloc_context();
    if (input_raw == nullptr)
    {
        return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
    }
    input_raw->pb = input_io.get();
    input_raw->flags |= AVFMT_FLAG_CUSTOM_IO;
    int operation_result = avformat_open_input(&input_raw, nullptr, nullptr, nullptr);
    if (operation_result < 0 || input_raw == nullptr)
    {
        return LPB_VIDEO_SIDECAR_DECODE_FAILURE;
    }
    input.reset(input_raw);
    operation_result = avformat_find_stream_info(input.get(), nullptr);
    if (operation_result < 0)
    {
        result_info->library_error = operation_result;
        return map_error(operation_result, result_info);
    }

    const int video_index = av_find_best_stream(input.get(), AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
    if (video_index < 0)
    {
        result_info->library_error = video_index;
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
    AVStream* input_video = input->streams[video_index];
    if (input_video == nullptr || input_video->codecpar == nullptr || input_video->time_base.num <= 0 ||
        input_video->time_base.den <= 0 || input_video->codecpar->width <= 0 || input_video->codecpar->height <= 0)
    {
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
    for (int i = 0; i < input_video->codecpar->nb_coded_side_data; ++i) {
        const auto type = input_video->codecpar->coded_side_data[i].type;
        switch (type) {
        case AV_PKT_DATA_DISPLAYMATRIX: case AV_PKT_DATA_FRAME_CROPPING:
        case AV_PKT_DATA_MASTERING_DISPLAY_METADATA: case AV_PKT_DATA_CONTENT_LIGHT_LEVEL:
        case AV_PKT_DATA_ICC_PROFILE: case AV_PKT_DATA_STEREO3D: case AV_PKT_DATA_SPHERICAL:
            break;
        default: return LPB_VIDEO_SIDECAR_UNSUPPORTED;
        }
        for (int j = 0; j < i; ++j)
            if (input_video->codecpar->coded_side_data[j].type == type) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }

    const AVCodec* decoder = is_stream_copy ? nullptr : avcodec_find_decoder(input_video->codecpar->codec_id);
    const AVCodec* encoder = is_stream_copy ? nullptr : avcodec_find_encoder_by_name(selected_encoder_name);
    if (!is_stream_copy && (decoder == nullptr || encoder == nullptr))
    {
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
    CodecContext decoder_context(decoder != nullptr ? avcodec_alloc_context3(decoder) : nullptr, free_codec);
    if (!is_stream_copy)
    {
        if (!decoder_context)
        {
            return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
        }
        operation_result = avcodec_parameters_to_context(decoder_context.get(), input_video->codecpar);
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return map_error(operation_result, result_info);
        }
        operation_result = avcodec_open2(decoder_context.get(), decoder, nullptr);
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_DECODE_FAILURE;
        }
    }

    const AVPixelFormat source_format = static_cast<AVPixelFormat>(input_video->codecpar->format);
    const unsigned int source_depth = pixel_depth(source_format);
    if (source_format == AV_PIX_FMT_NONE || source_depth == 0U)
    {
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
    const bool native_profile = (request->flags & LPB_VIDEO_SIDECAR_REQUEST_NATIVE_SOURCE_PROFILE) != 0U;
    if (native_profile)
    {
        const auto compatible_color = [](int actual, int expected) noexcept {
            return expected > 0 && expected < 256 && expected != 2 && (actual == 2 || actual == expected);
        };
        if (request->source_pixel_depth != source_depth ||
            (request->source_color_range != AVCOL_RANGE_MPEG && request->source_color_range != AVCOL_RANGE_JPEG) ||
            (input_video->codecpar->color_range != AVCOL_RANGE_UNSPECIFIED &&
                input_video->codecpar->color_range != request->source_color_range) ||
            !compatible_color(input_video->codecpar->color_primaries, request->source_color_primaries) ||
            !compatible_color(input_video->codecpar->color_trc, request->source_color_transfer) ||
            !compatible_color(input_video->codecpar->color_space, request->source_color_matrix))
            return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
    else if (request->source_color_range != 0 || request->source_color_primaries != 0 ||
        request->source_color_transfer != 0 || request->source_color_matrix != 0 || request->source_pixel_depth != 0U)
        return LPB_VIDEO_SIDECAR_INVALID_ARGUMENT;
    AVPixelFormat target_format = source_format;
    if (!is_stream_copy)
    {
        if (request->target_codec == LPB_VIDEO_SIDECAR_CODEC_HEVC)
        {
            if (source_depth != 8U && source_depth != 10U) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
            const x265_api* api = x265_api_get(static_cast<int>(source_depth));
            if (api == nullptr || api->bit_depth != static_cast<int>(source_depth))
                return LPB_VIDEO_SIDECAR_UNSUPPORTED;
        }
        if (!encoder_supports_pixel_format(encoder, target_format))
        {
            const AVPixFmtDescriptor* source_descriptor = av_pix_fmt_desc_get(source_format);
            if (source_depth > 8U || source_descriptor == nullptr || source_descriptor->nb_components != 3 ||
                (source_descriptor->flags & AV_PIX_FMT_FLAG_RGB) != 0 ||
                source_descriptor->log2_chroma_w != 1 || source_descriptor->log2_chroma_h != 1)
            {
                return LPB_VIDEO_SIDECAR_UNSUPPORTED;
            }
            target_format = AV_PIX_FMT_YUV420P;
            if (!encoder_supports_pixel_format(encoder, target_format))
            {
                return LPB_VIDEO_SIDECAR_UNSUPPORTED;
            }
        }
        if (pixel_depth(target_format) < source_depth)
        {
            return LPB_VIDEO_SIDECAR_UNSUPPORTED;
        }
    }

    const char* format_name = request->target_container == LPB_VIDEO_SIDECAR_CONTAINER_MOV
        ? "mov"
        : "mp4";
    AVFormatContext* output_raw = nullptr;
    operation_result = avformat_alloc_output_context2(&output_raw, nullptr, format_name, nullptr);
    if (operation_result < 0 || output_raw == nullptr)
    {
        result_info->library_error = operation_result;
        return LPB_VIDEO_SIDECAR_MUX_FAILURE;
    }
    OutputFormat output(output_raw, free_output);
    output->pb = output_io.get();
    output->flags |= AVFMT_FLAG_CUSTOM_IO;
    if (!compatible_profile)
    {
        operation_result = av_dict_copy(&output->metadata, input->metadata, 0);
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_MUX_FAILURE;
        }
    }

    AVStream* output_video = avformat_new_stream(output.get(), nullptr);
    if (output_video == nullptr)
    {
        return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
    }
    int rot_cw = 0;
    if (const AVDictionaryEntry* rot_entry = av_dict_get(input_video->metadata, "rotate", nullptr, 0)) {
        rot_cw = (std::atoi(rot_entry->value) % 360 + 360) % 360;
    }
    for (int i = 0; i < input_video->codecpar->nb_coded_side_data; ++i) {
        if (input_video->codecpar->coded_side_data[i].type == AV_PKT_DATA_DISPLAYMATRIX &&
            input_video->codecpar->coded_side_data[i].size >= static_cast<int>(sizeof(int32_t) * 9)) {
            double angle = av_display_rotation_get(reinterpret_cast<const int32_t*>(input_video->codecpar->coded_side_data[i].data));
            if (!std::isnan(angle)) {
                int ccw = static_cast<int>(std::round(angle));
                rot_cw = ((-ccw) % 360 + 360) % 360;
            }
        }
    }
    if (is_stream_copy && rot_cw != 0)
    {
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
    CodecContext encoder_context(nullptr, free_codec);
    if (is_stream_copy)
    {
        operation_result = avcodec_parameters_copy(output_video->codecpar, input_video->codecpar);
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_MUX_FAILURE;
        }
        output_video->time_base = input_video->time_base;
        output_video->avg_frame_rate = input_video->avg_frame_rate;
        output_video->sample_aspect_ratio = input_video->codecpar->sample_aspect_ratio;
        output_video->codecpar->codec_tag = 0;
    }
    else
    {
        encoder_context.reset(avcodec_alloc_context3(encoder));
        if (!encoder_context)
        {
            return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
        }
        encoder_context->codec_type = AVMEDIA_TYPE_VIDEO;
        encoder_context->codec_id = request->target_codec == LPB_VIDEO_SIDECAR_CODEC_H264
            ? AV_CODEC_ID_H264
            : AV_CODEC_ID_HEVC;
        encoder_context->width = (rot_cw == 90 || rot_cw == 270) ? input_video->codecpar->height : input_video->codecpar->width;
        encoder_context->height = (rot_cw == 90 || rot_cw == 270) ? input_video->codecpar->width : input_video->codecpar->height;
        encoder_context->pix_fmt = target_format;
        encoder_context->time_base = input_video->time_base;
        encoder_context->framerate = av_guess_frame_rate(input.get(), input_video, nullptr);
        encoder_context->sample_aspect_ratio = input_video->codecpar->sample_aspect_ratio;
        encoder_context->gop_size = 60;
        encoder_context->max_b_frames = 0;
        encoder_context->color_range = input_video->codecpar->color_range;
        encoder_context->color_primaries = input_video->codecpar->color_primaries;
        encoder_context->color_trc = input_video->codecpar->color_trc;
        encoder_context->colorspace = input_video->codecpar->color_space;
        encoder_context->chroma_sample_location = input_video->codecpar->chroma_location;
        if (encoder_context->chroma_sample_location < AVCHROMA_LOC_UNSPECIFIED ||
            encoder_context->chroma_sample_location >= AVCHROMA_LOC_NB)
            return LPB_VIDEO_SIDECAR_UNSUPPORTED;
        if (native_profile)
        {
            encoder_context->color_range = static_cast<AVColorRange>(request->source_color_range);
            encoder_context->color_primaries = static_cast<AVColorPrimaries>(request->source_color_primaries);
            encoder_context->color_trc = static_cast<AVColorTransferCharacteristic>(request->source_color_transfer);
            encoder_context->colorspace = static_cast<AVColorSpace>(request->source_color_matrix);
        }
        if ((output->oformat->flags & AVFMT_GLOBALHEADER) != 0)
        {
            encoder_context->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
        }

        const auto encoder_workers = std::clamp(std::thread::hardware_concurrency(), 1U, 4U);
        const bool hevc_reencode = input_video->codecpar->codec_id == AV_CODEC_ID_HEVC ||
            request->target_codec == LPB_VIDEO_SIDECAR_CODEC_HEVC;
        if (hevc_reencode) encoder_context->thread_count = static_cast<int>(encoder_workers);
        const std::string codec_parameters = request->target_codec == LPB_VIDEO_SIDECAR_CODEC_HEVC
            ? "annexb=1:aud=0:pools=" + std::to_string(encoder_workers) +
                ":frame-threads=1"
            : "annexb=1:aud=0";
        AVDictionary* encoder_options_raw = nullptr;
        (void)av_dict_set(&encoder_options_raw, "preset", "medium", 0);
        (void)av_dict_set_int(&encoder_options_raw, "crf", request->crf, 0);
        (void)av_dict_set(&encoder_options_raw,
            request->target_codec == LPB_VIDEO_SIDECAR_CODEC_HEVC ? "x265-params" : "x264-params",
            codec_parameters.c_str(), 0);
        for (const auto types : {std::pair{AV_PKT_DATA_MASTERING_DISPLAY_METADATA, AV_FRAME_DATA_MASTERING_DISPLAY_METADATA},
                                std::pair{AV_PKT_DATA_CONTENT_LIGHT_LEVEL, AV_FRAME_DATA_CONTENT_LIGHT_LEVEL}}) {
            const auto* side = av_packet_side_data_get(input_video->codecpar->coded_side_data,
                input_video->codecpar->nb_coded_side_data, types.first);
            if (side == nullptr) continue;
            const size_t minimum = types.first == AV_PKT_DATA_MASTERING_DISPLAY_METADATA
                ? sizeof(AVMasteringDisplayMetadata) : sizeof(AVContentLightMetadata);
            if (side->size < minimum) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
            auto* copy = av_frame_side_data_new(&encoder_context->decoded_side_data,
                &encoder_context->nb_decoded_side_data, types.second, side->size, 0U);
            if (copy == nullptr) return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
            std::memcpy(copy->data, side->data, side->size);
        }
        operation_result = avcodec_open2(encoder_context.get(), encoder, &encoder_options_raw);
        av_dict_free(&encoder_options_raw);
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return map_error(operation_result, result_info);
        }
        if (pixel_depth(encoder_context->pix_fmt) < source_depth)
        {
            return LPB_VIDEO_SIDECAR_UNSUPPORTED;
        }
        operation_result = avcodec_parameters_from_context(output_video->codecpar, encoder_context.get());
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_MUX_FAILURE;
        }
        output_video->time_base = encoder_context->time_base;
        output_video->avg_frame_rate = input_video->avg_frame_rate;
        output_video->sample_aspect_ratio = input_video->sample_aspect_ratio;
        output_video->codecpar->codec_tag = 0;
    }
    if (!compatible_profile)
    {
        operation_result = av_dict_copy(&output_video->metadata, input_video->metadata, 0);
    }
    if (operation_result >= 0) operation_result = av_dict_set(&output_video->metadata, "encoder", selected_encoder_name, 0);
    if (rot_cw != 0) {
        (void)av_dict_set(&output_video->metadata, "rotate", nullptr, 0);
        (void)av_dict_set(&output->metadata, "rotate", nullptr, 0);
    }
    if (operation_result < 0) {
        result_info->library_error = operation_result;
        return LPB_VIDEO_SIDECAR_MUX_FAILURE;
    }

    if (!is_stream_copy) {
        for (int index = 0; index < input_video->codecpar->nb_coded_side_data; ++index) {
            const auto* side = &input_video->codecpar->coded_side_data[index];
            if (rot_cw != 0 && side->type == AV_PKT_DATA_DISPLAYMATRIX) continue;
            AVPacketSideData* target_side = av_packet_side_data_new(
                &output_video->codecpar->coded_side_data,
                &output_video->codecpar->nb_coded_side_data,
                side->type,
                side->size,
                0);
            if (target_side == nullptr)
            {
                return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
            }
            std::memcpy(target_side->data, side->data, side->size);
        }
    }

    std::vector<int> stream_map(input->nb_streams, -1);
    uint32_t mebx_stream_count = 0U;
    bool mapped_audio = false;
    uint32_t dropped_aux_video = 0U;
    uint32_t dropped_mebx = 0U;
    uint32_t dropped_other = 0U;
    for (unsigned int index = 0; index < input->nb_streams; ++index)
    {
        if (static_cast<int>(index) == video_index)
        {
            stream_map[index] = output_video->index;
            continue;
        }
        AVStream* input_stream = input->streams[index];
        if (input_stream == nullptr || input_stream->codecpar == nullptr)
        {
            return LPB_VIDEO_SIDECAR_UNSUPPORTED;
        }
        const bool mebx_stream = input_stream->codecpar->codec_type == AVMEDIA_TYPE_DATA &&
            input_stream->codecpar->codec_tag == kMebxCodecTag;
        if (compatible_profile)
        {
            if (mebx_stream)
            {
                ++dropped_mebx;
                stream_map[index] = -2;
                continue;
            }
            if (input_stream->codecpar->codec_type == AVMEDIA_TYPE_VIDEO)
            {
                ++dropped_aux_video;
                stream_map[index] = -2;
                continue;
            }
            if (input_stream->codecpar->codec_type != AVMEDIA_TYPE_AUDIO)
            {
                ++dropped_other;
                stream_map[index] = -2;
                continue;
            }
            if (mapped_audio)
            {
                ++dropped_other;
                stream_map[index] = -2;
                continue;
            }
            if (request->target_container == LPB_VIDEO_SIDECAR_CONTAINER_MP4 &&
                input_stream->codecpar->codec_id != AV_CODEC_ID_AAC)
            {
                stream_map[index] = -2;
                continue;
            }
        }
        else
        {
            if (mebx_stream &&
                (request->flags & LPB_VIDEO_SIDECAR_REQUEST_NATIVE_TRANSPLANTS_MEBX) != 0U)
            {
                ++mebx_stream_count;
                stream_map[index] = -2;
                continue;
            }
            if (input_stream->codecpar->codec_type != AVMEDIA_TYPE_AUDIO)
            {
                return LPB_VIDEO_SIDECAR_UNSUPPORTED;
            }
        }
        AVStream* output_stream = avformat_new_stream(output.get(), nullptr);
        if (output_stream == nullptr)
        {
            return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
        }
        operation_result = avcodec_parameters_copy(output_stream->codecpar, input_stream->codecpar);
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_MUX_FAILURE;
        }
        output_stream->codecpar->codec_tag = 0;
        output_stream->time_base = input_stream->time_base;
        if (!compatible_profile)
        {
            operation_result = av_dict_copy(&output_stream->metadata, input_stream->metadata, 0);
            if (operation_result < 0)
            {
                result_info->library_error = operation_result;
                return LPB_VIDEO_SIDECAR_MUX_FAILURE;
            }
        }
        stream_map[index] = output_stream->index;
        mapped_audio = true;
    }
    if (!compatible_profile &&
        (((request->flags & LPB_VIDEO_SIDECAR_REQUEST_NATIVE_TRANSPLANTS_MEBX) != 0U &&
            mebx_stream_count == 0U) ||
        ((request->flags & LPB_VIDEO_SIDECAR_REQUEST_NATIVE_TRANSPLANTS_MEBX) == 0U &&
            mebx_stream_count != 0U)))
    {
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }

    AVPacket* input_packet_raw = av_packet_alloc();
    if (input_packet_raw == nullptr)
    {
        return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
    }
    Packet input_packet(input_packet_raw, free_packet);
    Frame decoded(nullptr, free_frame);
    Frame pre_rotated(nullptr, free_frame);
    Frame converted(nullptr, free_frame);
    ScaleContext scaler(nullptr, free_scale);
    if (!is_stream_copy)
    {
        AVFrame* decoded_raw = av_frame_alloc();
        AVFrame* pre_rotated_raw = rot_cw != 0 ? av_frame_alloc() : nullptr;
        AVFrame* converted_raw = av_frame_alloc();
        if (decoded_raw == nullptr || converted_raw == nullptr ||
            (rot_cw != 0 && pre_rotated_raw == nullptr))
        {
            if (decoded_raw != nullptr) av_frame_free(&decoded_raw);
            if (pre_rotated_raw != nullptr) av_frame_free(&pre_rotated_raw);
            if (converted_raw != nullptr) av_frame_free(&converted_raw);
            return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
        }
        decoded.reset(decoded_raw);
        pre_rotated.reset(pre_rotated_raw);
        converted.reset(converted_raw);
        if (rot_cw != 0)
        {
            pre_rotated->format = encoder_context->pix_fmt;
            pre_rotated->width = decoder_context->width;
            pre_rotated->height = decoder_context->height;
            operation_result = av_frame_get_buffer(pre_rotated.get(), 32);
            if (operation_result < 0)
            {
                result_info->library_error = operation_result;
                return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
            }
        }
        converted->format = encoder_context->pix_fmt;
        converted->width = encoder_context->width;
        converted->height = encoder_context->height;
        converted->color_range = encoder_context->color_range;
        converted->color_primaries = encoder_context->color_primaries;
        converted->color_trc = encoder_context->color_trc;
        converted->colorspace = encoder_context->colorspace;
        converted->chroma_location = encoder_context->chroma_sample_location;
        operation_result = av_frame_get_buffer(converted.get(), 32);
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
        }

        scaler.reset(sws_getContext(
            decoder_context->width,
            decoder_context->height,
            decoder_context->pix_fmt,
            rot_cw != 0 ? decoder_context->width : encoder_context->width,
            rot_cw != 0 ? decoder_context->height : encoder_context->height,
            encoder_context->pix_fmt,
            SWS_BICUBIC,
            nullptr,
            nullptr,
            nullptr));
        if (!scaler)
        {
            return LPB_VIDEO_SIDECAR_UNSUPPORTED;
        }
    }

    if (is_stream_copy)
    {
        result_info->selected_capability = 0U;
        result_info->target_codec = request->target_codec;
        result_info->output_width = static_cast<uint32_t>(input_video->codecpar->width);
        result_info->output_height = static_cast<uint32_t>(input_video->codecpar->height);
        result_info->output_pixel_depth = source_depth;
        result_info->color_range = input_video->codecpar->color_range;
        result_info->color_primaries = input_video->codecpar->color_primaries;
        result_info->color_transfer = input_video->codecpar->color_trc;
        result_info->color_matrix = input_video->codecpar->color_space;
        result_info->time_base_numerator = input_video->time_base.num;
        result_info->time_base_denominator = input_video->time_base.den;
    }
    else
    {
        result_info->selected_capability = request->target_codec == LPB_VIDEO_SIDECAR_CODEC_HEVC
            ? LPB_VIDEO_SIDECAR_CAP_HEVC
            : LPB_VIDEO_SIDECAR_CAP_H264;
        if (source_depth > 8U)
        {
            result_info->selected_capability |= request->target_codec == LPB_VIDEO_SIDECAR_CODEC_HEVC
                ? LPB_VIDEO_SIDECAR_CAP_HEVC_10BIT
                : LPB_VIDEO_SIDECAR_CAP_H264_10BIT;
        }
        result_info->target_codec = request->target_codec;
        result_info->output_width = static_cast<uint32_t>(encoder_context->width);
        result_info->output_height = static_cast<uint32_t>(encoder_context->height);
        result_info->output_pixel_depth = pixel_depth(encoder_context->pix_fmt);
        result_info->color_range = encoder_context->color_range;
        result_info->color_primaries = encoder_context->color_primaries;
        result_info->color_transfer = encoder_context->color_trc;
        result_info->color_matrix = encoder_context->colorspace;
        result_info->time_base_numerator = encoder_context->time_base.num;
        result_info->time_base_denominator = encoder_context->time_base.den;
    }
    copy_fixed_string(result_info->encoder_name, selected_encoder_name);
    copy_fixed_string(result_info->library_version, LPB_VIDEO_SIDECAR_BUILD_VERSION);

    operation_result = avformat_write_header(output.get(), nullptr);
    if (operation_result < 0)
    {
        result_info->library_error = operation_result;
        return LPB_VIDEO_SIDECAR_MUX_FAILURE;
    }

    uint64_t encoded_packet_count = 0;
    PendingSei pending_sei;
    std::array<bool, 145> seen_sei{};
    const bool source_hevc = input_video->codecpar->codec_id == AV_CODEC_ID_HEVC;
    const unsigned length_offset = source_hevc ? 21U : 4U;
    if ((input_video->codecpar->codec_id != AV_CODEC_ID_H264 && !source_hevc) ||
        input_video->codecpar->extradata_size <= static_cast<int>(length_offset) ||
        input_video->codecpar->extradata[0] != 1U) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    const unsigned length_bytes = (input_video->codecpar->extradata[length_offset] & 3U) + 1U;
    std::vector<lpb::media::preserved_sei_message> header_sei;
    if (source_hevc && !lpb::media::collect_hevc_configuration_sei(
            {input_video->codecpar->extradata, static_cast<size_t>(input_video->codecpar->extradata_size)}, header_sei))
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    for (const auto& item : header_sei) seen_sei[item.type] = true;
    std::array<std::vector<uint8_t>, 145> static_sei;
    const auto validate_static_sei = [&](std::span<const lpb::media::preserved_sei_message> entries) {
        for (const auto& item : entries) {
            if (item.type != 137U && item.type != 144U) continue;
            auto& prior = static_sei[item.type];
            if (!prior.empty() && prior != item.payload) return false;
            prior = item.payload;
        }
        return true;
    };
    if (!validate_static_sei(header_sei) || !lpb::media::sei_metadata_supports_target(header_sei,
            request->target_codec == LPB_VIDEO_SIDECAR_CODEC_HEVC)) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    const auto pending_bytes_fit = [&](std::span<const lpb::media::preserved_sei_message> extra) {
        size_t bytes = 0U, messages = 0U;
        const auto add = [&](std::span<const lpb::media::preserved_sei_message> entries) {
            for (const auto& item : entries) {
                if (++messages > lpb::media::sei_pending_message_budget) return false;
                if (item.payload.size() > lpb::media::sei_pending_byte_budget - bytes) return false;
                bytes += item.payload.size();
            }
            return true;
        };
        if (!add(extra) || !add(header_sei)) return false;
        for (const auto& entry : pending_sei) if (!add(entry.second)) return false;
        return true;
    };
    const auto attach_header_sei = [&](int64_t pts) {
        if (header_sei.empty()) return true;
        if (pts == AV_NOPTS_VALUE) return false;
        auto& destination = pending_sei[pts];
        destination.insert(destination.begin(), std::make_move_iterator(header_sei.begin()), std::make_move_iterator(header_sei.end()));
        header_sei.clear();
        return true;
    };
    while ((operation_result = av_read_frame(input.get(), input_packet.get())) >= 0)
    {
        if (check_cancelled(callbacks, result_info))
        {
            av_packet_unref(input_packet.get());
            return result_info->status == LPB_VIDEO_SIDECAR_IO_FAILURE
                ? LPB_VIDEO_SIDECAR_IO_FAILURE
                : LPB_VIDEO_SIDECAR_CANCELLED;
        }
        const int stream_index = input_packet->stream_index;
        if (stream_index < 0 || static_cast<size_t>(stream_index) >= stream_map.size())
        {
            av_packet_unref(input_packet.get());
            return LPB_VIDEO_SIDECAR_DECODE_FAILURE;
        }
        if (stream_index == video_index)
        {
            if (is_stream_copy)
            {
                input_packet->stream_index = output_video->index;
                av_packet_rescale_ts(input_packet.get(), input_video->time_base, output_video->time_base);
                operation_result = av_interleaved_write_frame(output.get(), input_packet.get());
                av_packet_unref(input_packet.get());
                if (operation_result < 0)
                {
                    result_info->library_error = operation_result;
                    return LPB_VIDEO_SIDECAR_MUX_FAILURE;
                }
                ++result_info->video_frames;
                continue;
            }
            std::vector<lpb::media::preserved_sei_message> metadata;
            if (!lpb::media::collect_packet_sei(
                    {input_packet->data, static_cast<size_t>(input_packet->size)}, length_bytes, source_hevc, metadata))
                return LPB_VIDEO_SIDECAR_UNSUPPORTED;
            if (!lpb::media::sei_metadata_supports_target(metadata,
                    request->target_codec == LPB_VIDEO_SIDECAR_CODEC_HEVC)) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
            if (!metadata.empty()) {
                if (!pending_bytes_fit(metadata) || !validate_static_sei(metadata)) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
                for (const auto& item : metadata) seen_sei[item.type] = true;
                if ((input_packet->flags & AV_PKT_FLAG_DISCARD) != 0) {
                    // ISO edit-list preroll is decoded for references, not an
                    // output picture. Keep persistent static HDR for the first
                    // visible frame; picture-local metadata has no output owner.
                    for (auto& item : metadata) {
                        if ((item.type == 137U || item.type == 144U) &&
                            std::none_of(header_sei.begin(), header_sei.end(), [&](const auto& prior) { return prior.type == item.type; }))
                            header_sei.push_back(std::move(item));
                    }
                } else {
                    if (input_packet->pts == AV_NOPTS_VALUE || pending_sei.size() >= 1024U ||
                        !pending_sei.emplace(input_packet->pts, std::move(metadata)).second) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
                }
            }
            operation_result = avcodec_send_packet(decoder_context.get(), input_packet.get());
            av_packet_unref(input_packet.get());
            if (operation_result < 0)
            {
                result_info->library_error = operation_result;
                return LPB_VIDEO_SIDECAR_DECODE_FAILURE;
            }
            while ((operation_result = avcodec_receive_frame(decoder_context.get(), decoded.get())) >= 0)
            {
                const auto current = pending_sei.find(decoded->pts);
                const std::span<const lpb::media::preserved_sei_message> frame_metadata = current == pending_sei.end()
                    ? std::span<const lpb::media::preserved_sei_message>{} : current->second;
                if (decoded->format != source_format ||
                    (decoded->chroma_location != AVCHROMA_LOC_UNSPECIFIED &&
                     decoded->chroma_location != encoder_context->chroma_sample_location) ||
                    !frame_hdr_matches_container(decoded.get(), input_video->codecpar,
                        request->target_codec == LPB_VIDEO_SIDECAR_CODEC_HEVC, seen_sei, frame_metadata))
                {
                    return LPB_VIDEO_SIDECAR_UNSUPPORTED;
                }
                operation_result = av_frame_make_writable(converted.get());
                if (operation_result < 0)
                {
                    result_info->library_error = operation_result;
                    return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
                }
    if (native_profile)
    {
        int coefficient_space = SWS_CS_ITU601;
        if (encoder_context->colorspace == AVCOL_SPC_BT709) coefficient_space = SWS_CS_ITU709;
        else if (encoder_context->colorspace == AVCOL_SPC_BT2020_NCL ||
            encoder_context->colorspace == AVCOL_SPC_BT2020_CL) coefficient_space = SWS_CS_BT2020;
        else if (encoder_context->colorspace == AVCOL_SPC_SMPTE240M) coefficient_space = SWS_CS_SMPTE240M;
        else if (encoder_context->colorspace == AVCOL_SPC_FCC) coefficient_space = SWS_CS_FCC;
        const int* coefficients = sws_getCoefficients(coefficient_space);
        const int full_range = encoder_context->color_range == AVCOL_RANGE_JPEG ? 1 : 0;
        if (sws_setColorspaceDetails(scaler.get(), coefficients, full_range,
                coefficients, full_range, 0, 1 << 16, 1 << 16) < 0)
            return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
                AVFrame* scale_dst = rot_cw != 0 ? pre_rotated.get() : converted.get();
                if (rot_cw != 0)
                {
                    operation_result = av_frame_make_writable(pre_rotated.get());
                    if (operation_result < 0)
                    {
                        result_info->library_error = operation_result;
                        return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
                    }
                }
                const int rows = sws_scale(
                    scaler.get(), decoded->data, decoded->linesize, 0, decoder_context->height,
                    scale_dst->data, scale_dst->linesize);
                if (rows != decoder_context->height)
                {
                    return LPB_VIDEO_SIDECAR_ENCODE_FAILURE;
                }
                if (rot_cw != 0)
                {
                    rotate_frame(pre_rotated.get(), converted.get(), rot_cw, source_depth);
                }
                const int64_t frame_timestamp = decoded->pts != AV_NOPTS_VALUE ? decoded->pts : decoded->best_effort_timestamp;
                converted->pts = frame_timestamp == AV_NOPTS_VALUE
                    ? AV_NOPTS_VALUE
                    : av_rescale_q(frame_timestamp, input_video->time_base, encoder_context->time_base);
                if (!attach_header_sei(converted->pts)) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
                operation_result = encode_frame(
                    encoder_context.get(), output.get(), output_video, converted.get(), encoded_packet_count, pending_sei);
                av_frame_unref(decoded.get());
                if (operation_result < 0)
                {
                    result_info->library_error = operation_result;
                    return LPB_VIDEO_SIDECAR_ENCODE_FAILURE;
                }
                ++result_info->video_frames;
            }
            if (operation_result != AVERROR(EAGAIN) && operation_result != AVERROR_EOF)
            {
                result_info->library_error = operation_result;
                return LPB_VIDEO_SIDECAR_DECODE_FAILURE;
            }
            operation_result = 0;
            continue;
        }

        const int mapped_stream = stream_map[static_cast<size_t>(stream_index)];
        if (mapped_stream == -2)
        {
            av_packet_unref(input_packet.get());
            continue;
        }
        if (mapped_stream < 0)
        {
            av_packet_unref(input_packet.get());
            return LPB_VIDEO_SIDECAR_UNSUPPORTED;
        }
        AVStream* input_stream = input->streams[static_cast<size_t>(stream_index)];
        AVStream* output_stream = output->streams[static_cast<size_t>(mapped_stream)];
        av_packet_rescale_ts(input_packet.get(), input_stream->time_base, output_stream->time_base);
        input_packet->stream_index = mapped_stream;
        input_packet->pos = -1;
        operation_result = av_interleaved_write_frame(output.get(), input_packet.get());
        av_packet_unref(input_packet.get());
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_MUX_FAILURE;
        }
        if (input_stream->codecpar->codec_type == AVMEDIA_TYPE_AUDIO)
        {
            ++result_info->audio_packets;
        }
        else
        {
            ++result_info->other_packets;
        }
    }
    if (operation_result != AVERROR_EOF)
    {
        result_info->library_error = operation_result;
        return map_error(operation_result, result_info);
    }

    if (!is_stream_copy)
    {
        operation_result = avcodec_send_packet(decoder_context.get(), nullptr);
        if (operation_result < 0 && operation_result != AVERROR_EOF)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_DECODE_FAILURE;
        }
        while ((operation_result = avcodec_receive_frame(decoder_context.get(), decoded.get())) >= 0)
        {
            const auto current = pending_sei.find(decoded->pts);
            const std::span<const lpb::media::preserved_sei_message> frame_metadata = current == pending_sei.end()
                ? std::span<const lpb::media::preserved_sei_message>{} : current->second;
            if (decoded->format != source_format ||
                (decoded->chroma_location != AVCHROMA_LOC_UNSPECIFIED &&
                 decoded->chroma_location != encoder_context->chroma_sample_location) ||
                !frame_hdr_matches_container(decoded.get(), input_video->codecpar,
                    request->target_codec == LPB_VIDEO_SIDECAR_CODEC_HEVC, seen_sei, frame_metadata))
            {
                return LPB_VIDEO_SIDECAR_UNSUPPORTED;
            }
            operation_result = av_frame_make_writable(converted.get());
            if (operation_result < 0)
            {
                result_info->library_error = operation_result;
                return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
            }
            AVFrame* drain_scale_dst = rot_cw != 0 ? pre_rotated.get() : converted.get();
            if (rot_cw != 0)
            {
                operation_result = av_frame_make_writable(pre_rotated.get());
                if (operation_result < 0)
                {
                    result_info->library_error = operation_result;
                    return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
                }
            }
            const int rows = sws_scale(
                scaler.get(), decoded->data, decoded->linesize, 0, decoder_context->height,
                drain_scale_dst->data, drain_scale_dst->linesize);
            if (rows != decoder_context->height)
            {
                return LPB_VIDEO_SIDECAR_ENCODE_FAILURE;
            }
            if (rot_cw != 0)
            {
                rotate_frame(pre_rotated.get(), converted.get(), rot_cw, source_depth);
            }
            const int64_t frame_timestamp = decoded->pts != AV_NOPTS_VALUE ? decoded->pts : decoded->best_effort_timestamp;
            converted->pts = frame_timestamp == AV_NOPTS_VALUE
                ? AV_NOPTS_VALUE
                : av_rescale_q(frame_timestamp, input_video->time_base, encoder_context->time_base);
            if (!attach_header_sei(converted->pts)) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
            operation_result = encode_frame(
                encoder_context.get(), output.get(), output_video, converted.get(), encoded_packet_count, pending_sei);
            av_frame_unref(decoded.get());
            if (operation_result < 0)
            {
                result_info->library_error = operation_result;
                return LPB_VIDEO_SIDECAR_ENCODE_FAILURE;
            }
            ++result_info->video_frames;
        }
        if (operation_result != AVERROR(EAGAIN) && operation_result != AVERROR_EOF)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_DECODE_FAILURE;
        }

        operation_result = avcodec_send_frame(encoder_context.get(), nullptr);
        if (operation_result < 0 && operation_result != AVERROR_EOF)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_ENCODE_FAILURE;
        }
        operation_result = write_encoded_packets(
            encoder_context.get(), output.get(), output_video, encoded_packet_count, pending_sei);
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_ENCODE_FAILURE;
        }

        if (!pending_sei.empty() || !header_sei.empty() || result_info->video_frames == 0U) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
    else
    {
        if (result_info->video_frames == 0U) return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }

    if (check_cancelled(callbacks, result_info))
    {
        return result_info->status == LPB_VIDEO_SIDECAR_IO_FAILURE
            ? LPB_VIDEO_SIDECAR_IO_FAILURE
            : LPB_VIDEO_SIDECAR_CANCELLED;
    }
    operation_result = av_write_trailer(output.get());
    if (operation_result < 0)
    {
        result_info->library_error = operation_result;
        return LPB_VIDEO_SIDECAR_MUX_FAILURE;
    }
    avio_flush(output_io.get());
    if (output_io->error < 0)
    {
        result_info->library_error = output_io->error;
        return LPB_VIDEO_SIDECAR_IO_FAILURE;
    }

    uint64_t output_bytes = 0;
    try
    {
        operation_result = callbacks->output_size(callbacks->output_context, &output_bytes);
    }
    catch (...)
    {
        operation_result = LPB_VIDEO_SIDECAR_IO_ERROR;
    }
    if (operation_result != LPB_VIDEO_SIDECAR_IO_OK || output_bytes == 0U)
    {
        return LPB_VIDEO_SIDECAR_IO_FAILURE;
    }

    result_info->output_bytes = output_bytes;
    result_info->reserved[0] = dropped_aux_video;
    result_info->reserved[1] = dropped_mebx;
    result_info->reserved[2] = dropped_other;
    result_info->reserved[3] = is_stream_copy ? 1U : 0U;
    result_info->status = LPB_VIDEO_SIDECAR_OK;
    (void)encoded_packet_count;
    return LPB_VIDEO_SIDECAR_OK;
}

uint32_t supported_capabilities() noexcept
{
    uint32_t flags = LPB_VIDEO_SIDECAR_CAP_TRANSCODE;
    const AVCodec* h264 = avcodec_find_encoder_by_name("libx264");
    const AVCodec* hevc = avcodec_find_encoder_by_name("libx265");
    if (h264 != nullptr)
    {
        flags |= LPB_VIDEO_SIDECAR_CAP_H264;
        if (encoder_supports_pixel_format(h264, AV_PIX_FMT_YUV420P10LE))
        {
            flags |= LPB_VIDEO_SIDECAR_CAP_H264_10BIT;
        }
    }
    if (hevc != nullptr)
    {
        flags |= LPB_VIDEO_SIDECAR_CAP_HEVC;
        if (encoder_supports_pixel_format(hevc, AV_PIX_FMT_YUV420P10LE))
        {
            flags |= LPB_VIDEO_SIDECAR_CAP_HEVC_10BIT;
        }
    }
    return flags;
}

} // namespace

extern "C" __declspec(dllexport) int32_t LPB_VIDEO_SIDECAR_CALL lpb_video_sidecar_query_abi_v1(
    lpb_video_sidecar_abi_info_v1* info) noexcept
{
    if (info == nullptr || info->struct_size < sizeof(lpb_video_sidecar_abi_info_v1))
    {
        return LPB_VIDEO_SIDECAR_INVALID_ARGUMENT;
    }
    *info = {};
    info->struct_size = sizeof(lpb_video_sidecar_abi_info_v1);
    info->abi_major = LPB_VIDEO_SIDECAR_ABI_MAJOR;
    info->abi_minor = LPB_VIDEO_SIDECAR_ABI_MINOR;
    info->capability_flags = supported_capabilities();
    info->avformat_version = avformat_version();
    info->avcodec_version = avcodec_version();
    info->avutil_version = avutil_version();
    info->swscale_version = swscale_version();
    info->maximum_pixel_depth = (info->capability_flags & LPB_VIDEO_SIDECAR_CAP_HEVC_10BIT) != 0U ? 10U : 8U;
    copy_fixed_string(info->library_version, LPB_VIDEO_SIDECAR_BUILD_VERSION);
    return LPB_VIDEO_SIDECAR_OK;
}

extern "C" __declspec(dllexport) int32_t LPB_VIDEO_SIDECAR_CALL lpb_video_sidecar_transcode_v1(
    const lpb_video_sidecar_request_v1* request,
    const lpb_video_sidecar_io_v1* io,
    lpb_video_sidecar_result_v1* result) noexcept
{
    if (result == nullptr || result->struct_size < sizeof(lpb_video_sidecar_result_v1))
    {
        return LPB_VIDEO_SIDECAR_INVALID_ARGUMENT;
    }
    const uint32_t output_capacity = result->struct_size;
    *result = {};
    result->struct_size = output_capacity;
    result->status = LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
    copy_fixed_string(result->library_version, LPB_VIDEO_SIDECAR_BUILD_VERSION);
    if (request == nullptr || request->struct_size < sizeof(lpb_video_sidecar_request_v1) || io == nullptr)
    {
        result->status = LPB_VIDEO_SIDECAR_INVALID_ARGUMENT;
        return result->status;
    }

    try
    {
        const int32_t status = transcode_impl(request, io, result);
        result->status = status;
        return status;
    }
    catch (...)
    {
        result->status = LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
        result->library_error = AVERROR(ENOMEM);
        return result->status;
    }
}
