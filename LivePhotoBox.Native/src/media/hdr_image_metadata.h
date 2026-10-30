#pragma once
#include "media/target_primary_observation.h"
#include "media/jpeg_backend.h"
#include "media/jpeg_marker.h"
#include "media/target_writer.h" // Read-only neutral Exif/CID eligibility.
#include "containers/original_jpeg_metadata.h"
#include "protocols/samsung_inventory.h"
#include "foundation/sha256_core.h"
#include "containers/original_jpeg_padding.h"
#include <array>
#include <cstring>

namespace lpb::media {
// Native-private producer inventory. Offsets in IMv1 always refer to the single
// bound original JPEG artifact, not to either current HEIF metadata owner.
struct hdr_image_metadata_inventory {
    std::vector<uint8_t> primary_exif,primary_icc,gainmap_exif,gainmap_icc,original_markers,ordinary_sef;
    std::string primary_xmp,gainmap_xmp;
    // Independent original-container byte preservation gate, not a ninth
    // metadata item and not a reinterpretation of the frozen ledger encoding.
    std::vector<uint8_t> original_padding_record;
};
inline auto hdr_image_metadata_parts(const hdr_image_metadata_inventory& value) {
    const auto xml=[](const std::string& s){return std::span<const uint8_t>(reinterpret_cast<const uint8_t*>(s.data()),s.size());};
    return std::array<std::span<const uint8_t>,8>{value.primary_exif,xml(value.primary_xmp),value.primary_icc,
        value.gainmap_exif,xml(value.gainmap_xmp),value.gainmap_icc,value.original_markers,value.ordinary_sef};
}
inline void hdr_image_metadata_hash(const hdr_image_metadata_inventory& value,uint8_t output[32]) {
    lpb::crypto::sha256_ctx hash;const auto parts=hdr_image_metadata_parts(value);
    for(size_t i=0;i<parts.size();++i) {
        uint8_t header[9]{static_cast<uint8_t>(i+1)};
        for(unsigned j=0;j<8;++j)header[j+1]=static_cast<uint8_t>(uint64_t(parts[i].size())>>(j*8));
        hash.update(header,sizeof(header));if(!parts[i].empty())hash.update(parts[i].data(),parts[i].size());
    }
    hash.finalize(output);
}
inline bool capture_hdr_jpeg_image_metadata(std::span<const uint8_t> source,lpb_media_range gainmap_range,
    const jpeg_rgb_image& primary_decoded,const jpeg_rgb_image& gainmap_decoded,
    target_primary_observation& observed_primary,jpeg_observation& observed_gainmap,hdr_image_metadata_inventory& output,
    std::string& error) noexcept {
    output={};observed_primary={};observed_gainmap={};error.clear();
    try {
        target_primary_observation primary;jpeg_observation gainmap;
        if(source.size()>32ULL*1024*1024||!observe_target_primary(source,primary,error)||
            primary.container!=LPB_IMAGE_CONTAINER_JPEG||primary.has_video||primary.auxiliaries.size()!=1||
            primary.auxiliaries[0].semantic!="GainMap"||primary.auxiliaries[0].range.offset!=gainmap_range.offset||
            primary.auxiliaries[0].range.length!=gainmap_range.length||target_primary_has_live_binding(primary)||
            primary.jpeg.metadata_after_scan||primary.jpeg.extended||!primary.extended_xml.empty()||
            primary.orientation<1||primary.orientation>8||primary.width!=primary_decoded.width||primary.height!=primary_decoded.height||
            !gainmap_range.length||gainmap_range.offset<primary.jpeg.eoi_end||gainmap_range.offset>source.size()||
            gainmap_range.length!=source.size()-gainmap_range.offset)return false;
        const auto auxiliary=source.subspan(static_cast<size_t>(gainmap_range.offset),static_cast<size_t>(gainmap_range.length));
        lpb::memory_random_access aux_reader{auxiliary};jpeg_observation_failure why{};uint64_t bad{};
        if(!observe_jpeg(aux_reader.view(),gainmap,why,bad,{true,false,true,false,true})||gainmap.metadata_after_scan||gainmap.extended||
            gainmap.width!=gainmap_decoded.width||gainmap.height!=gainmap_decoded.height||gainmap.eoi_end>auxiliary.size())return false;
        hdr_image_metadata_inventory captured;
        if(!original_jpeg_padding::encode(source,gainmap_range.offset+gainmap.eoi_end,captured.original_padding_record))return false;
        captured.primary_exif=primary.exif_payload;captured.primary_xmp=primary.standard_xml;captured.primary_icc=primary_decoded.icc_profile;
        captured.gainmap_exif=gainmap.exif_payload;captured.gainmap_xmp=gainmap.xmp;captured.gainmap_icc=gainmap_decoded.icc_profile;
        if((!captured.primary_exif.empty()&&observe_target_neutral_exif(captured.primary_exif)!=LPB_RESULT_OK)||
            (!captured.gainmap_exif.empty()&&observe_target_neutral_exif(captured.gainmap_exif)!=LPB_RESULT_OK))return false;
        if(gainmap_range.offset>primary.jpeg.eoi_end) {
            lpb::memory_random_access reader{source};lpb::protocols::sef_inventory sef;std::string problem;
            if(!lpb::protocols::observe_sef(reader.view(),primary.jpeg.eoi_end,gainmap_range.offset,sef,problem)||
                sef.begin!=primary.jpeg.eoi_end||!lpb::protocols::ordinary_sef(sef))return false;
            captured.ordinary_sef.assign(source.begin()+static_cast<ptrdiff_t>(primary.jpeg.eoi_end),
                source.begin()+static_cast<ptrdiff_t>(gainmap_range.offset));
        }
        std::vector<original_jpeg_metadata::marker> markers;
        const auto capture=[&](std::span<const uint8_t> jpeg,uint64_t base,std::span<const uint8_t> icc) {
            lpb::memory_random_access reader{jpeg};uint64_t eoi{},icc_bytes{};
            std::vector<original_jpeg_metadata::marker> raw_icc;
            const bool valid=walk_jpeg_markers(reader.view(),eoi,[&](const jpeg_marker_range& marker) {
                if(marker.marker==0xc0||marker.marker==0xc1||marker.marker==0xc2) {
                    const auto frame=jpeg.subspan(static_cast<size_t>(marker.payload),static_cast<size_t>(marker.length));
                    return frame.size()>=6&&(frame[5]==1||frame[5]==3); // No unproven CMYK/YCCK -> RGB mapping.
                }
                if(marker.after_scan&&((marker.marker>=0xe0&&marker.marker<=0xef)||marker.marker==0xfe))return false;
                if(!((marker.marker>=0xe0&&marker.marker<=0xef)||marker.marker==0xfe))return true;
                const auto payload=jpeg.subspan(static_cast<size_t>(marker.payload),static_cast<size_t>(marker.length));
                if(marker.marker==0xe1) {
                    if((payload.size()>=6&&!std::memcmp(payload.data(),"Exif\0\0",6))||
                        (payload.size()>=29&&!std::memcmp(payload.data(),"http://ns.adobe.com/xap/1.0/\0",29)))return true;
                    return false; // Unknown/extended APP1 is not a proven metadata owner.
                }
                const auto save=[&](std::vector<original_jpeg_metadata::marker>& to) {
                    to.push_back({base+marker.start,std::vector<uint8_t>(jpeg.begin()+static_cast<ptrdiff_t>(marker.start),
                        jpeg.begin()+static_cast<ptrdiff_t>(marker.payload+marker.length))});return true;
                };
                if(marker.marker==0xe2&&payload.size()>=14&&!std::memcmp(payload.data(),"ICC_PROFILE\0",12)) {
                    icc_bytes+=payload.size()-14;return save(raw_icc);
                }
                // Original MPF and JFIF values remain historical source bytes,
                // never an alternative active HEIF auxiliary binding.
                return save(markers);
            });
            if(!valid||icc_bytes<icc.size())return false;
            if(icc_bytes!=icc.size())markers.insert(markers.end(),raw_icc.begin(),raw_icc.end());
            return true;
        };
        if(!capture(source.first(static_cast<size_t>(primary.jpeg.eoi_end)),0,captured.primary_icc)||
            !capture(auxiliary.first(static_cast<size_t>(gainmap.eoi_end)),gainmap_range.offset,captured.gainmap_icc))return false;
        std::sort(markers.begin(),markers.end(),[](const auto& a,const auto& b){return a.offset<b.offset;});
        uint8_t hash[32]{};lpb::crypto::sha256_buffer(source.data(),source.size(),hash);
        if(!original_jpeg_metadata::encode(source.size(),std::span<const uint8_t,32>(hash),markers,captured.original_markers))return false;
        output=std::move(captured);observed_primary=std::move(primary);observed_gainmap=std::move(gainmap);return true;
    }catch(...){output={};observed_primary={};observed_gainmap={};error="HDR JPEG metadata inventory failed.";return false;}
}
}
