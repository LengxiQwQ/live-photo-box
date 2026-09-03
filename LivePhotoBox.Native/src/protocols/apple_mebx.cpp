#include "livephotobox_native.h"
#include "foundation/internal.h"
#include "binary/endian.h"
#include "apple_mebx_templates.h"
#include "containers/isobmff.h"

#include <vector>
#include <string>
#include <cstring>
#include <cmath>
#include <algorithm>
#include <limits>

namespace { static bool is_box_type(const uint8_t* p, const char* type) { return p[4] == type[0] && p[5] == type[1] && p[6] == type[2] && p[7] == type[3]; }
    bool find_box_local(
        const uint8_t* data, size_t start, size_t end, const char* type,
        size_t& box_start, size_t& box_len, size_t& body_start) {
        size_t p = start;
        while (p <= end && end - p >= 8) {
            isobmff_box_header box{};
            if (!try_read_box_header(data, p, end, box)) break;
            if (is_box_type(data + p, type)) {
                box_start = p;
                box_len = box.size;
                body_start = p + box.header_size;
                return true;
            }
            p += box.size;
        }
        return false;
    }
    
    struct BoxInfo {
        std::string type;
        std::vector<uint8_t> data;
    };
    std::vector<BoxInfo> parse_children(const uint8_t* data, size_t start, size_t end) {
        std::vector<BoxInfo> list;
        size_t p = start;
        while (p <= end && end - p >= 8) {
            isobmff_box_header box{};
            if (!try_read_box_header(data, p, end, box)) return {};
            std::string type(reinterpret_cast<const char*>(data + p + 4), 4);
            list.push_back({type, std::vector<uint8_t>(data + p, data + p + box.size)});
            p += box.size;
        }
        if (p != end) return {};
        return list;
    }
    ptrdiff_t find_atom(const std::vector<uint8_t>& box, size_t start, size_t end, const char* type) {
        size_t p = start;
        while (p <= end && end - p >= 8) {
            isobmff_box_header header{};
            if (!try_read_box_header(box.data(), p, end, header)) break;
            if (is_box_type(box.data() + p, type)) return p;
            p += header.size;
        }
        return -1;
    }
    ptrdiff_t find_atom_recursive(
        const std::vector<uint8_t>& data, size_t start, size_t end, const char* type);
    void write_type(uint8_t* data, size_t off, const char* type) {
        data[off] = type[0]; data[off+1] = type[1];
        data[off+2] = type[2]; data[off+3] = type[3];
    }
    std::vector<uint8_t> build_container(const char* type, const std::vector<std::vector<uint8_t>>& children) {
        size_t total = 8;
        for (const auto& c : children) total += c.size();
        std::vector<uint8_t> box(total);
        write_be32(box.data(), static_cast<int32_t>(total));
        write_type(box.data(), 4, type);
        size_t off = 8;
        for (const auto& c : children) {
            std::memcpy(box.data() + off, c.data(), c.size());
            off += c.size();
        }
        return box;
    }
    std::vector<uint8_t> build_box_from_body(const char* type, const std::vector<uint8_t>& body) {
        std::vector<uint8_t> box(8 + body.size(), 0);
        write_be32(box.data(), static_cast<int32_t>(box.size()));
        write_type(box.data(), 4, type);
        if (!body.empty()) std::memcpy(box.data() + 8, body.data(), body.size());
        return box;
    }
    std::vector<uint8_t> build_hdlr(const char* pre, const char* htype, const char* name) {
        size_t name_len = std::strlen(name);
        std::vector<uint8_t> box(33 + name_len);
        write_be32(box.data(), static_cast<int32_t>(box.size()));
        write_type(box.data(), 4, "hdlr");
        write_type(box.data(), 12, pre);
        write_type(box.data(), 16, htype);
        write_type(box.data(), 20, "appl");
        box[32] = static_cast<uint8_t>(name_len);
        std::memcpy(box.data() + 33, name, name_len);
        return box;
    }
    void write_aperture(uint8_t* box, size_t off, const char* type, int w, int h) {
        write_be32(box + off, 20);
        write_type(box, off + 4, type);
        write_be32(box + off + 8, 0);
        write_be32(box + off + 12, w << 16);
        write_be32(box + off + 16, h << 16);
    }
    std::vector<uint8_t> build_tapt(int w, int h) {
        std::vector<uint8_t> box(68, 0);
        write_be32(box.data(), 68);
        write_type(box.data(), 4, "tapt");
        write_aperture(box.data(), 8, "clef", w, h);
        write_aperture(box.data(), 28, "prof", w, h);
        write_aperture(box.data(), 48, "enof", w, h);
        return box;
    }
    std::vector<uint8_t> build_vmhd() {
        std::vector<uint8_t> box(20, 0);
        write_be32(box.data(), 20);
        write_type(box.data(), 4, "vmhd");
        write_be16(box.data() + 12, 0x40);
        write_be16(box.data() + 14, 0x8000);
        write_be16(box.data() + 16, 0x8000);
        write_be16(box.data() + 18, 0x8000);
        return box;
    }

