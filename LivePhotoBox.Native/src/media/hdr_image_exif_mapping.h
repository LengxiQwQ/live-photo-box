#pragma once
#include "media/image_exif_mapping.h"
#include "media/jpeg_backend.h"
#include "media/pixel_orientation.h"

namespace lpb::media {
// HDR proof producer only. Preserve the original TIFF bytes and append the
// physically normalized thumbnail; only typed active geometry/offset fields
// change. This does not broaden the ordinary-image proof's thumbnail scope.
inline bool map_hdr_image_exif(lpb_context* context,std::vector<uint8_t>& payload,
    const target_primary_observation& facts) {
    if(payload.empty()||facts.orientation==1)return map_image_exif_orientation(payload,facts);
    uint32_t direction{};exif_observation observed;jpeg_observation_failure failure{};
    if(!observe_exif(payload,direction,observed,failure,true)||direction!=facts.orientation)return false;
    auto result=payload;const bool be=observed.big_endian;
    const auto read=[&](size_t at,size_t n)->uint32_t {
        uint32_t v{};for(size_t i=0;i<n;++i)v|=uint32_t(result[6+at+i])<<((be?n-i-1:i)*8);return v;
    };
    const auto put=[&](size_t at,size_t n,uint32_t v){
        for(size_t i=0;i<n;++i)result[6+at+i]=static_cast<uint8_t>(v>>((be?n-i-1:i)*8));
    };
    const size_t size=result.size()-6,root=observed.ifd0_offset;
    if(root>size||size-root<6)return false;
    const auto count=read(root,2);if(count>(size-root-6)/12)return false;
    const size_t next=root+2+12*count;const uint32_t thumb=read(next,4);
    if(!thumb)return map_image_exif_orientation(payload,facts);
    if(thumb>size||size-thumb<6)return false;
    const auto n=read(thumb,2);if(n>(size-thumb-6)/12||read(thumb+2+12*n,4))return false;
    size_t offset_field{},length_field{},width_field{},height_field{},orientation_field{};
    uint32_t offset{},length{},width{},height{},compression{};std::set<uint32_t> tags;
    for(size_t i=0;i<n;++i) {
        const size_t at=thumb+2+12*i;const auto tag=read(at,2),type=read(at+2,2),elements=read(at+4,4);
        if(!tags.insert(tag).second)return false;
        // Other IFD1 layouts need their own coordinate/ownership mapping.
        if(tag!=0x100&&tag!=0x101&&tag!=0x103&&tag!=0x112&&tag!=0x11a&&tag!=0x11b&&
            tag!=0x128&&tag!=0x201&&tag!=0x202)return false;
        if(elements!=1)return false;
        if(tag==0x11a||tag==0x11b){if(type!=5)return false;continue;}
        if(type!=3&&type!=4)return false;
        const auto v=read(at+8,type==3?2:4);
        if(tag==0x100){width=v;width_field=at;}
        if(tag==0x101){height=v;height_field=at;}
        if(tag==0x103)compression=v;
        if(tag==0x112){if(type!=3||v!=direction)return false;orientation_field=at;}
        if(tag==0x201){if(type!=4)return false;offset=v;offset_field=at;}
        if(tag==0x202){if(type!=4)return false;length=v;length_field=at;}
    }
    if(compression!=6||!offset_field||!length_field||!width_field||!height_field||!width||!height||
        !length||offset>size||length>size-offset||uint64_t(width)*facts.height!=uint64_t(height)*facts.width)return false;
    for(const auto& range:observed.occupied_tiff_ranges)
        if(offset<range.second&&range.first<uint64_t(offset)+length)return false;
    const auto thumbnail=std::span<const uint8_t>(result).subspan(6+offset,length);
    lpb::memory_random_access reader{thumbnail};jpeg_observation jpeg;uint64_t bad{};
    if(!observe_jpeg(reader.view(),jpeg,failure,bad,{true,false,false,false})||jpeg.eoi_end!=thumbnail.size()||
        jpeg.width!=width||jpeg.height!=height||!jpeg.exif_payload.empty()||!jpeg.xmp.empty()||jpeg.metadata_after_scan)return false;
    uint64_t end{},icc_bytes{};std::vector<uint8_t> original_jfif;
    if(!walk_jpeg_markers(reader.view(),end,[&](const jpeg_marker_range& marker){
        if(marker.marker==0xc0||marker.marker==0xc1||marker.marker==0xc2) {
            const auto frame=thumbnail.subspan(static_cast<size_t>(marker.payload),static_cast<size_t>(marker.length));
            return frame.size()>=6&&(frame[5]==1||frame[5]==3);
        }
        if((marker.marker<0xe0||marker.marker>0xef)&&marker.marker!=0xfe)return true;
        const auto p=thumbnail.subspan(static_cast<size_t>(marker.payload),static_cast<size_t>(marker.length));
        if(marker.marker==0xe2&&p.size()>=14&&!std::memcmp(p.data(),"ICC_PROFILE\0",12)) {
            icc_bytes+=p.size()-14;return true;
        }
        if(marker.marker!=0xe0||!original_jfif.empty()||p.size()!=14||std::memcmp(p.data(),"JFIF\0",5)||
            p[5]!=1||p[6]>2||p[12]||p[13]||p[8]!=p[10]||p[9]!=p[11])return false;
        original_jfif.assign(thumbnail.begin()+static_cast<ptrdiff_t>(marker.start),
            thumbnail.begin()+static_cast<ptrdiff_t>(marker.payload+marker.length));return true;
    }))return false;
    jpeg_rgb_image decoded;if(decode_jpeg_rgb_bytes(context,thumbnail,decoded)!=LPB_RESULT_OK||icc_bytes!=decoded.icc_profile.size())return false;
    pixel_surface pixels;pixels.width=decoded.width;pixels.height=decoded.height;pixels.channels=3;
    pixels.signal_bit_depth=pixels.storage_bit_depth=8;pixels.stride=uint64_t(pixels.width)*3;pixels.pixels=std::move(decoded.pixels);
    if(!normalize_pixel_orientation(pixels,direction))return false;
    std::vector<uint8_t> encoded;
    if(encode_rgb_jpeg_bytes(context,pixels.width,pixels.height,pixels.pixels,95,decoded.icc_profile,encoded)!=LPB_RESULT_OK||
        encoded.size()>UINT32_MAX||size>UINT32_MAX)return false;
    if(!original_jfif.empty()) {
        lpb::memory_random_access generated{encoded};std::vector<std::pair<size_t,size_t>> jfif;
        if(!walk_jpeg_markers(generated.view(),end,[&](const jpeg_marker_range& marker){
            if(marker.marker==0xe0)jfif.emplace_back(static_cast<size_t>(marker.start),static_cast<size_t>(marker.payload+marker.length));
            return true;
        })||jfif.size()>1)return false;
        if(!jfif.empty())encoded.erase(encoded.begin()+static_cast<ptrdiff_t>(jfif[0].first),encoded.begin()+static_cast<ptrdiff_t>(jfif[0].second));
        encoded.insert(encoded.begin()+2,original_jfif.begin(),original_jfif.end());
    }
    jpeg_rgb_image reread;
    if(decode_jpeg_rgb_bytes(context,encoded,reread)!=LPB_RESULT_OK||reread.width!=pixels.width||reread.height!=pixels.height||
        reread.icc_profile!=decoded.icc_profile)return false;
    // The existing mapper sees no inherited thumbnail until its actual raster
    // and active fields have been mapped. Restore that exact owner afterwards.
    put(next,4,0);
    if(!map_image_exif_orientation(result,facts))return false;
    put(next,4,thumb);put(offset_field+8,4,static_cast<uint32_t>(size));put(length_field+8,4,static_cast<uint32_t>(encoded.size()));
    put(width_field+8,read(width_field+2,2)==3?2:4,pixels.width);
    put(height_field+8,read(height_field+2,2)==3?2:4,pixels.height);
    if(orientation_field)put(orientation_field+8,2,1);
    result.insert(result.end(),encoded.begin(),encoded.end());
    target_primary_observation normal=facts;normal.width=direction>=5?facts.height:facts.width;
    normal.height=direction>=5?facts.width:facts.height;normal.orientation=1;
    if(!map_image_exif_orientation(result,normal))return false;
    payload=std::move(result);return true;
}
}
