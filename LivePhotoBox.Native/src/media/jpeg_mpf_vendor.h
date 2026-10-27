#pragma once
#include "media/jpeg_gainmap_mpf.h"

namespace lpb::media {
struct mpf_vendor_primary {
    uint64_t image_end{}, auxiliary_offset{}, auxiliary_length{}, metadata_offset{}, metadata_length{};
};
inline bool valid_mpf_owned_secondary(std::span<const uint8_t> image) noexcept {
    if(jpeg_gainmap_mpf::valid_jpeg_media_range(image,0,image.size()))return true;
    // An MPF-owned secondary camera JPEG may retain an attribute-only TIFF
    // declaring MPFVersion and NumberOfImages without MPEntry. It supplies no
    // ranges: the OUTER complete MP Index is the sole owner. Do not relax the
    // normal primary/index parser or accept this as a standalone MP Index.
    memory_random_access memory{image};uint64_t end=0;unsigned attributes=0;
    const bool valid=walk_jpeg_markers(memory.view(),end,[&](const jpeg_marker_range& marker) {
        if(marker.marker!=0xe2||marker.length<4||std::memcmp(image.data()+marker.payload,"MPF\0",4))return true;
        if(++attributes!=1||marker.length!=42)return false;
        const auto p=image.subspan(static_cast<size_t>(marker.payload),42);const size_t t=4;
        const bool little=p[t]=='I'&&p[t+1]=='I';if(!little&&!(p[t]=='M'&&p[t+1]=='M'))return false;
        using namespace jpeg_gainmap_mpf::detail;
        if(read_u16(p,t+2,little)!=42||read_u32(p,t+4,little)!=8||read_u16(p,t+8,little)!=2||read_u32(p,38,little))return false;
        bool version=false,count=false;
        for(size_t i=0;i<2;++i) {
            const size_t a=14+i*12;const auto tag=read_u16(p,a,little);
            if(tag==0xb000&&!version&&read_u16(p,a+2,little)==7&&read_u32(p,a+4,little)==4&&std::memcmp(p.data()+a+8,"0100",4)==0)version=true;
            else if(tag==0xb001&&!count&&read_u16(p,a+2,little)==4&&read_u32(p,a+4,little)==1&&read_u32(p,a+8,little)==2)count=true;
            else return false;
        }
        return version&&count;
    });
    return valid&&attributes==1&&end==image.size();
}
// Read-only inventory of an MPF-owned second JPEG and optional opaque camera
// metadata. No GainMap/HDR semantic is inferred from MPF or from a signature.
// VLDMAGIC is documented ordinary camera data; its bytes are retained, never
// interpreted as protocol authority or emitted from a source-path lookup.
inline bool observe_mpf_vendor_primary(std::span<const uint8_t> input,mpf_vendor_primary& out) noexcept {
    out={};jpeg_gainmap_mpf::jpeg_info primary;
    if(!jpeg_gainmap_mpf::parse_jpeg(input,primary)||primary.mpf_status!=jpeg_gainmap_mpf::status::present||primary.mpf.image_count!=2)
        return false;
    const auto& first=primary.mpf.entries[0];const auto& second=primary.mpf.entries[1];
    if(first.absolute_offset||first.size!=primary.eoi_end||first.dependent_image_1||first.dependent_image_2||
       second.absolute_offset!=primary.eoi_end||!second.size||second.absolute_offset>input.size()||
       second.size>input.size()-second.absolute_offset||second.dependent_image_1||second.dependent_image_2||
       !valid_mpf_owned_secondary(input.subspan(static_cast<size_t>(second.absolute_offset),second.size)))return false;
    const uint64_t end=second.absolute_offset+second.size;
    if(end<input.size()&&(input.size()-end<8||std::memcmp(input.data()+static_cast<size_t>(end),"VLDMAGIC",8)))return false;
    out={primary.eoi_end,second.absolute_offset,second.size,end,input.size()-end};return true;
}
}
