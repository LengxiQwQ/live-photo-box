#include "livephotobox_native.h"
#include "containers/isobmff_mebx_transplant.h"
#include "containers/isobmff_video_profile.h"

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <memory>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace
{

struct cancel_state
{
    uint32_t calls{};
    uint32_t cancel_after{};
};

int32_t LPB_CALL cancel_callback(void* opaque)
{
    auto* state = static_cast<cancel_state*>(opaque);
    if (state == nullptr) return 1;
    ++state->calls;
    return state->cancel_after != 0U && state->calls >= state->cancel_after ? 1 : 0;
}

bool read_at(void* opaque, uint64_t offset, std::span<uint8_t> output) noexcept
{
    auto* input = static_cast<std::ifstream*>(opaque);
    if (input == nullptr || !input->is_open() || offset > static_cast<uint64_t>((std::numeric_limits<std::streamoff>::max)()) ||
        output.size() > static_cast<size_t>((std::numeric_limits<std::streamoff>::max)() - static_cast<std::streamoff>(offset))) return false;
    input->clear();
    input->seekg(static_cast<std::streamoff>(offset), std::ios::beg);
    if (!input->good()) return false;
    if (output.empty()) return true;
    input->read(reinterpret_cast<char*>(output.data()), static_cast<std::streamsize>(output.size()));
    return input->good() || input->gcount() == static_cast<std::streamsize>(output.size());
}

bool inspect_profile(const std::filesystem::path& path, lpb::media::isobmff_video_profile& profile)
{
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file.is_open()) return false;
    const std::streamoff end = file.tellg();
    if (end <= 0) return false;
    lpb::random_access_reader source{static_cast<uint64_t>(end), &file, read_at};
    lpb::media::video_profile_failure failure{};
    return lpb::media::inspect_isobmff_video_profile(source, profile, failure);
}

std::string get_last_error(lpb_context* context)
{
    std::array<char, 2048> buffer{};
    size_t required = 0;
    if (lpb_get_last_error(context, buffer.data(), buffer.size(), &required) != LPB_RESULT_OK) return {};
    return buffer.data();
}

bool no_owned_staging_residue(const std::filesystem::path& directory)
{
    std::error_code error;
    for (std::filesystem::directory_iterator entry(directory, error), end; !error && entry != end; entry.increment(error))
    {
        const std::wstring filename = entry->path().filename().wstring();
        if (filename.rfind(L"lpb-video-sidecar-", 0U) == 0U) return false;
    }
    return !error;
}

bool profile_matches_source(const lpb::media::isobmff_video_profile& input,
    const lpb::media::isobmff_video_profile& output)
{
    return output.classification == lpb::media::video_profile_class::preservation_critical &&
        output.bit_depth_luma > 8U && output.bit_depth_chroma > 8U &&
        output.color_primaries == input.color_primaries &&
        output.transfer_characteristics == input.transfer_characteristics &&
        output.matrix_coefficients == input.matrix_coefficients &&
        output.full_range_known != 0U && input.full_range_known != 0U &&
        output.full_range == input.full_range;
}

bool matches_minimal_libav_success_identity(
    const lpb_video_backend_diagnostics& diagnostics,
    lpb_video_codec expected_codec)
{
    std::array<std::string_view, 5> fields{};
    const char* cursor = diagnostics.selected_encoder;
    for (std::size_t index = 0; index < fields.size(); ++index) {
        const char* delimiter = std::strchr(cursor, '|');
        if (index + 1U < fields.size()) {
            if (delimiter == nullptr) return false;
            fields[index] = std::string_view(cursor, static_cast<std::size_t>(delimiter - cursor));
            cursor = delimiter + 1;
        } else {
            if (delimiter != nullptr) return false;
            fields[index] = cursor;
        }
    }

    const std::string_view expected_encoder = expected_codec == LPB_VIDEO_CODEC_HEVC
        ? "libx265" : "libx264";
    const std::string_view expected_output_profile = expected_codec == LPB_VIDEO_CODEC_HEVC
        ? "oH10.10.9.18.9.F" : "oA10.10.9.18.9.F";
    return fields[0] == "s1v" && fields[1] == "9.0.1" &&
        fields[2] == expected_encoder && fields[3] == "iH10.10.9.18.9.F" &&
        fields[4] == expected_output_profile;
}

bool load_bytes(const std::filesystem::path& path, std::vector<uint8_t>& bytes)
{
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file.is_open()) return false;
    const std::streamoff end = file.tellg();
    if (end <= 0 || static_cast<uint64_t>(end) > static_cast<uint64_t>((std::numeric_limits<size_t>::max)())) return false;
    bytes.resize(static_cast<size_t>(end));
    file.seekg(0, std::ios::beg);
    file.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    return file.gcount() == static_cast<std::streamsize>(bytes.size());
}

