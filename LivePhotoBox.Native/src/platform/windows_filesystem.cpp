#include "foundation/internal.h"
#include "platform/windows_filesystem.h"

lpb_platform_error lpb_platform_classify_error(uint32_t code) noexcept {
    switch (code) {
    case ERROR_DISK_FULL: case ERROR_HANDLE_DISK_FULL: return lpb_platform_error::disk_full;
    case ERROR_FILE_EXISTS: case ERROR_ALREADY_EXISTS: return lpb_platform_error::destination_exists;
    case ERROR_SHARING_VIOLATION: return lpb_platform_error::sharing_violation;
    case ERROR_ACCESS_DENIED: return lpb_platform_error::access_denied;
    default: return lpb_platform_error::other;
    }
}

lpb_platform_capabilities lpb_platform_query_capabilities() noexcept {
    // Replacement needs a separate exact-target authority protocol. No current
    // production caller has one, so the backend advertises it as unavailable.
    return {true, true, true, false};
}

bool lpb_platform_dispose_owned(void* owned_handle) noexcept {
    const HANDLE handle = static_cast<HANDLE>(owned_handle);
    if (handle == nullptr || handle == INVALID_HANDLE_VALUE) {
        SetLastError(ERROR_INVALID_HANDLE);
        return false;
    }
    FILE_DISPOSITION_INFO disposition{};
    disposition.DeleteFile = TRUE;
    return SetFileInformationByHandle(handle, FileDispositionInfo,
        &disposition, sizeof(disposition)) != FALSE;
}

std::filesystem::path utf8_to_path(const char* utf8_str) noexcept
{
    if (utf8_str == nullptr || *utf8_str == '\0')
    {
        return {};
    }
    try {
        int wlen = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8_str, -1, nullptr, 0);
        if (wlen <= 1) return {};
        std::wstring wstr(wlen, 0);
        if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8_str, -1, wstr.data(), wlen) != wlen)
            return {};
        wstr.pop_back();
        return std::filesystem::path(wstr);
    } catch (...) { return {}; }
}

std::string path_to_utf8(const std::filesystem::path& path) noexcept
{
    try {
        const std::wstring wstr = path.wstring();
        if (wstr.empty() || wstr.size() > static_cast<size_t>(std::numeric_limits<int>::max())) return {};
        const int size = static_cast<int>(wstr.size());
        int ulen = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wstr.data(), size,
            nullptr, 0, nullptr, nullptr);
        if (ulen <= 0) return {};
        std::string ustr(ulen, 0);
        if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wstr.data(), size,
                ustr.data(), ulen, nullptr, nullptr) != ulen) return {};
        return ustr;
    } catch (...) { return {}; }
}

bool capture_file_identity_from_handle(
    void* file_handle,
    lpb_file_identity& identity,
    std::wstring& final_path) noexcept
{
    identity = {};
    final_path.clear();
    const HANDLE handle = static_cast<HANDLE>(file_handle);
    if (handle == nullptr || handle == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(handle, &info))
    {
        return false;
    }

    identity.volume_serial = info.dwVolumeSerialNumber;
    identity.file_index = (static_cast<uint64_t>(info.nFileIndexHigh) << 32) |
        static_cast<uint64_t>(info.nFileIndexLow);
    identity.file_size = (static_cast<uint64_t>(info.nFileSizeHigh) << 32) |
        static_cast<uint64_t>(info.nFileSizeLow);
    identity.link_count = info.nNumberOfLinks;

    try {
      std::vector<wchar_t> buffer(512);
      for (;;) {
        const DWORD length = GetFinalPathNameByHandleW(
            handle, buffer.data(), static_cast<DWORD>(buffer.size()), FILE_NAME_NORMALIZED);
        if (length == 0)
        {
            return false;
        }
        if (length < buffer.size())
        {
            final_path.assign(buffer.data(), length);
            return true;
        }
        if (buffer.size() >= 32768)
        {
            return false;
        }
        buffer.resize(buffer.size() * 2);
      }
    } catch (...) { identity = {}; final_path.clear(); return false; }
}

