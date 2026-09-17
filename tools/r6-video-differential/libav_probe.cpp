// R6 research executable only.  It is intentionally not linked by the
// production Native DLL and never invokes ffmpeg.exe/ffprobe.exe.
#include <array>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <memory>
#include <string>
#include <vector>

#include <windows.h>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/display.h>
#include <libavutil/error.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>
}

namespace {

std::string error_text(int error) {
    std::array<char, AV_ERROR_MAX_STRING_SIZE> buffer{};
    av_strerror(error, buffer.data(), buffer.size());
    return buffer.data();
}

const char* safe_name(const char* value) noexcept {
    return value != nullptr ? value : "unknown";
}

bool encoder_supports_pixel_format(const AVCodec* encoder, AVPixelFormat format) noexcept {
    const void* configs = nullptr;
    int count = 0;
    if (encoder == nullptr || avcodec_get_supported_config(
        nullptr, encoder, AV_CODEC_CONFIG_PIX_FORMAT, 0, &configs, &count) < 0 || configs == nullptr) {
        return false;
    }
    const auto* formats = static_cast<const AVPixelFormat*>(configs);
    for (int index = 0; index < count; ++index) {
        if (formats[index] == format) return true;
    }
    return false;
}

const AVPacketSideData* display_matrix_side_data(const AVStream* stream) noexcept {
    if (stream == nullptr || stream->codecpar == nullptr) return nullptr;
    return av_packet_side_data_get(stream->codecpar->coded_side_data,
        stream->codecpar->nb_coded_side_data, AV_PKT_DATA_DISPLAYMATRIX);
}

void close_format_context(AVFormatContext* context) noexcept {
    avformat_close_input(&context);
}

std::string utf8_from_windows_wide(const wchar_t* value) {
    if (value == nullptr) return {};
    const int source_length = static_cast<int>(std::wcslen(value));
    if (source_length == 0) return {};
    const int output_length = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, value, source_length, nullptr, 0, nullptr, nullptr);
    if (output_length <= 0) {
        std::fprintf(stderr, "UTF-16 to UTF-8 command-line conversion failed (Win32 error %lu).\n", GetLastError());
        return {};
    }
    std::string output(static_cast<size_t>(output_length), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, source_length,
        output.data(), output_length, nullptr, nullptr) != output_length) {
        std::fprintf(stderr, "UTF-16 to UTF-8 command-line conversion was incomplete (Win32 error %lu).\n", GetLastError());
        return {};
    }
    return output;
}

void json_string(const char* value) {
    std::putchar('"');
    for (const char* p = safe_name(value); *p != '\0'; ++p) {
        if (*p == '"' || *p == '\\') std::putchar('\\');
        std::putchar(*p);
    }
    std::putchar('"');
}

