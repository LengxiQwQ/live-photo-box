#include "livephotobox_native.h"
#include "foundation/portable_internal.h"
#include "binary/endian.h"
#include "apple_mebx_templates.h"
#include "containers/isobmff.h"

#include <vector>
#include <string>
#include <cstring>
#include <cmath>
#include <algorithm>
#include <limits>
#include <numeric>
#include <ctime>

namespace { static bool is_box_type(const uint8_t* p, const char* type) { return p[4] == type[0] && p[5] == type[1] && p[6] == type[2] && p[7] == type[3]; }
    bool has_complete_box_sequence(const uint8_t* data, size_t data_size) {
        if (!data || data_size < 8) return false;
        size_t p = 0;
        while (p < data_size) {
            isobmff_box_header box{};
            if (!try_read_box_header(data, p, data_size, box)) return false;
            p += box.size;
        }
        return p == data_size;
    }
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
        for (const auto& c : children) {
            if (c.size() > std::numeric_limits<size_t>::max() - total) return {};
            total += c.size();
        }
        if (total > std::numeric_limits<uint32_t>::max()) return {};
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
        if (body.size() > std::numeric_limits<size_t>::max() - 8 || body.size() + 8 > std::numeric_limits<uint32_t>::max()) return {};
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
    std::vector<uint8_t> build_vmhd(bool target) {
        std::vector<uint8_t> box(20, 0);
        write_be32(box.data(), 20);
        write_type(box.data(), 4, "vmhd");
        if(target) box[11]=1;
        write_be16(box.data() + 12, 0x40);
        write_be16(box.data() + 14, 0x8000);
        write_be16(box.data() + 16, 0x8000);
        write_be16(box.data() + 18, 0x8000);
        return box;
    }

    std::vector<uint8_t> rebuild_dref(const std::vector<uint8_t>& dref) {
        if (dref.size() < 16) return {};
        std::vector<uint8_t> b = dref;
        uint32_t count = read_be32u(b.data() + 12);
        size_t e = 16;
        for (uint32_t i = 0; i < count; i++) {
            if (e > b.size() || b.size() - e < 8) return {};
            uint32_t esz = read_be32u(b.data() + e);
            if (esz < 8 || esz > b.size() - e) return {};
            if (is_box_type(b.data() + e, "url ")) write_type(b.data(), e + 4, "alis");
            e += esz;
        }
        if (e != b.size()) return {};
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
        if (tkhd.size() < 20) return {};
        std::vector<uint8_t> b = tkhd;
        b[9] = 0; b[10] = 0; b[11] = 0x0f;
        write_be32(b.data() + 12, appleTime);
        write_be32(b.data() + 16, appleTime);
        return b;
    }

    std::vector<uint8_t> rebuild_mdhd(const std::vector<uint8_t>& mdhd, uint32_t appleTime) {
        if (mdhd.size() < 20) return {};
        std::vector<uint8_t> b = mdhd;
        write_be32(b.data() + 12, appleTime);
        write_be32(b.data() + 16, appleTime);
        return b;
    }

    std::vector<uint8_t> rebuild_minf(const std::vector<uint8_t>& minf, bool isVideo, bool target) {
        auto children = parse_children(minf.data(), 8, minf.size());
        if (children.empty()) return {};
        std::vector<std::vector<uint8_t>> res;
        if(target && std::none_of(children.begin(),children.end(),[](const auto& c){return c.type=="hdlr";}))
            res.push_back(build_hdlr("dhlr", "alis", "Core Media Data Handler"));
        for (auto& c : children) {
            if (c.type == "vmhd" && isVideo) res.push_back(build_vmhd(target));
            else if (c.type == "hdlr") res.push_back(build_hdlr("dhlr", "alis", "Core Media Data Handler"));
            else if (c.type == "dinf") res.push_back(rebuild_dinf(c.data));
            else res.push_back(c.data);
        }
        return build_container("minf", res);
    }

    std::vector<uint8_t> rebuild_mdia(const std::vector<uint8_t>& mdia, bool isVideo, uint32_t appleTime, bool target) {
        auto children = parse_children(mdia.data(), 8, mdia.size());
        if (children.empty()) return {};
        std::vector<std::vector<uint8_t>> res;
        for (auto& c : children) {
            if (c.type == "mdhd") res.push_back(rebuild_mdhd(c.data, appleTime));
            else if (c.type == "hdlr") res.push_back(build_hdlr("mhlr", isVideo ? "vide" : "soun", isVideo ? "Core Media Video" : "Core Media Audio"));
            else if (c.type == "minf") res.push_back(rebuild_minf(c.data, isVideo, target));
            else res.push_back(c.data);
        }
        return build_container("mdia", res);
    }

    std::vector<uint8_t> normalize_trak(const std::vector<uint8_t>& trak, bool isVideo, int vw, int vh, uint32_t appleTime, bool target) {
        auto children = parse_children(trak.data(), 8, trak.size());
        if (children.empty()) return {};
        std::vector<std::vector<uint8_t>> res;
        for (auto& c : children) {
            if (c.type == "tkhd") {
                res.push_back(rebuild_tkhd(c.data, appleTime));
                if (isVideo) res.push_back(build_tapt(vw, vh));
            } else if (c.type == "mdia") {
                res.push_back(rebuild_mdia(c.data, isVideo, appleTime, target));
            } else if (!(target && isVideo && c.type == "tapt")) {
                res.push_back(c.data);
            }
        }
        return build_container("trak", res);
    }