bool capture_file_identity(
    const char* utf8_path,
    lpb_file_identity& identity,
    std::wstring& final_path) noexcept
{
    identity = {};
    final_path.clear();
    const auto path = utf8_to_path(utf8_path);
    if (path.empty())
    {
        return false;
    }

    const HANDLE handle = CreateFileW(
        path.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_BACKUP_SEMANTICS,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    const bool result = capture_file_identity_from_handle(handle, identity, final_path);
    CloseHandle(handle);
    return result;
}

bool paths_alias(const char* first, const char* second) noexcept
{
    try
    {
        const auto left = utf8_to_path(first);
        const auto right = utf8_to_path(second);
        if (left.empty() || right.empty()) return false;

        std::error_code left_ec;
        std::error_code right_ec;
        auto left_absolute = std::filesystem::absolute(left, left_ec);
        auto right_absolute = std::filesystem::absolute(right, right_ec);
        if (left_ec) left_absolute = left;
        if (right_ec) right_absolute = right;
        std::wstring left_norm = left_absolute.lexically_normal().wstring();
        std::wstring right_norm = right_absolute.lexically_normal().wstring();
        if (_wcsicmp(left_norm.c_str(), right_norm.c_str()) == 0) return true;

        std::error_code equivalent_ec;
        return std::filesystem::equivalent(left, right, equivalent_ec) && !equivalent_ec;
    }
    catch (...)
    {
        return true; // Fail closed: cannot prove paths are distinct
    }
}



bool lpb_platform_publish_owned_no_replace(
    void* owned_handle, const std::filesystem::path& destination) noexcept
{
    const HANDLE handle = static_cast<HANDLE>(owned_handle);
    if (handle == nullptr || handle == INVALID_HANDLE_VALUE || destination.empty()) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }
    try {
        const std::wstring name = destination.native();
        if (name.size() > (std::numeric_limits<DWORD>::max() / sizeof(wchar_t))) {
            SetLastError(ERROR_INVALID_PARAMETER);
            return false;
        }
        std::vector<uint8_t> buffer(sizeof(FILE_RENAME_INFO) + name.size() * sizeof(wchar_t));
        auto* rename = reinterpret_cast<FILE_RENAME_INFO*>(buffer.data());
        std::memset(rename, 0, buffer.size());
        rename->ReplaceIfExists = FALSE;
        rename->RootDirectory = nullptr;
        rename->FileNameLength = static_cast<DWORD>(name.size() * sizeof(wchar_t));
        std::memcpy(rename->FileName, name.data(), rename->FileNameLength);
        return SetFileInformationByHandle(handle, FileRenameInfo, rename,
            static_cast<DWORD>(buffer.size())) != FALSE;
    } catch (...) {
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return false;
    }
}

// Win32 exact-object ownership and publish backend. This file initially keeps
// the proven P1–P3 behavior while the callers are routed through this boundary.

// Creates a unique temporary file with CREATE_NEW so the returned handle is
// provably the FIRST creation of that filesystem object: the kernel atomically
// fails with ERROR_FILE_EXISTS if any other object (including a pre-seeded
// foreign file) already owns the candidate name, and the caller holds the
// creating handle from the very first moment of the object's existence.  This
// is the ownership anchor for the whole Cleaner publish chain: unlike
// GetTempFileNameW (which creates an empty file, closes its handle, and
// returns a pathname that can be raced), no code path ever re-opens a pathname
// to re-establish ownership.  A name collision simply retries with a fresh
// random name.
HANDLE lpb_create_unique_temp_file(const std::filesystem::path& dir,
    const wchar_t* prefix, std::filesystem::path& out_path,
    const wchar_t* suffix, bool deny_foreign_writes) noexcept
{
    try
    {
        std::random_device rd;
        for (int attempt = 0; attempt < 16; ++attempt)
        {
            const unsigned long r1 = rd();
            const unsigned long r2 = rd();
            std::wstring name = std::wstring(prefix) + L"-"
                + std::to_wstring(static_cast<unsigned long>(GetCurrentProcessId())) + L"-"
                + std::to_wstring(static_cast<unsigned long>(GetTickCount64() & 0xFFFFFFFFull)) + L"-"
                + std::to_wstring(r1) + L"-" + std::to_wstring(r2) + suffix;
            const std::filesystem::path candidate = dir / name;
            HANDLE h = CreateFileW(candidate.c_str(), GENERIC_READ | GENERIC_WRITE | DELETE,
                deny_foreign_writes ? (FILE_SHARE_READ | FILE_SHARE_DELETE)
                                    : (FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE), nullptr,
                CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h != INVALID_HANDLE_VALUE)
            {
                out_path = candidate;
                return h;
            }
            const DWORD err = GetLastError();
            if (err != ERROR_FILE_EXISTS && err != ERROR_ALREADY_EXISTS)
            {
                return INVALID_HANDLE_VALUE;
            }
            // Name collision: retry with a fresh random name.
        }
        SetLastError(ERROR_FILE_EXISTS);
        return INVALID_HANDLE_VALUE;
    }
    catch (...)
    {
        // Any random-device / allocation / conversion failure must surface as
        // a clean failure, never terminate through noexcept.
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return INVALID_HANDLE_VALUE;
    }
}