bool validate_success(lpb_context* context, const std::filesystem::path& input_path,
    const std::filesystem::path& output_path, lpb_video_codec expected_codec,
    const lpb_video_backend_diagnostics& diagnostics)
{
    if (!std::filesystem::is_regular_file(output_path) ||
        diagnostics.hardware_mode != LPB_VIDEO_HARDWARE_SOFTWARE_FORCED ||
        diagnostics.hardware_fallback_occurred != 0 ||
        !matches_minimal_libav_success_identity(diagnostics, expected_codec) ||
        std::strstr(diagnostics.fallback_reason, "no fallback") == nullptr) return false;

    lpb::media::isobmff_video_profile input_profile{};
    lpb::media::isobmff_video_profile output_profile{};
    if (!inspect_profile(input_path, input_profile) || !inspect_profile(output_path, output_profile) ||
        !profile_matches_source(input_profile, output_profile)) return false;

    lpb_video_item_facts facts{};
    facts.struct_size = sizeof(facts);
    if (lpb_probe_video(context, output_path.string().c_str(), &facts) != LPB_RESULT_OK ||
        facts.container != LPB_VIDEO_CONTAINER_MP4 ||
        facts.codec != expected_codec) return false;
    std::vector<uint8_t> source_bytes;
    std::vector<uint8_t> output_bytes;
    if (!load_bytes(input_path, source_bytes) || !load_bytes(output_path, output_bytes)) return false;
    bool source_has_mebx = false;
    bool output_has_mebx = false;
    lpb::media::isobmff_mebx_track_facts source_mebx{};
    lpb::media::isobmff_mebx_track_facts output_mebx{};
    if (!lpb::media::inspect_isobmff_mebx_track(source_bytes, source_has_mebx, source_mebx) ||
        !lpb::media::inspect_isobmff_mebx_track(output_bytes, output_has_mebx, output_mebx) ||
        !source_has_mebx || !output_has_mebx ||
        source_mebx.track_id != 3U || output_mebx.track_id != 3U ||
        source_mebx.sample_count != 86U || output_mebx.sample_count != 86U ||
        source_mebx.chunk_count != 4U || output_mebx.chunk_count != 4U ||
        source_mebx.payload_bytes != 2818048U || output_mebx.payload_bytes != 2818048U ||
        !lpb::media::validate_isobmff_mebx_transplant(source_bytes, output_bytes, output_mebx) ||
        std::strstr(diagnostics.fallback_reason,
            "Native MEBX preserved 86 samples/2818048 bytes; FFmpeg 9.0.1; no fallback.") == nullptr) return false;
    return no_owned_staging_residue(output_path.parent_path());
}

} // namespace

