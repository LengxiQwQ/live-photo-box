#include "media/video_sidecar_loader.h"

#include "foundation/sha256.h"
#if defined(LPB_VIDEO_SIDECAR_BUILD_IDENTITY)
#include "video_sidecar_build_identity.h"
#endif

#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <limits>
#include <string>
#include <string_view>
#include <vector>

namespace lpb::media
{
namespace
{

int g_module_anchor = 0;
constexpr uint32_t kRequiredCapabilities =
    LPB_VIDEO_SIDECAR_CAP_TRANSCODE |
    LPB_VIDEO_SIDECAR_CAP_HEVC |
    LPB_VIDEO_SIDECAR_CAP_HEVC_10BIT;
constexpr char kExpectedLibraryVersion[] = "9.0.1";
constexpr wchar_t kSidecarFilename[] = L"LivePhotoBox.Video.Libav.dll";

struct file_identity
{
    DWORD volume_serial{};
    DWORD file_index_high{};
    DWORD file_index_low{};
    DWORD file_size_high{};
    DWORD file_size_low{};
    DWORD number_of_links{};
    DWORD attributes{};
    FILETIME last_write_time{};
};

struct pe_section
{
    uint32_t virtual_address{};
    uint32_t virtual_size{};
    uint32_t raw_size{};
    uint32_t raw_offset{};
};

template<typename TFunction>
bool resolve_export(HMODULE module, const char* name, TFunction& function) noexcept
{
    const FARPROC exported = GetProcAddress(module, name);
    if (exported == nullptr)
    {
        return false;
    }
    static_assert(sizeof(function) == sizeof(exported));
    std::memcpy(&function, &exported, sizeof(function));
    return true;
}

bool get_module_path(HMODULE module, std::filesystem::path& path) noexcept
{
    std::array<wchar_t, 32768> buffer{};
    const DWORD length = GetModuleFileNameW(module, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0U || length >= buffer.size()) return false;
    try
    {
        path.assign(std::wstring(buffer.data(), length));
        return path.is_absolute();
    }
    catch (...)
    {
        return false;
    }
}

bool get_sibling_path(std::filesystem::path& path) noexcept
{
    HMODULE containing_module = nullptr;
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&g_module_anchor),
            &containing_module))
    {
        return false;
    }

    std::filesystem::path module_path;
    try
    {
        if (!get_module_path(containing_module, module_path)) return false;
        path = module_path.parent_path() / kSidecarFilename;
        return path.is_absolute();
    }
    catch (...)
    {
        return false;
    }
}

