#pragma once

#include <filesystem>
#include <string>
#include <span>
#include <vector>
#include <cstdint>
#include "binary/portable_io.h"

struct lpb_context;
struct lpb_file_identity;

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

// Handle-pinned read-only source lease. Rejects reparse points and directories
// on the source file and (when pin_ancestors=true) on every ancestor directory,
// denies concurrent writers/deleters via FILE_SHARE_READ, and provides bounded
// or random-access reads directly from the pinned Win32 handle.
class windows_readonly_source {
public:
    windows_readonly_source() = default;
    windows_readonly_source(const windows_readonly_source&) = delete;
    windows_readonly_source& operator=(const windows_readonly_source&) = delete;
    ~windows_readonly_source() noexcept;

    bool open(const std::filesystem::path& source, bool pin_ancestors = true) noexcept;
    void close() noexcept;
    void* handle() const noexcept { return file_; }
    uint64_t size() const noexcept { return file_size_; }
    uint32_t volume_serial() const noexcept { return volume_serial_; }
    uint64_t file_index() const noexcept { return file_index_; }
    bool read_at(uint64_t offset, std::span<uint8_t> bytes) const noexcept;
    bool read_all_bounded(uint64_t min_bytes, uint64_t max_bytes,
        std::vector<uint8_t>& out_bytes) const noexcept;
    lpb::random_access_reader reader() const noexcept;
    bool verify_same_identity(const std::filesystem::path& source) const noexcept;

private:
    void* file_{};
    uint64_t file_size_{};
    uint32_t volume_serial_{};
    uint64_t file_index_{};
    std::vector<void*> directories_;
};

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
        const wchar_t* prefix, const wchar_t* suffix = L".tmp",
        bool deny_foreign_writes = false) noexcept;
    const std::filesystem::path& path() const noexcept { return temp_path_; }
    bool write_all(std::span<const uint8_t> bytes) noexcept;
    bool write_at(uint64_t offset, std::span<const uint8_t> bytes) noexcept;
    bool size(uint64_t& out_size) noexcept;
    bool copy_from_readonly(const std::filesystem::path& source) noexcept;
    bool compute_sha256(uint8_t out_hash[32]) noexcept;
    bool flush() noexcept;
    bool ready_to_consume() noexcept;
    lpb::random_access_reader reader() noexcept;
    bool publish_no_replace(const std::filesystem::path& destination,
        const lpb_file_identity* expected_identity = nullptr) noexcept;
    // Private conversion binding: register the creating-handle identity in
    // the existing context registry before publication, confirm the exact
    // single record, then publish the same handle. This adds no C ABI surface.
    bool publish_registered_no_replace(lpb_context* context, int32_t artifact_role,
        const std::filesystem::path& destination) noexcept;
    bool capture_cleanup_identity(lpb_file_identity& identity,
        std::string& original_staging_path, std::string& actual_owned_handle_path) noexcept;
    bool abort_and_confirm() noexcept;
    void abort() noexcept;

private:
    std::filesystem::path temp_path_;
    void* handle_{};
    void* directory_handle_{};
};

// Cleans the exact owned staging object and preserves the operation failure
// unless cleanup itself fails, in which case the NativeContext error is
// classified as [CleanupFailed]. Pass nullptr to retain the current Native
// error as the original failure. Returns true only when cleanup was confirmed.
// report_original_on_success=false is for explicit abort operations whose
// success should not overwrite the NativeContext error state.
bool abort_owned_output_and_report(lpb_context* context, windows_owned_output& output,
    const char* original_failure, bool report_original_on_success = true) noexcept;

