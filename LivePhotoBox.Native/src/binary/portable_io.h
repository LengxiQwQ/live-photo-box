#pragma once

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <span>

namespace lpb {

// Core sees byte ranges and callbacks; the platform adapter owns any file
// descriptor/handle, path encoding and error mapping.
struct random_access_reader {
    uint64_t length{};
    void* user{};
    bool (*read_at)(void*, uint64_t, std::span<uint8_t>) noexcept{};

    bool read_exact(uint64_t offset, std::span<uint8_t> bytes) const noexcept {
        return read_at != nullptr && offset <= length &&
            bytes.size() <= length - offset && read_at(user, offset, bytes);
    }
};

struct sequential_writer {
    void* user{};
    size_t (*write_some)(void*, std::span<const uint8_t>) noexcept{};

    bool write_all(std::span<const uint8_t> bytes) const noexcept {
        if (write_some == nullptr) return false;
        while (!bytes.empty()) {
            const size_t written = write_some(user, bytes);
            if (written == 0 || written > bytes.size()) return false;
            bytes = bytes.subspan(written);
        }
        return true;
    }
};

struct memory_random_access {
    std::span<const uint8_t> bytes;

    static bool read(void* user, uint64_t offset, std::span<uint8_t> output) noexcept {
        const auto& source = *static_cast<memory_random_access*>(user);
        if (offset > source.bytes.size() || output.size() > source.bytes.size() - offset) return false;
        if (output.empty()) return true;
        std::memcpy(output.data(), source.bytes.data() + static_cast<size_t>(offset), output.size());
        return true;
    }

    random_access_reader view() noexcept {
        return {static_cast<uint64_t>(bytes.size()), this, read};
    }
};

} // namespace lpb