    std::vector<uint8_t> rebuild_dref(const std::vector<uint8_t>& dref) {
        std::vector<uint8_t> b = dref;
        uint32_t count = read_be32u(b.data() + 12);
        size_t e = 16;
        for (uint32_t i = 0; i < count && e + 8 <= b.size(); i++) {
            uint32_t esz = read_be32u(b.data() + e);
            if (esz < 8 || e + esz > b.size()) break;
            if (is_box_type(b.data() + e, "url ")) write_type(b.data(), e + 4, "alis");
            e += esz;
        }
        return b;
    }

    std::vector<uint8_t> rebuild_dinf(const std::vector<uint8_t>& dinf) {
        auto children = parse_children(dinf.data(), 8, dinf.size());
        std::vector<std::vector<uint8_t>> res;
        for (auto& c : children) {
            if (c.type == "dref") res.push_back(rebuild_dref(c.data));
            else res.push_back(c.data);
        }
        return build_container("dinf", res);
    }

    std::vector<uint8_t> rebuild_tkhd(const std::vector<uint8_t>& tkhd, uint32_t appleTime) {
        std::vector<uint8_t> b = tkhd;
        b[9] = 0; b[10] = 0; b[11] = 0x0f;
        write_be32(b.data() + 12, appleTime);
        write_be32(b.data() + 16, appleTime);
        return b;
    }

    std::vector<uint8_t> rebuild_mdhd(const std::vector<uint8_t>& mdhd, uint32_t appleTime) {
        std::vector<uint8_t> b = mdhd;
        write_be32(b.data() + 12, appleTime);
        write_be32(b.data() + 16, appleTime);
        return b;
    }

    std::vector<uint8_t> rebuild_minf(const std::vector<uint8_t>& minf, bool isVideo) {
        auto children = parse_children(minf.data(), 8, minf.size());
        std::vector<std::vector<uint8_t>> res;
        for (auto& c : children) {
            if (c.type == "vmhd" && isVideo) res.push_back(build_vmhd());
            else if (c.type == "hdlr") res.push_back(build_hdlr("dhlr", "alis", "Core Media Data Handler"));
            else if (c.type == "dinf") res.push_back(rebuild_dinf(c.data));
            else res.push_back(c.data);
        }
        return build_container("minf", res);
    }

    std::vector<uint8_t> rebuild_mdia(const std::vector<uint8_t>& mdia, bool isVideo, uint32_t appleTime) {
        auto children = parse_children(mdia.data(), 8, mdia.size());
        std::vector<std::vector<uint8_t>> res;
        for (auto& c : children) {
            if (c.type == "mdhd") res.push_back(rebuild_mdhd(c.data, appleTime));
            else if (c.type == "hdlr") res.push_back(build_hdlr("mhlr", isVideo ? "vide" : "soun", isVideo ? "Core Media Video" : "Core Media Audio"));
            else if (c.type == "minf") res.push_back(rebuild_minf(c.data, isVideo));
            else res.push_back(c.data);
        }
        return build_container("mdia", res);
    }

