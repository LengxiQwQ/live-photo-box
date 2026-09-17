#pragma once

#include <cstddef>
#include <cstdint>

namespace lpb::crypto {

class sha256_ctx {
public:
    sha256_ctx() noexcept;
    void update(const uint8_t* data, size_t len) noexcept;
    void finalize(uint8_t out_hash[32]) noexcept;

private:
    void transform(const uint8_t block[64]) noexcept;
    uint32_t state_[8];
    uint64_t count_{0};
    uint8_t buffer_[64];
};

void sha256_buffer(const uint8_t* data, size_t len, uint8_t out_hash[32]) noexcept;

} // namespace lpb::crypto