// Unified handle-based no-overwrite publication for Cleaner-owned outputs.
//
// The SOURCE object is the one `handle` was created for (CREATE_NEW first
// creation).  It is renamed to `dest` THROUGH THE HANDLE itself
// (SetFileInformationByHandle / FileRenameInfo with ReplaceIfExists = FALSE),
// never through MoveFileExW(sourcePathname, ...): a foreign object that takes
// over the source pathname after creation must not be able to redirect the
// publish.  Identity is captured from the same handle
// (GetFileInformationByHandle) and registered in the transaction's staged
// output registry; the caller then closes the handle.  `temp_source` is the
// pathname the object was created at and is used ONLY by the test-harness
// race seam and for naming; it never re-establishes ownership.
//
// On any failure the object is left in place (either still at `temp_source`,
// or, after a successful rename, at `dest`), and the caller must dispose of
// it through this same handle (FileDispositionInfo).  This function never
// deletes by pathname and never touches a foreign object.  A destination that
// already exists (including a foreign object placed there mid-flight) fails
// closed and the owned object is deleted through the handle.
bool lpb_publish_cleaner_output_handle(
    lpb_context* context,
    int32_t artifact_role,
    HANDLE handle,
    const std::filesystem::path& temp_source,
    const std::filesystem::path& dest,
    const std::string& dest_path) noexcept
{
    // temp_source is consumed only by the test-harness race seam below; in
    // production builds it is intentionally unused (never re-establishes
    // ownership) and is referenced purely to keep the parameter meaningful.
    static_cast<void>(temp_source);
#if defined(LPB_NATIVE_TEST_HARNESS)
    if (context != nullptr && context->cleaner_hook.swap_temp_source_before_publish != 0)
    {
        // Role-targeted seam: only a publish whose artifact_role equals the
        // armed target (or the ANY sentinel) consumes the one-shot fault.
        // Publishes for any other role run normally and leave the seam armed,
        // so a test can prove the fault fired on exactly the intended sink.
        const int32_t target = context->cleaner_hook.target_artifact_role;
        const bool role_matches =
            target == LPB_CLEANER_TEST_HOOK_TARGET_ANY || target == artifact_role;
        if (role_matches)
        {
            context->cleaner_hook.swap_temp_source_before_publish = 0;
            context->cleaner_hook.last_triggered_artifact_role = artifact_role;
            context->cleaner_hook.trigger_count += 1;
            try
            {
            // Stage the race: move the owned object (by THIS handle) to a side
            // pathname, then create a foreign object at the original temp
            // pathname.  The publish below must still move the ORIGINAL object
            // and must leave the foreign object untouched.
            std::filesystem::path side = temp_source;
            side += L".lpb-race-side";
            const std::wstring side_w = side.native();
            std::vector<uint8_t> side_buf(sizeof(FILE_RENAME_INFO) + side_w.size() * sizeof(wchar_t));
            auto* side_info = reinterpret_cast<FILE_RENAME_INFO*>(side_buf.data());
            std::memset(side_info, 0, side_buf.size());
            side_info->ReplaceIfExists = FALSE;
            side_info->RootDirectory = nullptr;
            side_info->FileNameLength = static_cast<DWORD>(side_w.size() * sizeof(wchar_t));
            std::memcpy(side_info->FileName, side_w.c_str(), side_info->FileNameLength);
            if (!SetFileInformationByHandle(handle, FileRenameInfo, side_info, static_cast<DWORD>(side_buf.size())))
            {
                // Could not stage the takeover; fail closed rather than run a
                // publish the test cannot distinguish from a benign path.
                return false;
            }
            HANDLE foreign = CreateFileW(temp_source.c_str(), GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (foreign == INVALID_HANDLE_VALUE)
            {
                return false;
            }
            const char marker[] = "LPB-FOREIGN-RACE-MARKER";
            DWORD written = 0;
            static_cast<void>(WriteFile(foreign, marker, static_cast<DWORD>(sizeof(marker) - 1), &written, nullptr));
            CloseHandle(foreign);
        }
            catch (...)
            {
                return false;
            }
        }
    }
#endif
    try
    {
        if (!lpb_platform_publish_owned_no_replace(handle, dest))
        {
            return false;
        }
        BY_HANDLE_FILE_INFORMATION finfo{};
        if (!GetFileInformationByHandle(handle, &finfo))
        {
            return false;
        }
        lpb_file_identity identity{};
        identity.volume_serial = finfo.dwVolumeSerialNumber;
        identity.file_index = (static_cast<uint64_t>(finfo.nFileIndexHigh) << 32) | finfo.nFileIndexLow;
        identity.file_size = (static_cast<uint64_t>(finfo.nFileSizeHigh) << 32) | finfo.nFileSizeLow;
        identity.link_count = finfo.nNumberOfLinks;
        record_cleaner_staged_output(context, artifact_role, dest_path, identity);
        return true;
    }
    catch (...)
    {
        return false;
    }
}

namespace {
std::wstring normalized_path(const std::filesystem::path& path) {
    std::wstring value = std::filesystem::absolute(path).lexically_normal().wstring();
    if (value.compare(0, 4, L"\\\\?\\") == 0) value.erase(0, 4);
    return value;
}

bool exact_owned_path(HANDLE handle, const std::filesystem::path& path) noexcept {
    try {
        lpb_file_identity owned{}, named{};
        std::wstring owned_path, named_path;
        if (!capture_file_identity_from_handle(handle, owned, owned_path)) return false;
        HANDLE named_handle = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
            OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (named_handle == INVALID_HANDLE_VALUE) return false;
        FILE_ATTRIBUTE_TAG_INFO tag{};
        const bool valid = GetFileInformationByHandleEx(named_handle, FileAttributeTagInfo,
            &tag, sizeof(tag)) != FALSE &&
            (tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0 &&
            capture_file_identity_from_handle(named_handle, named, named_path);
        CloseHandle(named_handle);
        return valid && owned.volume_serial == named.volume_serial &&
            owned.file_index == named.file_index && owned.link_count == 1 &&
            named.link_count == 1 &&
            _wcsicmp(normalized_path(path).c_str(), normalized_path(owned_path).c_str()) == 0;
    } catch (...) {
        return false;
    }
}

bool owned_handle_read_at(void* user, uint64_t offset, std::span<uint8_t> bytes) noexcept {
    const HANDLE file = static_cast<HANDLE>(user);
    if (file == nullptr || file == INVALID_HANDLE_VALUE ||
        offset > static_cast<uint64_t>(std::numeric_limits<LONGLONG>::max())) return false;
    LARGE_INTEGER seek{};
    seek.QuadPart = static_cast<LONGLONG>(offset);
    if (!SetFilePointerEx(file, seek, nullptr, FILE_BEGIN)) return false;
    while (!bytes.empty()) {
        const DWORD count = static_cast<DWORD>(std::min<size_t>(bytes.size(), 1024 * 1024));
        DWORD read = 0;
        if (!ReadFile(file, bytes.data(), count, &read, nullptr) || read == 0 || read > count)
            return false;
        bytes = bytes.subspan(read);
    }
    return true;
}
}

bool lpb_platform_path_is_reparse_point(const std::filesystem::path& path) noexcept {
    if (path.empty()) return false;
    HANDLE handle = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
        OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE) return false;
    FILE_ATTRIBUTE_TAG_INFO tag{};
    const bool result = GetFileInformationByHandleEx(handle, FileAttributeTagInfo,
        &tag, sizeof(tag)) != FALSE &&
        ((tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 || tag.ReparseTag != 0);
    CloseHandle(handle);
    return result;
}

bool lpb_platform_pin_directory(const std::filesystem::path& directory,
    void*& owned_handle) noexcept
{
    owned_handle = nullptr;
    if (directory.empty() || lpb_platform_path_is_reparse_point(directory)) return false;
    HANDLE handle = CreateFileW(directory.c_str(), FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
        // Pin the exact directory object against rename-away until commit.
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return false;
    try {
        lpb_file_identity identity{};
        std::wstring actual;
        if (!capture_file_identity_from_handle(handle, identity, actual) ||
            _wcsicmp(normalized_path(directory).c_str(), normalized_path(actual).c_str()) != 0) {
            CloseHandle(handle);
            SetLastError(ERROR_INVALID_NAME);
            return false;
        }
        owned_handle = handle;
        return true;
    } catch (...) {
        CloseHandle(handle);
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return false;
    }
}

windows_owned_output::~windows_owned_output() noexcept { abort(); }

bool windows_owned_output::create(const std::filesystem::path& destination,
    const wchar_t* prefix, const wchar_t* suffix) noexcept
{
    if (handle_ != nullptr || destination.empty() || prefix == nullptr || suffix == nullptr) return false;
    try {
        const auto directory = destination.parent_path();
        if (directory.empty()) return false;
        if (!lpb_platform_pin_directory(directory, directory_handle_)) return false;
        HANDLE file = lpb_create_unique_temp_file(directory, prefix, temp_path_, suffix);
        if (file == INVALID_HANDLE_VALUE) {
            CloseHandle(static_cast<HANDLE>(directory_handle_));
            directory_handle_ = nullptr;
            return false;
        }
        handle_ = file;
        return exact_owned_path(file, temp_path_);
    } catch (...) {
        abort();
        return false;
    }
}

bool windows_owned_output::write_all(std::span<const uint8_t> bytes) noexcept {
    HANDLE file = static_cast<HANDLE>(handle_);
    if (file == nullptr || file == INVALID_HANDLE_VALUE) return false;
    while (!bytes.empty()) {
        const DWORD count = static_cast<DWORD>(std::min<size_t>(bytes.size(), 1024 * 1024));
        DWORD written = 0;
        if (!WriteFile(file, bytes.data(), count, &written, nullptr) || written == 0 || written > count)
            return false;
        bytes = bytes.subspan(written);
    }
    return true;
}

bool windows_owned_output::copy_from_readonly(const std::filesystem::path& source) noexcept {
    HANDLE input = CreateFileW(source.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (input == INVALID_HANDLE_VALUE) return false;
    bool okay = true;
    uint8_t buffer[64 * 1024];
    for (;;) {
        DWORD read = 0;
        if (!ReadFile(input, buffer, sizeof(buffer), &read, nullptr)) { okay = false; break; }
        if (read == 0) break;
        if (!write_all(std::span<const uint8_t>(buffer, read))) { okay = false; break; }
    }
    CloseHandle(input);
    return okay;
}

bool windows_owned_output::flush() noexcept {
    HANDLE file = static_cast<HANDLE>(handle_);
    return file != nullptr && file != INVALID_HANDLE_VALUE && FlushFileBuffers(file) != FALSE;
}

bool windows_owned_output::ready_to_consume() noexcept {
    HANDLE file = static_cast<HANDLE>(handle_);
    lpb_file_identity staged{};
    std::wstring staged_path;
    return file != nullptr && file != INVALID_HANDLE_VALUE &&
        exact_owned_path(file, temp_path_) && flush() &&
        capture_file_identity_from_handle(file, staged, staged_path) &&
        staged.file_size > 0 && staged.link_count == 1;
}

lpb::random_access_reader windows_owned_output::reader() noexcept {
    HANDLE file = static_cast<HANDLE>(handle_);
    lpb_file_identity identity{};
    std::wstring path;
    if (file == nullptr || file == INVALID_HANDLE_VALUE ||
        !capture_file_identity_from_handle(file, identity, path)) return {};
    return {identity.file_size, file, owned_handle_read_at};
}

bool windows_owned_output::publish_no_replace(const std::filesystem::path& destination) noexcept {
    HANDLE file = static_cast<HANDLE>(handle_);
    if (file == nullptr || file == INVALID_HANDLE_VALUE || destination.empty() ||
        destination.parent_path() != temp_path_.parent_path() ||
        !ready_to_consume()) return false;
    lpb_file_identity staged{};
    std::wstring staged_path;
    if (!capture_file_identity_from_handle(file, staged, staged_path) ||
        staged.file_size == 0 || staged.link_count != 1) return false;
    if (GetFileAttributesW(destination.c_str()) != INVALID_FILE_ATTRIBUTES ||
        GetLastError() != ERROR_FILE_NOT_FOUND) return false;
    if (!lpb_platform_publish_owned_no_replace(file, destination)) return false;
    lpb_file_identity published{};
    std::wstring published_path;
    if (!capture_file_identity_from_handle(file, published, published_path) ||
        published.volume_serial != staged.volume_serial ||
        published.file_index != staged.file_index ||
        published.file_size != staged.file_size ||
        !exact_owned_path(file, destination)) return false;
    CloseHandle(file);
    CloseHandle(static_cast<HANDLE>(directory_handle_));
    handle_ = nullptr;
    directory_handle_ = nullptr;
    temp_path_.clear();
    return true;
}

void windows_owned_output::abort() noexcept {
    HANDLE file = static_cast<HANDLE>(handle_);
    if (file != nullptr && file != INVALID_HANDLE_VALUE) {
        static_cast<void>(lpb_platform_dispose_owned(file));
        CloseHandle(file);
    }
    HANDLE directory = static_cast<HANDLE>(directory_handle_);
    if (directory != nullptr && directory != INVALID_HANDLE_VALUE) CloseHandle(directory);
    handle_ = nullptr;
    directory_handle_ = nullptr;
    temp_path_.clear();
}