    std::vector<uint8_t> normalize_trak(const std::vector<uint8_t>& trak, bool isVideo, int vw, int vh, uint32_t appleTime) {
        auto children = parse_children(trak.data(), 8, trak.size());
        std::vector<std::vector<uint8_t>> res;
        for (auto& c : children) {
            if (c.type == "tkhd") {
                res.push_back(rebuild_tkhd(c.data, appleTime));
                if (isVideo) res.push_back(build_tapt(vw, vh));
            } else if (c.type == "mdia") {
                res.push_back(rebuild_mdia(c.data, isVideo, appleTime));
            } else {
                res.push_back(c.data);
            }
        }
        return build_container("trak", res);
    }

    void patch_track_timestamps(std::vector<uint8_t>& trak, uint32_t appleTime) {
        ptrdiff_t tkhd = find_atom(trak, 8, trak.size(), "tkhd");
        if (tkhd >= 0) { write_be32(trak.data() + tkhd + 12, appleTime); write_be32(trak.data() + tkhd + 16, appleTime); }
        ptrdiff_t mdhd = find_atom_recursive(trak, 8, trak.size(), "mdhd");
        if (mdhd >= 0) { write_be32(trak.data() + mdhd + 12, appleTime); write_be32(trak.data() + mdhd + 16, appleTime); }
    }

    bool is_box_container(const std::string& type) {
        return type == "moov" || type == "trak" || type == "mdia" ||
               type == "minf" || type == "stbl" || type == "udta";
    }

    bool contains_atom_recursive(
        const std::vector<uint8_t>& data, size_t start, size_t end, const char* type) {
        size_t p = start;
        while (p <= end && end - p >= 8) {
            isobmff_box_header box{};
            if (!try_read_box_header(data.data(), p, end, box)) break;
            std::string current(reinterpret_cast<const char*>(data.data() + p + 4), 4);
            if (is_box_type(data.data() + p, type)) return true;
            if (is_box_container(current) &&
                contains_atom_recursive(data, p + box.header_size, p + box.size, type)) {
                return true;
            }
            p += box.size;
        }
        return false;
    }

    ptrdiff_t find_atom_recursive(
        const std::vector<uint8_t>& data, size_t start, size_t end, const char* type) {
        size_t p = start;
        while (p <= end && end - p >= 8) {
            isobmff_box_header box{};
            if (!try_read_box_header(data.data(), p, end, box)) break;
            std::string current(reinterpret_cast<const char*>(data.data() + p + 4), 4);
            if (is_box_type(data.data() + p, type)) return static_cast<ptrdiff_t>(p);
            if (is_box_container(current)) {
                ptrdiff_t nested = find_atom_recursive(data, p + box.header_size, p + box.size, type);
                if (nested >= 0) return nested;
            }
            p += box.size;
        }
        return -1;
    }
    ptrdiff_t find_atom_recursive(
        const std::vector<uint8_t>& data, size_t start, size_t end, const char* type);

