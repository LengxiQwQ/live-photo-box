#include "exif_rewrite.h"
#include "binary/binary_io.h"
#include <set>
#include <algorithm>
#include <cstring>
#include <limits>

using namespace lpb;

namespace {

static uint16_t read_u16(const uint8_t* p, bool big_endian) {
    uint16_t val = 0;
    binary_reader reader(p, 2);
    reader.try_read_u16_endian(val, big_endian);
    return val;
}

static uint32_t read_u32(const uint8_t* p, bool big_endian) {
    uint32_t val = 0;
    binary_reader reader(p, 4);
    reader.try_read_u32_endian(val, big_endian);
    return val;
}

static void write_u16(uint8_t* p, uint16_t val, bool big_endian) {
    binary_writer writer(p, 2);
    writer.try_write_u16_endian(val, big_endian);
}

static void write_u32(uint8_t* p, uint32_t val, bool big_endian) {
    binary_writer writer(p, 4);
    writer.try_write_u32_endian(val, big_endian);
}

static size_t type_to_data_length(uint16_t type, uint32_t count) {
    size_t unit = 0;
    switch (type) {
        case 1: case 2: case 7: unit = 1; break;
        case 3: case 8: unit = 2; break;
        case 4: case 9: unit = 4; break;
        case 5: case 10: unit = 8; break;
        case 6: case 11: unit = 4; break;
        case 12: unit = 8; break;
        case 13: case 14: unit = 4; break;
        case 16: unit = 8; break;
    }
    if (unit == 0 || count > std::numeric_limits<size_t>::max() / unit) return 0;
    const size_t len = unit * static_cast<size_t>(count);
    return len > 4 ? len : 0;
}

struct Fixup {
    size_t pos;
    uint32_t value;
};

struct DataRange {
    size_t start;
    size_t length;
};

static void collect_makernote_cleanup(
    const uint8_t* data, size_t data_size,
    size_t ifd_rel, bool big_endian,
    std::vector<size_t>& removal_starts,
    std::vector<size_t>& count_positions,
    std::vector<Fixup>& fixups,
    std::set<size_t>& visited,
    std::vector<DataRange>& data_ranges,
    std::vector<DataRange>& protected_ranges)
{
    if (ifd_rel == 0 || !visited.insert(ifd_rel).second) return;
    
    size_t p = ifd_rel;
    if (p + 2 > data_size) return;
    
    uint16_t count = read_u16(data + p, big_endian);
    if (count == 0 || count > 512) return;

    size_t next_ifd_pos = p + 2 + count * 12;
    if (next_ifd_pos + 4 <= data_size) {
        uint32_t next_val = read_u32(data + next_ifd_pos, big_endian);
        fixups.push_back({next_ifd_pos, next_val});
        if (next_val > 0) {
            collect_makernote_cleanup(data, data_size, next_val, big_endian, removal_starts, count_positions, fixups, visited, data_ranges, protected_ranges);
        }
    }

    for (uint16_t i = 0; i < count; i++) {
        size_t e = p + 2 + i * 12;
        if (e + 12 > data_size) break;
        
        uint16_t tag = read_u16(data + e, big_endian);
        uint16_t type = read_u16(data + e + 2, big_endian);
        uint32_t cnt = read_u32(data + e + 4, big_endian);
        size_t value_pos = e + 8;
        uint32_t off = read_u32(data + value_pos, big_endian);

        if (tag == 0x927C) {
            removal_starts.push_back(e);
            count_positions.push_back(p);
            
            const size_t mn_data_len = type_to_data_length(type, cnt);
            if (mn_data_len > 0 && static_cast<size_t>(off) <= data_size &&
                mn_data_len <= data_size - static_cast<size_t>(off)) {
                data_ranges.push_back({static_cast<size_t>(off), mn_data_len});
            }
            continue;
        }

        if (tag == 0x8769 || tag == 0x8825 || tag == 0xA005 || tag == 0x014A) {
            fixups.push_back({value_pos, off});
            if (off > 0 && (tag != 0x014A || cnt == 1)) {
                collect_makernote_cleanup(data, data_size, off, big_endian, removal_starts, count_positions, fixups, visited, data_ranges, protected_ranges);
            }
            continue;
        }

        const size_t data_len = type_to_data_length(type, cnt);
        if (data_len > 0 && static_cast<size_t>(off) <= data_size &&
            data_len <= data_size - static_cast<size_t>(off)) {
            fixups.push_back({value_pos, off});
            protected_ranges.push_back({static_cast<size_t>(off), data_len});
        }
    }
}

static void collect_ifd_fixups(
    const uint8_t* data, size_t data_size,
    size_t ifd_rel, size_t insert_at_rel, bool big_endian,
    std::vector<Fixup>& fixups,
    std::set<size_t>& visited)
{
    if (ifd_rel == 0 || !visited.insert(ifd_rel).second) return;
    
    size_t p = ifd_rel;
    if (p + 2 > data_size) return;
    
    uint16_t count = read_u16(data + p, big_endian);
    if (count == 0 || count > 512) return;

    size_t next_ifd_pos = p + 2 + count * 12;
    if (next_ifd_pos + 4 <= data_size) {
        uint32_t next_val = read_u32(data + next_ifd_pos, big_endian);
        if (next_val >= insert_at_rel) fixups.push_back({next_ifd_pos, next_val});
        if (next_val > 0) {
            collect_ifd_fixups(data, data_size, next_val, insert_at_rel, big_endian, fixups, visited);
        }
    }

    for (uint16_t i = 0; i < count; i++) {
        size_t e = p + 2 + i * 12;
        if (e + 12 > data_size) break;
        
        uint16_t tag = read_u16(data + e, big_endian);
        uint16_t type = read_u16(data + e + 2, big_endian);
        uint32_t cnt = read_u32(data + e + 4, big_endian);
        size_t value_pos = e + 8;
        uint32_t off = read_u32(data + value_pos, big_endian);

        if (tag == 0x8769 || tag == 0x8825 || tag == 0xA005 || tag == 0x014A) {
            if (off >= insert_at_rel) fixups.push_back({value_pos, off});
            if (off > 0 && (tag != 0x014A || cnt == 1)) {
                collect_ifd_fixups(data, data_size, off, insert_at_rel, big_endian, fixups, visited);
            }
            continue;
        }

        const size_t data_len = type_to_data_length(type, cnt);
        if (data_len == 0) continue;
        
        if (off >= insert_at_rel) {
            fixups.push_back({value_pos, off});
        }
    }
}

} // namespace

