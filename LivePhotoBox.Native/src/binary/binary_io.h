#pragma once
#include <cstdint>
#include <cstddef>
#include <span>
#include <cstring>
#include "endian.h"

namespace lpb {

class binary_reader {
    std::span<const uint8_t> m_data;
    size_t m_pos;

public:
    binary_reader(std::span<const uint8_t> data) noexcept : m_data(data), m_pos(0) {}
    binary_reader(const uint8_t* data, size_t size) noexcept : m_data(data, size), m_pos(0) {}

    size_t position() const noexcept { return m_pos; }
    size_t size() const noexcept { return m_data.size(); }
    size_t remaining() const noexcept { return m_data.size() - m_pos; }
    bool eof() const noexcept { return m_pos >= m_data.size(); }
    
    std::span<const uint8_t> data() const noexcept { return m_data; }
    const uint8_t* current_ptr() const noexcept { return m_data.data() + m_pos; }

    bool try_seek(size_t pos) noexcept {
        if (pos > m_data.size()) return false;
        m_pos = pos;
        return true;
    }

    bool skip(size_t bytes) noexcept {
        if (bytes > remaining()) return false;
        m_pos += bytes;
        return true;
    }

    bool try_read_u8(uint8_t& out_val) noexcept {
        if (remaining() < 1) return false;
        out_val = m_data[m_pos++];
        return true;
    }

    bool try_read_be16u(uint16_t& out_val) noexcept {
        if (remaining() < 2) return false;
        out_val = read_be16u(m_data.data() + m_pos);
        m_pos += 2;
        return true;
    }

    bool try_read_le16u(uint16_t& out_val) noexcept {
        if (remaining() < 2) return false;
        out_val = static_cast<uint16_t>(m_data[m_pos]) | (static_cast<uint16_t>(m_data[m_pos + 1]) << 8);
        m_pos += 2;
        return true;
    }
    
    bool try_read_u16_endian(uint16_t& out_val, bool is_big_endian) noexcept {
        return is_big_endian ? try_read_be16u(out_val) : try_read_le16u(out_val);
    }

    bool try_read_be32u(uint32_t& out_val) noexcept {
        if (remaining() < 4) return false;
        out_val = read_be32u(m_data.data() + m_pos);
        m_pos += 4;
        return true;
    }
    
    bool try_read_le32u(uint32_t& out_val) noexcept {
        if (remaining() < 4) return false;
        out_val = static_cast<uint32_t>(m_data[m_pos]) |
                 (static_cast<uint32_t>(m_data[m_pos + 1]) << 8) |
                 (static_cast<uint32_t>(m_data[m_pos + 2]) << 16) |
                 (static_cast<uint32_t>(m_data[m_pos + 3]) << 24);
        m_pos += 4;
        return true;
    }

    bool try_read_u32_endian(uint32_t& out_val, bool is_big_endian) noexcept {
        return is_big_endian ? try_read_be32u(out_val) : try_read_le32u(out_val);
    }
    
    bool try_read_be32(int32_t& out_val) noexcept {
        if (remaining() < 4) return false;
        out_val = read_be32(m_data.data() + m_pos);
        m_pos += 4;
        return true;
    }

    bool try_read_be64(int64_t& out_val) noexcept {
        if (remaining() < 8) return false;
        out_val = read_be64(m_data.data() + m_pos);
        m_pos += 8;
        return true;
    }

    bool try_read_bytes(uint8_t* dest, size_t count) noexcept {
        if (remaining() < count) return false;
        std::memcpy(dest, m_data.data() + m_pos, count);
        m_pos += count;
        return true;
    }
    
    bool try_read_span(size_t count, std::span<const uint8_t>& out_span) noexcept {
        if (remaining() < count) return false;
        out_span = m_data.subspan(m_pos, count);
        m_pos += count;
        return true;
    }
};

class binary_writer {
    std::span<uint8_t> m_data;
    size_t m_pos;
    
public:
    binary_writer(std::span<uint8_t> data) noexcept : m_data(data), m_pos(0) {}
    binary_writer(uint8_t* data, size_t size) noexcept : m_data(data, size), m_pos(0) {}
    
    size_t position() const noexcept { return m_pos; }
    size_t size() const noexcept { return m_data.size(); }
    size_t remaining() const noexcept { return m_data.size() - m_pos; }
    std::span<uint8_t> data() const noexcept { return m_data; }
    uint8_t* current_ptr() const noexcept { return m_data.data() + m_pos; }
    
    bool try_seek(size_t pos) noexcept {
        if (pos > m_data.size()) return false;
        m_pos = pos;
        return true;
    }

    bool skip(size_t bytes) noexcept {
        if (bytes > remaining()) return false;
        m_pos += bytes;
        return true;
    }

    bool try_write_u8(uint8_t val) noexcept {
        if (remaining() < 1) return false;
        m_data[m_pos++] = val;
        return true;
    }

    bool try_write_be16(uint16_t val) noexcept {
        if (remaining() < 2) return false;
        write_be16(m_data.data() + m_pos, val);
        m_pos += 2;
        return true;
    }

    bool try_write_le16(uint16_t val) noexcept {
        if (remaining() < 2) return false;
        m_data[m_pos] = static_cast<uint8_t>(val);
        m_data[m_pos + 1] = static_cast<uint8_t>(val >> 8);
        m_pos += 2;
        return true;
    }
    
    bool try_write_u16_endian(uint16_t val, bool is_big_endian) noexcept {
        return is_big_endian ? try_write_be16(val) : try_write_le16(val);
    }

    bool try_write_be32(int32_t val) noexcept {
        if (remaining() < 4) return false;
        write_be32(m_data.data() + m_pos, val);
        m_pos += 4;
        return true;
    }
    
    bool try_write_be32u(uint32_t val) noexcept {
        if (remaining() < 4) return false;
        write_be32(m_data.data() + m_pos, static_cast<int32_t>(val));
        m_pos += 4;
        return true;
    }

    bool try_write_le32u(uint32_t val) noexcept {
        if (remaining() < 4) return false;
        m_data[m_pos] = static_cast<uint8_t>(val);
        m_data[m_pos + 1] = static_cast<uint8_t>(val >> 8);
        m_data[m_pos + 2] = static_cast<uint8_t>(val >> 16);
        m_data[m_pos + 3] = static_cast<uint8_t>(val >> 24);
        m_pos += 4;
        return true;
    }
    
    bool try_write_u32_endian(uint32_t val, bool is_big_endian) noexcept {
        return is_big_endian ? try_write_be32u(val) : try_write_le32u(val);
    }

    bool try_write_be64(int64_t val) noexcept {
        if (remaining() < 8) return false;
        write_be64(m_data.data() + m_pos, val);
        m_pos += 8;
        return true;
    }

    bool try_write_bytes(const uint8_t* src, size_t count) noexcept {
        if (remaining() < count) return false;
        std::memcpy(m_data.data() + m_pos, src, count);
        m_pos += count;
        return true;
    }
};

} // namespace lpb