int report(const char* path) {
    AVFormatContext* raw_format = nullptr;
    int result = avformat_open_input(&raw_format, path, nullptr, nullptr);
    if (result < 0) {
        std::fprintf(stderr, "avformat_open_input failed: %s\n", error_text(result).c_str());
        return 2;
    }
    std::unique_ptr<AVFormatContext, decltype(&close_format_context)> format(raw_format, &close_format_context);

    result = avformat_find_stream_info(format.get(), nullptr);
    if (result < 0) {
        std::fprintf(stderr, "avformat_find_stream_info failed: %s\n", error_text(result).c_str());
        return 3;
    }

    std::printf("{\"candidate\":\"research-libav-libraries\",\"libraries\":{");
    std::printf("\"avformat\":%u,\"avcodec\":%u,\"avutil\":%u", avformat_version(), avcodec_version(), avutil_version());
    std::printf("},\"format\":");
    json_string(format->iformat != nullptr ? format->iformat->name : "unknown");
    std::printf(",\"durationUs\":%lld,\"streams\":[", static_cast<long long>(format->duration));

    for (unsigned int index = 0; index < format->nb_streams; ++index) {
        if (index != 0) std::putchar(',');
        const AVStream* stream = format->streams[index];
        const AVCodecParameters* parameters = stream->codecpar;
        const AVCodec* decoder = avcodec_find_decoder(parameters->codec_id);
        bool decoder_opens = false;
        std::string open_error;
        if (decoder != nullptr) {
            AVCodecContext* raw_decoder = avcodec_alloc_context3(decoder);
            if (raw_decoder != nullptr) {
                const int copy_result = avcodec_parameters_to_context(raw_decoder, parameters);
                const int open_result = copy_result < 0 ? copy_result : avcodec_open2(raw_decoder, decoder, nullptr);
                decoder_opens = open_result >= 0;
                if (open_result < 0) open_error = error_text(open_result);
                avcodec_free_context(&raw_decoder);
            } else {
                open_error = "avcodec_alloc_context3 failed";
            }
        } else {
            open_error = "decoder not present in candidate build";
        }

        double rotation = 0.0;
        const AVPacketSideData* display_matrix = display_matrix_side_data(stream);
        if (display_matrix != nullptr && display_matrix->size >= 9U * sizeof(int32_t)) {
            rotation = -av_display_rotation_get(reinterpret_cast<const int32_t*>(display_matrix->data));
        }

        const AVPixFmtDescriptor* pixel = av_pix_fmt_desc_get(static_cast<AVPixelFormat>(parameters->format));
        std::printf("{\"index\":%u,\"type\":", index);
        json_string(av_get_media_type_string(parameters->codec_type));
        std::printf(",\"codec\":");
        json_string(avcodec_get_name(parameters->codec_id));
        std::printf(",\"profile\":");
        json_string(avcodec_profile_name(parameters->codec_id, parameters->profile));
        std::printf(",\"width\":%d,\"height\":%d,\"sampleRate\":%d,\"channels\":%d",
            parameters->width, parameters->height, parameters->sample_rate, parameters->ch_layout.nb_channels);
        std::printf(",\"pixelFormat\":");
        json_string(pixel != nullptr ? pixel->name : "unknown");
        std::printf(",\"colorPrimaries\":%d,\"colorTransfer\":%d,\"colorSpace\":%d",
            parameters->color_primaries, parameters->color_trc, parameters->color_space);
        std::printf(",\"rotationDegrees\":%.3f,\"timeBase\":\"%d/%d\",\"averageFrameRate\":\"%d/%d\"",
            rotation, stream->time_base.num, stream->time_base.den,
            stream->avg_frame_rate.num, stream->avg_frame_rate.den);
        std::printf(",\"decoder\":");
        json_string(decoder != nullptr ? decoder->name : "missing");
        std::printf(",\"decoderOpened\":%s", decoder_opens ? "true" : "false");
        if (!open_error.empty()) {
            std::printf(",\"decoderError\":");
            json_string(open_error.c_str());
        }
        std::putchar('}');
    }
    std::puts("]}");
    return 0;
}

int write_encoded_packets(AVCodecContext* encoder, AVFormatContext* output, AVStream* output_stream) {
    AVPacket* packet = av_packet_alloc();
    if (packet == nullptr) return AVERROR(ENOMEM);

    int result = 0;
    while ((result = avcodec_receive_packet(encoder, packet)) >= 0) {
        av_packet_rescale_ts(packet, encoder->time_base, output_stream->time_base);
        packet->stream_index = output_stream->index;
        const int write_result = av_interleaved_write_frame(output, packet);
        av_packet_unref(packet);
        if (write_result < 0) {
            av_packet_free(&packet);
            return write_result;
        }
    }
    av_packet_free(&packet);
    return result == AVERROR(EAGAIN) || result == AVERROR_EOF ? 0 : result;
}