    void patch_track_timestamps(std::vector<uint8_t>& trak, uint32_t appleTime) {
        ptrdiff_t tkhd = find_atom(trak, 8, trak.size(), "tkhd");
        if (tkhd >= 0 && static_cast<size_t>(tkhd) + 20 <= trak.size()) { write_be32(trak.data() + tkhd + 12, appleTime); write_be32(trak.data() + tkhd + 16, appleTime); }
        ptrdiff_t mdhd = find_atom_recursive(trak, 8, trak.size(), "mdhd");
        if (mdhd >= 0 && static_cast<size_t>(mdhd) + 20 <= trak.size()) { write_be32(trak.data() + mdhd + 12, appleTime); write_be32(trak.data() + mdhd + 16, appleTime); }
    }

    bool is_box_container(const std::string& type) {
        return type == "moov" || type == "trak" || type == "mdia" ||
               type == "minf" || type == "stbl" || type == "udta" || type == "edts";
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
        if (tkhd < 0 || elst < 0 || mdhd < 0 || stts < 0 || stsc < 0 || stsz < 0 || stco < 0) return {};

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
        if (tkhd < 0 || elst < 0 || stco < 0) return {};

        int coverDur = static_cast<int>(std::round(coverSeconds * timescale));
        if (coverDur < 0) coverDur = 0;
        int oneFrame = static_cast<int>(std::round((double)timescale / 600.0));
        if (oneFrame < 1) oneFrame = 1;

        write_be32(trak.data() + elst + 16, coverDur);
        write_be32(trak.data() + elst + 28, oneFrame);
        write_be32(trak.data() + tkhd + 20, trackId);
        write_be32(trak.data() + tkhd + 28, coverDur + oneFrame);
        write_be32(trak.data() + stco + 16, dataOff);
        if(coverDur==0) {
            // The first frame needs no empty edit. A zero-duration empty edit
            // is invalid; retain one positive media edit at media time zero.
            ptrdiff_t edts=find_atom(trak,8,trak.size(),"edts");
            if(edts<0)return {};
            trak.erase(trak.begin()+elst+16,trak.begin()+elst+28);
            write_be32(trak.data()+elst,28);write_be32(trak.data()+elst+12,1);
            write_be32(trak.data()+edts,36);write_be32(trak.data(),static_cast<int32_t>(trak.size()));
        }
        return trak;
    }

    std::vector<uint8_t> build_apple_content_identifier_meta(const char* contentId, bool target_metadata=false) {
        if (contentId == nullptr || *contentId == '\0') return {};
        std::vector<std::pair<std::string,std::string>> fields{{"com.apple.quicktime.content.identifier",contentId}};
        if(target_metadata) {
            const std::time_t now=std::time(nullptr);std::tm utc{};
#if defined(_WIN32)
            if(gmtime_s(&utc,&now)!=0)return {};
#else
            if(!gmtime_r(&now,&utc))return {};
#endif
            char text[32]{};if(!std::strftime(text,sizeof(text),"%Y-%m-%dT%H:%M:%SZ",&utc))return {};
            fields.emplace_back("com.apple.quicktime.software","17.0.2");
            fields.emplace_back("com.apple.quicktime.creationdate",text);
        }
        std::vector<uint8_t> keysBody(8,0),items;write_be32(keysBody.data()+4,static_cast<uint32_t>(fields.size()));
        for(size_t i=0;i<fields.size();++i) {
            const auto& [key,value]=fields[i];
            std::vector<uint8_t> entry(8+key.size());write_be32(entry.data(),static_cast<uint32_t>(entry.size()));
            write_type(entry.data(),4,"mdta");std::memcpy(entry.data()+8,key.data(),key.size());
            keysBody.insert(keysBody.end(),entry.begin(),entry.end());
            std::vector<uint8_t> data(16+value.size(),0);write_be32(data.data(),static_cast<uint32_t>(data.size()));
            write_type(data.data(),4,"data");write_be32(data.data()+8,1);std::memcpy(data.data()+16,value.data(),value.size());
            std::vector<uint8_t> item(8+data.size(),0);write_be32(item.data(),static_cast<uint32_t>(item.size()));
            write_be32(item.data()+4,static_cast<uint32_t>(i+1));std::memcpy(item.data()+8,data.data(),data.size());
            items.insert(items.end(),item.begin(),item.end());
        }
        auto keys=build_box_from_body("keys",keysBody);auto ilst=build_box_from_body("ilst",items);
        auto hdlr=build_hdlr("\0\0\0\0","mdta","");
        if(target_metadata) std::fill(hdlr.begin()+20,hdlr.begin()+24,uint8_t{0});
        std::vector<uint8_t> body;
        body.insert(body.end(),hdlr.begin(),hdlr.end());body.insert(body.end(),keys.begin(),keys.end());body.insert(body.end(),ilst.begin(),ilst.end());
        return build_box_from_body("meta",body);
    }

