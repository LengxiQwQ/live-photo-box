#include "livephotobox_native.h"
#include "media/video_sidecar_abi.h"

#define NOMINMAX
#include <windows.h>

#include <array>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>

namespace
{

bool verify_sidecar_abi_sibling(int expected_available)
{
    std::array<wchar_t, 32768> executable_path{};
    const DWORD path_length = GetModuleFileNameW(nullptr, executable_path.data(),
        static_cast<DWORD>(executable_path.size()));
    if (path_length == 0U || path_length >= executable_path.size())
    {
        std::fprintf(stderr, "GetModuleFileNameW failed for the smoke executable: %lu\n", GetLastError());
        return false;
    }

    const std::filesystem::path sidecar_path =
        std::filesystem::path(std::wstring(executable_path.data(), path_length)).parent_path() /
        L"LivePhotoBox.Video.Libav.dll";
    HMODULE module = LoadLibraryExW(sidecar_path.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (expected_available == 0)
    {
        if (module != nullptr)
        {
            FreeLibrary(module);
            std::fprintf(stderr, "Negative runtime package unexpectedly contains a loadable exact sidecar sibling.\n");
            return false;
        }
        std::printf("sidecarAbiSibling=missing; expectedAvailable=0; win32Error=%lu\n", GetLastError());
        return true;
    }
    if (module == nullptr)
    {
        std::fprintf(stderr, "Positive sidecar sibling could not be loaded: %lu\n", GetLastError());
        return false;
    }

    const FARPROC exported = GetProcAddress(module, "lpb_video_sidecar_query_abi_v1");
    lpb_video_sidecar_query_fn_v1 query = nullptr;
    static_assert(sizeof(exported) == sizeof(query));
    std::memcpy(&query, &exported, sizeof(query));
    if (query == nullptr)
    {
        FreeLibrary(module);
        std::fprintf(stderr, "Versioned sidecar ABI query export is absent.\n");
        return false;
    }

    lpb_video_sidecar_abi_info_v1 info{};
    info.struct_size = static_cast<uint32_t>(sizeof(info));
    const int32_t status = query(&info);
    std::printf("sidecarAbiQuery: status=%d structSize=%u abi=%u.%u capabilities=0x%08X maxDepth=%u version=%s\n",
        status, info.struct_size, info.abi_major, info.abi_minor, info.capability_flags,
        info.maximum_pixel_depth, info.library_version);
    const uint32_t required_capabilities = LPB_VIDEO_SIDECAR_CAP_TRANSCODE |
        LPB_VIDEO_SIDECAR_CAP_HEVC | LPB_VIDEO_SIDECAR_CAP_HEVC_10BIT;
    const bool valid = status == LPB_VIDEO_SIDECAR_OK &&
        info.struct_size == static_cast<uint32_t>(sizeof(info)) &&
        info.abi_major == LPB_VIDEO_SIDECAR_ABI_MAJOR &&
        info.abi_minor == LPB_VIDEO_SIDECAR_ABI_MINOR &&
        std::strcmp(info.library_version, "9.0.1") == 0 &&
        (info.capability_flags & required_capabilities) == required_capabilities;
    FreeLibrary(module);
    if (!valid)
    {
        std::fprintf(stderr, "Direct sidecar ABI/version/HDR capability validation failed.\n");
    }
    return valid;
}

} // namespace

int main(int argc, char** argv)
{
    if (argc != 2 || (std::strcmp(argv[1], "0") != 0 && std::strcmp(argv[1], "1") != 0))
    {
        std::fprintf(stderr, "usage: video_sidecar_runtime_smoke <expected-available:0|1>\n");
        return 2;
    }

    const int32_t expected_available = std::strcmp(argv[1], "1") == 0 ? 1 : 0;
    if (!verify_sidecar_abi_sibling(expected_available))
    {
        return 8;
    }

    lpb_context_options options{};
    options.struct_size = static_cast<uint32_t>(sizeof(options));
    options.abi_version = LPB_NATIVE_ABI_VERSION;
    lpb_context* context = nullptr;
    const lpb_result create_result = lpb_create_context(&options, &context);
    if (create_result != LPB_RESULT_OK || context == nullptr)
    {
        std::fprintf(stderr, "lpb_create_context failed: %d\n", static_cast<int>(create_result));
        return 3;
    }

    lpb_runtime_capability_identity identity{};
    identity.struct_size = static_cast<uint32_t>(sizeof(identity));
    const lpb_result query_result = lpb_get_runtime_capability_identity(
        context,
        LPB_RUNTIME_OPERATION_VIDEO_TRANSCODE_HDR_10BIT,
        &identity);
    lpb_destroy_context(context);
    if (query_result != LPB_RESULT_OK)
    {
        std::fprintf(stderr, "lpb_get_runtime_capability_identity failed: %d\n", static_cast<int>(query_result));
        return 4;
    }

    const bool correct_owner = identity.backend == LPB_RUNTIME_BACKEND_MINIMAL_LIBAV &&
        identity.codec == LPB_RUNTIME_CODEC_UNKNOWN &&
        identity.hardware_mode == LPB_VIDEO_HARDWARE_SOFTWARE_FORCED &&
        identity.fallback_occurred == 0;
    if (!correct_owner || identity.is_available != expected_available)
    {
        std::fprintf(stderr, "runtime identity mismatch: available=%d backend=%d codec=%d hardware=%d fallback=%d version=%s reason=%s\n",
            identity.is_available,
            static_cast<int>(identity.backend),
            static_cast<int>(identity.codec),
            identity.hardware_mode,
            identity.fallback_occurred,
            identity.backend_version,
            identity.fallback_reason);
        return 5;
    }

    if (expected_available != 0)
    {
        if (std::strcmp(identity.backend_version, "FFmpeg 9.0.1 (minimal static sidecar)") != 0 ||
            std::strcmp(identity.fallback_reason, "The exact versioned video sidecar was loaded and validated.") != 0)
        {
            std::fprintf(stderr, "validated sidecar version/reason mismatch: %s | %s\n",
                identity.backend_version,
                identity.fallback_reason);
            return 6;
        }
    }
    else if (std::strcmp(identity.backend_version, "not packaged") != 0 ||
        std::strstr(identity.fallback_reason, "exact LivePhotoBox.Video.Libav.dll sibling") == nullptr)
    {
        std::fprintf(stderr, "missing-sidecar result was not truthful: %s | %s\n",
            identity.backend_version,
            identity.fallback_reason);
        return 7;
    }

    std::printf(
        "{\"expectedAvailable\":%d,\"isAvailable\":%d,\"backend\":%d,\"codec\":%d,\"hardwareMode\":%d,\"fallbackOccurred\":%d,\"backendVersion\":\"%s\",\"fallbackReason\":\"%s\"}\n",
        expected_available,
        identity.is_available,
        static_cast<int>(identity.backend),
        static_cast<int>(identity.codec),
        identity.hardware_mode,
        identity.fallback_occurred,
        identity.backend_version,
        identity.fallback_reason);
    return 0;
}