int transcode(const char* input_path, const char* output_path, const char* target_codec_name) {
    const AVCodecID target_id = std::strcmp(target_codec_name, "h264") == 0
        ? AV_CODEC_ID_H264
        : std::strcmp(target_codec_name, "hevc") == 0 ? AV_CODEC_ID_HEVC : AV_CODEC_ID_NONE;
    if (target_id == AV_CODEC_ID_NONE) {
        std::fputs("target codec must be h264 or hevc\n", stderr);
        return 64;
    }

    AVFormatContext* input = nullptr;
    AVFormatContext* output = nullptr;
    AVCodecContext* decoder_context = nullptr;
    AVCodecContext* encoder_context = nullptr;
    AVFrame* decoded = nullptr;
    AVFrame* converted = nullptr;
    AVPacket* input_packet = nullptr;
    SwsContext* scaler = nullptr;
    AVStream* input_video = nullptr;
    AVStream* output_video = nullptr;
    AVStream* output_audio = nullptr;
    const AVCodec* decoder = nullptr;
    const AVCodec* encoder = nullptr;
    const char* encoder_name = nullptr;
    AVDictionary* encoder_options = nullptr;
    AVPixelFormat source_format = AV_PIX_FMT_NONE;
    const AVPacketSideData* display_matrix = nullptr;
    AVPacketSideData* output_matrix = nullptr;
    int video_index = -1;
    int audio_index = -1;
    int status = 1;

    int result = avformat_open_input(&input, input_path, nullptr, nullptr);
    if (result < 0) goto fail;
    result = avformat_find_stream_info(input, nullptr);
    if (result < 0) goto fail;

    video_index = av_find_best_stream(input, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
    if (video_index < 0) {
        result = video_index;
        goto fail;
    }
    audio_index = av_find_best_stream(input, AVMEDIA_TYPE_AUDIO, -1, -1, nullptr, 0);
    input_video = input->streams[video_index];

    decoder = avcodec_find_decoder(input_video->codecpar->codec_id);
    if (decoder == nullptr) {
        result = AVERROR_DECODER_NOT_FOUND;
        goto fail;
    }
    decoder_context = avcodec_alloc_context3(decoder);
    if (decoder_context == nullptr) {
        result = AVERROR(ENOMEM);
        goto fail;
    }
    result = avcodec_parameters_to_context(decoder_context, input_video->codecpar);
    if (result < 0) goto fail;
    result = avcodec_open2(decoder_context, decoder, nullptr);
    if (result < 0) goto fail;

    encoder_name = target_id == AV_CODEC_ID_H264 ? "libx264" : "libx265";
    encoder = avcodec_find_encoder_by_name(encoder_name);
    if (encoder == nullptr) {
        result = AVERROR_ENCODER_NOT_FOUND;
        goto fail;
    }

    result = avformat_alloc_output_context2(&output, nullptr, nullptr, output_path);
    if (result < 0 || output == nullptr) {
        if (result >= 0) result = AVERROR_UNKNOWN;
        goto fail;
    }
    output_video = avformat_new_stream(output, nullptr);
    if (output_video == nullptr) {
        result = AVERROR(ENOMEM);
        goto fail;
    }

    encoder_context = avcodec_alloc_context3(encoder);
    if (encoder_context == nullptr) {
        result = AVERROR(ENOMEM);
        goto fail;
    }
    encoder_context->codec_type = AVMEDIA_TYPE_VIDEO;
    encoder_context->codec_id = target_id;
    encoder_context->width = decoder_context->width;
    encoder_context->height = decoder_context->height;
    // A 10-bit HEVC input is not silently normalized to 8-bit when the
    // selected library encoder actually advertises the matching format.  If it
    // cannot accept the source precision, the candidate reports that failure;
    // it does not relabel an 8-bit output as HDR-preserved.
    source_format = decoder_context->pix_fmt;
    encoder_context->pix_fmt = encoder_supports_pixel_format(encoder, source_format)
        ? source_format : AV_PIX_FMT_YUV420P;
    if (source_format != AV_PIX_FMT_YUV420P && encoder_context->pix_fmt == AV_PIX_FMT_YUV420P &&
        av_pix_fmt_desc_get(source_format) != nullptr &&
        av_pix_fmt_desc_get(source_format)->comp[0].depth > 8) {
        result = AVERROR(ENOSYS);
        goto fail;
    }
    encoder_context->time_base = input_video->time_base.num > 0 && input_video->time_base.den > 0
        ? input_video->time_base : AVRational{1, 30};
    encoder_context->framerate = av_guess_frame_rate(input, input_video, nullptr);
    encoder_context->bit_rate = target_id == AV_CODEC_ID_H264 ? 8'000'000 : 5'000'000;
    encoder_context->gop_size = 60;
    encoder_context->max_b_frames = 0;
    encoder_context->color_range = decoder_context->color_range;
    encoder_context->color_primaries = decoder_context->color_primaries;
    encoder_context->color_trc = decoder_context->color_trc;
    encoder_context->colorspace = decoder_context->colorspace;
    if ((output->oformat->flags & AVFMT_GLOBALHEADER) != 0) encoder_context->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    av_dict_set(&encoder_options, "preset", "medium", 0);
    av_dict_set(&encoder_options, "crf", "23", 0);
    result = avcodec_open2(encoder_context, encoder, &encoder_options);
    av_dict_free(&encoder_options);
    if (result < 0) goto fail;
    result = avcodec_parameters_from_context(output_video->codecpar, encoder_context);
    if (result < 0) goto fail;
    output_video->time_base = encoder_context->time_base;
    display_matrix = display_matrix_side_data(input_video);
    if (display_matrix != nullptr && display_matrix->size > 0) {
        output_matrix = av_packet_side_data_new(
            &output_video->codecpar->coded_side_data, &output_video->codecpar->nb_coded_side_data,
            AV_PKT_DATA_DISPLAYMATRIX, display_matrix->size, 0);
        if (output_matrix == nullptr) {
            result = AVERROR(ENOMEM);
            goto fail;
        }
        std::memcpy(output_matrix->data, display_matrix->data, display_matrix->size);
    }

    if (audio_index >= 0) {
        output_audio = avformat_new_stream(output, nullptr);
        if (output_audio == nullptr) {
            result = AVERROR(ENOMEM);
            goto fail;
        }
        result = avcodec_parameters_copy(output_audio->codecpar, input->streams[audio_index]->codecpar);
        if (result < 0) goto fail;
        output_audio->codecpar->codec_tag = 0;
        output_audio->time_base = input->streams[audio_index]->time_base;
    }

    if ((output->oformat->flags & AVFMT_NOFILE) == 0) {
        result = avio_open(&output->pb, output_path, AVIO_FLAG_WRITE);
        if (result < 0) goto fail;
    }
    result = avformat_write_header(output, nullptr);
    if (result < 0) goto fail;

    decoded = av_frame_alloc();
    converted = av_frame_alloc();
    input_packet = av_packet_alloc();
    if (decoded == nullptr || converted == nullptr || input_packet == nullptr) {
        result = AVERROR(ENOMEM);
        goto fail;
    }
    converted->format = encoder_context->pix_fmt;
    converted->width = encoder_context->width;
    converted->height = encoder_context->height;
    result = av_frame_get_buffer(converted, 32);
    if (result < 0) goto fail;
    scaler = sws_getContext(
        decoder_context->width, decoder_context->height, decoder_context->pix_fmt,
        encoder_context->width, encoder_context->height, encoder_context->pix_fmt,
        SWS_BICUBIC, nullptr, nullptr, nullptr);
    if (scaler == nullptr) {
        result = AVERROR(EINVAL);
        goto fail;
    }

    while ((result = av_read_frame(input, input_packet)) >= 0) {
        if (input_packet->stream_index == video_index) {
            result = avcodec_send_packet(decoder_context, input_packet);
            av_packet_unref(input_packet);
            if (result < 0) goto fail;
            while ((result = avcodec_receive_frame(decoder_context, decoded)) >= 0) {
                result = av_frame_make_writable(converted);
                if (result < 0) goto fail;
                sws_scale(scaler, decoded->data, decoded->linesize, 0, decoder_context->height,
                    converted->data, converted->linesize);
                const int64_t source_pts = decoded->best_effort_timestamp == AV_NOPTS_VALUE
                    ? decoded->pts : decoded->best_effort_timestamp;
                converted->pts = source_pts == AV_NOPTS_VALUE ? AV_NOPTS_VALUE
                    : av_rescale_q(source_pts, input_video->time_base, encoder_context->time_base);
                result = avcodec_send_frame(encoder_context, converted);
                av_frame_unref(decoded);
                if (result < 0) goto fail;
                result = write_encoded_packets(encoder_context, output, output_video);
                if (result < 0) goto fail;
            }
            if (result != AVERROR(EAGAIN) && result != AVERROR_EOF) goto fail;
            result = 0;
        } else if (output_audio != nullptr && input_packet->stream_index == audio_index) {
            av_packet_rescale_ts(input_packet, input->streams[audio_index]->time_base, output_audio->time_base);
            input_packet->stream_index = output_audio->index;
            result = av_interleaved_write_frame(output, input_packet);
            av_packet_unref(input_packet);
            if (result < 0) goto fail;
        } else {
            av_packet_unref(input_packet);
        }
    }
    if (result != AVERROR_EOF) goto fail;

    result = avcodec_send_packet(decoder_context, nullptr);
    if (result < 0) goto fail;
    while ((result = avcodec_receive_frame(decoder_context, decoded)) >= 0) {
        result = av_frame_make_writable(converted);
        if (result < 0) goto fail;
        sws_scale(scaler, decoded->data, decoded->linesize, 0, decoder_context->height,
            converted->data, converted->linesize);
        converted->pts = decoded->best_effort_timestamp == AV_NOPTS_VALUE ? decoded->pts
            : av_rescale_q(decoded->best_effort_timestamp, input_video->time_base, encoder_context->time_base);
        result = avcodec_send_frame(encoder_context, converted);
        av_frame_unref(decoded);
        if (result < 0) goto fail;
        result = write_encoded_packets(encoder_context, output, output_video);
        if (result < 0) goto fail;
    }
    if (result != AVERROR_EOF && result != AVERROR(EAGAIN)) goto fail;

    result = avcodec_send_frame(encoder_context, nullptr);
    if (result < 0) goto fail;
    result = write_encoded_packets(encoder_context, output, output_video);
    if (result < 0) goto fail;
    result = av_write_trailer(output);
    if (result < 0) goto fail;

    std::printf("{\"candidate\":\"minimal-libav-libraries\",\"operation\":\"transcode\",\"encoder\":");
    json_string(encoder_name);
    std::printf(",\"audioMode\":");
    json_string(output_audio == nullptr ? "none" : "packet-copy");
    std::puts(",\"metadataTracks\":\"not-muxed-by-candidate\"}");
    status = 0;

fail:
    if (status != 0) std::fprintf(stderr, "libav transcode failed: %s\n", error_text(result).c_str());
    if (encoder_options != nullptr) av_dict_free(&encoder_options);
    if (input_packet != nullptr) av_packet_free(&input_packet);
    if (converted != nullptr) av_frame_free(&converted);
    if (decoded != nullptr) av_frame_free(&decoded);
    if (scaler != nullptr) sws_freeContext(scaler);
    if (encoder_context != nullptr) avcodec_free_context(&encoder_context);
    if (decoder_context != nullptr) avcodec_free_context(&decoder_context);
    if (output != nullptr) {
        if ((output->oformat->flags & AVFMT_NOFILE) == 0 && output->pb != nullptr) avio_closep(&output->pb);
        avformat_free_context(output);
    }
    if (input != nullptr) avformat_close_input(&input);
    return status;
}

} // namespace