    // Merge a new mdta key into a structurally bounded existing QuickTime meta.
    // Unknown child boxes and key values are retained byte-for-byte.
    bool merge_apple_meta(const std::vector<uint8_t>& old, const char* id, std::vector<uint8_t>& result) {
        if(old.size()<16) return false;
        size_t offset=8;
        auto children=parse_children(old.data(),offset,old.size());
        if(children.empty() && old.size()>=20 && read_be32u(old.data()+8)==0) {
            offset=12; children=parse_children(old.data(),offset,old.size());
        }
        if(children.empty()) return false;
        size_t ki=SIZE_MAX,li=SIZE_MAX,hi=SIZE_MAX;
        for(size_t i=0;i<children.size();++i) {
            size_t* slot=children[i].type=="keys"?&ki:children[i].type=="ilst"?&li:children[i].type=="hdlr"?&hi:nullptr;
            if(slot) {if(*slot!=SIZE_MAX)return false;*slot=i;}
        }
        if(ki==SIZE_MAX || li==SIZE_MAX || hi==SIZE_MAX || children[hi].data.size()<20 ||
           std::memcmp(children[hi].data.data()+16,"mdta",4)!=0) return false;
        auto& keys=children[ki].data; auto& ilst=children[li].data;
        if(keys.size()<16 || read_be32u(keys.data()+8)!=0) return false;
        const uint32_t count=read_be32u(keys.data()+12);
        const auto existing=parse_children(keys.data(),16,keys.size());
        if(existing.size()!=count || count==UINT32_MAX) return false;
        const std::string wanted="com.apple.quicktime.content.identifier";
        for(const auto& key:existing)
            if(key.type=="mdta" && key.data.size()==8+wanted.size() &&
               !std::memcmp(key.data.data()+8,wanted.data(),wanted.size())) return false;
        const auto items=parse_children(ilst.data(),8,ilst.size());
        if(ilst.size()!=8 && items.empty()) return false;
        for(const auto& item:items) {
            const uint32_t index=read_be32u(item.data.data()+4);
            if(index==0 || index>count) return false;
        }
        auto added=build_apple_content_identifier_meta(id,true);
        auto parts=parse_children(added.data(),8,added.size());
        if(parts.size()!=3 || parts[1].type!="keys" || parts[2].type!="ilst") return false;
        auto added_keys=parse_children(parts[1].data.data(),16,parts[1].data.size());
        auto added_items=parse_children(parts[2].data.data(),8,parts[2].data.size());
        if(added_keys.size()!=3 || added_items.size()!=3)return false;
        size_t software_index=SIZE_MAX;
        for(size_t i=0;i<existing.size();++i) if(existing[i].type==added_keys[1].type && existing[i].data==added_keys[1].data) {
            if(software_index!=SIZE_MAX)return false;software_index=i+1;
        }
        if(software_index!=SIZE_MAX) {
            size_t matches=0;std::vector<uint8_t> values;
            for(const auto& item:items) {
                const uint32_t index=read_be32u(item.data.data()+4);
                if(index!=software_index) {values.insert(values.end(),item.data.begin(),item.data.end());continue;}
                if(++matches!=1)return false;
                const auto data_boxes=parse_children(item.data.data(),8,item.data.size());
                if(data_boxes.size()!=1 || data_boxes[0].type!="data" || data_boxes[0].data.size()<16 ||
                   read_be32u(data_boxes[0].data.data()+8)!=1)return false;
                auto replacement=added_items[1].data;write_be32(replacement.data()+4,static_cast<uint32_t>(software_index));
                values.insert(values.end(),replacement.begin(),replacement.end());
            }
            if(matches!=1)return false;
            ilst=build_box_from_body("ilst",values);if(ilst.empty())return false;
        }
        uint32_t total=count;
        for(size_t i=0;i<added_keys.size();++i) {
            bool exists=false;
            for(const auto& key:existing) if(key.type==added_keys[i].type && key.data==added_keys[i].data) exists=true;
            if(exists) {if(i==0)return false;continue;}
            if(total==UINT32_MAX)return false;++total;
            keys.insert(keys.end(),added_keys[i].data.begin(),added_keys[i].data.end());
            auto item=added_items[i].data;write_be32(item.data()+4,total);
            ilst.insert(ilst.end(),item.begin(),item.end());
        }
        write_be32(keys.data(),static_cast<uint32_t>(keys.size()));write_be32(keys.data()+12,total);
        write_be32(ilst.data(),static_cast<uint32_t>(ilst.size()));
        std::fill(children[hi].data.begin()+12,children[hi].data.begin()+16,uint8_t{0});
        if(children[hi].data.size()<24)return false;
        std::fill(children[hi].data.begin()+20,children[hi].data.begin()+24,uint8_t{0});
        std::vector<uint8_t> body;
        for(const auto& child:children)body.insert(body.end(),child.data.begin(),child.data.end());
        result=build_box_from_body("meta",body);return !result.empty();
    }