    bool shift_trak_chunk_offsets_in_range(
        std::vector<uint8_t>& data, size_t start, size_t end,
        size_t oldMoovEnd, int delta) {
        size_t p = start;
        while (p <= end && end - p >= 8) {
            isobmff_box_header box{};
            if (!try_read_box_header(data.data(), p, end, box)) return false;
            const size_t size = box.size;

            std::string type(reinterpret_cast<const char*>(data.data() + p + 4), 4);
            if (type == "stco") {
                if (size < 16) return false;
                uint32_t count = read_be32u(data.data() + p + 12);
                if (count > (size - 16) / 4) return false;
                for (uint32_t i = 0; i < count; i++) {
                    size_t off = p + 16 + static_cast<size_t>(i) * 4;
                    uint32_t v = read_be32u(data.data() + off);
                    if (v >= oldMoovEnd) {
                        if (delta > 0 && v > std::numeric_limits<uint32_t>::max() - static_cast<uint32_t>(delta)) return false;
                        if (delta < 0 && v < static_cast<uint32_t>(-static_cast<int64_t>(delta))) return false;
                        write_be32(data.data() + off, static_cast<uint32_t>(static_cast<int64_t>(v) + delta));
                    }
                }
            } else if (type == "co64") {
                // Large media files use the 64-bit co64 variant.  These
                // offsets must move together with the rebuilt moov box.
                if (size < 16) return false;
                uint32_t count = read_be32u(data.data() + p + 12);
                if (count > (size - 16) / 8) return false;
                for (uint32_t i = 0; i < count; i++) {
                    size_t off = p + 16 + static_cast<size_t>(i) * 8;
                    uint64_t v = static_cast<uint64_t>(read_be64(data.data() + off));
                    if (v >= oldMoovEnd) {
                        if (delta > 0 && v > std::numeric_limits<uint64_t>::max() - static_cast<uint64_t>(delta)) return false;
                        if (delta < 0 && v < static_cast<uint64_t>(-static_cast<int64_t>(delta))) return false;
                        write_be64(data.data() + off, static_cast<int64_t>(v) + delta);
                    }
                }
            } else if (is_box_container(type)) {
                if (!shift_trak_chunk_offsets_in_range(data, p + box.header_size, p + size, oldMoovEnd, delta)) return false;
            }

            p += size;
        }
        return p == end;
    }

    bool shift_trak_chunk_offsets(std::vector<uint8_t>& trak, size_t oldMoovEnd, int delta) {
        return shift_trak_chunk_offsets_in_range(trak, 8, trak.size(), oldMoovEnd, delta);
    }

    std::vector<uint8_t> build_content_describes_track(int trackId, int timescale, double /*videoSeconds*/, int sampleCount, int chunk1, int dataOff) {
        std::vector<uint8_t> trak(ContentDescribesTrackTemplate, ContentDescribesTrackTemplate + sizeof(ContentDescribesTrackTemplate));
        ptrdiff_t tkhd = find_atom_recursive(trak, 8, trak.size(), "tkhd");
        ptrdiff_t elst = find_atom_recursive(trak, 8, trak.size(), "elst");
        ptrdiff_t mdhd = find_atom_recursive(trak, 8, trak.size(), "mdhd");
        ptrdiff_t stts = find_atom_recursive(trak, 8, trak.size(), "stts");
        ptrdiff_t stsc = find_atom_recursive(trak, 8, trak.size(), "stsc");
        ptrdiff_t stsz = find_atom_recursive(trak, 8, trak.size(), "stsz");
        ptrdiff_t stco = find_atom_recursive(trak, 8, trak.size(), "stco");

        int mediaDur = sampleCount * 1000;
        int leadIn = static_cast<int>(std::round(0.05 * timescale));
        int mediaMovie = static_cast<int>(std::round((double)mediaDur * timescale / 60000.0));
        if (mediaMovie < 1) mediaMovie = 1;

        write_be32(trak.data() + mdhd + 24, mediaDur);
        write_be32(trak.data() + elst + 16, leadIn);
        write_be32(trak.data() + elst + 28, mediaMovie);
        write_be32(trak.data() + tkhd + 20, trackId);
        write_be32(trak.data() + tkhd + 28, leadIn + mediaMovie);

        write_be32(trak.data() + stts + 16, sampleCount);
        write_be32(trak.data() + stsc + 20, chunk1);
        write_be32(trak.data() + stsc + 32, sampleCount - chunk1);
        write_be32(trak.data() + stsz + 16, sampleCount);
        write_be32(trak.data() + stco + 16, dataOff);
        write_be32(trak.data() + stco + 20, dataOff + chunk1 * sizeof(LivePhotoInfoSample));

        return trak;
    }

