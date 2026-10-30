#pragma once
#include "media/ordinary_metadata_capsule.h"
#include "media/jpeg_gainmap_mpf.h"

namespace lpb::media::ordinary_metadata {
// Framing helpers only. The Writer must first bind and validate the complete
// image/item graph through its retained input lease; these helpers do not
// establish image validity or mutation authority.
enum class observation { absent,present,malformed };
struct observed_capsule {
    std::vector<uint8_t> raw;
    std::vector<std::pair<size_t,size_t>> ranges;
    uint32_t chunks{};
};

inline observation observe_jpeg_capsule(std::span<const uint8_t> bytes,observed_capsule& output) noexcept {
    output={};
    try {
        lpb::memory_random_access source{bytes};uint64_t eoi=0;
        std::vector<std::span<const uint8_t>> parts;observed_capsule observed;bool closed=false;
        if(!lpb::media::walk_jpeg_markers(source.view(),eoi,[&](const jpeg_marker_range& marker){
            if(marker.payload>bytes.size()||marker.length>bytes.size()-marker.payload)return false;
            const auto payload=bytes.subspan(static_cast<size_t>(marker.payload),static_cast<size_t>(marker.length));
            const bool owned=marker.marker==0xef&&namespace_claim(payload);
            if(!owned){if(!parts.empty())closed=true;return true;}
            if(closed||marker.after_scan||parts.size()>=(maximum_raw+chunk_bytes-1)/chunk_bytes)return false;
            parts.push_back(payload);observed.ranges.emplace_back(static_cast<size_t>(marker.start),static_cast<size_t>(marker.payload+marker.length));return true;
        }))return observation::malformed;
        if(parts.empty())return observation::absent;
        if(!decode_chunks(parts,observed.raw))return observation::malformed;
        observed.chunks=static_cast<uint32_t>(parts.size());output=std::move(observed);return observation::present;
    }catch(...){output={};return observation::malformed;}
}

inline bool insert_jpeg_capsule(std::span<const uint8_t> primary,std::span<const uint8_t> raw,
    std::vector<uint8_t>& output) noexcept {
    output.clear();
    try {
        observed_capsule existing;
        if(observe_jpeg_capsule(primary,existing)!=observation::absent)return false;
        jpeg_gainmap_mpf::jpeg_info before;
        if(!jpeg_gainmap_mpf::parse_jpeg(primary,before))return false;
        // Reuse the existing bounded singleton self-entry rule. Only that
        // exact no-external-image shape permits its redundant old size to be
        // stale; multi-image ownership keeps the exact original-size check.
        const bool singleton=jpeg_gainmap_mpf::singleton_primary(before)&&before.eoi_end==primary.size();
        if(before.mpf_status==jpeg_gainmap_mpf::status::present&&before.mpf.entries[0].size!=before.eoi_end&&!singleton)return false;
        std::vector<std::vector<uint8_t>> chunks;
        if(!encode_chunks(raw,chunks))return false;
        const uint64_t added=raw.size()+chunks.size()*(header_size+4ULL);
        if(primary.size()>64ULL*1024*1024||added>64ULL*1024*1024-primary.size())return false;
        std::vector<uint8_t> result(primary.begin(),primary.begin()+2);result.reserve(primary.size()+static_cast<size_t>(added));
        for(const auto& part:chunks){const size_t length=part.size()+2;result.insert(result.end(),{0xff,0xef,static_cast<uint8_t>(length>>8),static_cast<uint8_t>(length)});result.insert(result.end(),part.begin(),part.end());}
        result.insert(result.end(),primary.begin()+2,primary.end());
        jpeg_gainmap_mpf::jpeg_info after;
        if(!jpeg_gainmap_mpf::parse_jpeg(result,after)||after.eoi_end!=before.eoi_end+added||after.mpf_status!=before.mpf_status)return false;
        if(after.mpf_status==jpeg_gainmap_mpf::status::present) {
            if(after.eoi_end>UINT32_MAX||after.mpf.image_count!=before.mpf.image_count)return false;
            for(size_t i=1;i<after.mpf.image_count;++i)if(after.mpf.entries[i].absolute_offset!=before.mpf.entries[i].absolute_offset+added||after.mpf.entries[i].size!=before.mpf.entries[i].size)return false;
            jpeg_gainmap_mpf::write_u32(result,after.mpf.entries[0].size_field_offset,static_cast<uint32_t>(after.eoi_end),after.mpf.little_endian);
        }
        observed_capsule observed;
        if(observe_jpeg_capsule(result,observed)!=observation::present||observed.raw.size()!=raw.size()||!std::equal(raw.begin(),raw.end(),observed.raw.begin()))return false;
        output=std::move(result);return true;
    }catch(...){output.clear();return false;}
}

inline observation observe_heif_capsule(std::span<const uint8_t> bytes,observed_capsule& output) noexcept {
    output={};
    try {
        observed_capsule observed;bool found=false;size_t at=0;
        while(at<bytes.size()) {
            isobmff_box_header box{};
            if(!try_read_box_header(bytes.data(),at,bytes.size(),box)||box.extends_to_end)return observation::malformed;
            const size_t body=at+box.header_size,end=at+box.size;
            if(std::string_view(reinterpret_cast<const char*>(bytes.data()+at+4),4)=="uuid"&&
                end-body>=name_space.size()&&namespace_claim(bytes.subspan(body,end-body))) {
                if(found||end-body<24)return observation::malformed;found=true;
                if(get32(bytes,body+16)!=1)return observation::malformed;
                const uint32_t count=get32(bytes,body+20);
                if(!count||count>(maximum_raw+chunk_bytes-1)/chunk_bytes)return observation::malformed;
                std::vector<std::span<const uint8_t>> chunks;size_t cursor=body+24;
                for(uint32_t i=0;i<count;++i) {
                    if(end-cursor<4)return observation::malformed;
                    const size_t length=get32(bytes,cursor);cursor+=4;
                    if(length>end-cursor||length>maximum_payload)return observation::malformed;
                    chunks.emplace_back(bytes.subspan(cursor,length));cursor+=length;
                }
                if(cursor!=end||!decode_chunks(chunks,observed.raw))return observation::malformed;
                observed.ranges.emplace_back(at,end);observed.chunks=count;
            }
            at=end;
        }
        if(!found)return observation::absent;output=std::move(observed);return observation::present;
    }catch(...){output={};return observation::malformed;}
}

inline bool insert_heif_capsule(std::span<const uint8_t> primary,std::span<const uint8_t> raw,std::vector<uint8_t>& output) noexcept {
    output.clear();
    try {
        observed_capsule observed;if(observe_heif_capsule(primary,observed)!=observation::absent)return false;
        std::vector<std::vector<uint8_t>> chunks;if(!encode_chunks(raw,chunks))return false;
        const uint64_t size=8+24+raw.size()+chunks.size()*(header_size+4ULL);
        if(size>UINT32_MAX||primary.size()>64ULL*1024*1024||size>64ULL*1024*1024-primary.size())return false;
        // A final ordinary SEFD index owns EOF. Keep it final and move the
        // complete unchanged box. The caller's item-graph validation is a
        // prerequisite; item extents may only use its validated media owners.
        size_t insertion=primary.size(),cursor=0;
        while(cursor<primary.size()) {
            isobmff_box_header box{};if(!try_read_box_header(primary.data(),cursor,primary.size(),box)||box.extends_to_end)return false;
            if(std::string_view(reinterpret_cast<const char*>(primary.data()+cursor+4),4)=="sefd") {
                if(insertion!=primary.size()||cursor+box.size!=primary.size())return false;insertion=cursor;
            }
            cursor+=box.size;
        }
        std::vector<uint8_t> result(primary.begin(),primary.begin()+static_cast<ptrdiff_t>(insertion));const size_t start=result.size();
        result.resize(start+32);put32(result,start,static_cast<uint32_t>(size));
        result[start+4]='u';result[start+5]='u';result[start+6]='i';result[start+7]='d';
        std::copy(name_space.begin(),name_space.end(),result.begin()+static_cast<ptrdiff_t>(start+8));
        put32(result,start+24,1);put32(result,start+28,static_cast<uint32_t>(chunks.size()));
        for(const auto& part:chunks){const size_t at=result.size();result.resize(at+4);put32(result,at,static_cast<uint32_t>(part.size()));result.insert(result.end(),part.begin(),part.end());}
        if(result.size()!=start+size)return false;
        result.insert(result.end(),primary.begin()+static_cast<ptrdiff_t>(insertion),primary.end());
        if(observe_heif_capsule(result,observed)!=observation::present||observed.raw.size()!=raw.size()||!std::equal(raw.begin(),raw.end(),observed.raw.begin()))return false;
        output=std::move(result);return true;
    }catch(...){output.clear();return false;}
}
}
