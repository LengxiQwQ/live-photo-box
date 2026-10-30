#pragma once
#include "media/target_primary_observation.h"
#include "media/jpeg_marker.h"
#include <algorithm>
#include <cstdint>

#include <limits>

#include <set>

#include <span>

#include <vector>

namespace lpb::media {
// Private typed TIFF/thumbnail mapper shared by bound image proof producers.
inline bool map_image_exif_orientation(std::vector<uint8_t>& payload,const target_primary_observation& facts) {
    if(payload.empty())return facts.orientation==1;
    uint32_t orientation=1;exif_observation exif;jpeg_observation_failure failure{};
    if(!observe_exif(payload,orientation,exif,failure,true)||orientation!=facts.orientation)return false;
    auto tiff=std::span<uint8_t>(payload).subspan(6);const bool be=exif.big_endian;
    const auto read=[&](size_t at,size_t size)->uint32_t {
        uint32_t value{};for(size_t i=0;i<size;++i)value|=uint32_t(tiff[at+i])<<((be?size-i-1:i)*8);return value;
    };
    const auto store=[&](size_t at,size_t size,uint32_t value) {
        for(size_t i=0;i<size;++i)tiff[at+i]=static_cast<uint8_t>(value>>((be?size-i-1:i)*8));
    };
    // A thumbnail with an active inherited transform needs its own verified
    // physical mapping. Retaining its old orientation is not normalization.
    if(exif.ifd0_offset>tiff.size()||tiff.size()-exif.ifd0_offset<6)return false;
    const auto count=read(exif.ifd0_offset,2);
    if(count>(tiff.size()-exif.ifd0_offset-6)/12)return false;
    uint32_t thumbnail=read(exif.ifd0_offset+2+count*12,4);
    if(orientation!=1&&thumbnail)return false;
    for(unsigned depth=0;thumbnail;++depth) {
        if(depth>=31||thumbnail>tiff.size()||tiff.size()-thumbnail<6)return false;
        const auto n=read(thumbnail,2);if(n>(tiff.size()-thumbnail-6)/12)return false;
        uint32_t jpeg_offset{},jpeg_length{};
        for(size_t i=0;i<n;++i) {
            const size_t at=thumbnail+2+i*12;const auto tag=read(at,2);
            if(tag!=0x112&&tag!=0x201&&tag!=0x202)continue;
            const auto type=read(at+2,2);
            if(read(at+4,4)!=1||(tag==0x112?type!=3:type!=4))return false;
            const auto value=read(at+8,type==3?2:4);
            if(tag==0x112&&value!=1)return false;
            if(tag==0x201)jpeg_offset=value;if(tag==0x202)jpeg_length=value;
        }
        if(bool(jpeg_offset)!=bool(jpeg_length))return false;
        if(jpeg_length) {
            if(jpeg_offset>tiff.size()||jpeg_length>tiff.size()-jpeg_offset)return false;
            const auto data=std::span<const uint8_t>(tiff).subspan(jpeg_offset,jpeg_length);
            lpb::memory_random_access memory{data};jpeg_observation thumb;jpeg_observation_failure why{};uint64_t bad{};
            if(!observe_jpeg(memory.view(),thumb,why,bad,{true,false,false,false})||thumb.orientation!=1||
                !thumb.exif_payload.empty()||thumb.metadata_after_scan||thumb.eoi_end>data.size()||
                !std::all_of(data.begin()+static_cast<size_t>(thumb.eoi_end),data.end(),[](uint8_t value){return value==0;}))return false;
            std::string normalized;xmp_observation_failure xml_failure{};
            if(!rewrite_neutral_image_xmp(thumb.xmp,"image/jpeg","image/jpeg",1,thumb.width,thumb.height,normalized,xml_failure)||normalized!=thumb.xmp)return false;
        }
        thumbnail=read(thumbnail+2+n*12,4);
    }
    if(orientation==1)return true;
    const uint32_t out_width=orientation>=5?facts.height:facts.width,out_height=orientation>=5?facts.width:facts.height;
    bool found_orientation=false;
    const auto map_ifd=[&](uint32_t offset,bool root) {
        if(!offset||offset>tiff.size()||tiff.size()-offset<6)return false;
        const auto n=read(offset,2);if(n>(tiff.size()-offset-6)/12)return false;
        std::set<uint32_t> tags;
        for(size_t i=0;i<n;++i) {
            const size_t at=offset+2+i*12;const auto tag=read(at,2);
            if(!tags.insert(tag).second)return false;
            // SubjectArea/SubjectLocation are coordinates, not passive strings.
            // Do not retain stale active coordinates after rotating the raster.
            if(!root&&(tag==0x9214||tag==0xa214))return false;
            const bool orient=root&&tag==0x112;
            const bool horizontal=root?tag==0x100:tag==0xa002,vertical=root?tag==0x101:tag==0xa003;
            if(!orient&&!horizontal&&!vertical)continue;
            const auto type=read(at+2,2),elements=read(at+4,4);
            if(elements!=1||(type!=3&&type!=4)||(orient&&type!=3))return false;
            const size_t size=type==3?2U:4U;const auto old=read(at+8,size);
            const uint32_t expected=orient?orientation:horizontal?facts.width:facts.height;
            const uint32_t value=orient?1:horizontal?out_width:out_height;
            if(old!=expected&&!(old==0&&!orient))return false;
            if(old)store(at+8,size,value);if(orient)found_orientation=true;
        }
        return true;
    };
    if(!map_ifd(exif.ifd0_offset,true)||!found_orientation)return false;
    if(exif.exif_ifd_offset) {
        const auto offset=exif.exif_ifd_offset;
        if(offset<tiff.size()&&tiff.size()-offset>=6) {
            const auto n=read(offset,2);
            if(n>(tiff.size()-offset-6)/12)return false;
            const uint64_t root_end=uint64_t(exif.ifd0_offset)+6+uint64_t(count)*12,other_end=uint64_t(offset)+6+uint64_t(n)*12;
            if(offset<root_end&&exif.ifd0_offset<other_end)return false;
        }
        if(!map_ifd(offset,false))return false;
    }
    uint32_t normalized{};exif_observation after;
    return observe_exif(payload,normalized,after,failure,true)&&normalized==1;
}

inline bool standalone_jpeg_codestream(std::span<const uint8_t> bytes) noexcept {
    if (bytes.size() < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
    size_t cursor = 2;
    bool saw_scan = false;
    while (cursor < bytes.size()) {
        if (bytes[cursor] != 0xFF) return false;
        while (cursor < bytes.size() && bytes[cursor] == 0xFF) ++cursor;
        if (cursor >= bytes.size()) return false;
        const uint8_t marker = bytes[cursor++];
        if (marker == 0xD9) return !saw_scan && cursor == bytes.size();
        if (marker == 0x00 || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
            return false;
        if (cursor + 2 > bytes.size()) return false;
        const size_t segment_length = (static_cast<size_t>(bytes[cursor]) << 8) | bytes[cursor + 1];
        if (segment_length < 2 || segment_length > bytes.size() - cursor) return false;
        cursor += segment_length;
        if (marker != 0xDA) continue;
        if (saw_scan) return false;
        saw_scan = true;
        while (cursor + 1 < bytes.size()) {
            if (bytes[cursor] != 0xFF) { ++cursor; continue; }
            size_t marker_start = cursor++;
            while (cursor < bytes.size() && bytes[cursor] == 0xFF) ++cursor;
            if (cursor >= bytes.size()) return false;
            const uint8_t scan_marker = bytes[cursor++];
            if (scan_marker == 0x00 || (scan_marker >= 0xD0 && scan_marker <= 0xD7)) continue;
            if (scan_marker == 0xD9) return cursor == bytes.size();
            static_cast<void>(marker_start);
            return false;
        }
        return false;
    }
    return false;
}

inline bool insert_exact_exif(std::span<const uint8_t> jpeg, std::span<const uint8_t> payload,
    std::vector<uint8_t>& output, uint32_t expected_orientation = 1) {
    if (payload.empty()) { output.assign(jpeg.begin(), jpeg.end()); return true; }
    if (!standalone_jpeg_codestream(jpeg) || payload.size() > 65533U) return false;
    uint32_t orientation = 0;
    exif_observation observed;
    jpeg_observation_failure failure{};
    if (!observe_exif(payload, orientation, observed, failure, true) || orientation != expected_orientation) return false;
    output.assign(jpeg.begin(), jpeg.begin() + 2);
    const auto length = static_cast<uint16_t>(payload.size() + 2U);
    output.insert(output.end(), { 0xff, 0xe1, static_cast<uint8_t>(length >> 8), static_cast<uint8_t>(length) });
    output.insert(output.end(), payload.begin(), payload.end());
    output.insert(output.end(), jpeg.begin() + 2, jpeg.end());
    return standalone_jpeg_codestream(output);
}
}
