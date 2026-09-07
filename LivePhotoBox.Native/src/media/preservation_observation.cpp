#include "livephotobox_native.h"
#include "foundation/internal.h"
#include "foundation/sha256.h"
#include "metadata/exif.h"
#include "containers/isobmff.h"
#include "binary/binary_io.h"
#include "binary/endian.h"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <fstream>
#include <string>
#include <string_view>
#include <vector>

namespace {

inline uint32_t read_le32u(const uint8_t* data) noexcept {
    return static_cast<uint32_t>(data[0])
        | (static_cast<uint32_t>(data[1]) << 8)
        | (static_cast<uint32_t>(data[2]) << 16)
        | (static_cast<uint32_t>(data[3]) << 24);
}

inline uint64_t read_be64u(const uint8_t* data) noexcept {
    return (static_cast<uint64_t>(read_be32u(data)) << 32)
        | static_cast<uint64_t>(read_be32u(data + 4));
}

void sha256_to_hex_upper(const uint8_t hash[32], char out_hex[LPB_POBS_SHA256_LEN]) {
    static const char hex_chars[] = "0123456789ABCDEF";
    for (int i = 0; i < 32; ++i) {
        out_hex[i * 2] = hex_chars[(hash[i] >> 4) & 0x0F];
        out_hex[i * 2 + 1] = hex_chars[hash[i] & 0x0F];
    }
    out_hex[64] = '\0';
}

bool read_file_binary(const char* path, std::vector<uint8_t>& out_data) {
    if (!path) return false;
    auto p = utf8_to_path(path);
    std::ifstream ifs(p, std::ios::binary | std::ios::ate);
    if (!ifs.is_open()) return false;
    const auto pos = ifs.tellg();
    if (pos < std::streampos(0)) return false;
    const auto size = static_cast<std::streamsize>(pos);
    if (size < 0 || static_cast<uint64_t>(size) > 1024ULL * 1024ULL * 1024ULL) return false;
    out_data.resize(static_cast<size_t>(size));
    ifs.seekg(0, std::ios::beg);
    ifs.read(reinterpret_cast<char*>(out_data.data()), size);
    return ifs.gcount() == size;
}

size_t get_tiff_type_size(uint16_t type) {
    switch (type) {
        case 1: return 1; // BYTE
        case 2: return 1; // ASCII
        case 3: return 2; // SHORT
        case 4: return 4; // LONG
        case 5: return 8; // RATIONAL
        case 6: return 1; // SBYTE
        case 7: return 1; // UNDEFINED
        case 8: return 2; // SSHORT
        case 9: return 4; // SLONG
        case 10: return 8; // SRATIONAL
        case 11: return 4; // FLOAT
        case 12: return 8; // DOUBLE
        case 13: return 4; // IFD
        case 16: return 8; // LONG8
        default: return 1;
    }
}

struct canonical_entry {
    uint16_t tag;
    uint16_t type;
    uint32_t count;
    std::vector<uint8_t> value_bytes;
};

void hash_canonical_entries(const std::vector<canonical_entry>& entries, char out_hex[LPB_POBS_SHA256_LEN]) {
    lpb::crypto::sha256_ctx sha;
    for (const auto& entry : entries) {
        uint8_t hdr[8];
        hdr[0] = static_cast<uint8_t>(entry.tag >> 8);
        hdr[1] = static_cast<uint8_t>(entry.tag);
        hdr[2] = static_cast<uint8_t>(entry.type >> 8);
        hdr[3] = static_cast<uint8_t>(entry.type);
        hdr[4] = static_cast<uint8_t>(entry.count >> 24);
        hdr[5] = static_cast<uint8_t>(entry.count >> 16);
        hdr[6] = static_cast<uint8_t>(entry.count >> 8);
        hdr[7] = static_cast<uint8_t>(entry.count);
        sha.update(hdr, 8);
        if (!entry.value_bytes.empty()) {
            sha.update(entry.value_bytes.data(), entry.value_bytes.size());
        }
    }
    uint8_t hash[32];
    sha.finalize(hash);
    sha256_to_hex_upper(hash, out_hex);
}

void observe_video_mdat(lpb_context* context, const char* media_path, lpb_preservation_observation* out) {
    auto p = utf8_to_path(media_path);
    std::ifstream ifs(p, std::ios::binary);
    if (!ifs.is_open()) return;

    ifs.seekg(0, std::ios::end);
    std::streampos total_file_size = ifs.tellg();
    if (total_file_size <= 0) return;
    ifs.seekg(0, std::ios::beg);

    lpb::crypto::sha256_ctx sha;
    bool found_mdat = false;
    std::vector<uint8_t> buffer(64 * 1024);

    while (ifs.tellg() < total_file_size && ifs.good()) {
        if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return;
        std::streampos box_start = ifs.tellg();
        uint8_t hdr[16];
        ifs.read(reinterpret_cast<char*>(hdr), 8);
        if (ifs.gcount() < 8) break;

        uint32_t size32 = read_be32u(hdr);
        uint64_t box_size = size32;
        size_t hdr_size = 8;
        if (size32 == 1) {
            ifs.read(reinterpret_cast<char*>(hdr + 8), 8);
            if (ifs.gcount() < 8) break;
            box_size = read_be64u(hdr + 8);
            hdr_size = 16;
        } else if (size32 == 0) {
            box_size = static_cast<uint64_t>(total_file_size - box_start);
        }

        if (box_size < hdr_size || static_cast<uint64_t>(box_start) + box_size > static_cast<uint64_t>(total_file_size)) {
            break;
        }

        bool is_mdat = (hdr[4] == 'm' && hdr[5] == 'd' && hdr[6] == 'a' && hdr[7] == 't');
        if (is_mdat) {
            found_mdat = true;
            uint64_t payload_remaining = box_size - hdr_size;
            while (payload_remaining > 0 && ifs.good()) {
                if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return;
                size_t to_read = static_cast<size_t>(std::min<uint64_t>(payload_remaining, buffer.size()));
                ifs.read(reinterpret_cast<char*>(buffer.data()), to_read);
                size_t bytes_read = static_cast<size_t>(ifs.gcount());
                if (bytes_read == 0) break;
                sha.update(buffer.data(), bytes_read);
                payload_remaining -= bytes_read;
            }
        } else {
            ifs.seekg(box_start + static_cast<std::streamoff>(box_size), std::ios::beg);
        }
    }

    if (found_mdat) {
        uint8_t hash[32];
        sha.finalize(hash);
        sha256_to_hex_upper(hash, out->video_mdat_sha256);
        out->flags |= LPB_POBS_HAS_VIDEO_MDAT;
    }
}

void observe_jpeg_codestream(const std::vector<uint8_t>& data, lpb_preservation_observation* out) {
    if (data.size() < 4 || data[0] != 0xFF || data[1] != 0xD8) {
        out->flags |= LPB_POBS_CODESTREAM_ERROR;
        return;
    }

    size_t p = 2;
    while (p + 4 <= data.size()) {
        if (data[p] != 0xFF) break;
        while (p < data.size() && data[p] == 0xFF) ++p;
        if (p >= data.size()) break;
        uint8_t marker = data[p++];
        if (marker == 0xDA) { // SOS
            if (p + 2 > data.size()) {
                out->flags |= LPB_POBS_CODESTREAM_ERROR;
                return;
            }
            uint16_t sos_len = read_be16u(data.data() + p);
            size_t scan_start = p + sos_len;
            if (scan_start > data.size()) {
                out->flags |= LPB_POBS_CODESTREAM_ERROR;
                return;
            }

            size_t scan_end = 0;
            for (size_t i = scan_start; i + 1 < data.size(); ++i) {
                if (data[i] == 0xFF) {
                    uint8_t next = data[i + 1];
                    if (next == 0x00 || (next >= 0xD0 && next <= 0xD7)) {
                        ++i;
                        continue;
                    }
                    if (next == 0xD9) { // EOI
                        scan_end = i;
                        break;
                    }
                }
            }

            if (scan_end <= scan_start) {
                out->flags |= LPB_POBS_CODESTREAM_ERROR;
                return;
            }

            uint8_t hash[32];
            lpb::crypto::sha256_buffer(data.data() + scan_start, scan_end - scan_start, hash);
            sha256_to_hex_upper(hash, out->image_codestream_sha256);
            return;
        }
        if (marker == 0xD9) break;
        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (p + 2 > data.size()) break;
        uint16_t len = read_be16u(data.data() + p);
        if (len < 2 || p + len > data.size()) break;
        p += len;
    }

    out->flags |= LPB_POBS_CODESTREAM_ERROR;
}

void observe_makernote(const std::vector<uint8_t>& bytes, lpb_source_protocol protocol_hint, lpb_preservation_observation* out) {
    if (bytes.empty()) return;

    if (protocol_hint == LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO) {
        const char sig[] = "Apple iOS\0";
        size_t p = 0;
        bool found = false;
        for (; p + 16 <= bytes.size(); ++p) {
            if (std::memcmp(bytes.data() + p, sig, 10) == 0 &&
                bytes[p + 10] == 0 && bytes[p + 11] == 1 &&
                bytes[p + 12] == 'M' && bytes[p + 13] == 'M') {
                found = true;
                break;
            }
        }

        if (!found) {
            out->flags |= LPB_POBS_MAKERNOTE_MALFORMED;
            return;
        }

        uint16_t count = read_be16u(bytes.data() + p + 14);
        if (count == 0 || count > 64 || p + 16 + static_cast<size_t>(count) * 12 > bytes.size()) {
            out->flags |= LPB_POBS_MAKERNOTE_MALFORMED;
            return;
        }

        size_t entries_start = p + 16;
        std::vector<canonical_entry> apple_entries;

        for (uint16_t i = 0; i < count; ++i) {
            size_t e = entries_start + static_cast<size_t>(i) * 12;
            uint16_t tag = read_be16u(bytes.data() + e);
            uint16_t type = read_be16u(bytes.data() + e + 2);
            uint32_t entry_count = read_be32u(bytes.data() + e + 4);
            uint32_t val_or_offset = read_be32u(bytes.data() + e + 8);

            // Exclude Apple live photo tags: 0x0011, 0x0017, 0x0025, 0x002b
            if (tag == 0x0011 || tag == 0x0017 || tag == 0x0025 || tag == 0x002b) {
                continue;
            }

            size_t type_size = get_tiff_type_size(type);
            uint64_t total_bytes = static_cast<uint64_t>(entry_count) * type_size;
            std::vector<uint8_t> val_bytes;

            if (total_bytes <= 4 && total_bytes > 0) {
                val_bytes.assign(bytes.data() + e + 8, bytes.data() + e + 8 + total_bytes);
            } else if (total_bytes > 4) {
                if (p + val_or_offset + total_bytes <= bytes.size()) {
                    val_bytes.assign(bytes.data() + p + val_or_offset, bytes.data() + p + val_or_offset + total_bytes);
                } else {
                    out->flags |= LPB_POBS_MAKERNOTE_MALFORMED;
                    return;
                }
            }

            apple_entries.push_back({ tag, type, entry_count, std::move(val_bytes) });
        }

        std::sort(apple_entries.begin(), apple_entries.end(), [](const auto& a, const auto& b) { return a.tag < b.tag; });
        hash_canonical_entries(apple_entries, out->makernote_nonlive_sha256);
        out->flags |= LPB_POBS_HAS_MAKERNOTE;
    } else {
        uint8_t hash[32];
        lpb::crypto::sha256_buffer(bytes.data(), bytes.size(), hash);
        sha256_to_hex_upper(hash, out->makernote_nonlive_sha256);
        out->flags |= LPB_POBS_HAS_MAKERNOTE;
    }
}

void observe_tiff_common(const uint8_t* tiff_data, size_t tiff_len, lpb_source_protocol protocol_hint, lpb_preservation_observation* out) {
    if (!tiff_data || tiff_len < 8) {
        out->flags |= LPB_POBS_EXIF_PARSE_ERROR;
        return;
    }

    bool is_big_endian = false;
    if (tiff_data[0] == 0x49 && tiff_data[1] == 0x49 && tiff_data[2] == 0x2A && tiff_data[3] == 0x00) {
        is_big_endian = false;
    } else if (tiff_data[0] == 0x4D && tiff_data[1] == 0x4D && tiff_data[2] == 0x00 && tiff_data[3] == 0x2A) {
        is_big_endian = true;
    } else {
        out->flags |= LPB_POBS_EXIF_PARSE_ERROR;
        return;
    }

    uint32_t ifd0_offset = is_big_endian ? read_be32u(tiff_data + 4) : read_le32u(tiff_data + 4);
    if (ifd0_offset < 8 || ifd0_offset >= tiff_len) {
        out->flags |= LPB_POBS_EXIF_PARSE_ERROR;
        return;
    }

    tiff_ifd ifd0{};
    if (!parse_ifd(tiff_data, tiff_len, 0, ifd0_offset, is_big_endian, &ifd0)) {
        out->flags |= LPB_POBS_EXIF_PARSE_ERROR;
        return;
    }

    uint32_t exif_ifd_offset = 0;
    uint32_t gps_ifd_offset = 0;
    std::vector<canonical_entry> ifd0_entries;

    for (const auto& entry : ifd0.entries) {
        if (entry.tag == 0x8769) {
            exif_ifd_offset = entry.value_offset;
            continue;
        }
        if (entry.tag == 0x8825) {
            gps_ifd_offset = entry.value_offset;
            continue;
        }

        size_t type_size = get_tiff_type_size(entry.type);
        uint64_t total_bytes = static_cast<uint64_t>(entry.count) * type_size;
        std::vector<uint8_t> val_bytes;

        if (total_bytes <= 4 && total_bytes > 0) {
            if (entry.absolute_pos + 8 + total_bytes <= tiff_len) {
                val_bytes.assign(tiff_data + entry.absolute_pos + 8, tiff_data + entry.absolute_pos + 8 + total_bytes);
            }
        } else if (total_bytes > 4) {
            if (static_cast<uint64_t>(entry.value_offset) + total_bytes <= tiff_len) {
                val_bytes.assign(tiff_data + entry.value_offset, tiff_data + entry.value_offset + total_bytes);
            }
        }

        if (entry.tag == 0x0112 && val_bytes.size() >= 2) {
            out->orientation = is_big_endian ? (val_bytes[0] << 8 | val_bytes[1]) : (val_bytes[0] | (val_bytes[1] << 8));
        }

        ifd0_entries.push_back({ entry.tag, entry.type, entry.count, std::move(val_bytes) });
    }

    std::sort(ifd0_entries.begin(), ifd0_entries.end(), [](const auto& a, const auto& b) { return a.tag < b.tag; });
    hash_canonical_entries(ifd0_entries, out->exif_ifd0_nonptr_sha256);
    out->flags |= LPB_POBS_HAS_EXIF;

    if (exif_ifd_offset > 0 && exif_ifd_offset < tiff_len) {
        tiff_ifd exif_ifd{};
        if (parse_ifd(tiff_data, tiff_len, 0, exif_ifd_offset, is_big_endian, &exif_ifd)) {
            std::vector<canonical_entry> exif_entries;
            std::vector<uint8_t> makernote_bytes;
            bool has_makernote = false;

            for (const auto& entry : exif_ifd.entries) {
                size_t type_size = get_tiff_type_size(entry.type);
                uint64_t total_bytes = static_cast<uint64_t>(entry.count) * type_size;
                std::vector<uint8_t> val_bytes;

                if (total_bytes <= 4 && total_bytes > 0) {
                    if (entry.absolute_pos + 8 + total_bytes <= tiff_len) {
                        val_bytes.assign(tiff_data + entry.absolute_pos + 8, tiff_data + entry.absolute_pos + 8 + total_bytes);
                    }
                } else if (total_bytes > 4) {
                    if (static_cast<uint64_t>(entry.value_offset) + total_bytes <= tiff_len) {
                        val_bytes.assign(tiff_data + entry.value_offset, tiff_data + entry.value_offset + total_bytes);
                    }
                }

                if (entry.tag == 0x9003 && !val_bytes.empty()) {
                    size_t copy_len = std::min<size_t>(val_bytes.size(), sizeof(out->datetime_original) - 1);
                    std::memcpy(out->datetime_original, val_bytes.data(), copy_len);
                    out->datetime_original[copy_len] = '\0';
                    while (copy_len > 0 && (out->datetime_original[copy_len - 1] == '\0' || out->datetime_original[copy_len - 1] == ' ')) {
                        out->datetime_original[--copy_len] = '\0';
                    }
                }

                if (entry.tag == 0x927C) {
                    makernote_bytes = val_bytes;
                    has_makernote = true;
                    continue;
                }

                exif_entries.push_back({ entry.tag, entry.type, entry.count, std::move(val_bytes) });
            }

            std::sort(exif_entries.begin(), exif_entries.end(), [](const auto& a, const auto& b) { return a.tag < b.tag; });
            hash_canonical_entries(exif_entries, out->exif_exif_ifd_sha256);

            if (has_makernote && !makernote_bytes.empty()) {
                observe_makernote(makernote_bytes, protocol_hint, out);
            }
        } else {
            out->flags |= LPB_POBS_EXIF_PARSE_ERROR;
        }
    }

    if (gps_ifd_offset > 0 && gps_ifd_offset < tiff_len) {
        tiff_ifd gps_ifd{};
        if (parse_ifd(tiff_data, tiff_len, 0, gps_ifd_offset, is_big_endian, &gps_ifd)) {
            std::vector<canonical_entry> gps_entries;
            for (const auto& entry : gps_ifd.entries) {
                size_t type_size = get_tiff_type_size(entry.type);
                uint64_t total_bytes = static_cast<uint64_t>(entry.count) * type_size;
                std::vector<uint8_t> val_bytes;
                if (total_bytes <= 4 && total_bytes > 0) {
                    if (entry.absolute_pos + 8 + total_bytes <= tiff_len) {
                        val_bytes.assign(tiff_data + entry.absolute_pos + 8, tiff_data + entry.absolute_pos + 8 + total_bytes);
                    }
                } else if (total_bytes > 4) {
                    if (static_cast<uint64_t>(entry.value_offset) + total_bytes <= tiff_len) {
                        val_bytes.assign(tiff_data + entry.value_offset, tiff_data + entry.value_offset + total_bytes);
                    }
                }
                gps_entries.push_back({ entry.tag, entry.type, entry.count, std::move(val_bytes) });
            }

            if (!gps_entries.empty()) {
                std::sort(gps_entries.begin(), gps_entries.end(), [](const auto& a, const auto& b) { return a.tag < b.tag; });
                hash_canonical_entries(gps_entries, out->gps_sha256);
                out->flags |= LPB_POBS_HAS_GPS;
            }
        }
    }
}

void observe_jpeg_exif(const std::vector<uint8_t>& data, lpb_source_protocol protocol_hint, lpb_preservation_observation* out) {
    if (data.size() < 4 || data[0] != 0xFF || data[1] != 0xD8) return;

    size_t p = 2;
    const uint8_t exif_header[] = { 'E', 'x', 'i', 'f', 0, 0 };
    while (p + 4 <= data.size()) {
        if (data[p] != 0xFF) break;
        while (p < data.size() && data[p] == 0xFF) ++p;
        if (p >= data.size()) break;
        uint8_t marker = data[p++];
        if (marker == 0xDA || marker == 0xD9) break;
        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (p + 2 > data.size()) break;
        uint16_t len = read_be16u(data.data() + p);
        if (len < 2 || p + len > data.size()) break;

        if (marker == 0xE1 && len >= 8) { // APP1
            if (std::memcmp(data.data() + p + 2, exif_header, 6) == 0) {
                const uint8_t* tiff_ptr = data.data() + p + 8;
                size_t tiff_len = len - 8;
                observe_tiff_common(tiff_ptr, tiff_len, protocol_hint, out);
                return;
            }
        }
        p += len;
    }
}

void observe_jpeg_icc(const std::vector<uint8_t>& data, lpb_preservation_observation* out) {
    if (data.size() < 4 || data[0] != 0xFF || data[1] != 0xD8) return;

    size_t p = 2;
    std::vector<std::vector<uint8_t>> chunks;
    const char icc_header[] = "ICC_PROFILE\0";

    while (p + 4 <= data.size()) {
        if (data[p] != 0xFF) break;
        while (p < data.size() && data[p] == 0xFF) ++p;
        if (p >= data.size()) break;
        uint8_t marker = data[p++];
        if (marker == 0xDA || marker == 0xD9) break;
        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (p + 2 > data.size()) break;
        uint16_t len = read_be16u(data.data() + p);
        if (len < 2 || p + len > data.size()) break;

        if (marker == 0xE2) { // APP2
            if (len >= 14 && p + 14 <= data.size() &&
                std::memcmp(data.data() + p + 2, icc_header, 12) == 0) {
                size_t payload_len = len - 14;
                std::vector<uint8_t> chunk(data.begin() + p + 14, data.begin() + p + 14 + payload_len);
                chunks.push_back(std::move(chunk));
            }
        }
        p += len;
    }

    if (!chunks.empty()) {
        lpb::crypto::sha256_ctx sha;
        for (const auto& c : chunks) {
            sha.update(c.data(), c.size());
        }
        uint8_t hash[32];
        sha.finalize(hash);
        sha256_to_hex_upper(hash, out->icc_sha256);
        out->flags |= LPB_POBS_HAS_ICC;
    }
}

void observe_jpeg_extended_xmp(const std::vector<uint8_t>& data, lpb_preservation_observation* out) {
    if (data.size() < 35) return;
    const char ext_xmp_header[] = "http://ns.adobe.com/xmp/extension/\0";
    const size_t header_len = 35;

    size_t p = 2;
    std::vector<uint8_t> combined;
    bool found_any = false;

    while (p + 4 <= data.size()) {
        if (data[p] != 0xFF) break;
        while (p < data.size() && data[p] == 0xFF) ++p;
        if (p >= data.size()) break;
        uint8_t marker = data[p++];
        if (marker == 0xDA || marker == 0xD9) break;
        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (p + 2 > data.size()) break;
        uint16_t len = read_be16u(data.data() + p);
        if (len < 2 || p + len > data.size()) break;

        if (marker == 0xE1 && len >= header_len + 2) {
            if (std::memcmp(data.data() + p + 2, ext_xmp_header, header_len) == 0) {
                found_any = true;
                size_t payload_start = p + 2 + header_len;
                size_t payload_len = len - 2 - header_len;
                combined.insert(combined.end(), data.begin() + payload_start, data.begin() + payload_start + payload_len);
            }
        }
        p += len;
    }

    if (found_any && !combined.empty()) {
        uint8_t hash[32];
        lpb::crypto::sha256_buffer(combined.data(), combined.size(), hash);
        sha256_to_hex_upper(hash, out->extended_xmp_sha256);
        out->flags |= LPB_POBS_HAS_EXTENDED_XMP;
    }
}

static bool is_rdf_or_toolkit_syntax(std::string_view name, std::string_view uri, std::string_view prefix) {
    auto equals_icase = [](std::string_view a, std::string_view b) {
        if (a.size() != b.size()) return false;
        for (size_t i = 0; i < a.size(); ++i) {
            if (std::tolower(static_cast<unsigned char>(a[i])) !=
                std::tolower(static_cast<unsigned char>(b[i]))) return false;
        }
        return true;
    };
    auto contains_icase = [](std::string_view str, std::string_view sub) {
        if (sub.empty() || str.size() < sub.size()) return false;
        for (size_t i = 0; i <= str.size() - sub.size(); ++i) {
            bool match = true;
            for (size_t j = 0; j < sub.size(); ++j) {
                if (std::tolower(static_cast<unsigned char>(str[i + j])) !=
                    std::tolower(static_cast<unsigned char>(sub[j]))) {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    };

    if (contains_icase(uri, "1999/02/22-rdf-syntax-ns") || equals_icase(prefix, "rdf")) {
        return true;
    }
    if (contains_icase(uri, "adobe:ns:meta") || equals_icase(prefix, "x")) {
        return true;
    }
    if (equals_icase(name, "RDF") ||
        equals_icase(name, "Description") ||
        equals_icase(name, "Seq") ||
        equals_icase(name, "Bag") ||
        equals_icase(name, "Alt") ||
        equals_icase(name, "li") ||
        equals_icase(name, "xmpmeta") ||
        equals_icase(name, "xmptk") ||
        equals_icase(name, "about") ||
        equals_icase(name, "parseType") ||
        equals_icase(name, "resource") ||
        equals_icase(name, "nodeID") ||
        equals_icase(name, "ID") ||
        equals_icase(name, "datatype")) {
        return true;
    }
    return false;
}

static bool is_live_protocol_property(std::string_view name, std::string_view uri) {
    auto equals_icase = [](std::string_view a, std::string_view b) {
        if (a.size() != b.size()) return false;
        for (size_t i = 0; i < a.size(); ++i) {
            if (std::tolower(static_cast<unsigned char>(a[i])) !=
                std::tolower(static_cast<unsigned char>(b[i]))) return false;
        }
        return true;
    };
    auto contains_icase = [](std::string_view str, std::string_view sub) {
        if (sub.empty() || str.size() < sub.size()) return false;
        for (size_t i = 0; i <= str.size() - sub.size(); ++i) {
            bool match = true;
            for (size_t j = 0; j < sub.size(); ++j) {
                if (std::tolower(static_cast<unsigned char>(str[i + j])) !=
                    std::tolower(static_cast<unsigned char>(sub[j]))) {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    };

    if (contains_icase(uri, "google.com/photos/1.0/container") ||
        contains_icase(uri, "google.com/photos/1.0/creations") ||
        contains_icase(uri, "google.com/photos/cr/1.0") ||
        contains_icase(uri, "google.com/photos/1.0/motion") ||
        contains_icase(uri, "oplus.com") ||
        contains_icase(uri, "oppo.com") ||
        contains_icase(uri, "vivo.com") ||
        contains_icase(uri, "vivoinc.com") ||
        contains_icase(uri, "xiaomi") ||
        contains_icase(uri, "com.samsung.android.photo") ||
        contains_icase(uri, "samsung.com/photo") ||
        contains_icase(uri, "apple.com/livephoto") ||
        contains_icase(uri, "ns.apple.com/livephoto") ||
        contains_icase(uri, "livephotobox")) {
        return true;
    }

    if (equals_icase(name, "Directory") ||
        equals_icase(name, "Item") ||
        equals_icase(name, "Mime") ||
        equals_icase(name, "Semantic") ||
        equals_icase(name, "Length") ||
        equals_icase(name, "Padding")) {
        return true;
    }

    if (equals_icase(name, "MotionPhoto") ||
        equals_icase(name, "MotionPhotoVersion") ||
        equals_icase(name, "MotionPhotoPresentationTimestampUs") ||
        equals_icase(name, "MicroVideo") ||
        equals_icase(name, "MicroVideoVersion") ||
        equals_icase(name, "MicroVideoOffset") ||
        equals_icase(name, "MicroVideoPresentationTimestampUs") ||
        equals_icase(name, "SpecialTypeID") ||
        equals_icase(name, "MovingPhoto") ||
        equals_icase(name, "LivePhoto") ||
        equals_icase(name, "LivePhotoBox") ||
        equals_icase(name, "OLivePhotoVersion") ||
        equals_icase(name, "MotionPhotoOwner") ||
        equals_icase(name, "VideoLength") ||
        equals_icase(name, "VMotionPhotoVersion") ||
        equals_icase(name, "VMotionPhotoSource") ||
        equals_icase(name, "VMediaKitVersion") ||
        equals_icase(name, "MediaGroupUUID") ||
        equals_icase(name, "QuickTimeTrackID")) {
        return true;
    }

    if (contains_icase(name, "MotionPhoto") ||
        contains_icase(name, "MicroVideo") ||
        contains_icase(name, "OLivePhoto") ||
        contains_icase(name, "VMotionPhoto") ||
        contains_icase(name, "VMediaKit") ||
        contains_icase(name, "LivePhotoBox")) {
        return true;
    }

    return false;
}

static bool is_gainmap_meta(std::string_view name, std::string_view uri) {
    auto contains_icase = [](std::string_view str, std::string_view sub) {
        if (sub.empty() || str.size() < sub.size()) return false;
        for (size_t i = 0; i <= str.size() - sub.size(); ++i) {
            bool match = true;
            for (size_t j = 0; j < sub.size(); ++j) {
                if (std::tolower(static_cast<unsigned char>(str[i + j])) !=
                    std::tolower(static_cast<unsigned char>(sub[j]))) {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    };

    return contains_icase(uri, "hdrgm") ||
           contains_icase(uri, "gainmap") ||
           contains_icase(name, "hdrgm") ||
           contains_icase(name, "gainmap");
}

void observe_xmp_common(std::string_view xml_text, lpb_preservation_observation* out) {
    if (xml_text.empty()) return;

    size_t xmp_start = xml_text.find("<x:xmpmeta");
    if (xmp_start == std::string_view::npos) xmp_start = xml_text.find("<rdf:RDF");
    if (xmp_start == std::string_view::npos) {
        return;
    }

    size_t xmp_end = xml_text.find("</x:xmpmeta>", xmp_start);
    if (xmp_end == std::string_view::npos) xmp_end = xml_text.find("</rdf:RDF>", xmp_start);
    if (xmp_end == std::string_view::npos) {
        out->flags |= LPB_POBS_XMP_MALFORMED;
        return;
    }

    out->flags |= LPB_POBS_HAS_XMP;

    std::vector<std::pair<std::string, std::string>> ns_bindings;
    size_t pos = xmp_start;
    while ((pos = xml_text.find("xmlns:", pos)) != std::string_view::npos && pos < xmp_end) {
        pos += 6;
        size_t eq = xml_text.find('=', pos);
        if (eq == std::string_view::npos || eq >= xmp_end) break;
        std::string prefix(xml_text.substr(pos, eq - pos));
        size_t q1 = xml_text.find_first_of("\"'", eq);
        if (q1 == std::string_view::npos || q1 >= xmp_end) break;
        char quote = xml_text[q1];
        size_t q2 = xml_text.find(quote, q1 + 1);
        if (q2 == std::string_view::npos || q2 >= xmp_end) break;
        std::string uri(xml_text.substr(q1 + 1, q2 - q1 - 1));
        ns_bindings.push_back({ prefix, uri });
        pos = q2 + 1;
    }

    auto resolve_prefix = [&](std::string_view prefix) -> std::string_view {
        for (const auto& b : ns_bindings) {
            if (b.first == prefix) return b.second;
        }
        return {};
    };

    std::vector<std::string> canonical_props;
    pos = xmp_start;

    while (pos < xmp_end) {
        size_t tag_open = xml_text.find('<', pos);
        if (tag_open == std::string_view::npos || tag_open >= xmp_end) break;
        if (tag_open + 1 < xmp_end && (xml_text[tag_open + 1] == '/' || xml_text[tag_open + 1] == '?' || xml_text[tag_open + 1] == '!')) {
            pos = tag_open + 1;
            continue;
        }

        size_t tag_close = xml_text.find('>', tag_open);
        if (tag_close == std::string_view::npos || tag_close >= xmp_end) break;

        std::string_view tag_content = xml_text.substr(tag_open + 1, tag_close - tag_open - 1);
        bool is_self_closing = (!tag_content.empty() && tag_content.back() == '/');
        if (is_self_closing) tag_content = tag_content.substr(0, tag_content.size() - 1);

        size_t space_pos = tag_content.find_first_of(" \t\r\n");
        std::string_view elem_name = (space_pos == std::string_view::npos) ? tag_content : tag_content.substr(0, space_pos);

        size_t attr_p = (space_pos == std::string_view::npos) ? tag_content.size() : space_pos;
        while (attr_p < tag_content.size()) {
            while (attr_p < tag_content.size() && std::isspace(static_cast<unsigned char>(tag_content[attr_p]))) ++attr_p;
            if (attr_p >= tag_content.size()) break;
            size_t eq_pos = tag_content.find('=', attr_p);
            if (eq_pos == std::string_view::npos) break;
            std::string_view attr_name = tag_content.substr(attr_p, eq_pos - attr_p);
            while (!attr_name.empty() && std::isspace(static_cast<unsigned char>(attr_name.back()))) attr_name.remove_suffix(1);
            size_t q_start = tag_content.find_first_of("\"'", eq_pos);
            if (q_start == std::string_view::npos) break;
            char q = tag_content[q_start];
            size_t q_end = tag_content.find(q, q_start + 1);
            if (q_end == std::string_view::npos) break;
            std::string_view attr_val = tag_content.substr(q_start + 1, q_end - q_start - 1);
            attr_p = q_end + 1;

            if (attr_name.rfind("xmlns", 0) == 0) continue;

            size_t colon = attr_name.find(':');
            std::string_view prefix = (colon == std::string_view::npos) ? "" : attr_name.substr(0, colon);
            std::string_view local = (colon == std::string_view::npos) ? attr_name : attr_name.substr(colon + 1);
            std::string_view uri = resolve_prefix(prefix);

            if (is_gainmap_meta(local, uri) || is_gainmap_meta(attr_val, uri) || is_gainmap_meta(prefix, uri)) {
                out->flags |= LPB_POBS_HAS_GAINMAP_META;
            }

            if (is_rdf_or_toolkit_syntax(local, uri, prefix) ||
                is_live_protocol_property(local, uri) ||
                is_live_protocol_property(attr_name, uri)) {
                continue;
            }

            std::string prop(uri);
            prop.push_back(':');
            prop.append(local);
            prop.push_back('=');
            prop.append(attr_val);
            canonical_props.push_back(std::move(prop));
        }

        if (!is_self_closing) {
            std::string close_tag = "</" + std::string(elem_name) + ">";
            size_t close_pos = xml_text.find(close_tag, tag_close + 1);
            if (close_pos != std::string_view::npos && close_pos < xmp_end) {
                std::string_view inner = xml_text.substr(tag_close + 1, close_pos - tag_close - 1);
                if (inner.find('<') == std::string_view::npos) {
                    while (!inner.empty() && std::isspace(static_cast<unsigned char>(inner.front()))) inner.remove_prefix(1);
                    while (!inner.empty() && std::isspace(static_cast<unsigned char>(inner.back()))) inner.remove_suffix(1);
                    if (!inner.empty()) {
                        size_t colon = elem_name.find(':');
                        std::string_view prefix = (colon == std::string_view::npos) ? "" : elem_name.substr(0, colon);
                        std::string_view local = (colon == std::string_view::npos) ? elem_name : elem_name.substr(colon + 1);
                        std::string_view uri = resolve_prefix(prefix);

                        if (is_gainmap_meta(local, uri) || is_gainmap_meta(inner, uri) || is_gainmap_meta(prefix, uri)) {
                            out->flags |= LPB_POBS_HAS_GAINMAP_META;
                        }

                        if (!is_rdf_or_toolkit_syntax(local, uri, prefix) &&
                            !is_live_protocol_property(local, uri) &&
                            !is_live_protocol_property(elem_name, uri)) {
                            std::string prop(uri);
                            prop.push_back(':');
                            prop.append(local);
                            prop.push_back('=');
                            prop.append(inner);
                            canonical_props.push_back(std::move(prop));
                        }
                    }
                }
            }
        }

        pos = tag_close + 1;
    }

    if (canonical_props.empty()) {
        out->xmp_nonprotocol_sha256[0] = '\0';
        return;
    }

    std::sort(canonical_props.begin(), canonical_props.end());
    canonical_props.erase(std::unique(canonical_props.begin(), canonical_props.end()), canonical_props.end());

    lpb::crypto::sha256_ctx sha;
    for (const auto& p : canonical_props) {
        sha.update(reinterpret_cast<const uint8_t*>(p.data()), p.size());
        sha.update(reinterpret_cast<const uint8_t*>("\n"), 1);
    }
    uint8_t hash[32];
    sha.finalize(hash);
    sha256_to_hex_upper(hash, out->xmp_nonprotocol_sha256);
}

void observe_jpeg_xmp(const std::vector<uint8_t>& data, lpb_preservation_observation* out) {
    if (data.size() < 4 || data[0] != 0xFF || data[1] != 0xD8) return;

    size_t p = 2;
    const char xmp_header[] = "http://ns.adobe.com/xap/1.0/\0";
    const size_t header_len = 29;

    while (p + 4 <= data.size()) {
        if (data[p] != 0xFF) break;
        while (p < data.size() && data[p] == 0xFF) ++p;
        if (p >= data.size()) break;
        uint8_t marker = data[p++];
        if (marker == 0xDA || marker == 0xD9) break;
        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (p + 2 > data.size()) break;
        uint16_t len = read_be16u(data.data() + p);
        if (len < 2 || p + len > data.size()) break;

        if (marker == 0xE1 && len >= header_len + 2) {
            if (std::memcmp(data.data() + p + 2, xmp_header, header_len) == 0) {
                size_t payload_start = p + 2 + header_len;
                size_t payload_len = len - 2 - header_len;
                std::string_view xml_text(reinterpret_cast<const char*>(data.data() + payload_start), payload_len);
                observe_xmp_common(xml_text, out);
                return;
            }
        }
        p += len;
    }
}

// HEIC ISOBMFF helpers
struct isobmff_box {
    std::string type;
    size_t start{};
    size_t size{};
    size_t header_size{};
    size_t body_start{};
    size_t body_size{};
};

std::vector<isobmff_box> parse_boxes(const uint8_t* data, size_t start, size_t end) {
    std::vector<isobmff_box> boxes;
    size_t p = start;
    while (p + 8 <= end) {
        isobmff_box_header hdr{};
        if (!try_read_box_header(data, p, end, hdr)) break;
        if (hdr.size < 8 || p + hdr.size > end) break;
        char type_str[5]{};
        std::memcpy(type_str, data + p + 4, 4);
        isobmff_box box;
        box.type = type_str;
        box.start = p;
        box.size = hdr.size;
        box.header_size = hdr.header_size;
        box.body_start = p + hdr.header_size;
        box.body_size = hdr.size - hdr.header_size;
        boxes.push_back(std::move(box));
        p += hdr.size;
    }
    return boxes;
}

bool extract_heic_item_payload(const uint8_t* data, size_t data_size, size_t iloc_body, size_t iloc_size, uint32_t target_item_id, std::vector<uint8_t>& out_payload) {
    if (iloc_size < 8) return false;
    size_t p = iloc_body;
    size_t end = iloc_body + iloc_size;

    uint8_t ver = data[p++];
    p += 3; // flags

    if (p + 2 > end) return false;
    uint8_t b1 = data[p++];
    uint8_t b2 = data[p++];
    uint8_t offset_size = (b1 >> 4) & 0x0F;
    uint8_t length_size = b1 & 0x0F;
    uint8_t base_offset_size = (b2 >> 4) & 0x0F;
    uint8_t index_size = (ver == 1 || ver == 2) ? (b2 & 0x0F) : 0;

    auto valid_sz = [](uint8_t s) { return s == 0 || s == 4 || s == 8; };
    if (!valid_sz(offset_size) || !valid_sz(length_size) || !valid_sz(base_offset_size) || !valid_sz(index_size)) return false;

    uint32_t item_count = 0;
    if (ver < 2) {
        if (p + 2 > end) return false;
        item_count = read_be16u(data + p);
        p += 2;
    } else {
        if (p + 4 > end) return false;
        item_count = read_be32u(data + p);
        p += 4;
    }

    auto read_uint_sz = [&](uint8_t sz) -> uint64_t {
        uint64_t v = 0;
        for (uint8_t i = 0; i < sz; ++i) {
            v = (v << 8) | data[p++];
        }
        return v;
    };

    for (uint32_t i = 0; i < item_count && p < end; ++i) {
        uint32_t item_id = (ver < 2) ? read_be16u(data + p) : read_be32u(data + p);
        p += (ver < 2) ? 2 : 4;

        if (ver == 1 || ver == 2) {
            if (p + 2 > end) return false;
            p += 2; // construction_method
        }
        if (p + 2 > end) return false;
        p += 2; // data_reference_index

        if (p + base_offset_size > end) return false;
        uint64_t base_offset = read_uint_sz(base_offset_size);

        if (p + 2 > end) return false;
        uint16_t extent_count = read_be16u(data + p);
        p += 2;

        if (item_id == target_item_id) {
            if (extent_count != 1) return false; // Multi-extent fail closed
            if ((ver == 1 || ver == 2) && index_size > 0) p += index_size;
            if (p + offset_size + length_size > end) return false;
            uint64_t extent_offset = read_uint_sz(offset_size);
            uint64_t extent_length = read_uint_sz(length_size);
            uint64_t abs_offset = base_offset + extent_offset;
            if (extent_length == 0 || abs_offset > data_size || extent_length > data_size - abs_offset) return false;
            out_payload.assign(data + abs_offset, data + abs_offset + extent_length);
            return true;
        }

        for (uint16_t e = 0; e < extent_count && p <= end; ++e) {
            if ((ver == 1 || ver == 2) && index_size > 0) p += index_size;
            p += offset_size + length_size;
        }
    }
    return false;
}

uint32_t extract_heic_primary_item_id(const uint8_t* data, const std::vector<isobmff_box>& meta_children) {
    const isobmff_box* pitm = nullptr;
    for (const auto& b : meta_children) {
        if (b.type == "pitm") {
            if (pitm) return 0; // Duplicate pitm rejected
            pitm = &b;
        }
    }
    if (!pitm || pitm->body_size < 4) return 0;
    uint8_t ver = data[pitm->body_start];
    if (ver == 0 && pitm->body_size >= 6) {
        return read_be16u(data + pitm->body_start + 4);
    } else if (ver == 1 && pitm->body_size >= 8) {
        return read_be32u(data + pitm->body_start + 4);
    }
    return 0;
}

void observe_heic_codestream(const std::vector<uint8_t>& data, const std::vector<isobmff_box>& top_boxes, const std::vector<isobmff_box>& meta_children, uint32_t primary_id, lpb_preservation_observation* out) {
    if (primary_id == 0) {
        for (const auto& b : top_boxes) {
            if (b.type == "mdat" && b.size >= 8) {
                uint8_t hash[32];
                lpb::crypto::sha256_buffer(data.data() + b.body_start, b.body_size, hash);
                sha256_to_hex_upper(hash, out->image_codestream_sha256);
                return;
            }
        }
        out->flags |= LPB_POBS_CODESTREAM_ERROR;
        return;
    }

    const isobmff_box* iloc = nullptr;
    for (const auto& b : meta_children) {
        if (b.type == "iloc") {
            if (iloc) {
                out->flags |= LPB_POBS_CODESTREAM_ERROR;
                return;
            }
            iloc = &b;
        }
    }
    if (!iloc) {
        out->flags |= LPB_POBS_CODESTREAM_ERROR;
        return;
    }

    std::vector<uint8_t> payload;
    if (extract_heic_item_payload(data.data(), data.size(), iloc->body_start, iloc->body_size, primary_id, payload)) {
        uint8_t hash[32];
        lpb::crypto::sha256_buffer(payload.data(), payload.size(), hash);
        sha256_to_hex_upper(hash, out->image_codestream_sha256);
    } else {
        out->flags |= LPB_POBS_CODESTREAM_ERROR;
    }
}

void observe_heic_icc(const std::vector<uint8_t>& data, const std::vector<isobmff_box>& meta_children, uint32_t primary_id, lpb_preservation_observation* out) {
    const isobmff_box* iprp = nullptr;
    for (const auto& b : meta_children) {
        if (b.type == "iprp") {
            if (iprp) return; // Duplicate iprp
            iprp = &b;
        }
    }
    if (!iprp || iprp->body_size < 8) return;

    auto iprp_children = parse_boxes(data.data(), iprp->body_start, iprp->start + iprp->size);
    const isobmff_box* ipco = nullptr;
    const isobmff_box* ipma = nullptr;
    for (const auto& b : iprp_children) {
        if (b.type == "ipco") {
            if (ipco) return;
            ipco = &b;
        } else if (b.type == "ipma") {
            if (ipma) return;
            ipma = &b;
        }
    }
    if (!ipco || ipco->body_size < 8) return;

    auto prop_boxes = parse_boxes(data.data(), ipco->body_start, ipco->start + ipco->size);
    std::vector<std::pair<size_t, const isobmff_box*>> colr_props;
    for (size_t i = 0; i < prop_boxes.size(); ++i) {
        if (prop_boxes[i].type == "colr") {
            colr_props.push_back({ i + 1, &prop_boxes[i] });
        }
    }

    if (colr_props.empty()) return;

    const isobmff_box* target_colr = nullptr;
    if (colr_props.size() == 1) {
        target_colr = colr_props[0].second;
    } else if (primary_id != 0 && ipma && ipma->body_size >= 8) {
        size_t p = ipma->body_start;
        size_t end = ipma->start + ipma->size;
        uint8_t ver = data[p++];
        int flags = (data[p] << 16) | (data[p + 1] << 8) | data[p + 2];
        p += 3;
        bool is_large_index = (flags & 1) != 0;

        if (p + 4 <= end) {
            uint32_t entry_count = read_be32u(data.data() + p);
            p += 4;
            std::vector<size_t> matched_indices;
            for (uint32_t i = 0; i < entry_count && p < end; ++i) {
                uint32_t item_id = (ver < 1) ? read_be16u(data.data() + p) : read_be32u(data.data() + p);
                p += (ver < 1) ? 2 : 4;
                if (p >= end) break;
                uint8_t assoc_count = data[p++];
                for (uint8_t a = 0; a < assoc_count && p < end; ++a) {
                    size_t prop_index = 0;
                    if (is_large_index) {
                        if (p + 2 > end) break;
                        prop_index = read_be16u(data.data() + p) & 0x7FFF;
                        p += 2;
                    } else {
                        prop_index = data[p++] & 0x7F;
                    }
                    if (item_id == primary_id) {
                        matched_indices.push_back(prop_index);
                    }
                }
            }

            std::vector<const isobmff_box*> matching_colrs;
            for (const auto& cp : colr_props) {
                if (std::find(matched_indices.begin(), matched_indices.end(), cp.first) != matched_indices.end()) {
                    matching_colrs.push_back(cp.second);
                }
            }
            if (matching_colrs.size() == 1) {
                target_colr = matching_colrs[0];
            } else {
                out->flags |= LPB_POBS_ICC_PARSE_ERROR;
                return;
            }
        }
    }

    if (target_colr) {
        uint8_t hash[32];
        lpb::crypto::sha256_buffer(data.data() + target_colr->start, target_colr->size, hash);
        sha256_to_hex_upper(hash, out->icc_sha256);
        out->flags |= LPB_POBS_HAS_ICC;
    }
}

void observe_heic_aux(const std::vector<uint8_t>& data, const std::vector<isobmff_box>& meta_children, uint32_t primary_id, lpb_preservation_observation* out) {
    if (primary_id == 0) return;

    const isobmff_box* iref = nullptr;
    for (const auto& b : meta_children) {
        if (b.type == "iref") {
            if (iref) {
                out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
                return;
            }
            iref = &b;
        }
    }
    if (!iref || iref->body_size < 4) return;

    uint8_t iref_ver = data[iref->body_start];
    if (iref_ver > 1) {
        out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
        return;
    }

    auto iref_children = parse_boxes(data.data(), iref->body_start + 4, iref->start + iref->size);
    struct aux_rel {
        uint32_t from_id;
        uint32_t to_id;
        uint32_t aux_id;
    };
    std::vector<aux_rel> candidate_relations;
    std::vector<std::pair<uint32_t, uint32_t>> seen_pairs;
    bool duplicate_found = false;

    for (const auto& ref_box : iref_children) {
        if (ref_box.type == "auxl") {
            size_t p = ref_box.body_start;
            size_t end = ref_box.start + ref_box.size;

            if (iref_ver == 0) {
                if (p + 4 > end) {
                    out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
                    return;
                }
                uint32_t from_id = read_be16u(data.data() + p);
                uint16_t ref_count = read_be16u(data.data() + p + 2);
                p += 4;
                if (p + static_cast<size_t>(ref_count) * 2 > end) {
                    out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
                    return;
                }
                bool box_has_primary = (from_id == primary_id);
                for (uint16_t r = 0; r < ref_count; ++r) {
                    uint32_t to_id = read_be16u(data.data() + p);
                    p += 2;
                    if (from_id == to_id) {
                        out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
                        return;
                    }
                    if (to_id == primary_id) {
                        box_has_primary = true;
                    }
                    if (from_id == primary_id || to_id == primary_id) {
                        auto pair = std::make_pair(from_id, to_id);
                        if (std::find(seen_pairs.begin(), seen_pairs.end(), pair) != seen_pairs.end()) {
                            duplicate_found = true;
                        }
                        seen_pairs.push_back(pair);
                        uint32_t aux_id = (from_id == primary_id) ? to_id : from_id;
                        candidate_relations.push_back({ from_id, to_id, aux_id });
                    }
                }
                if (box_has_primary && ref_count > 1) {
                    out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
                    return;
                }
            } else if (iref_ver == 1) {
                if (p + 6 > end) {
                    out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
                    return;
                }
                uint32_t from_id = read_be32u(data.data() + p);
                uint16_t ref_count = read_be16u(data.data() + p + 4);
                p += 6;
                if (p + static_cast<size_t>(ref_count) * 4 > end) {
                    out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
                    return;
                }
                bool box_has_primary = (from_id == primary_id);
                for (uint16_t r = 0; r < ref_count; ++r) {
                    uint32_t to_id = read_be32u(data.data() + p);
                    p += 4;
                    if (from_id == to_id) {
                        out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
                        return;
                    }
                    if (to_id == primary_id) {
                        box_has_primary = true;
                    }
                    if (from_id == primary_id || to_id == primary_id) {
                        auto pair = std::make_pair(from_id, to_id);
                        if (std::find(seen_pairs.begin(), seen_pairs.end(), pair) != seen_pairs.end()) {
                            duplicate_found = true;
                        }
                        seen_pairs.push_back(pair);
                        uint32_t aux_id = (from_id == primary_id) ? to_id : from_id;
                        candidate_relations.push_back({ from_id, to_id, aux_id });
                    }
                }
                if (box_has_primary && ref_count > 1) {
                    out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
                    return;
                }
            }
        }
    }

    if (duplicate_found || candidate_relations.size() > 1) {
        out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
        return;
    }

    if (candidate_relations.empty()) {
        return;
    }

    const auto& rel = candidate_relations[0];
    out->heic_primary_item_id = primary_id;
    out->heic_aux_item_id = rel.aux_id;
    out->heic_aux_from_item_id = rel.from_id;
    out->heic_aux_to_item_id = rel.to_id;

    const isobmff_box* iloc = nullptr;
    for (const auto& b : meta_children) {
        if (b.type == "iloc") {
            iloc = &b;
            break;
        }
    }

    if (!iloc) {
        out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
        return;
    }

    std::vector<uint8_t> aux_payload;
    if (!extract_heic_item_payload(data.data(), data.size(), iloc->body_start, iloc->body_size, rel.aux_id, aux_payload)) {
        out->flags |= (LPB_POBS_HAS_HEIC_AUX | LPB_POBS_HEIC_AUX_AMBIGUOUS);
        return;
    }

    uint8_t hash[32];
    lpb::crypto::sha256_buffer(aux_payload.data(), aux_payload.size(), hash);
    sha256_to_hex_upper(hash, out->heic_aux_item_sha256);

    const isobmff_box* iinf = nullptr;
    for (const auto& b : meta_children) {
        if (b.type == "iinf") {
            iinf = &b;
            break;
        }
    }

    if (iinf && iinf->body_size >= 4) {
        auto infe_boxes = parse_boxes(data.data(), iinf->body_start + 4, iinf->start + iinf->size);
        for (const auto& ib : infe_boxes) {
            if (ib.type == "infe" && ib.body_size >= 4) {
                uint8_t infe_ver = data[ib.body_start];
                size_t p = ib.body_start + 4;
                size_t end = ib.start + ib.size;
                uint32_t item_id = 0;
                if (infe_ver >= 3) {
                    if (p + 4 > end) continue;
                    item_id = read_be32u(data.data() + p);
                    p += 4;
                } else if (infe_ver == 2) {
                    if (p + 2 > end) continue;
                    item_id = read_be16u(data.data() + p);
                    p += 2;
                } else {
                    continue;
                }

                if (item_id == rel.aux_id) {
                    p += 2; // skip item_protection_index
                    if (p + 4 > end) continue;
                    p += 4; // skip item_type

                    std::string item_name;
                    while (p < end && data[p] != 0 && item_name.size() < sizeof(out->heic_aux_type) - 1) {
                        item_name.push_back(static_cast<char>(data[p++]));
                    }
                    if (p < end && data[p] == 0) ++p;

                    std::string content_type;
                    while (p < end && data[p] != 0 && content_type.size() < sizeof(out->heic_aux_type) - 1) {
                        content_type.push_back(static_cast<char>(data[p++]));
                    }

                    if (!content_type.empty()) {
                        std::memcpy(out->heic_aux_type, content_type.c_str(), content_type.size() + 1);
                    } else if (!item_name.empty()) {
                        std::memcpy(out->heic_aux_type, item_name.c_str(), item_name.size() + 1);
                    }
                    break;
                }
            }
        }
    }

    out->flags |= LPB_POBS_HAS_HEIC_AUX;
}

void observe_jpeg(lpb_context* context, const std::vector<uint8_t>& data, lpb_source_protocol protocol_hint, lpb_preservation_observation* out) {
    observe_jpeg_codestream(data, out);
    if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return;

    observe_jpeg_exif(data, protocol_hint, out);
    if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return;

    observe_jpeg_icc(data, out);
    if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return;

    observe_jpeg_xmp(data, out);
    if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return;

    observe_jpeg_extended_xmp(data, out);
}

void observe_heic(lpb_context* context, const std::vector<uint8_t>& data, lpb_source_protocol protocol_hint, lpb_preservation_observation* out) {
    auto top_boxes = parse_boxes(data.data(), 0, data.size());
    const isobmff_box* meta = nullptr;
    for (const auto& b : top_boxes) {
        if (b.type == "meta") {
            if (meta) {
                out->flags |= LPB_POBS_CODESTREAM_ERROR;
                return;
            }
            meta = &b;
        }
    }

    if (!meta || meta->body_size < 4) {
        out->flags |= LPB_POBS_CODESTREAM_ERROR;
        return;
    }

    auto meta_children = parse_boxes(data.data(), meta->body_start + 4, meta->start + meta->size);
    uint32_t primary_id = extract_heic_primary_item_id(data.data(), meta_children);
    out->heic_primary_item_id = primary_id;

    observe_heic_codestream(data, top_boxes, meta_children, primary_id, out);
    if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return;

    // Exif item
    uint64_t exif_offset = 0;
    uint64_t exif_len = 0;
    if (lpb_heif_locate_exif_item(context, data.data(), data.size(), &exif_offset, &exif_len) == LPB_RESULT_OK) {
        if (exif_offset < data.size() && exif_len > 4 && exif_offset + exif_len <= data.size()) {
            size_t item_start = static_cast<size_t>(exif_offset);
            size_t item_len = static_cast<size_t>(exif_len);
            uint32_t tiff_hdr_off = read_be32u(data.data() + item_start);

            size_t candidate_starts[] = {
                item_start + 4 + tiff_hdr_off,
                item_start + 10,
                item_start + 4,
                item_start
            };

            for (size_t tiff_start : candidate_starts) {
                if (tiff_start + 4 <= item_start + item_len) {
                    bool is_tiff = (data[tiff_start] == 0x49 && data[tiff_start + 1] == 0x49 && data[tiff_start + 2] == 0x2A && data[tiff_start + 3] == 0x00) ||
                                  (data[tiff_start] == 0x4D && data[tiff_start + 1] == 0x4D && data[tiff_start + 2] == 0x00 && data[tiff_start + 3] == 0x2A);
                    if (is_tiff) {
                        observe_tiff_common(data.data() + tiff_start, (item_start + item_len) - tiff_start, protocol_hint, out);
                        break;
                    }
                }
            }
        }
    }
    if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return;

    observe_heic_icc(data, meta_children, primary_id, out);
    if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return;

    // XMP item
    uint64_t xmp_offset = 0;
    uint64_t xmp_len = 0;
    if (lpb_heif_locate_xmp_item(context, data.data(), data.size(), &xmp_offset, &xmp_len) == LPB_RESULT_OK) {
        if (xmp_offset < data.size() && xmp_len > 0 && xmp_offset + xmp_len <= data.size()) {
            std::string_view xml_text(reinterpret_cast<const char*>(data.data() + xmp_offset), static_cast<size_t>(xmp_len));
            observe_xmp_common(xml_text, out);
        }
    }
    if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return;

    observe_heic_aux(data, meta_children, primary_id, out);
}

} // namespace

extern "C" {

LPB_API lpb_result LPB_CALL lpb_capture_preservation_observation(
    lpb_context* context,
    const char* media_path,
    lpb_source_protocol protocol_hint,
    lpb_image_container container_hint,
    lpb_preservation_observation* out_observation)
{
    if (!context || !media_path || !out_observation) return LPB_RESULT_INVALID_ARGUMENT;
    if (out_observation->struct_size < sizeof(lpb_preservation_observation)) return LPB_RESULT_INVALID_ARGUMENT;

    if (lpb_context_check_cancelled(context) != LPB_RESULT_OK) return LPB_RESULT_CANCELLED;

    uint32_t saved_size = out_observation->struct_size;
    std::memset(out_observation, 0, sizeof(lpb_preservation_observation));
    out_observation->struct_size = saved_size;

    bool is_video = (container_hint == LPB_IMAGE_CONTAINER_UNKNOWN);
    if (is_video) {
        observe_video_mdat(context, media_path, out_observation);
        return LPB_RESULT_OK;
    }

    std::vector<uint8_t> data;
    if (!read_file_binary(media_path, data)) {
        set_error(context, "Failed to read media file for preservation observation");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    bool is_jpeg = (container_hint == LPB_IMAGE_CONTAINER_JPEG) ||
                   (data.size() >= 2 && data[0] == 0xFF && data[1] == 0xD8);
    bool is_heic = !is_jpeg && ((container_hint == LPB_IMAGE_CONTAINER_HEIC) ||
                   (data.size() >= 12 && data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p'));

    if (is_jpeg) {
        observe_jpeg(context, data, protocol_hint, out_observation);
    } else if (is_heic) {
        observe_heic(context, data, protocol_hint, out_observation);
    }

    return LPB_RESULT_OK;
}

} // extern "C"
