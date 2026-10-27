#include "media/target_primary_observation.h"
#include "media/jpeg_gainmap_mpf.h"
#include "media/jpeg_mpf_vendor.h"
#include "media/media_inspector.h"
#include "media/target_writer.h"
#include "containers/heif_internal.h"
#include "containers/isobmff.h"
#include "protocols/samsung_inventory.h"
#include <algorithm>
#include <cstring>
#include <set>

namespace lpb::media {
namespace {
bool xml_facts(target_primary_observation& out, std::string& error) {
    if (out.standard_xml.empty() && out.extended_xml.empty()) return true;
    xmp_observation_failure failure{};
    if (!observe_target_xmp_packets(out.standard_xml, out.extended_xml, out.extended_guid, out.xmp, failure)) {
        error = "Primary XMP is malformed, shadowed, or has unsupported cross-packet ownership."; return false;
    }
    return true;
}
bool jpeg_facts(std::span<const uint8_t> input, target_primary_observation& out, std::string& error,
    uint64_t full_file_length, bool prefix_mode,uint64_t samsung_padding=UINT64_MAX) {
    memory_random_access memory{input}; jpeg_observation_failure failure{}; uint64_t bad = 0;
    if (!observe_jpeg(memory.view(), out.jpeg, failure, bad, {true,true,true,true})) {
        error = "Prepared JPEG marker/Exif/XMP transport observation failed."; return false;
    }
    out.container = LPB_IMAGE_CONTAINER_JPEG; out.width = out.jpeg.width; out.height = out.jpeg.height;
    out.orientation = out.jpeg.orientation; out.encoding_boundary = out.jpeg.eoi_end;
    out.primary_selector = "jpeg:primary:0"; out.item_type = "jpeg";
    out.primary_extents.push_back({0,out.encoding_boundary});
    out.standard_xml = out.jpeg.xmp; out.extended_xml = out.jpeg.extended_xmp;
    out.extended_guid = out.jpeg.extended_guid; out.exif_payload = out.jpeg.exif_payload;
    if (!xml_facts(out,error)) return false;
    jpeg_gainmap_mpf::jpeg_info mpf;
    if (!jpeg_gainmap_mpf::parse_jpeg(input,mpf) || mpf.mpf_status == jpeg_gainmap_mpf::status::malformed) {
        error = "Prepared JPEG has malformed MPF ownership."; return false;
    }
    out.singleton_mpf = jpeg_gainmap_mpf::singleton_primary(mpf);
    out.singleton_size_matches = out.singleton_mpf && mpf.mpf.entries[0].size == out.encoding_boundary;
    lpb_media_range subimage{},watermark{};
    int micamera=0;
    if(out.standard_xml.find("XMPMeta")!=std::string::npos) {
        const std::vector<uint8_t> owned_prefix(input.begin(),input.end());
        micamera=observe_micamera_assets(owned_prefix,out.standard_xml,full_file_length,subimage,watermark);
        if(micamera<0){error="MiCamera asset graph is malformed or outside its actual owner.";return false;}
    }
    if (out.xmp.directory_present) {
        if (out.xmp.items.empty() || out.xmp.items.front().semantic != "Primary" || out.xmp.items.front().mime != "image/jpeg" ||
            (out.xmp.items.front().has_length && out.xmp.items.front().length != 0)) {
            error = "JPEG inventory requires one first Primary image/jpeg item without a positive length."; return false;
        }
    }
    uint64_t cursor = out.encoding_boundary;
    if(out.xmp.items.empty()&&mpf.mpf_status==jpeg_gainmap_mpf::status::present&&mpf.mpf.image_count==2) {
        mpf_vendor_primary vendor;
        if(!observe_mpf_vendor_primary(input,vendor)) {error="MPF auxiliary/ordinary metadata inventory is incomplete.";return false;}
        target_primary_auxiliary aux;
        aux.selector="jpeg:mpf:2";aux.owner_selector=out.primary_selector;aux.selector_kind=4;aux.selector_id=2;
        aux.semantic="MpfAuxiliaryImage";aux.mime="image/jpeg";aux.item_type="jpeg";aux.relationship="mpf-image";
        aux.range={vendor.auxiliary_offset,vendor.auxiliary_length};out.auxiliaries.push_back(std::move(aux));
        cursor=input.size();
    }
    const uint64_t samsung_embedded_padding=micamera==1?watermark.offset+watermark.length-out.encoding_boundary:0;
    if(samsung_padding!=UINT64_MAX&&(out.xmp.items.empty()||out.xmp.items.front().padding!=samsung_padding+samsung_embedded_padding)) {
        error="Samsung compatibility padding differs from actual tag extent.";return false;
    }
    if (!out.xmp.items.empty()&&samsung_padding==UINT64_MAX) {
        if (out.xmp.items.front().padding > input.size() - cursor) { error="JPEG primary padding exceeds the observed span."; return false; }
        const uint64_t padding=out.xmp.items.front().padding;
        if(padding>=20&&input[cursor+padding-4]=='S'&&input[cursor+padding-3]=='E'&&input[cursor+padding-2]=='F'&&input[cursor+padding-1]=='T') {
            lpb::protocols::sef_inventory inventory;
            if(!lpb::protocols::observe_sef(memory.view(),cursor,cursor+padding,inventory,error)||inventory.begin!=cursor||!lpb::protocols::ordinary_sef(inventory))return false;
            out.samsung_metadata={cursor,padding};
        }
        cursor += out.xmp.items.front().padding;
    }
    if(micamera==1) {
        if(subimage.offset!=out.encoding_boundary||subimage.offset+subimage.length!=watermark.offset||
            watermark.offset+watermark.length>input.size()||
            (out.xmp.directory_present&&samsung_padding==UINT64_MAX&&cursor!=watermark.offset+watermark.length)) {
            error="MiCamera auxiliary placement disagrees with the bounded primary padding.";return false;
        }
        if(mpf.mpf_status!=jpeg_gainmap_mpf::status::absent){error="MiCamera and MPF overlapping ownership is unsupported.";return false;}
        cursor=watermark.offset+watermark.length;
        for(uint32_t i=0;i<2;++i){
            target_primary_auxiliary a;
            a.selector="jpeg:micamera:"+std::to_string(i+1);a.selector_kind=3;a.selector_id=i+1;
            a.owner_selector=out.primary_selector;a.semantic=i?"MiCameraLensWatermark":"MiCameraSubimage";
            a.relationship=i?"micamera-xmpmeta-lenswatermark":"micamera-xmpmeta-subimage";
            a.mime=i?"image/png":"image/jpeg";a.item_type=i?"png":"jpeg";a.range=i?watermark:subimage;
            out.auxiliaries.push_back(std::move(a));
        }
    }
    std::set<std::string> seen;
    unsigned last_rank = 0;
    for (size_t i = 1; i < out.xmp.items.size(); ++i) {
        const auto& item = out.xmp.items[i];
        const unsigned rank = item.semantic == "GainMap" ? 1u : item.semantic == "Original" ? 2u : item.semantic == "MotionPhoto" ? 3u : 0u;
        const bool external_final_video=prefix_mode && item.semantic=="MotionPhoto";
        if (!rank || rank <= last_rank || !seen.insert(item.semantic).second || !item.has_length || !item.length ||
            cursor > input.size() || (!external_final_video &&
                (item.length > input.size() - cursor || item.padding > input.size() - cursor - item.length))) {
            error = "JPEG directory inventory is ambiguous, not ordered, or exceeds its observed span."; return false;
        }
        last_rank = rank;
        if (item.semantic == "MotionPhoto") {
            if (i+1 != out.xmp.items.size() || item.padding || (item.mime != "video/mp4" && item.mime != "video/quicktime")) {
                error = "JPEG MotionPhoto must be the final bounded video item."; return false;
            }
            const uint64_t gap=samsung_padding==UINT64_MAX?0:samsung_padding;
            if (prefix_mode && (cursor != input.size() || gap>=full_file_length-cursor || item.length != full_file_length - cursor-gap)) {
                error="JPEG prefix boundary or full file length disagrees with the final MotionPhoto inventory."; return false;
            }
            out.has_video=true; out.video_offset=cursor+gap; out.video_length=item.length;
            out.primary_size=cursor;
        } else {
            const auto bytes=input.subspan(static_cast<size_t>(cursor),static_cast<size_t>(item.length));
            const bool valid_image=item.semantic=="Original"?observe_original_jpeg_asset(bytes,error):
                jpeg_gainmap_mpf::valid_jpeg_media_range(input,static_cast<size_t>(cursor),static_cast<size_t>(item.length));
            if (item.mime != "image/jpeg" || !valid_image) {
                error = "Declared JPEG auxiliary has incompatible MIME or invalid encoded JPEG bytes."; return false;
            }
            target_primary_auxiliary aux;
            aux.selector="jpeg:directory:"+std::to_string(i); aux.owner_selector=out.primary_selector;
            aux.selector_kind=1; aux.selector_id=static_cast<uint32_t>(i);
            aux.semantic=item.semantic; aux.mime=item.mime; aux.item_type="jpeg";
            aux.relationship=item.semantic=="GainMap" ? "gain-map" : "Original"; aux.range={cursor,item.length};
            out.auxiliaries.push_back(std::move(aux));
        }
        if (!external_final_video) cursor += item.length + item.padding;
    }
    if (prefix_mode && !out.has_video) { error="JPEG prefix observation requires an actual final MotionPhoto directory item."; return false; }
    if(cursor<input.size()&&out.xmp.items.empty()) {
        lpb::protocols::sef_inventory inventory;
        if(!lpb::protocols::observe_sef(memory.view(),cursor,input.size(),inventory,error)||inventory.begin!=cursor||!lpb::protocols::ordinary_sef(inventory)) {
            error="Prepared JPEG has unowned or non-tightly-packed trailing bytes: "+error;return false;
        }
        out.samsung_metadata={cursor,input.size()-cursor};cursor=input.size();
    }
    if (cursor != input.size()) { error="Prepared JPEG has unowned or non-tightly-packed trailing bytes."; return false; }
    if (!out.has_video) out.primary_size=cursor;
    if (mpf.mpf_status == jpeg_gainmap_mpf::status::present && !out.singleton_mpf) {
        if (mpf.mpf.image_count != out.auxiliaries.size()+1 || mpf.mpf.entries[0].size != out.encoding_boundary) {
            error="MPF image cardinality/primary size disagrees with the semantic directory."; return false;
        }
        for (size_t i=0; i<mpf.mpf.image_count; ++i) {
            const auto& entry=mpf.mpf.entries[i];
            if ((entry.dependent_image_1 && entry.dependent_image_1==entry.dependent_image_2) ||
                entry.dependent_image_1>mpf.mpf.image_count || entry.dependent_image_2>mpf.mpf.image_count ||
                entry.dependent_image_1==i+1 || entry.dependent_image_2==i+1) {
                error="MPF dependency table has missing, duplicate, or self-referential identity."; return false;
            }
            if(i==0) for(const auto dependency : {entry.dependent_image_1,entry.dependent_image_2}) if(dependency) {
                out.primary_dependency_ids.push_back(dependency);
                const auto& referenced=mpf.mpf.entries[dependency-1];
                out.primary_dependency_ranges.push_back({referenced.absolute_offset,referenced.size});
            }
        }
        for (size_t i=0; i<out.auxiliaries.size(); ++i) {
            const auto& entry=mpf.mpf.entries[i+1]; auto& aux=out.auxiliaries[i];
            if (entry.absolute_offset != aux.range.offset || entry.size != aux.range.length) {
                error="MPF ordinal extent disagrees with the semantic directory item."; return false;
            }
            for (const auto dependency : {entry.dependent_image_1,entry.dependent_image_2}) if (dependency) {
                if (dependency > mpf.mpf.image_count || dependency == i+2 ||
                    std::find(aux.dependency_ids.begin(),aux.dependency_ids.end(),dependency)!=aux.dependency_ids.end()) {
                    error="MPF dependency identity is missing, duplicate, or self-referential."; return false;
                }
                aux.dependency_ids.push_back(dependency);
                const auto& referenced=mpf.mpf.entries[dependency-1]; aux.dependency_ranges.push_back({referenced.absolute_offset,referenced.size});
            }
        }
    } else if (out.singleton_mpf && !out.auxiliaries.empty()) {
        error="A singleton MPF self-entry cannot own a multi-image semantic directory."; return false;
    }
    return true;
}
bool heif_facts(std::span<const uint8_t> input, target_primary_observation& out, std::string& error, bool allow_video) {
    size_t image_end=input.size(), p=0; bool saw_video=false;
    while (p<input.size()) {
        isobmff_box_header box{};
        if (!try_read_box_header(input.data(),p,input.size(),box) || box.extends_to_end || box.size < box.header_size || box.size > input.size()-p) {
            error="HEIF top-level box region has a malformed or size-zero append boundary."; return false;
        }
        if (!std::memcmp(input.data()+p+4,"mpvd",4)) {
            if (!allow_video || saw_video || p+box.size != input.size() || box.size == box.header_size) {
                error="HEIF requires one nonempty final mpvd, explicitly enabled for output observation."; return false;
            }
            saw_video=true; image_end=p; out.has_video=true; out.video_header_size=box.header_size;
            out.video_offset=p+box.header_size; out.video_length=box.size-box.header_size;
        }
        if(!std::memcmp(input.data()+p+4,"sefd",4)) {
            memory_random_access memory{input};lpb::protocols::sef_inventory inventory;
            if(out.samsung_metadata.length||p+box.size!=input.size()||
               !lpb::protocols::observe_sef(memory.view(),p+box.header_size,input.size(),inventory,error)||
               inventory.begin!=p+box.header_size||!lpb::protocols::ordinary_sef(inventory))return false;
            out.samsung_metadata={p,box.size};
        }
        p+=box.size;
    }
    const auto image=input.first(image_end);
    if (detect_image_container(image) != LPB_IMAGE_CONTAINER_HEIC) {
        error="Target HEIC requires a supported HEIC file type brand."; return false;
    }
    containers::heif_primary_properties primary;
    if (!containers::observe_primary_item_properties(image,primary,error)) return false;
    out.container=LPB_IMAGE_CONTAINER_HEIC; out.width=primary.width; out.height=primary.height;
    out.orientation=primary.orientation; out.primary_item_id=primary.item_id; out.item_type=primary.item_type;
    out.primary_selector="heif:item:"+std::to_string(primary.item_id);
    out.primary_extents=std::move(primary.encoding_extents); out.exif_payload=std::move(primary.exif_payload);
    out.encoding_boundary=image_end; out.primary_size=image_end;
    uint32_t primary_id=0;
    if (!containers::read_primary_xmp(image,primary_id,out.standard_xml,error) || primary_id!=out.primary_item_id || !xml_facts(out,error)) return false;
    std::vector<lpb_auxiliary_item_facts> facts; containers::heif_graph_diagnostic diagnostic;
    if (!containers::enumerate_auxiliary_items(image,facts,diagnostic)) { error=diagnostic.message; return false; }
    for (const auto& fact : facts) {
        target_primary_auxiliary aux;
        aux.item_id=fact.item_id; aux.selector="heif:item:"+std::to_string(fact.item_id); aux.owner_selector=out.primary_selector;
        aux.selector_kind=2; aux.selector_id=fact.item_id;
        aux.semantic=fact.semantic; aux.relationship=fact.relationship; aux.range=fact.file_range;
        aux.item_type=fact.item_type;
        if (aux.item_type=="jpeg") aux.mime="image/jpeg";
        else if (aux.item_type=="hvc1" || aux.item_type=="grid" || aux.item_type=="tmap") aux.mime="image/heic";
        for (uint32_t i=0; i<fact.dependency_count; ++i) {
            aux.dependency_ids.push_back(fact.dependency_item_ids[i]);
            aux.dependency_ranges.push_back({fact.dependency_offsets[i],fact.dependency_lengths[i]});
        }
        out.auxiliaries.push_back(std::move(aux));
    }
    return true;
}
}
bool observe_target_primary(std::span<const uint8_t> input, target_primary_observation& output,
    std::string& error, bool allow_final_heif_video, uint64_t full_file_length) noexcept {
    output={}; error.clear();
    try {
        target_primary_observation result;
        if (input.size()<8) { error="Prepared primary is too short."; return false; }
        const bool jpeg=input[0]==0xff && input[1]==0xd8;
        const bool ok=jpeg ? jpeg_facts(input,result,error,full_file_length?full_file_length:input.size(),false) : heif_facts(input,result,error,allow_final_heif_video);
        if (!ok) return false;
        output=std::move(result); return true;
    } catch (...) { output={}; error="Prepared primary observation could not allocate or complete."; return false; }
}
bool observe_target_jpeg_prefix(std::span<const uint8_t> prefix, uint64_t full_file_length,
    target_primary_observation& output, std::string& error) noexcept {
    output={}; error.clear();
    try {
        if (prefix.size()<8 || prefix[0]!=0xff || prefix[1]!=0xd8 || full_file_length<=prefix.size()) {
            error="JPEG streaming observation requires a bounded JPEG prefix shorter than the pinned full file."; return false;
        }
        target_primary_observation result;
        if (!jpeg_facts(prefix,result,error,full_file_length,true)) return false;
        output=std::move(result); return true;
    } catch (...) { output={}; error="JPEG prefix observation could not allocate or complete."; return false; }
}
bool observe_samsung_jpeg_prefix(std::span<const uint8_t> prefix,uint64_t full_length,uint64_t metadata_padding,
 target_primary_observation& output,std::string& error) noexcept {
 output={};error.clear();
 try {return jpeg_facts(prefix,output,error,full_length,true,metadata_padding);}
 catch(...){output={};error="Samsung JPEG prefix observation failed";return false;}
}
bool target_primary_has_live_binding(const target_primary_observation& facts) noexcept {
    if (facts.has_video || facts.xmp.live_claim || facts.xmp.micro_claim) return true;
    for (const auto& item : facts.xmp.items) if (item.semantic=="MotionPhoto") return true;
    // Inventory entries are expanded RDF property keys, not raw XML strings.
    // Namespace declarations are omitted by the structural XMP observer.
    for (const auto& key : facts.xmp.property_inventory) {
        std::string_view remaining=key;
        const auto consume_length=[&](size_t& value) {
            value=0; size_t p=0;
            while(p<remaining.size() && remaining[p]>='0' && remaining[p]<='9') {
                const size_t digit=static_cast<size_t>(remaining[p]-'0');
                if(value>(SIZE_MAX-digit)/10) return false;
                value=value*10+digit; ++p;
            }
            if(!p || p==remaining.size() || remaining[p]!=':') return false;
            remaining.remove_prefix(p+1); return true;
        };
        size_t subject_length=0,uri_length=0;
        if(!consume_length(subject_length) || subject_length>remaining.size()) return true;
        remaining.remove_prefix(subject_length);
        if(!consume_length(uri_length) || uri_length>remaining.size()) return true;
        const auto observed_uri=remaining.substr(0,uri_length), observed_name=remaining.substr(uri_length);
        const auto property=[&](std::string_view uri,std::string_view name) {
            return observed_uri==uri && observed_name==name;
        };
        if (property("http://ns.oplus.com/photos/1.0/camera/","MotionPhotoOwner") ||
            property("http://ns.oplus.com/photos/1.0/camera/","MotionPhotoVersion") ||
            property("http://ns.oplus.com/photos/1.0/camera/","VideoLength") ||
            property("http://ns.vivo.com/photos/1.0/camera/","VMotionPhotoVersion") ||
            property("http://ns.vivo.com/photos/1.0/camera/","VMotionPhotoSource")) return true;
    }
    return false;
}
bool observe_original_jpeg_asset(std::span<const uint8_t> input,std::string& error) noexcept {
    error.clear();
    try {
        memory_random_access memory{input};jpeg_observation jpeg;jpeg_observation_failure failure;uint64_t bad=0;
        if(!observe_jpeg(memory.view(),jpeg,failure,bad,{true,true,true,true})) {
            error="Original JPEG marker/Exif/XMP transport is invalid.";return false;
        }
        target_xmp_observation x;xmp_observation_failure why;
        if((!jpeg.xmp.empty()||!jpeg.extended_xmp.empty())&&
            !observe_target_xmp_packets(jpeg.xmp,jpeg.extended_xmp,jpeg.extended_guid,x,why)) {
            error="Original logical XMP is malformed or shadowed.";return false;
        }
        jpeg_gainmap_mpf::jpeg_info mpf;
        if(!jpeg_gainmap_mpf::parse_jpeg(input,mpf)||mpf.mpf_status==jpeg_gainmap_mpf::status::malformed){error="Original MPF is malformed.";return false;}
        if(jpeg.eoi_end==input.size()) {
            if(mpf.mpf_status==jpeg_gainmap_mpf::status::present&&!jpeg_gainmap_mpf::singleton_primary(mpf)) {
                error="Original MPF references missing image assets.";return false;
            }
            return true;
        }
        const target_xmp_item* gain=nullptr;
        for(const auto& item:x.items)if(item.semantic=="GainMap") {if(gain){error="Original has duplicate GainMap ownership.";return false;}gain=&item;}
        if(!gain||!gain->has_length||gain->mime!="image/jpeg"||gain->padding||
            gain->length!=input.size()-jpeg.eoi_end||
            !jpeg_gainmap_mpf::valid_jpeg_media_range(input,static_cast<size_t>(jpeg.eoi_end),static_cast<size_t>(gain->length))) {
            error="Original trailing bytes are not its uniquely owned complete GainMap.";return false;
        }
        if(mpf.mpf_status==jpeg_gainmap_mpf::status::present) {
            // The Original is immutable. Its historical redundant first-image
            // size does not select bytes; the actual complete EOI and second
            // entry must agree exactly with the bounded directory asset.
            if(mpf.mpf.image_count!=2||mpf.mpf.entries[0].relative_offset!=0||
                mpf.mpf.entries[1].absolute_offset!=jpeg.eoi_end||mpf.mpf.entries[1].size!=gain->length) {
                error="Original MPF and actual nested GainMap extents disagree.";return false;
            }
        }
        return true;
    }catch(...){error="Original image asset observation could not complete.";return false;}
}
bool observe_gainmap_jpeg_asset(std::span<const uint8_t> input,std::string& error) noexcept {
    error.clear();
    try {
        memory_random_access memory{input};jpeg_observation jpeg;jpeg_observation_failure failure;uint64_t bad=0;
        if(!observe_jpeg(memory.view(),jpeg,failure,bad,{true,true,true,true})||jpeg.mpf_index||jpeg.metadata_after_scan||
            !jpeg_gainmap_mpf::valid_jpeg_media_range(input,0,input.size())) {
            error="GainMap must be one complete JPEG with at most legal zero/FF end padding.";return false;
        }
        target_xmp_observation x;xmp_observation_failure why;
        if((!jpeg.xmp.empty()||!jpeg.extended_xmp.empty())&&
            (!observe_target_xmp_packets(jpeg.xmp,jpeg.extended_xmp,jpeg.extended_guid,x,why)||
             x.live_claim||x.micro_claim||x.foreign_claim||x.oppo_claim||x.vivo_claim||x.directory_present)) {
            error="GainMap metadata retains a live binding or nested ownership.";return false;
        }
        if(!jpeg.exif_payload.empty()&&observe_target_neutral_exif(jpeg.exif_payload)!=LPB_RESULT_OK){error="GainMap Exif retains foreign live ownership.";return false;}
        return true;
    }catch(...){error="GainMap image asset observation could not complete.";return false;}
}
} // namespace lpb::media
