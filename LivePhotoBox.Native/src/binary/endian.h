#pragma once

#include <cstdint>
#include <vector>

inline uint16_t read_be16u(const uint8_t* data) noexcept
{
    return (static_cast<uint16_t>(data[0]) << 8) | static_cast<uint16_t>(data[1]);
}

inline void write_be16(uint8_t* data, uint16_t value) noexcept
{
    data[0] = static_cast<uint8_t>(value >> 8);
    data[1] = static_cast<uint8_t>(value);
}

inline uint32_t read_be32u(const uint8_t* data) noexcept
{
    return (static_cast<uint32_t>(data[0]) << 24)
        | (static_cast<uint32_t>(data[1]) << 16)
        | (static_cast<uint32_t>(data[2]) << 8)
        | static_cast<uint32_t>(data[3]);
}

inline int32_t read_be32(const uint8_t* data) noexcept
{
    return static_cast<int32_t>(read_be32u(data));
}

inline int64_t read_be64(const uint8_t* data) noexcept
{
    const uint64_t value = (static_cast<uint64_t>(read_be32u(data)) << 32)
        | static_cast<uint64_t>(read_be32u(data + 4));
    return static_cast<int64_t>(value);
}

inline void write_be32(uint8_t* data, int32_t value) noexcept
{
    const uint32_t bits = static_cast<uint32_t>(value);
    data[0] = static_cast<uint8_t>(bits >> 24);
    data[1] = static_cast<uint8_t>(bits >> 16);
    data[2] = static_cast<uint8_t>(bits >> 8);
    data[3] = static_cast<uint8_t>(bits);
}

inline void write_be64(uint8_t* data, int64_t value) noexcept
{
    const uint64_t bits = static_cast<uint64_t>(value);
    write_be32(data, static_cast<int32_t>(bits >> 32));
    write_be32(data + 4, static_cast<int32_t>(bits));
}

inline void append_be32(std::vector<uint8_t>& output, uint32_t value)
{
    output.push_back(static_cast<uint8_t>(value >> 24));
    output.push_back(static_cast<uint8_t>(value >> 16));
    output.push_back(static_cast<uint8_t>(value >> 8));
    output.push_back(static_cast<uint8_t>(value));
}