std::vector<uint8_t> lpb_tiff_remove_makernotes(const uint8_t* tiff, size_t tiff_size, size_t ifd0_offset, bool big_endian)
{
    if (!tiff || ifd0_offset > tiff_size) return std::vector<uint8_t>();
    std::vector<size_t> entry_starts;
    std::vector<size_t> count_positions;
    std::vector<Fixup> fixups;
    std::set<size_t> visited;
    std::vector<DataRange> data_ranges;
    std::vector<DataRange> protected_ranges;
    
    collect_makernote_cleanup(tiff, tiff_size, ifd0_offset, big_endian, entry_starts, count_positions, fixups, visited, data_ranges, protected_ranges);
    
    if (entry_starts.empty()) {
        return std::vector<uint8_t>();
    }
    
    std::vector<DataRange> intervals;
    for (size_t s : entry_starts) {
        intervals.push_back({s, 12});
    }
    for (const auto& range : data_ranges) {
        if (range.length == 0) continue;
        const size_t range_end = range.start + range.length;
        bool exclusively_owned = true;
        for (const auto& protected_range : protected_ranges) {
            const size_t protected_end = protected_range.start + protected_range.length;
            if (range.start < protected_end && protected_range.start < range_end) {
                exclusively_owned = false;
                break;
            }
        }
        // A malformed TIFF may point MakerNote into another tag's value.
        // Removing that shared range would silently destroy unrelated EXIF;
        // remove only payload whose ownership is proven.
        if (exclusively_owned) intervals.push_back(range);
    }
    
    std::sort(intervals.begin(), intervals.end(), [](const DataRange& a, const DataRange& b) {
        return a.start < b.start;
    });
    
    std::vector<DataRange> merged;
    for (const auto& iv : intervals) {
        if (merged.empty() || iv.start > merged.back().start + merged.back().length) {
            merged.push_back(iv);
        } else {
            size_t new_end = std::max(merged.back().start + merged.back().length, iv.start + iv.length);
            merged.back().length = new_end - merged.back().start;
        }
    }
    
    size_t total_removed = 0;
    for (const auto& m : merged) {
        if (m.start > tiff_size || m.length > tiff_size - m.start || m.length > tiff_size - total_removed) return std::vector<uint8_t>();
        total_removed += m.length;
    }
    
    std::vector<uint8_t> cleaned(tiff_size - total_removed);
    size_t src = 0, dst = 0;
    for (const auto& m : merged) {
        size_t len = m.start - src;
        if (len > 0) {
            std::memcpy(cleaned.data() + dst, tiff + src, len);
            dst += len;
        }
        src = m.start + m.length;
    }
    if (src < tiff_size) {
        std::memcpy(cleaned.data() + dst, tiff + src, tiff_size - src);
    }
    
    auto map_abs = [&](size_t abs_pos) -> size_t {
        size_t shift = 0;
        for (const auto& m : merged) {
            if (m.start < abs_pos) shift += m.length; else break;
        }
        return abs_pos - shift;
    };
    
    for (const auto& fix : fixups) {
        uint32_t new_val = fix.value;
        for (const auto& m : merged) {
            if (m.start < fix.value) {
                if (m.length > new_val) return std::vector<uint8_t>();
                new_val -= static_cast<uint32_t>(m.length);
            } else break;
        }
        size_t mapped_pos = map_abs(fix.pos);
        if (mapped_pos + 4 <= cleaned.size()) {
            write_u32(cleaned.data() + mapped_pos, new_val, big_endian);
        }
    }
    
    for (size_t cp : count_positions) {
        size_t p = map_abs(cp);
        if (p + 2 <= cleaned.size()) {
            uint16_t cnt = read_u16(cleaned.data() + p, big_endian);
            if (cnt > 0) {
                write_u16(cleaned.data() + p, cnt - 1, big_endian);
            }
        }
    }
    
    return cleaned;
}