#if defined(LPB_R6_BACKEND_LIBRARY)
// This is deliberately a tiny C ABI surface: a production-shaped candidate
// must not expose C++/libav types across a future managed/native boundary.
extern "C" __declspec(dllexport) int lpb_r6_transcode_video_utf16(
    const wchar_t* input_path, const wchar_t* output_path, const wchar_t* target_codec_name) {
    const std::string input = utf8_from_windows_wide(input_path);
    const std::string output = utf8_from_windows_wide(output_path);
    const std::string target = utf8_from_windows_wide(target_codec_name);
    if ((input_path != nullptr && *input_path != L'\0' && input.empty()) ||
        (output_path != nullptr && *output_path != L'\0' && output.empty()) ||
        (target_codec_name != nullptr && *target_codec_name != L'\0' && target.empty())) {
        return 64;
    }
    return transcode(input.c_str(), output.c_str(), target.c_str());
}
#else
int wmain(int argc, wchar_t** argv) {
    std::vector<std::string> utf8_arguments;
    utf8_arguments.reserve(static_cast<size_t>(argc));
    for (int index = 0; index < argc; ++index) {
        std::string value = utf8_from_windows_wide(argv[index]);
        if (argv[index] != nullptr && *argv[index] != L'\0' && value.empty()) return 64;
        utf8_arguments.push_back(std::move(value));
    }

    if (argc == 3 && utf8_arguments[1] == "probe") return report(utf8_arguments[2].c_str());
    if (argc == 5 && utf8_arguments[1] == "transcode") {
        return transcode(utf8_arguments[2].c_str(), utf8_arguments[3].c_str(), utf8_arguments[4].c_str());
    }
    {
        std::fputs("usage: lpb_r6_libav_probe probe <standalone-video>\n"
                   "       lpb_r6_libav_probe transcode <input> <output.mov|output.mp4> <h264|hevc>\n", stderr);
        return 64;
    }
}
#endif
