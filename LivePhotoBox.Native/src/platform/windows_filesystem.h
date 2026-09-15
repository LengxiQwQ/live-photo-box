#pragma once

#include <filesystem>
#include <span>
#include <cstdint>
#include "binary/portable_io.h"

enum class lpb_platform_error { other, disk_full, destination_exists, sharing_violation, access_denied };
struct lpb_platform_capabilities {
    bool random_access_source;
    bool owned_temp;
    bool atomic_no_replace;
    bool authorized_atomic_replace;
};
lpb_platform_error lpb_platform_classify_error(uint32_t native_code) noexcept;
lpb_platform_capabilities lpb_platform_query_capabilities() noexcept;

// Windows backend primitive. The owned handle is retained by the caller from
// CREATE_NEW through flush, publication and identity/post-publish verification.
// Never re-resolve a temporary pathname. Destination collision fails closed.
bool lpb_platform_publish_owned_no_replace(
    void* owned_handle, const std::filesystem::path& destination) noexcept;
bool lpb_platform_dispose_owned(void* owned_handle) noexcept;
bool lpb_platform_path_is_reparse_point(const std::filesystem::path& path) noexcept;
bool lpb_platform_pin_directory(const std::filesystem::path& directory,
    void*& owned_handle) noexcept;

// Path-based Windows codecs may open this staging name, but the backend keeps
// the CREATE_NEW handle and publishes only that exact object after verifying
// its file identity. An unowned replacement at the staging name is untouched.
class windows_owned_output {
public:
    windows_owned_output() = default;
    windows_owned_output(const windows_owned_output&) = delete;
    windows_owned_output& operator=(const windows_owned_output&) = delete;
    ~windows_owned_output() noexcept;

    bool create(const std::filesystem::path& destination,
        const wchar_t* prefix, const wchar_t* suffix = L".tmp") noexcept;
    const std::filesystem::path& path() const noexcept { return temp_path_; }
    bool write_all(std::span<const uint8_t> bytes) noexcept;
    bool copy_from_readonly(const std::filesystem::path& source) noexcept;
    bool flush() noexcept;
    bool ready_to_consume() noexcept;
    lpb::random_access_reader reader() noexcept;
    bool publish_no_replace(const std::filesystem::path& destination) noexcept;
    void abort() noexcept;

private:
    std::filesystem::path temp_path_;
    void* handle_{};
    void* directory_handle_{};
};