std::vector<uint8_t> lpb_tiff_insert_makernote(const uint8_t* tiff, size_t tiff_size, size_t ifd0_offset, size_t exif_ifd_offset, const uint8_t* makernote, size_t makernote_size, bool big_endian)
{
    if (!tiff || (makernote_size > 0 && !makernote) ||
        tiff_size > std::numeric_limits<uint32_t>::max()) return std::vector<uint8_t>();
    size_t target_ifd = exif_ifd_offset > 0 ? exif_ifd_offset : ifd0_offset;
    if (target_ifd > tiff_size || tiff_size - target_ifd < 2) return std::vector<uint8_t>();
    
    uint16_t entry_count = read_u16(tiff + target_ifd, big_endian);
    if (entry_count == 0 || entry_count > 256) return std::vector<uint8_t>();
    
    if (tiff_size - target_ifd - 2 < static_cast<size_t>(entry_count) * 12) return std::vector<uint8_t>();
    size_t insert_at = target_ifd + 2 + entry_count * 12;
    if (insert_at > tiff_size || tiff_size - insert_at < 4) { return std::vector<uint8_t>(); }
    
    size_t pad = (tiff_size % 2 == 0) ? 0 : 1;
    if (tiff_size > std::numeric_limits<uint32_t>::max() - 12 - pad ||
        makernote_size > std::numeric_limits<uint32_t>::max() ||
        makernote_size > std::numeric_limits<size_t>::max() - tiff_size - 12 - pad) return std::vector<uint8_t>();
    size_t mn_offset = tiff_size + 12 + pad;
    
    std::vector<Fixup> fixups;
    std::set<size_t> visited;
    collect_ifd_fixups(tiff, tiff_size, ifd0_offset, insert_at, big_endian, fixups, visited);
    if (target_ifd != ifd0_offset) {
        collect_ifd_fixups(tiff, tiff_size, target_ifd, insert_at, big_endian, fixups, visited);
    }
    for (const auto& fix : fixups) {
        if (fix.value > std::numeric_limits<uint32_t>::max() - 12) return std::vector<uint8_t>();
    }
    
    uint8_t entry[12];
    write_u16(entry, 0x927C, big_endian);
    write_u16(entry + 2, 7, big_endian);
    write_u32(entry + 4, (uint32_t)makernote_size, big_endian);
    write_u32(entry + 8, (uint32_t)mn_offset, big_endian);
    
    size_t grown_size = tiff_size + 12 + pad + makernote_size;
    std::vector<uint8_t> grown(grown_size);
    
    std::memcpy(grown.data(), tiff, insert_at);
    std::memcpy(grown.data() + insert_at, entry, 12);
    std::memcpy(grown.data() + insert_at + 12, tiff + insert_at, tiff_size - insert_at);
    
    size_t mn_insert_at = tiff_size + 12;
    if (pad > 0) grown[mn_insert_at] = 0;
    std::memcpy(grown.data() + mn_insert_at + pad, makernote, makernote_size);
    
    for (const auto& fix : fixups) {
        size_t new_pos = fix.pos < insert_at ? fix.pos : fix.pos + 12;
        if (new_pos + 4 <= grown_size) {
            write_u32(grown.data() + new_pos, fix.value + 12, big_endian);
        }
    }
    
    write_u16(grown.data() + target_ifd, entry_count + 1, big_endian);
    
    return grown;
}

uint32_t lpb_tiff_find_exif_ptr(const uint8_t* tiff, size_t tiff_size, size_t ifd_offset, bool big_endian)
{
    if (!tiff || ifd_offset > tiff_size || tiff_size - ifd_offset < 2) return 0;
    uint16_t count = read_u16(tiff + ifd_offset, big_endian);
    if (static_cast<size_t>(count) > (tiff_size - ifd_offset - 2) / 12) return 0;
    
    size_t p = ifd_offset + 2;
    for (uint16_t i = 0; i < count; i++) {
        if (p > tiff_size || tiff_size - p < 12) break;
        uint16_t tag = read_u16(tiff + p, big_endian);
        if (tag == 0x8769) {
            return read_u32(tiff + p + 8, big_endian);
        }
        p += 12;
    }
    return 0;
}




