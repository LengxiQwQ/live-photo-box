#include "foundation/sha256.h"
#include <cstring>
#include <vector>

namespace lpb::crypto {

namespace {

inline uint32_t rotr(uint32_t x, uint32_t n) noexcept {
    return (x >> n) | (x << (32 - n));
}

inline uint32_t ch(uint32_t x, uint32_t y, uint32_t z) noexcept {
    return (x & y) ^ (~x & z);
}

inline uint32_t maj(uint32_t x, uint32_t y, uint32_t z) noexcept {
    return (x & y) ^ (x & z) ^ (y & z);
}

inline uint32_t sigma0(uint32_t x) noexcept {
    return rotr(x, 2) ^ rotr(x, 13) ^ rotr(x, 22);
}

inline uint32_t sigma1(uint32_t x) noexcept {
    return rotr(x, 6) ^ rotr(x, 11) ^ rotr(x, 25);
}

inline uint32_t gamma0(uint32_t x) noexcept {
    return rotr(x, 7) ^ rotr(x, 18) ^ (x >> 3);
}

inline uint32_t gamma1(uint32_t x) noexcept {
    return rotr(x, 17) ^ rotr(x, 19) ^ (x >> 10);
}

static const uint32_t K[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
};

} // namespace

sha256_ctx::sha256_ctx() noexcept {
    state_[0] = 0x6a09e667;
    state_[1] = 0xbb67ae85;
    state_[2] = 0x3c6ef372;
    state_[3] = 0xa54ff53a;
    state_[4] = 0x510e527f;
    state_[5] = 0x9b05688c;
    state_[6] = 0x1f83d9ab;
    state_[7] = 0x5be0cd19;
    count_ = 0;
}

void sha256_ctx::transform(const uint8_t block[64]) noexcept {
    uint32_t w[64];
    for (int i = 0; i < 16; ++i) {
        w[i] = (static_cast<uint32_t>(block[i * 4]) << 24) |
               (static_cast<uint32_t>(block[i * 4 + 1]) << 16) |
               (static_cast<uint32_t>(block[i * 4 + 2]) << 8) |
               (static_cast<uint32_t>(block[i * 4 + 3]));
    }
    for (int i = 16; i < 64; ++i) {
        w[i] = gamma1(w[i - 2]) + w[i - 7] + gamma0(w[i - 15]) + w[i - 16];
    }

    uint32_t a = state_[0];
    uint32_t b = state_[1];
    uint32_t c = state_[2];
    uint32_t d = state_[3];
    uint32_t e = state_[4];
    uint32_t f = state_[5];
    uint32_t g = state_[6];
    uint32_t h = state_[7];

    for (int i = 0; i < 64; ++i) {
        uint32_t t1 = h + sigma1(e) + ch(e, f, g) + K[i] + w[i];
        uint32_t t2 = sigma0(a) + maj(a, b, c);
        h = g;
        g = f;
        f = e;
        e = d + t1;
        d = c;
        c = b;
        b = a;
        a = t1 + t2;
    }

    state_[0] += a;
    state_[1] += b;
    state_[2] += c;
    state_[3] += d;
    state_[4] += e;
    state_[5] += f;
    state_[6] += g;
    state_[7] += h;
}

void sha256_ctx::update(const uint8_t* data, size_t len) noexcept {
    size_t buffer_index = static_cast<size_t>(count_ % 64);
    count_ += len;

    size_t part_len = 64 - buffer_index;
    size_t i = 0;

    if (len >= part_len) {
        std::memcpy(&buffer_[buffer_index], data, part_len);
        transform(buffer_);
        for (i = part_len; i + 63 < len; i += 64) {
            transform(&data[i]);
        }
        buffer_index = 0;
    }

    if (i < len) {
        std::memcpy(&buffer_[buffer_index], &data[i], len - i);
    }
}

void sha256_ctx::finalize(uint8_t out_hash[32]) noexcept {
    uint8_t final_count[8];
    uint64_t bit_count = count_ * 8;
    for (int i = 0; i < 8; ++i) {
        final_count[7 - i] = static_cast<uint8_t>((bit_count >> (i * 8)) & 0xFF);
    }

    size_t buffer_index = static_cast<size_t>(count_ % 64);
    size_t pad_len = (buffer_index < 56) ? (56 - buffer_index) : (120 - buffer_index);

    static const uint8_t padding[64] = {
        0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0,    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0,    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0,    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    update(padding, pad_len);
    update(final_count, 8);

    for (int i = 0; i < 8; ++i) {
        out_hash[i * 4]     = static_cast<uint8_t>((state_[i] >> 24) & 0xFF);
        out_hash[i * 4 + 1] = static_cast<uint8_t>((state_[i] >> 16) & 0xFF);
        out_hash[i * 4 + 2] = static_cast<uint8_t>((state_[i] >> 8) & 0xFF);
        out_hash[i * 4 + 3] = static_cast<uint8_t>(state_[i] & 0xFF);
    }
}

void sha256_buffer(const uint8_t* data, size_t len, uint8_t out_hash[32]) noexcept {
    sha256_ctx ctx;
    ctx.update(data, len);
    ctx.finalize(out_hash);
}

bool sha256_file(HANDLE file_handle, uint8_t out_hash[32]) noexcept {
    if (file_handle == INVALID_HANDLE_VALUE || file_handle == NULL) {
        return false;
    }

    LARGE_INTEGER orig_pos{};
    LARGE_INTEGER zero{};
    if (!SetFilePointerEx(file_handle, zero, &orig_pos, FILE_CURRENT)) {
        return false;
    }
    if (!SetFilePointerEx(file_handle, zero, NULL, FILE_BEGIN)) {
        return false;
    }

    sha256_ctx ctx;
    std::vector<uint8_t> buffer(64 * 1024);
    DWORD bytes_read = 0;

    while (true) {
        BOOL ok = ReadFile(file_handle, buffer.data(), static_cast<DWORD>(buffer.size()), &bytes_read, NULL);
        if (!ok) {
            DWORD err = GetLastError();
            SetFilePointerEx(file_handle, orig_pos, NULL, FILE_BEGIN);
            SetLastError(err);
            return false;
        }
        if (bytes_read == 0) {
            // EOF reached successfully
            break;
        }
        ctx.update(buffer.data(), bytes_read);
    }

    ctx.finalize(out_hash);
    SetFilePointerEx(file_handle, orig_pos, NULL, FILE_BEGIN);
    return true;
}

bool sha256_path(const wchar_t* path, uint8_t out_hash[32]) noexcept {
    if (!path || path[0] == L'\0') return false;

    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;

    bool ok = sha256_file(h, out_hash);
    CloseHandle(h);
    return ok;
}

} // namespace lpb::crypto