int main(int argc, char** argv)
{
    if (argc != 4 || (std::strcmp(argv[1], "critical-hevc") != 0 &&
            std::strcmp(argv[1], "critical-h264") != 0 &&
            std::strcmp(argv[1], "missing-sidecar") != 0 &&
            std::strcmp(argv[1], "cancel") != 0))
    {
        std::fprintf(stderr, "usage: video_sidecar_conversion_smoke <critical-hevc|critical-h264|missing-sidecar|cancel> <input> <new-output>\n");
        return 2;
    }

    const std::string mode = argv[1];
    const std::filesystem::path input_path = argv[2];
    const std::filesystem::path output_path = argv[3];
    if (!std::filesystem::is_regular_file(input_path) || std::filesystem::exists(output_path) ||
        !std::filesystem::is_directory(output_path.parent_path()) ||
        !no_owned_staging_residue(output_path.parent_path()))
    {
        std::fprintf(stderr, "Input, create-only output, or staging-directory precondition failed.\n");
        return 3;
    }

    lpb::media::isobmff_video_profile input_profile{};
    if (!inspect_profile(input_path, input_profile) ||
        input_profile.classification != lpb::media::video_profile_class::preservation_critical ||
        input_profile.bit_depth_luma <= 8U || input_profile.bit_depth_chroma <= 8U)
    {
        std::fprintf(stderr, "The canonical Huawei video is not a valid preservation-critical high-bit-depth source.\n");
        return 4;
    }

    cancel_state cancellation{};
    cancellation.cancel_after = mode == "cancel" ? 32U : 0U;
    lpb_context_options options{};
    options.struct_size = sizeof(options);
    options.abi_version = LPB_NATIVE_ABI_VERSION;
    options.cancel_callback = cancel_callback;
    options.user_data = &cancellation;
    lpb_context* context = nullptr;
    const lpb_result create_result = lpb_create_context(&options, &context);
    if (create_result != LPB_RESULT_OK || context == nullptr)
    {
        std::fprintf(stderr, "lpb_create_context failed: %d\n", static_cast<int>(create_result));
        return 5;
    }
    std::unique_ptr<lpb_context, decltype(&lpb_destroy_context)> context_owner(context, &lpb_destroy_context);

    lpb_video_backend_diagnostics diagnostics{};
    diagnostics.struct_size = sizeof(diagnostics);
    const lpb_video_codec codec = mode == "critical-h264" ? LPB_VIDEO_CODEC_H264 : LPB_VIDEO_CODEC_HEVC;
    const lpb_result result = lpb_transcode_video_v2(context, input_path.string().c_str(), output_path.string().c_str(),
        LPB_VIDEO_CONTAINER_MP4, codec, 23, &diagnostics);
    const std::string error = get_last_error(context);

    if (mode == "critical-hevc")
    {
        if (result != LPB_RESULT_OK || !validate_success(context, input_path, output_path, LPB_VIDEO_CODEC_HEVC, diagnostics))
        {
            std::fprintf(stderr, "Huawei HEVC preservation conversion failed: result=%d encoder=%s reason=%s error=%s\n",
                static_cast<int>(result), diagnostics.selected_encoder, diagnostics.fallback_reason, error.c_str());
            return 6;
        }
        std::printf("{\"route\":\"Huawei HEVC to HEVC\",\"result\":\"success\",\"encoder\":\"%s\",\"hardwareMode\":%d,\"fallback\":%d}\n",
            diagnostics.selected_encoder, static_cast<int>(diagnostics.hardware_mode), diagnostics.hardware_fallback_occurred);
        return 0;
    }

    if (mode == "critical-h264")
    {
        if (result == LPB_RESULT_OK)
        {
            if (!validate_success(context, input_path, output_path, LPB_VIDEO_CODEC_H264, diagnostics))
            {
                std::fprintf(stderr, "Huawei H.264 success did not preserve the source profile or backend truth.\n");
                return 7;
            }
            std::printf("{\"route\":\"Huawei HEVC to H.264\",\"result\":\"preserved\",\"encoder\":\"%s\"}\n",
                diagnostics.selected_encoder);
            return 0;
        }
        if (std::filesystem::exists(output_path) || !no_owned_staging_residue(output_path.parent_path()) ||
            diagnostics.hardware_fallback_occurred != 0 ||
            std::strstr(error.c_str(), "Unsupported:") == nullptr ||
            std::strstr(error.c_str(), "no Media Foundation retry") == nullptr)
        {
            std::fprintf(stderr, "Huawei H.264 negative route was not truthful/fail-closed: result=%d error=%s\n",
                static_cast<int>(result), error.c_str());
            return 8;
        }
        std::printf("{\"route\":\"Huawei HEVC to H.264\",\"result\":\"Unsupported\",\"outputExists\":false,\"mfRetry\":false,\"detail\":\"%s\"}\n",
            error.c_str());
        return 0;
    }

    if (mode == "missing-sidecar")
    {
        if (result == LPB_RESULT_OK || std::filesystem::exists(output_path) ||
            !no_owned_staging_residue(output_path.parent_path()) ||
            std::strstr(error.c_str(), "BackendUnavailable:") == nullptr ||
            std::strstr(error.c_str(), "no Media Foundation retry") == nullptr ||
            diagnostics.hardware_fallback_occurred != 0)
        {
            std::fprintf(stderr, "Missing-sidecar conversion did not fail closed: result=%d error=%s\n",
                static_cast<int>(result), error.c_str());
            return 9;
        }
        std::printf("{\"route\":\"Huawei HEVC with missing sidecar\",\"result\":\"BackendUnavailable\",\"outputExists\":false,\"mfRetry\":false}\n");
        return 0;
    }

    if (result != LPB_RESULT_CANCELLED || std::filesystem::exists(output_path) ||
        !no_owned_staging_residue(output_path.parent_path()) ||
        std::strstr(error.c_str(), "Video sidecar conversion was cancelled") == nullptr ||
        cancellation.calls < cancellation.cancel_after)
    {
        std::fprintf(stderr, "Cancelled sidecar conversion left output or reported an incorrect result: result=%d checks=%u error=%s\n",
            static_cast<int>(result), cancellation.calls, error.c_str());
        return 10;
    }
    std::printf("{\"route\":\"Huawei HEVC cancellation\",\"result\":\"cancelled\",\"outputExists\":false,\"cancelChecks\":%u}\n",
        cancellation.calls);
    return 0;
}
