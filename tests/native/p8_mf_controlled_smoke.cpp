#include "livephotobox_native.h"
#include "media/video_converter.h"
#include <array>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <memory>
#include <string>
#include <vector>

namespace fs = std::filesystem;
using lpb::media::mf_deployment_state;

static std::string utf8(const fs::path& path) {
    const auto text = path.u8string();
    return {reinterpret_cast<const char*>(text.data()), text.size()};
}
static std::vector<char> bytes(const fs::path& path) {
    std::ifstream stream(path, std::ios::binary);
    return {std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>()};
}
static std::string last_error(lpb_context* context) {
    size_t needed = 0U;
    (void)lpb_get_last_error(context, nullptr, 0U, &needed);
    std::string result(needed, '\0');
    if (needed != 0U) (void)lpb_get_last_error(context, result.data(), result.size(), &needed);
    return result;
}

int wmain(int argc, wchar_t** argv) {
    const bool unavailable_mode = argc == 4 && std::wstring(argv[3]) == L"--expect-sidecar-unavailable";
    const bool mov_only = argc == 4 && std::wstring(argv[3]) == L"--mov-only";
    if (argc != 3 && !unavailable_mode && !mov_only) {
        std::fprintf(stderr, "Usage: SyntheticMFSmoke input-vivo-AVC output-new-directory [--expect-sidecar-unavailable|--mov-only]\n"); return 2;
    }
    const fs::path input(argv[1]), root(argv[2]);
    if (!fs::is_regular_file(input) || !fs::create_directory(root)) return 3;
    const auto original = bytes(input);
    if (original.empty()) return 4;
    lpb_context* raw = nullptr;
    if (lpb_create_context(nullptr, &raw) != LPB_RESULT_OK || raw == nullptr) return 5;
    std::unique_ptr<lpb_context, decltype(&lpb_destroy_context)> context(raw, &lpb_destroy_context);
    const std::string input_utf8 = utf8(input);
    if (unavailable_mode) {
        lpb::media::set_synthetic_mf_probe(mf_deployment_state::component_missing);
        const fs::path destination = root / L"unavailable.mp4";
        lpb_video_backend_diagnostics diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
        lpb_video_preservation_request_v1 request{sizeof(request), 1U, 0U, 0U};
        lpb_video_preservation_result_v1 preservation{}; preservation.struct_size = sizeof(preservation);
        const auto result = lpb_transcode_video_v3(context.get(), input_utf8.c_str(), utf8(destination).c_str(),
            LPB_VIDEO_CONTAINER_MP4, LPB_VIDEO_CODEC_H264, 23, &request, &diagnostic, &preservation);
        const std::string error = last_error(context.get());
        const bool valid = result != LPB_RESULT_OK && diagnostic.backend == LPB_VIDEO_BACKEND_MINIMAL_LIBAV &&
            diagnostic.hardware_fallback_occurred != 0 && preservation.status == 0U &&
            std::strncmp(diagnostic.selected_encoder, "s1s|?|?|", sizeof("s1s|?|?|") - 1U) == 0 &&
            std::strstr(diagnostic.selected_encoder, "libx264") == nullptr &&
            std::strstr(diagnostic.fallback_reason, "MFComponentMissing") != nullptr &&
            error.starts_with("BackendUnavailable:") && !fs::exists(destination) &&
            fs::is_empty(root) && bytes(input) == original;
        lpb::media::clear_synthetic_mf_probe();
        std::printf("SyntheticMFRealSidecarFilesystemUnavailable passed=%u expected=1 result=%d fallback=%d encoder=%s reason=%s\n",
            valid ? 1U : 0U, static_cast<int>(result), diagnostic.hardware_fallback_occurred,
            diagnostic.selected_encoder, diagnostic.fallback_reason);
        std::fprintf(stderr, "Native diagnostic: %s\n", error.c_str());
        return valid ? 0 : 11;
    }
    // Raw Win32 classifier coverage: FILE/PATH_NOT_FOUND are eligible;
    // ACCESS_DENIED, MOD_NOT_FOUND, BAD_EXE_FORMAT, PROC_NOT_FOUND are not.
    constexpr uint32_t open_errors[]{2U, 3U, 5U, 126U, 193U, 127U};
    for (size_t index = 0U; !mov_only && index < std::size(open_errors); ++index) {
        const auto actual = lpb::media::classify_mf_component_open_error(open_errors[index]);
        const auto expected = index < 2U ? mf_deployment_state::component_missing : mf_deployment_state::probe_failure;
        if (actual != expected) return 6;
    }
    if (!mov_only) std::printf("SyntheticRawWin32Classifier passed=6 expected=6; covers exact-file-open mapping only\n");
    int passed = 0;
    const auto convert = [&](const char* name, mf_deployment_state state, lpb_video_container container,
        lpb_video_codec codec, int crf, bool expect_success, bool expected_fallback, bool conflict) {
        lpb::media::set_synthetic_mf_probe(state);
        const fs::path destination = root / name;
        if (conflict) { std::ofstream file(destination, std::ios::binary); file << "FOREIGN-P8"; }
        const auto foreign = conflict ? bytes(destination) : std::vector<char>{};
        lpb_video_backend_diagnostics diagnostic{}; diagnostic.struct_size = sizeof(diagnostic);
        lpb_video_preservation_request_v1 request{sizeof(request), 1U, 0U, 0U};
        lpb_video_preservation_result_v1 preservation{}; preservation.struct_size = sizeof(preservation);
        const auto result = lpb_transcode_video_v3(context.get(), input_utf8.c_str(), utf8(destination).c_str(),
            container, codec, crf, &request, &diagnostic, &preservation);
        bool valid = (result == LPB_RESULT_OK) == expect_success &&
            (diagnostic.hardware_fallback_occurred != 0) == expected_fallback;
        if (expect_success) {
            valid = valid && fs::is_regular_file(destination) && preservation.status == 1U;
            if (expected_fallback) valid = valid && diagnostic.backend == LPB_VIDEO_BACKEND_MINIMAL_LIBAV &&
                std::strncmp(diagnostic.selected_encoder, "s1v|9.0.1|libx264|", sizeof("s1v|9.0.1|libx264|") - 1U) == 0 &&
                std::strstr(diagnostic.fallback_reason, "controlled x264 route") != nullptr;
            else valid = valid && diagnostic.backend == LPB_VIDEO_BACKEND_PROJECT_ISOBMFF_REMUX;
        } else valid = valid && preservation.status == 0U &&
            (conflict ? bytes(destination) == foreign : !fs::exists(destination));
        if (bytes(input) != original) valid = false;
        std::printf("Synthetic case=%s pass=%u result=%d fallback=%d encoder=%s reason=%s\n", name,
            valid ? 1U : 0U, static_cast<int>(result), diagnostic.hardware_fallback_occurred,
            diagnostic.selected_encoder, diagnostic.fallback_reason);
        if (!valid) std::fprintf(stderr, "Native error: %s\n", last_error(context.get()).c_str());
        if (valid) ++passed;
        return valid;
    };
    if (mov_only) {
        bool valid = convert("absent.mov", mf_deployment_state::encoder_absent,
            LPB_VIDEO_CONTAINER_MOV, LPB_VIDEO_CODEC_H264, 23, true, true, false);
        lpb::media::clear_synthetic_mf_probe();
        for (const auto& entry : fs::directory_iterator(root))
            if (entry.path().filename().wstring().starts_with(L"lpb-")) valid = false;
        std::printf("SyntheticMFMovOnly passed=%d expected=1 realMissingMFEnvironment=NOT_RUN_DEFERRED_BY_CURRENT_USER\n", passed);
        return valid && passed == 1 ? 0 : 12;
    }
    bool valid = true;
    valid &= convert("missing.mp4", mf_deployment_state::component_missing, LPB_VIDEO_CONTAINER_MP4, LPB_VIDEO_CODEC_H264, 23, true, true, false);
    valid &= convert("absent.mov", mf_deployment_state::encoder_absent, LPB_VIDEO_CONTAINER_MOV, LPB_VIDEO_CODEC_H264, 23, true, true, false);
    valid &= convert("probe-failure.mp4", mf_deployment_state::probe_failure, LPB_VIDEO_CONTAINER_MP4, LPB_VIDEO_CODEC_H264, 23, false, false, false);
    valid &= convert("copy.mp4", mf_deployment_state::component_missing, LPB_VIDEO_CONTAINER_MP4, LPB_VIDEO_CODEC_COPY, 23, true, false, false);
    valid &= convert("invalid-crf.mp4", mf_deployment_state::component_missing, LPB_VIDEO_CONTAINER_MP4, LPB_VIDEO_CODEC_H264, 52, false, true, false);
    valid &= convert("foreign.mp4", mf_deployment_state::component_missing, LPB_VIDEO_CONTAINER_MP4, LPB_VIDEO_CODEC_H264, 23, false, true, true);
    // Observational capability query may advertise a route but never claims conversion.
    lpb::media::set_synthetic_mf_probe(mf_deployment_state::component_missing);
    lpb_runtime_capability_identity capability{}; capability.struct_size = sizeof(capability);
    const bool query_valid = lpb_get_runtime_capability_identity(context.get(), LPB_RUNTIME_OPERATION_VIDEO_TRANSCODE_SDR_H264,
        &capability) == LPB_RESULT_OK && capability.is_available != 0U && capability.fallback_occurred == 0U &&
        std::strstr(capability.fallback_reason, "MFComponentMissing") != nullptr;
    if (query_valid) ++passed;
    valid &= query_valid;
    // Synthetic probe-failure facts must remain unavailable, not route through x264.
    lpb::media::set_synthetic_mf_probe(mf_deployment_state::probe_failure);
    capability = {}; capability.struct_size = sizeof(capability);
    const bool failure_query_valid = lpb_get_runtime_capability_identity(context.get(), LPB_RUNTIME_OPERATION_VIDEO_TRANSCODE_SDR_H264,
        &capability) == LPB_RESULT_OK && capability.is_available == 0U && capability.fallback_occurred == 0U &&
        std::strstr(capability.fallback_reason, "MFProbeFailure") != nullptr;
    if (failure_query_valid) ++passed;
    valid &= failure_query_valid;
    lpb::media::clear_synthetic_mf_probe();
    for (const auto& entry : fs::directory_iterator(root)) {
        if (entry.path().filename().wstring().starts_with(L"lpb-")) valid = false;
    }
    std::printf("SyntheticMFSmoke passed=%d expected=8 realMissingMFEnvironment=NOT_RUN_DEFERRED_BY_CURRENT_USER\n", passed);
    return valid && passed == 8 ? 0 : 10;
}
