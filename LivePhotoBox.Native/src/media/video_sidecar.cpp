#include "video_sidecar_abi.h"

#ifndef LPB_VIDEO_SIDECAR_BUILD_VERSION
#error LPB_VIDEO_SIDECAR_BUILD_VERSION must come from the pinned build profile.
#endif

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/error.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>
}

namespace {

constexpr int kIoBufferSize = 64 * 1024;
constexpr uint32_t kMebxCodecTag = static_cast<uint32_t>('m') |
    (static_cast<uint32_t>('e') << 8U) |
    (static_cast<uint32_t>('b') << 16U) |
    (static_cast<uint32_t>('x') << 24U);

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

int write_encoded_packets(AVCodecContext* encoder, AVFormatContext* output, AVStream* output_stream,
    uint64_t& packet_count)
{
    Packet packet(av_packet_alloc(), free_packet);
    if (!packet)
    {
        return AVERROR(ENOMEM);
    }

    int result = 0;
    while ((result = avcodec_receive_packet(encoder, packet.get())) >= 0)
    {
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
    AVFrame* frame, uint64_t& packet_count)
{
    int result = avcodec_send_frame(encoder, frame);
    if (result == AVERROR(EAGAIN))
    {
        result = write_encoded_packets(encoder, output, output_stream, packet_count);
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
    return write_encoded_packets(encoder, output, output_stream, packet_count);
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
        (request->flags & ~LPB_VIDEO_SIDECAR_REQUEST_NATIVE_TRANSPLANTS_MEBX) != 0U ||
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

    const AVCodec* decoder = avcodec_find_decoder(input_video->codecpar->codec_id);
    const AVCodec* encoder = avcodec_find_encoder_by_name(selected_encoder_name);
    if (decoder == nullptr || encoder == nullptr)
    {
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
    CodecContext decoder_context(avcodec_alloc_context3(decoder), free_codec);
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

    const AVPixelFormat source_format = static_cast<AVPixelFormat>(input_video->codecpar->format);
    const unsigned int source_depth = pixel_depth(source_format);
    if (source_format == AV_PIX_FMT_NONE || source_depth == 0U)
    {
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }
    AVPixelFormat target_format = source_format;
    if (!encoder_supports_pixel_format(encoder, target_format))
    {
        if (source_depth > 8U)
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
    operation_result = av_dict_copy(&output->metadata, input->metadata, 0);
    if (operation_result < 0)
    {
        result_info->library_error = operation_result;
        return LPB_VIDEO_SIDECAR_MUX_FAILURE;
    }

    AVStream* output_video = avformat_new_stream(output.get(), nullptr);
    if (output_video == nullptr)
    {
        return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
    }
    CodecContext encoder_context(avcodec_alloc_context3(encoder), free_codec);
    if (!encoder_context)
    {
        return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
    }
    encoder_context->codec_type = AVMEDIA_TYPE_VIDEO;
    encoder_context->codec_id = request->target_codec == LPB_VIDEO_SIDECAR_CODEC_H264
        ? AV_CODEC_ID_H264
        : AV_CODEC_ID_HEVC;
    encoder_context->width = input_video->codecpar->width;
    encoder_context->height = input_video->codecpar->height;
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
    if ((output->oformat->flags & AVFMT_GLOBALHEADER) != 0)
    {
        encoder_context->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    }

    AVDictionary* encoder_options_raw = nullptr;
    (void)av_dict_set(&encoder_options_raw, "preset", "medium", 0);
    (void)av_dict_set_int(&encoder_options_raw, "crf", request->crf, 0);
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

    const AVPacketSideData* display_matrix = av_packet_side_data_get(
        input_video->codecpar->coded_side_data,
        input_video->codecpar->nb_coded_side_data,
        AV_PKT_DATA_DISPLAYMATRIX);
    if (display_matrix != nullptr && display_matrix->size > 0U)
    {
        AVPacketSideData* target_matrix = av_packet_side_data_new(
            &output_video->codecpar->coded_side_data,
            &output_video->codecpar->nb_coded_side_data,
            AV_PKT_DATA_DISPLAYMATRIX,
            display_matrix->size,
            0);
        if (target_matrix == nullptr)
        {
            return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
        }
        std::memcpy(target_matrix->data, display_matrix->data, display_matrix->size);
    }

    std::vector<int> stream_map(input->nb_streams, -1);
    uint32_t mebx_stream_count = 0U;
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
        operation_result = av_dict_copy(&output_stream->metadata, input_stream->metadata, 0);
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_MUX_FAILURE;
        }
        stream_map[index] = output_stream->index;
    }
    if (((request->flags & LPB_VIDEO_SIDECAR_REQUEST_NATIVE_TRANSPLANTS_MEBX) != 0U &&
            mebx_stream_count != 1U) ||
        ((request->flags & LPB_VIDEO_SIDECAR_REQUEST_NATIVE_TRANSPLANTS_MEBX) == 0U &&
            mebx_stream_count != 0U))
    {
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }

    AVFrame* decoded_raw = av_frame_alloc();
    AVFrame* converted_raw = av_frame_alloc();
    AVPacket* input_packet_raw = av_packet_alloc();
    if (decoded_raw == nullptr || converted_raw == nullptr || input_packet_raw == nullptr)
    {
        if (decoded_raw != nullptr) av_frame_free(&decoded_raw);
        if (converted_raw != nullptr) av_frame_free(&converted_raw);
        if (input_packet_raw != nullptr) av_packet_free(&input_packet_raw);
        return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
    }
    Frame decoded(decoded_raw, free_frame);
    Frame converted(converted_raw, free_frame);
    Packet input_packet(input_packet_raw, free_packet);
    converted->format = encoder_context->pix_fmt;
    converted->width = encoder_context->width;
    converted->height = encoder_context->height;
    operation_result = av_frame_get_buffer(converted.get(), 32);
    if (operation_result < 0)
    {
        result_info->library_error = operation_result;
        return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
    }

    ScaleContext scaler(sws_getContext(
        decoder_context->width,
        decoder_context->height,
        decoder_context->pix_fmt,
        encoder_context->width,
        encoder_context->height,
        encoder_context->pix_fmt,
        SWS_BICUBIC,
        nullptr,
        nullptr,
        nullptr), free_scale);
    if (!scaler)
    {
        return LPB_VIDEO_SIDECAR_UNSUPPORTED;
    }

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
    copy_fixed_string(result_info->encoder_name, selected_encoder_name);
    copy_fixed_string(result_info->library_version, LPB_VIDEO_SIDECAR_BUILD_VERSION);

    operation_result = avformat_write_header(output.get(), nullptr);
    if (operation_result < 0)
    {
        result_info->library_error = operation_result;
        return LPB_VIDEO_SIDECAR_MUX_FAILURE;
    }

    uint64_t encoded_packet_count = 0;
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
            operation_result = avcodec_send_packet(decoder_context.get(), input_packet.get());
            av_packet_unref(input_packet.get());
            if (operation_result < 0)
            {
                result_info->library_error = operation_result;
                return LPB_VIDEO_SIDECAR_DECODE_FAILURE;
            }
            while ((operation_result = avcodec_receive_frame(decoder_context.get(), decoded.get())) >= 0)
            {
                if (decoded->format != source_format)
                {
                    return LPB_VIDEO_SIDECAR_UNSUPPORTED;
                }
                operation_result = av_frame_make_writable(converted.get());
                if (operation_result < 0)
                {
                    result_info->library_error = operation_result;
                    return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
                }
                const int rows = sws_scale(
                    scaler.get(), decoded->data, decoded->linesize, 0, decoder_context->height,
                    converted->data, converted->linesize);
                if (rows != decoder_context->height)
                {
                    return LPB_VIDEO_SIDECAR_ENCODE_FAILURE;
                }
                const int64_t frame_timestamp = decoded->best_effort_timestamp != AV_NOPTS_VALUE
                    ? decoded->best_effort_timestamp
                    : decoded->pts;
                converted->pts = frame_timestamp == AV_NOPTS_VALUE
                    ? AV_NOPTS_VALUE
                    : av_rescale_q(frame_timestamp, input_video->time_base, encoder_context->time_base);
                operation_result = encode_frame(
                    encoder_context.get(), output.get(), output_video, converted.get(), encoded_packet_count);
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

    operation_result = avcodec_send_packet(decoder_context.get(), nullptr);
    if (operation_result < 0 && operation_result != AVERROR_EOF)
    {
        result_info->library_error = operation_result;
        return LPB_VIDEO_SIDECAR_DECODE_FAILURE;
    }
    while ((operation_result = avcodec_receive_frame(decoder_context.get(), decoded.get())) >= 0)
    {
        if (decoded->format != source_format)
        {
            return LPB_VIDEO_SIDECAR_UNSUPPORTED;
        }
        operation_result = av_frame_make_writable(converted.get());
        if (operation_result < 0)
        {
            result_info->library_error = operation_result;
            return LPB_VIDEO_SIDECAR_INTERNAL_ERROR;
        }
        const int rows = sws_scale(
            scaler.get(), decoded->data, decoded->linesize, 0, decoder_context->height,
            converted->data, converted->linesize);
        if (rows != decoder_context->height)
        {
            return LPB_VIDEO_SIDECAR_ENCODE_FAILURE;
        }
        const int64_t frame_timestamp = decoded->best_effort_timestamp != AV_NOPTS_VALUE
            ? decoded->best_effort_timestamp
            : decoded->pts;
        converted->pts = frame_timestamp == AV_NOPTS_VALUE
            ? AV_NOPTS_VALUE
            : av_rescale_q(frame_timestamp, input_video->time_base, encoder_context->time_base);
        operation_result = encode_frame(
            encoder_context.get(), output.get(), output_video, converted.get(), encoded_packet_count);
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
        encoder_context.get(), output.get(), output_video, encoded_packet_count);
    if (operation_result < 0)
    {
        result_info->library_error = operation_result;
        return LPB_VIDEO_SIDECAR_ENCODE_FAILURE;
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
