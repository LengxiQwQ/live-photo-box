#include "foundation/sha256.h"

#include <vector>

namespace lpb::crypto {

bool sha256_file(HANDLE file_handle, uint8_t out_hash[32]) noexcept {
    if (file_handle == INVALID_HANDLE_VALUE || file_handle == NULL) return false;
    LARGE_INTEGER original_position{};
    LARGE_INTEGER zero{};
    if (!SetFilePointerEx(file_handle, zero, &original_position, FILE_CURRENT) ||
        !SetFilePointerEx(file_handle, zero, NULL, FILE_BEGIN)) return false;

    sha256_ctx ctx;
    std::vector<uint8_t> buffer(64 * 1024);
    DWORD bytes_read = 0;
    while (true) {
        if (!ReadFile(file_handle, buffer.data(), static_cast<DWORD>(buffer.size()), &bytes_read, NULL)) {
            const DWORD error = GetLastError();
            SetFilePointerEx(file_handle, original_position, NULL, FILE_BEGIN);
            SetLastError(error);
            return false;
        }
        if (bytes_read == 0) break;
        ctx.update(buffer.data(), bytes_read);
    }
    ctx.finalize(out_hash);
    SetFilePointerEx(file_handle, original_position, NULL, FILE_BEGIN);
    return true;
}

bool sha256_path(const wchar_t* path, uint8_t out_hash[32]) noexcept {
    if (!path || path[0] == L'\0') return false;
    HANDLE handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (handle == INVALID_HANDLE_VALUE) return false;
    const bool ok = sha256_file(handle, out_hash);
    CloseHandle(handle);
    return ok;
}

} // namespace lpb::crypto