    std::vector<uint8_t> build_mebx_cover_track(int trackId, int timescale, double coverSeconds, int dataOff) {
        std::vector<uint8_t> trak(MebxCoverTrackTemplate, MebxCoverTrackTemplate + sizeof(MebxCoverTrackTemplate));
        ptrdiff_t tkhd = find_atom_recursive(trak, 8, trak.size(), "tkhd");
        ptrdiff_t elst = find_atom_recursive(trak, 8, trak.size(), "elst");
        ptrdiff_t stco = find_atom_recursive(trak, 8, trak.size(), "stco");

        int coverDur = static_cast<int>(std::round(coverSeconds * timescale));
        if (coverDur < 0) coverDur = 0;
        int oneFrame = static_cast<int>(std::round((double)timescale / 600.0));
        if (oneFrame < 1) oneFrame = 1;

        write_be32(trak.data() + elst + 16, coverDur);
        write_be32(trak.data() + elst + 28, oneFrame);
        write_be32(trak.data() + tkhd + 20, trackId);
        write_be32(trak.data() + tkhd + 28, coverDur + oneFrame);
        write_be32(trak.data() + stco + 16, dataOff);

        return trak;
    }

    std::vector<uint8_t> build_apple_content_identifier_meta(const char* contentId) {
        if (contentId == nullptr || *contentId == '\0') return {};

        const std::string key = "com.apple.quicktime.content.identifier";
        const std::string value(contentId);

        std::vector<uint8_t> keyEntry(8 + key.size());
        write_be32(keyEntry.data(), static_cast<int32_t>(keyEntry.size()));
        write_type(keyEntry.data(), 4, "mdta");
        std::memcpy(keyEntry.data() + 8, key.data(), key.size());

        std::vector<uint8_t> keysBody(8 + keyEntry.size(), 0);
        write_be32(keysBody.data() + 4, 1);
        std::memcpy(keysBody.data() + 8, keyEntry.data(), keyEntry.size());
        std::vector<uint8_t> keys = build_box_from_body("keys", keysBody);

        std::vector<uint8_t> dataBox(16 + value.size(), 0);
        write_be32(dataBox.data(), static_cast<int32_t>(dataBox.size()));
        write_type(dataBox.data(), 4, "data");
        write_be32(dataBox.data() + 8, 1); // UTF-8
        std::memcpy(dataBox.data() + 16, value.data(), value.size());

        std::vector<uint8_t> item(8 + dataBox.size(), 0);
        write_be32(item.data(), static_cast<int32_t>(item.size()));
        item[7] = 1; // key index 1
        std::memcpy(item.data() + 8, dataBox.data(), dataBox.size());
        std::vector<uint8_t> ilst = build_box_from_body("ilst", item);

        std::vector<uint8_t> hdlr = build_hdlr("\0\0\0\0", "mdta", "");
        std::vector<uint8_t> metaBody;
        metaBody.reserve(hdlr.size() + keys.size() + ilst.size());
        metaBody.insert(metaBody.end(), hdlr.begin(), hdlr.end());
        metaBody.insert(metaBody.end(), keys.begin(), keys.end());
        metaBody.insert(metaBody.end(), ilst.begin(), ilst.end());
        return build_box_from_body("meta", metaBody);
    }
}
static lpb_result append_mebx_tracks_impl(
    lpb_context* context,
    const uint8_t* data, size_t data_size,
    double cover_seconds,
    const char* content_id,
    uint8_t* output, size_t output_size, size_t* out_written)
{
    if (!context || !data || !out_written) return LPB_RESULT_INVALID_ARGUMENT;
    *out_written = 0;
    

    size_t ftyp_start, ftyp_len, ftyp_body;
    if (!find_box_local(data, 0, data_size, "ftyp", ftyp_start, ftyp_len, ftyp_body)) {
        set_error(context, "No ftyp box found.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    size_t moov_start, moov_len, moov_body;
    if (!find_box_local(data, ftyp_start + ftyp_len, data_size, "moov", moov_start, moov_len, moov_body)) {
        set_error(context, "No moov box found.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    size_t oldMoovEnd = moov_start + moov_len;

    auto moov_children = parse_children(data, moov_start + 8, oldMoovEnd);
    int timescale = 600;
    int movieDuration = 0;
    uint32_t appleTime = 0;
    for (const auto& c : moov_children) {
        if (c.type == "mvhd") {
            appleTime = read_be32u(c.data.data() + 12);
            timescale = read_be32u(c.data.data() + 20);
            movieDuration = read_be32u(c.data.data() + 24);
        }
    }

    std::vector<uint8_t> normVideo;
    std::vector<uint8_t> normAudio;
    int videoOldLen = 0, audioOldLen = 0;
    int maxTrackId = 0;

    for (const auto& c : moov_children) {
        if (c.type != "trak") continue;
        bool isVideo = contains_atom_recursive(c.data, 8, c.data.size(), "vmhd");
        bool isAudio = contains_atom_recursive(c.data, 8, c.data.size(), "smhd");
        ptrdiff_t tkhd = find_atom(c.data, 8, c.data.size(), "tkhd");
        int tid = tkhd >= 0 ? read_be32u(c.data.data() + tkhd + 20) : 0;
        if (tid > maxTrackId) maxTrackId = tid;

        if (isVideo) {
            int vw = tkhd >= 0 ? (read_be32u(c.data.data() + tkhd + 84) >> 16) : 0;
            int vh = tkhd >= 0 ? (read_be32u(c.data.data() + tkhd + 88) >> 16) : 0;
            videoOldLen = static_cast<int>(c.data.size());
            normVideo = normalize_trak(c.data, true, vw, vh, appleTime);
        } else if (isAudio) {
            audioOldLen = static_cast<int>(c.data.size());
            normAudio = normalize_trak(c.data, false, 0, 0, appleTime);
        }
    }

    int normalizationDelta = (normVideo.empty() ? 0 : static_cast<int>(normVideo.size()) - videoOldLen) +
                             (normAudio.empty() ? 0 : static_cast<int>(normAudio.size()) - audioOldLen);
    const int MetadataTrakSize = 1043 + 672;
    std::vector<uint8_t> contentMeta;
    int replacedMetaSize = 0;
    if (content_id != nullptr) {
        contentMeta = build_apple_content_identifier_meta(content_id);
        for (const auto& c : moov_children) {
            if (c.type == "meta") replacedMetaSize += static_cast<int>(c.data.size());
        }
    }
    const int metadataDelta = static_cast<int>(contentMeta.size()) - replacedMetaSize;
    int moovDelta = normalizationDelta + MetadataTrakSize + metadataDelta;

    double videoSeconds = (double)movieDuration / timescale;
    int sampleCount = static_cast<int>(std::clamp(std::round(videoSeconds * 60.0), 2.0, 600.0));
    int chunk1 = (sampleCount + 1) / 2;
    int sampleDataOff = static_cast<int>(data_size) + moovDelta + 8;

    int contentTrackId = maxTrackId + 1;
    int coverTrackId = maxTrackId + 2;
    std::vector<uint8_t> contentTrak = build_content_describes_track(contentTrackId, timescale, videoSeconds, sampleCount, chunk1, sampleDataOff);
    std::vector<uint8_t> coverTrak = build_mebx_cover_track(coverTrackId, timescale, cover_seconds, sampleDataOff + sampleCount * sizeof(LivePhotoInfoSample));

    patch_track_timestamps(contentTrak, appleTime);
    patch_track_timestamps(coverTrak, appleTime);

    if ((!normVideo.empty() && !shift_trak_chunk_offsets(normVideo, oldMoovEnd, moovDelta)) ||
        (!normAudio.empty() && !shift_trak_chunk_offsets(normAudio, oldMoovEnd, moovDelta))) {
        set_error(context, "Apple MOV chunk offset table is malformed or overflows during relocation.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<std::vector<uint8_t>> newMoovPayload;
    std::vector<std::vector<uint8_t>> pending;
    for (const auto& c : moov_children) {
        if (c.type == "mvhd") {
            std::vector<uint8_t> m = c.data;
            write_be32(m.data() + 12, appleTime);
            write_be32(m.data() + 16, appleTime);
            write_be32(m.data() + 104, coverTrackId + 1);
            newMoovPayload.push_back(m);
        } else if (c.type == "trak") {
            bool isVideo = contains_atom_recursive(c.data, 8, c.data.size(), "vmhd");
            bool isAudio = contains_atom_recursive(c.data, 8, c.data.size(), "smhd");
            if (isVideo && !normVideo.empty()) newMoovPayload.push_back(normVideo);
            else if (isAudio && !normAudio.empty()) newMoovPayload.push_back(normAudio);
            else newMoovPayload.push_back(c.data);
        } else if (c.type == "meta" && content_id != nullptr) {
            // Replace stale top-level QuickTime metadata when the rebuilt
            // writer supplies a new ContentIdentifier.
        } else {
            pending.push_back(c.data);
        }
    }
    newMoovPayload.push_back(contentTrak);
    newMoovPayload.push_back(coverTrak);
    if (!contentMeta.empty()) newMoovPayload.push_back(std::move(contentMeta));
    newMoovPayload.insert(newMoovPayload.end(), pending.begin(), pending.end());

    std::vector<uint8_t> newMoov = build_container("moov", newMoovPayload);

    int samplesSize = sampleCount * sizeof(LivePhotoInfoSample) + sizeof(MebxCoverSample);
    int sampleMdatSize = 8 + samplesSize;
    size_t finalSize = data_size + moovDelta + sampleMdatSize;
    
    *out_written = finalSize;
    if (!output || output_size < finalSize) {
        return LPB_RESULT_BUFFER_TOO_SMALL;
    }

    std::memcpy(output, data, moov_start);
    std::memcpy(output + moov_start, newMoov.data(), newMoov.size());
    std::memcpy(output + moov_start + newMoov.size(), data + oldMoovEnd, data_size - oldMoovEnd);

    size_t sampleMdatOff = data_size + moovDelta;
    write_be32(output + sampleMdatOff, static_cast<int32_t>(sampleMdatSize));
    write_type(output, sampleMdatOff + 4, "mdat");
    for (int i = 0; i < sampleCount; i++) {
        std::memcpy(output + sampleMdatOff + 8 + i * sizeof(LivePhotoInfoSample), LivePhotoInfoSample, sizeof(LivePhotoInfoSample));
    }
    std::memcpy(output + sampleMdatOff + 8 + sampleCount * sizeof(LivePhotoInfoSample), MebxCoverSample, sizeof(MebxCoverSample));

    return LPB_RESULT_OK;
}

extern "C" LPB_API lpb_result LPB_CALL lpb_apple_append_mebx_tracks(
    lpb_context* context,
    const uint8_t* data, size_t data_size,
    double cover_seconds,
    uint8_t* output, size_t output_size, size_t* out_written)
{
    return append_mebx_tracks_impl(
        context, data, data_size, cover_seconds, nullptr,
        output, output_size, out_written);
}

extern "C" LPB_API lpb_result LPB_CALL lpb_apple_append_mebx_tracks_with_content_identifier(
    lpb_context* context,
    const uint8_t* data, size_t data_size,
    double cover_seconds,
    const char* content_id,
    uint8_t* output, size_t output_size, size_t* out_written)
{
    if (content_id == nullptr || *content_id == '\0') {
        set_error(context, "Apple ContentIdentifier is required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    return append_mebx_tracks_impl(
        context, data, data_size, cover_seconds, content_id,
        output, output_size, out_written);
}