    // Movie-level durations use mvhd's scale; media-time fields use each
    // mdhd's independent scale and must never be rewritten here.
    bool rescale_apple_movie_units(std::vector<uint8_t>& bytes,size_t start,size_t end,uint32_t from,uint32_t target) {
        const auto scale=[&](size_t at,size_t width) {
            const uint64_t value=width==4?read_be32u(bytes.data()+at):static_cast<uint64_t>(read_be64(bytes.data()+at));
            if(value>(UINT64_MAX-from/2)/target)return false;
            const uint64_t result=(value*target+from/2)/from;
            if(width==4) {if(result>UINT32_MAX)return false;write_be32(bytes.data()+at,static_cast<int32_t>(result));}
            else {if(result>INT64_MAX)return false;write_be64(bytes.data()+at,static_cast<int64_t>(result));}
            return true;
        };
        for(size_t p=start;p<end;) {
            isobmff_box_header box{};if(!try_read_box_header(bytes.data(),p,end,box))return false;
            const std::string type(reinterpret_cast<const char*>(bytes.data()+p+4),4);
            const size_t body=p+box.header_size;
            if(type=="mvhd") {
                if(box.header_size!=8||box.size<28||bytes[body]!=0||!scale(body+16,4))return false;
                write_be32(bytes.data()+body+12,static_cast<int32_t>(target));
            } else if(type=="tkhd") {
                if(box.header_size!=8||box.size<32||bytes[body]!=0||!scale(body+20,4))return false;
            } else if(type=="elst") {
                if(box.size<box.header_size+8)return false;
                const uint8_t version=bytes[body];if(version>1)return false;
                const size_t stride=version?20:12,width=version?8:4;
                const uint32_t count=read_be32u(bytes.data()+body+4);
                if(count>(box.size-box.header_size-8)/stride||box.size!=box.header_size+8+static_cast<uint64_t>(count)*stride)return false;
                for(uint32_t i=0;i<count;++i)if(!scale(body+8+static_cast<size_t>(i)*stride,width))return false;
            } else if(is_box_container(type)&&!rescale_apple_movie_units(bytes,body,p+box.size,from,target))return false;
            p+=box.size;
        }
        return true;
    }
    struct mdat_map { uint64_t begin,end,destination; };
    bool remap_chunks(std::vector<uint8_t>& bytes,size_t start,size_t end,const std::vector<mdat_map>& maps) {
        size_t p=start;
        while(p<end) {
            isobmff_box_header box{};
            if(!try_read_box_header(bytes.data(),p,end,box))return false;
            const std::string type(reinterpret_cast<const char*>(bytes.data()+p+4),4);
            if(type=="stco" || type=="co64") {
                const size_t width=type=="stco"?4:8;
                if(box.header_size!=8 || box.size<16)return false;
                const uint32_t count=read_be32u(bytes.data()+p+12);
                if(count!=(box.size-16)/width || (box.size-16)%width)return false;
                for(uint32_t i=0;i<count;++i) {
                    const size_t at=p+16+static_cast<size_t>(i)*width;
                    const uint64_t old=width==4?read_be32u(bytes.data()+at):static_cast<uint64_t>(read_be64(bytes.data()+at));
                    const mdat_map* owner=nullptr;
                    for(const auto& map:maps) if(old>=map.begin && old<map.end) {if(owner)return false;owner=&map;}
                    if(!owner || old-owner->begin>UINT64_MAX-owner->destination)return false;
                    const uint64_t value=owner->destination+(old-owner->begin);
                    if(width==4) {if(value>UINT32_MAX)return false;write_be32(bytes.data()+at,static_cast<uint32_t>(value));}
                    else {if(value>INT64_MAX)return false;write_be64(bytes.data()+at,static_cast<int64_t>(value));}
                }
            } else if(is_box_container(type) && !remap_chunks(bytes,p+box.header_size,p+box.size,maps))return false;
            p+=box.size;
        }
        return p==end;
    }
    bool canonicalize_apple_input(const std::vector<uint8_t>& input,std::vector<uint8_t>& out) {
        struct range {size_t at,size,header;};
        std::vector<range> mdats;range ftyp{},moov{};size_t p=0;
        while(p<input.size()) {
            isobmff_box_header box{};if(!try_read_box_header(input.data(),p,input.size(),box))return false;
            if(is_box_type(input.data()+p,"ftyp")) {if(ftyp.size)return false;ftyp={p,box.size,box.header_size};}
            else if(is_box_type(input.data()+p,"moov")) {if(moov.size)return false;moov={p,box.size,box.header_size};}
            else if(is_box_type(input.data()+p,"mdat")) mdats.push_back({p,box.size,box.header_size});
            else if(!is_box_type(input.data()+p,"free") && !is_box_type(input.data()+p,"wide") && !is_box_type(input.data()+p,"skip")) return false;
            p+=box.size;
        }
        if(!ftyp.size || ftyp.at!=0 || ftyp.header!=8 || ftyp.size<16 || (ftyp.size-16)%4 ||
           !moov.size || moov.header!=8 || mdats.empty())return false;
        const std::vector<uint8_t> brand={0,0,0,20,'f','t','y','p','q','t',' ',' ',0,0,0,0,'q','t',' ',' '};
        lpb::memory_random_access source{input};
        isobmff_reader_observation observed;isobmff_reader_diagnostic diagnostic;
        if(!inspect_isobmff_reader(source.view(),true,observed,diagnostic) || observed.moov_bytes.size()!=moov.size)return false;
        std::vector<uint8_t> rebuilt_moov=observed.moov_bytes;
        struct chunk_copy {uint64_t old,size;size_t field,width;int order;};
        std::vector<chunk_copy> chunks;
        for(const auto& table:observed.sample_tables) {
            int order=2;bool found=false;
            for(size_t at=8;at<rebuilt_moov.size();) {
                isobmff_box_header box{};if(!try_read_box_header(rebuilt_moov.data(),at,rebuilt_moov.size(),box))return false;
                if(is_box_type(rebuilt_moov.data()+at,"trak") && table.chunk_offsets_entries_start>=at && table.chunk_offsets_entries_start<at+box.size) {
                    if(found)return false;found=true;
                    std::vector<uint8_t> track(rebuilt_moov.begin()+at,rebuilt_moov.begin()+at+box.size);
                    const bool video=contains_atom_recursive(track,8,track.size(),"vmhd");
                    const bool audio=contains_atom_recursive(track,8,track.size(),"smhd");
                    if(video&&audio)return false;order=video?0:audio?1:2;
                }
                at+=box.size;
            }
            if(!found || (table.chunk_offset_width!=4 && table.chunk_offset_width!=8))return false;
            for(size_t i=0;i<table.chunks.size();++i) {
                const auto& c=table.chunks[i];bool owned=false;
                for(const auto& m:mdats) if(c.offset>=m.at+m.header && c.offset<=m.at+m.size && c.size<=m.at+m.size-c.offset) {
                    if(owned)return false;owned=true;
                }
                if(!owned || !c.size)return false;
                const size_t field=table.chunk_offsets_entries_start+i*table.chunk_offset_width;
                if(field>rebuilt_moov.size() || table.chunk_offset_width>rebuilt_moov.size()-field)return false;
                chunks.push_back({c.offset,c.size,field,table.chunk_offset_width,order});
            }
        }
        if(chunks.empty())return false;
        auto physical=chunks;std::sort(physical.begin(),physical.end(),[](const auto& a,const auto& b){return a.old<b.old;});
        for(size_t i=1;i<physical.size();++i) if(physical[i-1].old+physical[i-1].size>physical[i].old)return false;
        std::stable_sort(chunks.begin(),chunks.end(),[](const auto& a,const auto& b){return a.order<b.order;});
        std::vector<uint8_t> payload;
        for(const auto& c:chunks) {
            const uint64_t position=brand.size()+rebuilt_moov.size()+16ULL+payload.size();
            if(c.width==4) {if(position>UINT32_MAX)return false;write_be32(rebuilt_moov.data()+c.field,static_cast<uint32_t>(position));}
            else {if(position>INT64_MAX)return false;write_be64(rebuilt_moov.data()+c.field,static_cast<int64_t>(position));}
            payload.insert(payload.end(),input.begin()+static_cast<size_t>(c.old),input.begin()+static_cast<size_t>(c.old+c.size));
        }
        // Preserve unreferenced media bytes without letting them alter chunk ownership.
        for(const auto& m:mdats) {
            uint64_t cursor=m.at+m.header, limit=m.at+m.size;
            for(const auto& c:physical) if(c.old>=cursor && c.old<limit) {
                payload.insert(payload.end(),input.begin()+static_cast<size_t>(cursor),input.begin()+static_cast<size_t>(c.old));
                cursor=c.old+c.size;
            }
            payload.insert(payload.end(),input.begin()+static_cast<size_t>(cursor),input.begin()+static_cast<size_t>(limit));
        }
        if(payload.size()>UINT32_MAX-8)return false;
        out=brand;out.insert(out.end(),rebuilt_moov.begin(),rebuilt_moov.end());
        out.insert(out.end(),{0,0,0,8,'w','i','d','e'});append_be32(out,static_cast<uint32_t>(payload.size()+8));
        out.insert(out.end(),{'m','d','a','t'});out.insert(out.end(),payload.begin(),payload.end());
        return true;
    }

}
static lpb_result append_mebx_tracks_impl(
    lpb_context* context,
    const uint8_t* data, size_t data_size,
    double cover_seconds,
    const char* content_id,
    uint8_t* output, size_t output_size, size_t* out_written, bool preserve_meta = false)
{
    if (!context || !data || !out_written) return LPB_RESULT_INVALID_ARGUMENT;
    *out_written = 0;
    if (!std::isfinite(cover_seconds) || cover_seconds < 0.0) {
        set_error(context, "Apple cover time must be a finite non-negative value.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (!has_complete_box_sequence(data, data_size) || data_size > static_cast<size_t>(std::numeric_limits<int>::max())) {
        set_error(context, "Input MOV has an unsupported or malformed top-level box layout.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (content_id != nullptr && safe_strnlen(content_id, 256) >= 256) {
        set_error(context, "Apple ContentIdentifier is too long or not terminated.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    

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

    if (moov_body != moov_start + 8) {
        set_error(context, "Extended-size moov is unsupported by the MEBX template writer.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    auto moov_children = parse_children(data, moov_start + 8, oldMoovEnd);
    if (moov_children.empty()) {
        set_error(context, "MOV moov children are malformed or empty.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    uint32_t timescale = 600;
    uint32_t movieDuration = 0;
    bool found_mvhd = false;
    uint32_t appleTime = 0;
    for (const auto& c : moov_children) {
        if (c.type == "mvhd") {
            if (found_mvhd || c.data.size() < 28 || c.data[8] != 0) {
                set_error(context, "MOV mvhd box is truncated.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            found_mvhd = true;
            appleTime = read_be32u(c.data.data() + 12);
            timescale = read_be32u(c.data.data() + 20);
            movieDuration = read_be32u(c.data.data() + 24);
        }
    }
    if (!found_mvhd || timescale == 0 || timescale > static_cast<uint32_t>(std::numeric_limits<int>::max())) {
        set_error(context, "MOV mvhd has an unsupported or invalid timescale.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if(preserve_meta) {
        const uint32_t old_scale=timescale;
        // Keep every source edit exactly representable while giving the cover
        // at least 1/44100-second precision. Rounding source edits to a fixed
        // 44100 scale would introduce fractional-frame trim boundaries.
        const uint64_t target_scale=old_scale>=44100?old_scale:
            static_cast<uint64_t>(old_scale/std::gcd(old_scale,44100U))*44100ULL;
        if(target_scale>INT32_MAX)return LPB_RESULT_UNSUPPORTED;
        const auto target=static_cast<uint32_t>(target_scale);
        for(auto& c:moov_children)if(!rescale_apple_movie_units(c.data,0,c.data.size(),old_scale,target)) {
            set_error(context,"Movie-level timing cannot be safely mapped to a precise Apple cover scale");return LPB_RESULT_UNSUPPORTED;
        }
        const uint64_t duration=(static_cast<uint64_t>(movieDuration)*target+old_scale/2)/old_scale;
        if(duration>UINT32_MAX||duration==0)return LPB_RESULT_UNSUPPORTED;
        movieDuration=static_cast<uint32_t>(duration);timescale=target;
        // Keep rounding inside the final representable movie interval.
        cover_seconds=std::min(cover_seconds,static_cast<double>(movieDuration-1)/timescale);
    }
    std::vector<uint8_t> normVideo;
    std::vector<uint8_t> normAudio;
    int videoOldLen = 0, audioOldLen = 0;
    int video_track_count = 0, audio_track_count = 0;
    int maxTrackId = 0;
    uint32_t primaryVideoTrackId=0;

    for (const auto& c : moov_children) {
        if (c.type != "trak") continue;
        bool isVideo = contains_atom_recursive(c.data, 8, c.data.size(), "vmhd");
        bool isAudio = contains_atom_recursive(c.data, 8, c.data.size(), "smhd");
        ptrdiff_t tkhd = find_atom(c.data, 8, c.data.size(), "tkhd");
        if (tkhd >= 0 && static_cast<size_t>(tkhd) + 24 > c.data.size()) {
            set_error(context, "MOV track header is truncated.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const uint32_t tid_value = tkhd >= 0 ? read_be32u(c.data.data() + tkhd + 20) : 0;
        if (tid_value > static_cast<uint32_t>(std::numeric_limits<int>::max())) {
            set_error(context, "MOV track identifier overflows the template writer.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        int tid = static_cast<int>(tid_value);
        if (tid > maxTrackId) maxTrackId = tid;

        if (isVideo) {
            primaryVideoTrackId=tid_value;
            if (++video_track_count > 1) {
                set_error(context, "Multiple video tracks are unsupported by the MEBX template writer.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (tkhd < 0 || static_cast<size_t>(tkhd) + 92 > c.data.size()) {
                set_error(context, "MOV video track header is too short for dimensions.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            int vw = tkhd >= 0 ? (read_be32u(c.data.data() + tkhd + 84) >> 16) : 0;
            int vh = tkhd >= 0 ? (read_be32u(c.data.data() + tkhd + 88) >> 16) : 0;
            if (vw <= 0 || vh <= 0 || vw > 32767 || vh > 32767) {
                set_error(context, "MOV video dimensions are outside the template range.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            videoOldLen = static_cast<int>(c.data.size());
            normVideo = normalize_trak(c.data, true, vw, vh, appleTime, preserve_meta);
            if (normVideo.empty()) {
                set_error(context, "MOV video track could not be structurally rebuilt.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        } else if (isAudio) {
            if (++audio_track_count > 1) {
                set_error(context, "Multiple audio tracks are unsupported by the MEBX template writer.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            audioOldLen = static_cast<int>(c.data.size());
            normAudio = normalize_trak(c.data, false, 0, 0, appleTime, preserve_meta);
            if (normAudio.empty()) {
                set_error(context, "MOV audio track could not be structurally rebuilt.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
    }

    const int64_t normalizationDelta = (normVideo.empty() ? 0 : static_cast<int64_t>(normVideo.size()) - videoOldLen) +
                                       (normAudio.empty() ? 0 : static_cast<int64_t>(normAudio.size()) - audioOldLen);
    const int MetadataTrakSize = 1043 + 672 + (preserve_meta ? 20 : 0) - (std::round(cover_seconds*timescale)==0 ? 12 : 0);
    std::vector<uint8_t> contentMeta;
    int64_t replacedMetaSize = 0;
    if (content_id != nullptr) {
        contentMeta = build_apple_content_identifier_meta(content_id,preserve_meta);
        bool found_meta=false;
        for (const auto& c : moov_children) {
            if (c.type != "meta") continue;
            if(preserve_meta && (found_meta || !merge_apple_meta(c.data,content_id,contentMeta))) {
                set_error(context,"QuickTime metadata has conflicting or unsupported key ownership."); return LPB_RESULT_UNSUPPORTED;
            }
            found_meta=true;
            replacedMetaSize += static_cast<int64_t>(c.data.size());
        }
    }
    if (content_id != nullptr && contentMeta.empty()) {
        set_error(context, "Apple ContentIdentifier metadata could not be built.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const int64_t metadataDelta = static_cast<int64_t>(contentMeta.size()) - replacedMetaSize;
    const int64_t moovDelta = normalizationDelta + MetadataTrakSize + metadataDelta;
    if (moovDelta < std::numeric_limits<int>::min() || moovDelta > std::numeric_limits<int>::max()) {
        set_error(context, "MOV moov size delta overflows the template writer.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    double videoSeconds = static_cast<double>(movieDuration) / static_cast<double>(timescale);
    int sampleCount = static_cast<int>(std::clamp(std::round(videoSeconds * 60.0), 2.0, 600.0));
    int chunk1 = (sampleCount + 1) / 2;
    const int64_t sampleDataOff64 = static_cast<int64_t>(data_size) + moovDelta + 8;
    if (sampleDataOff64 < 0 || sampleDataOff64 > std::numeric_limits<int>::max()) {
        set_error(context, "MOV metadata relocation underflows the sample offset.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const int sampleDataOff = static_cast<int>(sampleDataOff64);
    const int64_t coverDataOff64 = sampleDataOff64 + static_cast<int64_t>(sampleCount) * sizeof(LivePhotoInfoSample);
    if (coverDataOff64 > std::numeric_limits<int>::max()) {
        set_error(context, "MEBX cover sample offset overflows the template writer.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    int contentTrackId = maxTrackId + 1;
    int coverTrackId = maxTrackId + 2;
    std::vector<uint8_t> contentTrak = build_content_describes_track(contentTrackId, timescale, videoSeconds, sampleCount, chunk1, sampleDataOff);
    std::vector<uint8_t> coverTrak = build_mebx_cover_track(coverTrackId, timescale, cover_seconds, static_cast<int>(coverDataOff64));
    if (contentTrak.empty() || coverTrak.empty()) {
        set_error(context, "MEBX metadata tracks could not be built safely.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if(preserve_meta) {
        if(!primaryVideoTrackId)return LPB_RESULT_INVALID_ARGUMENT;
        std::vector<uint8_t> reference(20,0);
        write_be32(reference.data(),20);write_type(reference.data(),4,"tref");
        write_be32(reference.data()+8,12);write_type(reference.data(),12,"cdsc");
        write_be32(reference.data()+16,static_cast<int32_t>(primaryVideoTrackId));
        contentTrak.insert(contentTrak.begin()+8,reference.begin(),reference.end());
        write_be32(contentTrak.data(),static_cast<int32_t>(contentTrak.size()));
    }
    patch_track_timestamps(contentTrak, appleTime);
    patch_track_timestamps(coverTrak, appleTime);

    if ((!normVideo.empty() && !shift_trak_chunk_offsets(normVideo, oldMoovEnd, static_cast<int>(moovDelta))) ||
        (!normAudio.empty() && !shift_trak_chunk_offsets(normAudio, oldMoovEnd, static_cast<int>(moovDelta)))) {
        set_error(context, "Apple MOV chunk offset table is malformed or overflows during relocation.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<std::vector<uint8_t>> newMoovPayload;
    std::vector<std::vector<uint8_t>> pending;
    for (const auto& c : moov_children) {
        if (c.type == "mvhd") {
            if (c.data.size() < 108) {
                set_error(context, "MOV mvhd box is too short for MEBX track insertion.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
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
            else if (preserve_meta) {
                auto preserved=c.data;
                if(!shift_trak_chunk_offsets(preserved,oldMoovEnd,static_cast<int>(moovDelta))) {
                    set_error(context,"Existing track offsets cannot be relocated safely."); return LPB_RESULT_UNSUPPORTED;
                }
                newMoovPayload.push_back(std::move(preserved));
            } else newMoovPayload.push_back(c.data);
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
    if (newMoov.empty()) {
        set_error(context, "Rebuilt MOV moov is too large or malformed.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (moovDelta != static_cast<int64_t>(newMoov.size()) - static_cast<int64_t>(moov_len)) {
        set_error(context, "MEBX moov size delta does not match the rebuilt box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    int samplesSize = sampleCount * sizeof(LivePhotoInfoSample) + sizeof(MebxCoverSample);
    int sampleMdatSize = 8 + samplesSize;
    const int64_t finalSize64 = static_cast<int64_t>(data_size) + moovDelta + sampleMdatSize;
    const int64_t sampleMdatOff64 = static_cast<int64_t>(data_size) + moovDelta;
    if (sampleCount <= 0 || samplesSize <= 0 || sampleMdatSize <= 8 || sampleMdatOff64 < 0 ||
        static_cast<uint64_t>(sampleMdatOff64) > std::numeric_limits<size_t>::max() || finalSize64 < 0 ||
        static_cast<uint64_t>(finalSize64) > std::numeric_limits<size_t>::max()) {
        set_error(context, "MEBX sample output size overflows.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t finalSize = static_cast<size_t>(finalSize64);
    
    *out_written = finalSize;
    if (!output || output_size < finalSize) {
        return LPB_RESULT_BUFFER_TOO_SMALL;
    }

    std::memcpy(output, data, moov_start);
    std::memcpy(output + moov_start, newMoov.data(), newMoov.size());
    std::memcpy(output + moov_start + newMoov.size(), data + oldMoovEnd, data_size - oldMoovEnd);

    const size_t sampleMdatOff = static_cast<size_t>(sampleMdatOff64);
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
#if !defined(LPB_NATIVE_TEST_HARNESS)
    // Production capability gate: appending Apple MEBX tracks rewrites the
    // container and requires an active Native cleanup-plan authority.  External
    // callers can never bypass the Cleaner trust chain through this primitive.
    if (!context) return LPB_RESULT_INVALID_ARGUMENT;
    if (!lpb_has_clean_authority(context))
    {
        set_error(context, "[AuthorityViolation] Apple MEBX track appending requires an active Native cleanup-plan authority.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
#endif

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
#if !defined(LPB_NATIVE_TEST_HARNESS)
    // Production capability gate (see lpb_apple_append_mebx_tracks).
    if (!context) return LPB_RESULT_INVALID_ARGUMENT;
    if (!lpb_has_clean_authority(context))
    {
        set_error(context, "[AuthorityViolation] Apple MEBX track appending requires an active Native cleanup-plan authority.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
#endif

    return append_mebx_tracks_impl(
        context, data, data_size, cover_seconds, content_id,
        output, output_size, out_written);
}



namespace lpb::target {
lpb_result apple_movie(lpb_context* ctx, const std::vector<uint8_t>& input, int64_t cover,
    const std::string& id, std::vector<uint8_t>& output) {
    std::vector<uint8_t> normalized;
    if(!canonicalize_apple_input(input,normalized)) {
        set_error(ctx,"Apple MOV input cannot be safely reorganized into a single mdat."); return LPB_RESULT_UNSUPPORTED;
    }
    if (cover < 0) {
        // A missing source cover must not become a fabricated zero-time cover.
        // Keep the known pairing ID without adding any timed metadata track.
        size_t moov_start{}, moov_length{}, moov_body{};
        if (!find_box_local(normalized.data(), 0, normalized.size(), "moov", moov_start, moov_length, moov_body) ||
            moov_body != moov_start + 8) return LPB_RESULT_INVALID_ARGUMENT;
        auto children = parse_children(normalized.data(), moov_body, moov_start + moov_length);
        auto meta = build_apple_content_identifier_meta(id.c_str(), true);
        if (meta.empty()) return LPB_RESULT_INVALID_ARGUMENT;
        std::vector<uint8_t> movie(8, 0);
        const int64_t delta = static_cast<int64_t>(meta.size());
        for (auto& child : children) {
            if (child.type == "meta") {
                set_error(ctx, "Untimed Apple repair requires a metadata-free prepared movie.");
                return LPB_RESULT_UNSUPPORTED;
            }
            if (child.type == "trak" &&
                !shift_trak_chunk_offsets(child.data, moov_start + moov_length, static_cast<int>(delta)))
                return LPB_RESULT_INVALID_ARGUMENT;
            movie.insert(movie.end(), child.data.begin(), child.data.end());
        }
        movie.insert(movie.end(), meta.begin(), meta.end());
        if (movie.size() > INT32_MAX) return LPB_RESULT_UNSUPPORTED;
        write_be32(movie.data(), static_cast<int32_t>(movie.size()));
        write_type(movie.data(), 4, "moov");
        output.assign(normalized.begin(), normalized.begin() + moov_start);
        output.insert(output.end(), movie.begin(), movie.end());
        output.insert(output.end(), normalized.begin() + moov_start + moov_length, normalized.end());
        return LPB_RESULT_OK;
    }
    size_t size=0;
    auto invoke=[&](uint8_t* out,size_t cap) {
        return append_mebx_tracks_impl(ctx,normalized.data(),normalized.size(),static_cast<double>(cover)/1000000.0,id.c_str(),out,cap,&size,true);
    };
    auto result=invoke(nullptr,0); if(result!=LPB_RESULT_BUFFER_TOO_SMALL) return result;
    output.resize(size); result=invoke(output.data(),output.size());
    if(result!=LPB_RESULT_OK) {output.clear();return result;} output.resize(size);
    auto boxes=parse_children(output.data(),0,output.size());
    if(boxes.size()!=5 || boxes[0].type!="ftyp" || boxes[1].type!="moov" ||
       boxes[2].type!="wide" || boxes[3].type!="mdat" || boxes[4].type!="mdat" ||
       read_be32u(boxes[3].data.data())<8 || read_be32u(boxes[4].data.data())<8) {
        set_error(ctx,"Apple MOV assembly did not produce the expected bounded top-level layout");
        output.clear();return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t mdat=boxes[0].data.size()+boxes[1].data.size()+boxes[2].data.size();
    const size_t next=mdat+boxes[3].data.size();
    if(boxes[3].data.size()>UINT32_MAX-boxes[4].data.size()+8 ||
       !shift_trak_chunk_offsets_in_range(output,boxes[0].data.size()+8,
           boxes[0].data.size()+boxes[1].data.size(),next+8,-8)) {
        set_error(ctx,"Apple MOV sample offsets could not be relocated during final mdat merge");
        output.clear();return LPB_RESULT_INVALID_ARGUMENT;
    }
    write_be32(output.data()+mdat,static_cast<uint32_t>(boxes[3].data.size()+boxes[4].data.size()-8));
    output.erase(output.begin()+next,output.begin()+next+8);
    return LPB_RESULT_OK;
}
}