bool get_final_path(HANDLE file, std::wstring& path) noexcept
{
    try
    {
        std::vector<wchar_t> buffer(512);
        for (;;)
        {
            const DWORD length = GetFinalPathNameByHandleW(
                file, buffer.data(), static_cast<DWORD>(buffer.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            if (length == 0U) return false;
            if (length < buffer.size())
            {
                path.assign(buffer.data(), length);
                return true;
            }
            if (buffer.size() >= 32768U) return false;
            buffer.resize(buffer.size() * 2U);
        }
    }
    catch (...)
    {
        path.clear();
        return false;
    }
}

std::wstring normalize_path(std::wstring path)
{
    if (path.compare(0, 8, L"\\\\?\\UNC\\") == 0)
    {
        path = L"\\\\" + path.substr(8);
    }
    else if (path.compare(0, 4, L"\\\\?\\") == 0)
    {
        path.erase(0, 4);
    }
    while (path.size() > 3U && (path.back() == L'\\' || path.back() == L'/')) path.pop_back();
    return path;
}

bool capture_identity(HANDLE file, file_identity& identity) noexcept
{
    BY_HANDLE_FILE_INFORMATION info{};
    if (file == nullptr || file == INVALID_HANDLE_VALUE || !GetFileInformationByHandle(file, &info)) return false;
    identity.volume_serial = info.dwVolumeSerialNumber;
    identity.file_index_high = info.nFileIndexHigh;
    identity.file_index_low = info.nFileIndexLow;
    identity.file_size_high = info.nFileSizeHigh;
    identity.file_size_low = info.nFileSizeLow;
    identity.number_of_links = info.nNumberOfLinks;
    identity.attributes = info.dwFileAttributes;
    identity.last_write_time = info.ftLastWriteTime;
    return true;
}

bool same_identity(const file_identity& left, const file_identity& right) noexcept
{
    return left.volume_serial == right.volume_serial &&
        left.file_index_high == right.file_index_high &&
        left.file_index_low == right.file_index_low &&
        left.file_size_high == right.file_size_high &&
        left.file_size_low == right.file_size_low &&
        left.number_of_links == right.number_of_links &&
        left.last_write_time.dwHighDateTime == right.last_write_time.dwHighDateTime &&
        left.last_write_time.dwLowDateTime == right.last_write_time.dwLowDateTime &&
        (left.attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ==
            (right.attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT));
}

bool checked_range(uint64_t offset, uint64_t length, uint64_t file_size) noexcept
{
    return offset <= file_size && length <= file_size - offset;
}

bool read_at(HANDLE file, uint64_t offset, void* destination, size_t length) noexcept
{
    if (file == nullptr || file == INVALID_HANDLE_VALUE || destination == nullptr ||
        offset > static_cast<uint64_t>((std::numeric_limits<LONGLONG>::max)()) ||
        length > static_cast<size_t>((std::numeric_limits<LONGLONG>::max)() - static_cast<LONGLONG>(offset))) return false;
    LARGE_INTEGER seek{};
    seek.QuadPart = static_cast<LONGLONG>(offset);
    if (!SetFilePointerEx(file, seek, nullptr, FILE_BEGIN)) return false;
    auto* output = static_cast<uint8_t*>(destination);
    while (length > 0U)
    {
        const DWORD count = static_cast<DWORD>(std::min<size_t>(length, 64U * 1024U));
        DWORD read = 0;
        if (!ReadFile(file, output, count, &read, nullptr) || read == 0U || read > count) return false;
        output += read;
        length -= read;
    }
    return true;
}

bool read_u16(HANDLE file, uint64_t offset, uint64_t file_size, uint16_t& value) noexcept
{
    std::array<uint8_t, 2> bytes{};
    if (!checked_range(offset, bytes.size(), file_size) || !read_at(file, offset, bytes.data(), bytes.size())) return false;
    value = static_cast<uint16_t>(bytes[0]) | static_cast<uint16_t>(static_cast<uint16_t>(bytes[1]) << 8U);
    return true;
}

bool read_u32(HANDLE file, uint64_t offset, uint64_t file_size, uint32_t& value) noexcept
{
    std::array<uint8_t, 4> bytes{};
    if (!checked_range(offset, bytes.size(), file_size) || !read_at(file, offset, bytes.data(), bytes.size())) return false;
    value = static_cast<uint32_t>(bytes[0]) |
        (static_cast<uint32_t>(bytes[1]) << 8U) |
        (static_cast<uint32_t>(bytes[2]) << 16U) |
        (static_cast<uint32_t>(bytes[3]) << 24U);
    return true;
}

bool rva_to_offset(uint32_t rva, uint32_t length, uint32_t size_of_headers,
    const std::vector<pe_section>& sections, uint64_t file_size, uint64_t& offset) noexcept
{
    if (rva < size_of_headers && length <= size_of_headers - rva && checked_range(rva, length, file_size))
    {
        offset = rva;
        return true;
    }
    for (const pe_section& section : sections)
    {
        if (rva < section.virtual_address) continue;
        const uint64_t delta = static_cast<uint64_t>(rva) - section.virtual_address;
        if (delta > section.raw_size || length > static_cast<uint64_t>(section.raw_size) - delta) continue;
        const uint64_t candidate = static_cast<uint64_t>(section.raw_offset) + delta;
        if (!checked_range(candidate, length, file_size)) return false;
        offset = candidate;
        return true;
    }
    return false;
}

bool read_import_name(HANDLE file, uint32_t name_rva, uint32_t size_of_headers,
    const std::vector<pe_section>& sections, uint64_t file_size, std::string& name) noexcept
{
    try
    {
        name.clear();
        for (uint32_t index = 0; index < 260U; ++index)
        {
            const uint64_t current_rva = static_cast<uint64_t>(name_rva) + index;
            if (current_rva > UINT32_MAX) return false;
            uint64_t offset = 0;
            if (!rva_to_offset(static_cast<uint32_t>(current_rva), 1U, size_of_headers, sections, file_size, offset)) return false;
            uint8_t character = 0;
            if (!read_at(file, offset, &character, 1U)) return false;
            if (character == 0U)
            {
                return name.size() > 4U && name.ends_with(".dll");
            }
            if (character < 0x21U || character > 0x7EU || character == '/' || character == '\\') return false;
            char normalized = static_cast<char>(character);
            if (normalized >= 'A' && normalized <= 'Z') normalized = static_cast<char>(normalized - 'A' + 'a');
            name.push_back(normalized);
        }
    }
    catch (...)
    {
        name.clear();
    }
    return false;
}

bool inspect_pe_imports(HANDLE file, std::vector<std::string>& imports) noexcept
{
    imports.clear();
    LARGE_INTEGER size_value{};
    if (file == nullptr || file == INVALID_HANDLE_VALUE || !GetFileSizeEx(file, &size_value) ||
        size_value.QuadPart < 0) return false;
    const uint64_t file_size = static_cast<uint64_t>(size_value.QuadPart);

    std::array<uint8_t, 2> dos_signature{};
    if (!read_at(file, 0U, dos_signature.data(), dos_signature.size()) ||
        dos_signature[0] != 'M' || dos_signature[1] != 'Z') return false;
    uint32_t pe_offset = 0;
    if (!read_u32(file, 0x3CU, file_size, pe_offset) || pe_offset < 0x40U) return false;
    std::array<uint8_t, 4> signature{};
    if (!checked_range(pe_offset, signature.size(), file_size) ||
        !read_at(file, pe_offset, signature.data(), signature.size()) ||
        signature != std::array<uint8_t, 4>{'P', 'E', 0U, 0U}) return false;

    const uint64_t coff_offset = static_cast<uint64_t>(pe_offset) + 4U;
    uint16_t machine = 0;
    uint16_t section_count = 0;
    uint16_t optional_size = 0;
    if (!read_u16(file, coff_offset, file_size, machine) || !read_u16(file, coff_offset + 2U, file_size, section_count) ||
        !read_u16(file, coff_offset + 16U, file_size, optional_size) || machine != IMAGE_FILE_MACHINE_AMD64 ||
        section_count == 0U || section_count > 96U) return false;

    const uint64_t optional_offset = coff_offset + 20U;
    uint16_t optional_magic = 0;
    uint32_t number_of_directories = 0;
    uint32_t size_of_headers = 0;
    if (optional_size < 224U || !read_u16(file, optional_offset, file_size, optional_magic) || optional_magic != 0x20BU ||
        !read_u32(file, optional_offset + 108U, file_size, number_of_directories) || number_of_directories < 14U ||
        !read_u32(file, optional_offset + 60U, file_size, size_of_headers) || size_of_headers == 0U) return false;

    uint32_t import_rva = 0;
    uint32_t import_size = 0;
    uint32_t delay_import_rva = 0;
    uint32_t delay_import_size = 0;
    const uint64_t directory_offset = optional_offset + 112U;
    if (!read_u32(file, directory_offset + 8U, file_size, import_rva) ||
        !read_u32(file, directory_offset + 12U, file_size, import_size) ||
        !read_u32(file, directory_offset + 13U * 8U, file_size, delay_import_rva) ||
        !read_u32(file, directory_offset + 13U * 8U + 4U, file_size, delay_import_size) ||
        import_rva == 0U || import_size < 40U || import_size % 20U != 0U ||
        delay_import_rva != 0U || delay_import_size != 0U) return false;

    std::vector<pe_section> sections;
    try
    {
        sections.reserve(section_count);
        const uint64_t section_table = optional_offset + optional_size;
        if (!checked_range(section_table, static_cast<uint64_t>(section_count) * 40U, file_size)) return false;
        for (uint16_t index = 0; index < section_count; ++index)
        {
            const uint64_t offset = section_table + static_cast<uint64_t>(index) * 40U;
            pe_section section{};
            if (!read_u32(file, offset + 8U, file_size, section.virtual_size) ||
                !read_u32(file, offset + 12U, file_size, section.virtual_address) ||
                !read_u32(file, offset + 16U, file_size, section.raw_size) ||
                !read_u32(file, offset + 20U, file_size, section.raw_offset) ||
                (section.raw_size > 0U && !checked_range(section.raw_offset, section.raw_size, file_size))) return false;
            sections.push_back(section);
        }

        bool found_terminator = false;
        const uint32_t descriptor_count = import_size / 20U;
        if (descriptor_count > 1024U) return false;
        for (uint32_t index = 0; index < descriptor_count; ++index)
        {
            const uint64_t descriptor_rva = static_cast<uint64_t>(import_rva) + static_cast<uint64_t>(index) * 20U;
            if (descriptor_rva > UINT32_MAX) return false;
            uint64_t descriptor_offset = 0;
            if (!rva_to_offset(static_cast<uint32_t>(descriptor_rva), 20U, size_of_headers, sections, file_size, descriptor_offset)) return false;
            std::array<uint8_t, 20> descriptor{};
            if (!read_at(file, descriptor_offset, descriptor.data(), descriptor.size())) return false;
            if (std::all_of(descriptor.begin(), descriptor.end(), [](uint8_t byte) { return byte == 0U; }))
            {
                found_terminator = true;
                break;
            }
            uint32_t name_rva = static_cast<uint32_t>(descriptor[12]) |
                (static_cast<uint32_t>(descriptor[13]) << 8U) |
                (static_cast<uint32_t>(descriptor[14]) << 16U) |
                (static_cast<uint32_t>(descriptor[15]) << 24U);
            std::string name;
            if (name_rva == 0U || !read_import_name(file, name_rva, size_of_headers, sections, file_size, name) ||
                std::find(imports.begin(), imports.end(), name) != imports.end()) return false;
            imports.push_back(std::move(name));
        }
        if (!found_terminator || imports.empty()) return false;
        std::sort(imports.begin(), imports.end());
        return true;
    }
    catch (...)
    {
        imports.clear();
        return false;
    }
}

bool imports_match_build(HANDLE file) noexcept
{
#if defined(LPB_VIDEO_SIDECAR_BUILD_IDENTITY)
    std::vector<std::string> actual_imports;
    if (!inspect_pe_imports(file, actual_imports) || actual_imports.size() != build_identity::sidecar_imports.size()) return false;
    for (size_t index = 0; index < actual_imports.size(); ++index)
    {
        if (actual_imports[index] != build_identity::sidecar_imports[index]) return false;
        const std::string_view name = build_identity::sidecar_imports[index];
        if (name != "advapi32.dll" && name != "kernel32.dll" && name != "ncrypt.dll") return false;
        if (name.starts_with("avcodec") || name.starts_with("avformat") || name.starts_with("avutil") ||
            name.starts_with("swscale") || name.starts_with("x264") || name.starts_with("x265")) return false;
    }
    return true;
#else
    static_cast<void>(file);
    return false;
#endif
}

bool file_hash_matches_build(HANDLE file) noexcept
{
#if defined(LPB_VIDEO_SIDECAR_BUILD_IDENTITY)
    uint8_t hash[32]{};
    if (!lpb::crypto::sha256_file(file, hash)) return false;
    constexpr char hex[] = "0123456789abcdef";
    std::array<char, 65> actual{};
    for (size_t index = 0; index < sizeof(hash); ++index)
    {
        actual[index * 2U] = hex[hash[index] >> 4U];
        actual[index * 2U + 1U] = hex[hash[index] & 0x0FU];
    }
    return std::strcmp(actual.data(), build_identity::sidecar_sha256) == 0;
#else
    static_cast<void>(file);
    return false;
#endif
}

bool exact_module_object(HMODULE loaded, HANDLE pinned, const file_identity& pinned_identity,
    const std::wstring& pinned_final_path) noexcept
{
    std::filesystem::path loaded_path;
    if (!get_module_path(loaded, loaded_path)) return false;
    HANDLE loaded_file = CreateFileW(loaded_path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (loaded_file == INVALID_HANDLE_VALUE) return false;
    file_identity loaded_identity{};
    std::wstring loaded_final_path;
    const bool matched = capture_identity(loaded_file, loaded_identity) && same_identity(pinned_identity, loaded_identity) &&
        get_final_path(loaded_file, loaded_final_path) &&
        _wcsicmp(normalize_path(loaded_final_path).c_str(), normalize_path(pinned_final_path).c_str()) == 0;
    CloseHandle(loaded_file);
    static_cast<void>(pinned);
    return matched;
}

} // namespace

VideoSidecarModule::~VideoSidecarModule() noexcept
{
    if (module_ != nullptr)
    {
        FreeLibrary(static_cast<HMODULE>(module_));
        module_ = nullptr;
    }
    if (pinned_file_ != nullptr && pinned_file_ != INVALID_HANDLE_VALUE)
    {
        CloseHandle(static_cast<HANDLE>(pinned_file_));
        pinned_file_ = nullptr;
    }
}

bool VideoSidecarModule::load() noexcept
{
    if (module_ != nullptr)
    {
        return info_.abi_major == LPB_VIDEO_SIDECAR_ABI_MAJOR &&
            info_.abi_minor == LPB_VIDEO_SIDECAR_ABI_MINOR && transcode_ != nullptr && pinned_file_ != nullptr;
    }

#if !defined(LPB_VIDEO_SIDECAR_BUILD_IDENTITY)
    failure_reason_ = "The Native build has no build-bound video sidecar identity.";
    return false;
#else
    std::filesystem::path sibling_path;
    if (!get_sibling_path(sibling_path))
    {
        failure_reason_ = "The Native module path could not be resolved for exact-sibling loading.";
        return false;
    }

    HANDLE pinned = CreateFileW(sibling_path.c_str(), GENERIC_READ | FILE_READ_ATTRIBUTES, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (pinned == INVALID_HANDLE_VALUE)
    {
        failure_reason_ = "The exact LivePhotoBox.Video.Libav.dll sibling is missing or could not be pinned for read-only validation.";
        return false;
    }

    file_identity pinned_identity{};
    LARGE_INTEGER pinned_size{};
    std::wstring pinned_final_path;
    if (!capture_identity(pinned, pinned_identity) ||
        (pinned_identity.attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != 0U ||
        pinned_identity.number_of_links != 1U ||
        !GetFileSizeEx(pinned, &pinned_size) || pinned_size.QuadPart <= 0 ||
        !get_final_path(pinned, pinned_final_path))
    {
        CloseHandle(pinned);
        failure_reason_ = "The exact video sidecar sibling is not a single regular file with a stable identity.";
        return false;
    }

    HMODULE containing_module = nullptr;
    std::filesystem::path native_path;
    HANDLE native_file = INVALID_HANDLE_VALUE;
    std::wstring native_final_path;
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&g_module_anchor), &containing_module) ||
        !get_module_path(containing_module, native_path))
    {
        CloseHandle(pinned);
        failure_reason_ = "The Native module identity could not be verified for exact-sibling loading.";
        return false;
    }
    native_file = CreateFileW(native_path.c_str(), FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    const bool same_directory = native_file != INVALID_HANDLE_VALUE &&
        get_final_path(native_file, native_final_path) &&
        _wcsicmp(normalize_path(std::filesystem::path(native_final_path).parent_path().wstring()).c_str(),
            normalize_path(std::filesystem::path(pinned_final_path).parent_path().wstring()).c_str()) == 0 &&
        _wcsicmp(std::filesystem::path(pinned_final_path).filename().c_str(), kSidecarFilename) == 0;
    if (native_file != INVALID_HANDLE_VALUE) CloseHandle(native_file);
    if (!same_directory || !file_hash_matches_build(pinned) || !imports_match_build(pinned))
    {
        CloseHandle(pinned);
        failure_reason_ = "The exact video sidecar sibling failed build-bound SHA-256, import-set, or same-directory validation.";
        return false;
    }

    file_identity after_inspection{};
    if (!capture_identity(pinned, after_inspection) || !same_identity(pinned_identity, after_inspection))
    {
        CloseHandle(pinned);
        failure_reason_ = "The exact video sidecar sibling changed during hash/import validation.";
        return false;
    }

    HMODULE loaded = LoadLibraryExW(
        sibling_path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (loaded == nullptr || !exact_module_object(loaded, pinned, pinned_identity, pinned_final_path))
    {
        if (loaded != nullptr) FreeLibrary(loaded);
        CloseHandle(pinned);
        failure_reason_ = "The loaded video sidecar module did not resolve to the exact build-bound sibling file object.";
        return false;
    }

    lpb_video_sidecar_query_fn_v1 query = nullptr;
    lpb_video_sidecar_transcode_fn_v1 transcode = nullptr;
    if (!resolve_export(loaded, "lpb_video_sidecar_query_abi_v1", query) ||
        !resolve_export(loaded, "lpb_video_sidecar_transcode_v1", transcode))
    {
        FreeLibrary(loaded);
        CloseHandle(pinned);
        failure_reason_ = "The exact video sidecar is missing a required versioned ABI export.";
        return false;
    }

    lpb_video_sidecar_abi_info_v1 info{};
    info.struct_size = sizeof(info);
    const int32_t query_result = query(&info);
    if (query_result != LPB_VIDEO_SIDECAR_OK ||
        info.struct_size != static_cast<uint32_t>(sizeof(info)) ||
        info.abi_major != LPB_VIDEO_SIDECAR_ABI_MAJOR ||
        info.abi_minor != LPB_VIDEO_SIDECAR_ABI_MINOR ||
        std::strncmp(info.library_version, kExpectedLibraryVersion, sizeof(info.library_version)) != 0 ||
        (info.capability_flags & kRequiredCapabilities) != kRequiredCapabilities)
    {
        FreeLibrary(loaded);
        CloseHandle(pinned);
        failure_reason_ = "The exact video sidecar failed ABI, FFmpeg version, or HDR capability validation.";
        return false;
    }

    file_identity after_load{};
    if (!capture_identity(pinned, after_load) || !same_identity(pinned_identity, after_load))
    {
        FreeLibrary(loaded);
        CloseHandle(pinned);
        failure_reason_ = "The exact video sidecar sibling changed while loading its Native ABI.";
        return false;
    }

    module_ = loaded;
    pinned_file_ = pinned;
    query_ = query;
    transcode_ = transcode;
    info_ = info;
    failure_reason_ = "The exact versioned video sidecar was loaded and validated.";
    return true;
#endif
}

const lpb_video_sidecar_abi_info_v1* VideoSidecarModule::info() const noexcept
{
    return module_ == nullptr ? nullptr : &info_;
}

lpb_video_sidecar_transcode_fn_v1 VideoSidecarModule::transcode() const noexcept
{
    return module_ == nullptr ? nullptr : transcode_;
}

const char* VideoSidecarModule::failure_reason() const noexcept
{
    return failure_reason_;
}

} // namespace lpb::media
