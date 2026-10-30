#include "media/image_converter.h"
#include "media/heic_backend.h"
#include "media/jpeg_backend.h"
#include "media/jpeg_marker.h"
#include "media/pixel_orientation.h"
#include "media/image_exif_mapping.h"
#include "media/target_primary_observation.h"
#include "media/media_inspector.h"
#include "media/target_writer.h" // Existing read-only neutral Exif/CID eligibility.
#include "containers/heif_internal.h"
#include "containers/original_jpeg_metadata.h"
#include "foundation/internal.h"
#include "foundation/sha256_core.h"
#include "platform/windows_filesystem.h"
#include <algorithm>
#include <array>
#include <cstring>
#include <set>

static_assert(sizeof(lpb_image_conversion_request_v2)==96);
static_assert(offsetof(lpb_image_conversion_request_v2,expected_identity)==24);
static_assert(offsetof(lpb_image_conversion_request_v2,source_sha256)==56);
static_assert(sizeof(lpb_image_conversion_proof_v1)==240);
static_assert(offsetof(lpb_image_conversion_proof_v1,source_length)==144);
static_assert(offsetof(lpb_image_conversion_proof_v1,output_identity)==208);
static_assert(sizeof(lpb_unspecified_jpeg_orientation_request_v1)==80);
static_assert(offsetof(lpb_unspecified_jpeg_orientation_request_v1,source_sha256)==48);

namespace lpb::media {
namespace {
bool same_identity(const lpb_file_identity& a,const lpb_file_identity& b) noexcept {
    return a.volume_serial==b.volume_serial&&a.file_index==b.file_index&&a.file_size==b.file_size&&a.link_count==b.link_count;
}
struct metadata {std::vector<uint8_t> exif,icc,original_markers;std::string xmp;};
void metadata_hash(const metadata& m,uint8_t output[32]) {
    lpb::crypto::sha256_ctx hash;
    const auto item=[&](uint8_t kind,const void* data,size_t size) {
        uint8_t header[9]{kind};for(unsigned i=0;i<8;++i)header[i+1]=static_cast<uint8_t>(uint64_t(size)>>(i*8));
        hash.update(header,sizeof(header));if(size)hash.update(static_cast<const uint8_t*>(data),size);
    };
    item(1,m.exif.data(),m.exif.size());item(2,m.xmp.data(),m.xmp.size());item(3,m.icc.data(),m.icc.size());
    item(4,m.original_markers.data(),m.original_markers.size());hash.finalize(output);
}
bool map_exif(metadata& m,const target_primary_observation& facts) {
    return map_image_exif_orientation(m.exif,facts);
}
bool jpeg_metadata(std::span<const uint8_t> bytes,const target_primary_observation& facts,
    const jpeg_rgb_image& decoded,metadata& m) {
    m={};m.exif=facts.exif_payload;m.xmp=facts.standard_xml;m.icc=decoded.icc_profile;
    if(!facts.auxiliaries.empty()||facts.encoding_boundary!=bytes.size()||facts.has_video||
        !facts.extended_xml.empty()||facts.jpeg.extended||facts.jpeg.metadata_after_scan||
        target_primary_has_live_binding(facts)||facts.xmp.hdr_claim)return false;
    lpb::memory_random_access memory{bytes};uint64_t eoi{},icc_bytes{};unsigned jfif{};
    std::vector<original_jpeg_metadata::marker> opaque,icc_markers;
    const bool valid=walk_jpeg_markers(memory.view(),eoi,[&](const jpeg_marker_range& marker) {
        if(marker.payload>bytes.size()||marker.length>bytes.size()-marker.payload)return false;
        const auto p=bytes.subspan(static_cast<size_t>(marker.payload),static_cast<size_t>(marker.length));
        if(marker.marker==0xc0||marker.marker==0xc1||marker.marker==0xc2)
            return p.size()>=6&&(p[5]==1||p[5]==3); // No unproven CMYK/YCCK color conversion.
        if((marker.marker<0xe0||marker.marker>0xef)&&marker.marker!=0xfe)return true;
        if(marker.after_scan)return false;
        const auto save=[&](auto& records) {
            const uint64_t end=marker.payload+marker.length;
            if(marker.start>end||end>bytes.size())return false;
            records.push_back({marker.start,std::vector<uint8_t>(bytes.begin()+static_cast<size_t>(marker.start),bytes.begin()+static_cast<size_t>(end))});return true;
        };
        constexpr char xmp[]="http://ns.adobe.com/xap/1.0/";
        if(marker.marker==0xe1)return (p.size()>=6&&!std::memcmp(p.data(),"Exif\0\0",6))||
            (p.size()>=sizeof(xmp)&&!std::memcmp(p.data(),xmp,sizeof(xmp)));
        if(marker.marker==0xe2) {
            if(p.size()>=14&&!std::memcmp(p.data(),"ICC_PROFILE\0",12)){icc_bytes+=p.size()-14;return save(icc_markers);}
            if(p.size()>=4&&!std::memcmp(p.data(),"MPF\0",4))return facts.singleton_mpf;
            return save(opaque);
        }
        if(marker.marker==0xe0) {
            return ++jfif==1&&p.size()==14&&!std::memcmp(p.data(),"JFIF\0",5)&&p[5]==1&&p[6]<=2&&
                p[7]==0&&p[8]==0&&p[9]==1&&p[10]==0&&p[11]==1&&p[12]==0&&p[13]==0;
        }
        return save(opaque);
    });
    if(!valid||eoi!=bytes.size()||icc_bytes<m.icc.size())return false;
    if(icc_bytes!=m.icc.size())opaque.insert(opaque.end(),icc_markers.begin(),icc_markers.end());
    std::sort(opaque.begin(),opaque.end(),[](const auto& a,const auto& b){return a.offset<b.offset;});
    uint8_t hash[32]{};lpb::crypto::sha256_buffer(bytes.data(),bytes.size(),hash);
    return original_jpeg_metadata::encode(bytes.size(),std::span<const uint8_t,32>(hash),opaque,m.original_markers);
}
}

lpb_result resolve_unspecified_jpeg_orientation_file(lpb_context* context,const char* source_path,const char* output_path,
    const lpb_unspecified_jpeg_orientation_request_v1* request,lpb_image_conversion_proof_v1* proof) noexcept {
    if(!proof)return LPB_RESULT_INVALID_ARGUMENT;
    const auto capacity=proof->struct_size;const uint32_t version=capacity>=8?proof->api_version:0;
    std::memset(proof,0,std::min<size_t>(capacity,sizeof(*proof)));
    if(capacity>=4)proof->struct_size=capacity;if(capacity>=8)proof->api_version=version;
    if(capacity!=sizeof(*proof))return LPB_RESULT_INVALID_ARGUMENT;
    if(!context||lpb_writer_callback_active(context))return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_writer_frame writer_frame(context);windows_owned_output output;
    try {
        if(version!=1||!request||request->struct_size!=sizeof(*request)||request->api_version!=1||
            request->handling!=1||request->reserved||!source_path||!source_path[0]||!output_path||!output_path[0]||
            paths_alias(source_path,output_path))return LPB_RESULT_INVALID_ARGUMENT;
        windows_readonly_source source;lpb_file_identity identity{};std::vector<uint8_t> bytes;
        if(!source.open(utf8_to_path(source_path))||!source.capture_identity(identity)||!source.read_all_bounded(4,32ULL*1024*1024,bytes))
            return LPB_RESULT_INTERNAL_ERROR;
        uint8_t source_hash[32]{};lpb::crypto::sha256_buffer(bytes.data(),bytes.size(),source_hash);
        if(identity.link_count!=1||!same_identity(identity,request->expected_identity)||std::memcmp(source_hash,request->source_sha256,32))
            return LPB_RESULT_SOURCE_CHANGED;
        if(lpb_context_check_cancelled(context)==LPB_RESULT_CANCELLED)return LPB_RESULT_CANCELLED;
        lpb_source_media_facts inspected{};inspected.struct_size=sizeof(inspected);
        const auto inspection=inspect_source(context,source_path,nullptr,&inspected);
        if(inspection!=LPB_RESULT_OK)return inspection;
        if(inspected.protocol!=LPB_SOURCE_PROTOCOL_NON_LIVE||inspected.primary_image.container!=LPB_IMAGE_CONTAINER_JPEG||
            inspected.primary_image.file_range.offset||!inspected.primary_image.file_range.length||inspected.primary_image.file_range.length>bytes.size()||
            inspected.motion_video.is_present||std::memcmp(inspected.primary_sha256,source_hash,32)) {
            const std::string diagnostic="[Unsupported] Stored-direction source inspection requires a complete neutral owner [protocol="+
                std::to_string(inspected.protocol)+", primaryLength="+std::to_string(inspected.primary_image.file_range.length)+
                ", fileLength="+std::to_string(bytes.size())+", video="+std::to_string(inspected.motion_video.is_present)+"].";
            set_error(context,diagnostic.c_str());return LPB_RESULT_UNSUPPORTED;
        }
        target_primary_observation before;std::string error;
        if(!observe_target_primary(bytes,before,error)||before.container!=LPB_IMAGE_CONTAINER_JPEG||before.orientation!=0||
            before.exif_payload.empty()||before.has_video||target_primary_has_live_binding(before)||before.xmp.foreign_claim||
            !before.extended_xml.empty()||before.jpeg.metadata_after_scan||observe_target_neutral_exif(before.exif_payload)!=LPB_RESULT_OK) {
            const std::string diagnostic="[Unsupported] Explicit stored-pixel resolution requires a neutral JPEG with one unspecified primary Exif orientation. "+error+
                " [orientation="+std::to_string(before.orientation)+", container="+std::to_string(before.container)+"]";
            set_error(context,diagnostic.c_str());return LPB_RESULT_UNSUPPORTED;
        }
        // The complete TIFF walker already rejects table/value overlap, cyclic
        // pointers and duplicate tags. A raw offset match grants no authority.
        exif_observation exif;uint32_t unknown=1;jpeg_observation_failure failure{};
        if(!observe_exif(before.exif_payload,unknown,exif,failure,true,true)||unknown!=0) {
            set_error(context,"[Unsupported] Stored-direction complete TIFF ownership is invalid or not unspecified.");return LPB_RESULT_UNSUPPORTED;
        }
        auto mapped_exif=before.exif_payload;auto tiff=std::span<uint8_t>(mapped_exif).subspan(6);const bool be=exif.big_endian;
        const auto read=[&](size_t at,size_t n)->uint32_t {uint32_t v{};for(size_t i=0;i<n;++i)v|=uint32_t(tiff[at+i])<<((be?n-i-1:i)*8);return v;};
        const auto put=[&](size_t at,size_t n,uint32_t v){for(size_t i=0;i<n;++i)tiff[at+i]=static_cast<uint8_t>(v>>((be?n-i-1:i)*8));};
        const size_t root=exif.ifd0_offset;if(root>tiff.size()||tiff.size()-root<6)return LPB_RESULT_UNSUPPORTED;
        const auto count=read(root,2);if(count>(tiff.size()-root-6)/12)return LPB_RESULT_UNSUPPORTED;
        size_t selected=SIZE_MAX;
        for(size_t i=0;i<count;++i) {
            const size_t at=root+2+i*12;if(read(at,2)!=0x112)continue;
            const auto type=read(at+2,2);
            if(selected!=SIZE_MAX||(type!=3&&type!=4)||read(at+4,4)!=1||read(at+8,4)!=0)return LPB_RESULT_UNSUPPORTED;
            selected=at;
        }
        if(selected==SIZE_MAX){set_error(context,"[Unsupported] Stored-direction IFD0 has no explicit zero-valued orientation field.");return LPB_RESULT_UNSUPPORTED;}
        put(selected+2,2,3);put(selected+8,4,be?0x00010000u:1u);
        uint32_t normal=0;exif_observation after_exif;
        if(!observe_exif(mapped_exif,normal,after_exif,failure,true)||normal!=1)return LPB_RESULT_UNSUPPORTED;
        if(!validate_stored_pixel_xmp_direction(before.standard_xml)) {
            set_error(context,"[Unsupported] Ordinary image XML conflicts with the explicit stored-pixel direction.");return LPB_RESULT_UNSUPPORTED;
        }
        if(inspected.auxiliary_count!=before.auxiliaries.size())return LPB_RESULT_UNSUPPORTED;
        for(const auto& asset:before.auxiliaries) {
            unsigned matches_asset=0;
            for(uint32_t i=0;i<inspected.auxiliary_count;++i) {
                const auto& observed=inspected.auxiliary_items[i];
                if(std::string_view(observed.semantic)==asset.semantic&&observed.file_range.offset==asset.range.offset&&
                    observed.file_range.length==asset.range.length)++matches_asset;
            }
            if(matches_asset!=1)return LPB_RESULT_UNSUPPORTED;
        }
        std::vector<uint8_t> resolved=bytes;lpb::memory_random_access reader{bytes};uint64_t end{};unsigned matches{};
        if(!walk_jpeg_markers(reader.view(),end,[&](const jpeg_marker_range& marker) {
            if(marker.marker!=0xe1||marker.length<6||std::memcmp(bytes.data()+static_cast<size_t>(marker.payload),"Exif\0\0",6))return true;
            if(marker.after_scan||++matches!=1||marker.length!=mapped_exif.size()||
                !std::equal(before.exif_payload.begin(),before.exif_payload.end(),bytes.begin()+static_cast<ptrdiff_t>(marker.payload)))return false;
            std::copy(mapped_exif.begin(),mapped_exif.end(),resolved.begin()+static_cast<ptrdiff_t>(marker.payload));return true;
        })||matches!=1||end!=before.encoding_boundary||resolved.size()!=bytes.size())return LPB_RESULT_UNSUPPORTED;
        target_primary_observation actual;
        if(!observe_target_primary(resolved,actual,error)||actual.orientation!=1||actual.exif_payload!=mapped_exif||
            actual.width!=before.width||actual.height!=before.height||actual.standard_xml!=before.standard_xml||
            actual.auxiliaries.size()!=before.auxiliaries.size()||target_primary_has_live_binding(actual))return LPB_RESULT_INVALID_ARGUMENT;
        jpeg_rgb_image decoded_before,decoded_after;
        if(decode_jpeg_rgb_bytes(context,bytes,decoded_before)!=LPB_RESULT_OK||decode_jpeg_rgb_bytes(context,resolved,decoded_after)!=LPB_RESULT_OK||
            decoded_before.width!=decoded_after.width||decoded_before.height!=decoded_after.height||
            decoded_before.pixels!=decoded_after.pixels||decoded_before.icc_profile!=decoded_after.icc_profile)return LPB_RESULT_INVALID_ARGUMENT;
        lpb_image_conversion_proof_v1 verified{};verified.struct_size=sizeof(verified);verified.api_version=1;
        std::memcpy(verified.source_sha256,source_hash,32);lpb::crypto::sha256_buffer(resolved.data(),resolved.size(),verified.output_sha256);
        lpb::crypto::sha256_buffer(mapped_exif.data(),mapped_exif.size(),verified.mapped_source_metadata_sha256);
        lpb::crypto::sha256_buffer(actual.exif_payload.data(),actual.exif_payload.size(),verified.output_metadata_sha256);
        verified.source_length=bytes.size();verified.output_length=resolved.size();verified.source_orientation=0;verified.output_orientation=1;
        verified.operation=4;verified.metadata_count=1;verified.metadata_status=verified.orientation_status=1;
        verified.icc_status=decoded_after.icc_profile.empty()?2U:1U;
        verified.source_width=verified.output_width=actual.width;verified.source_height=verified.output_height=actual.height;
        const auto abort=[&](lpb_result code,const char* reason){return abort_owned_output_and_report(context,output,reason)?code:LPB_RESULT_INTERNAL_ERROR;};
        const auto destination=utf8_to_path(output_path);
        if(!output.create(destination,L"lpb-stored-direction",L".tmp",true)||!output.write_all(resolved)||!output.flush()||!output.ready_to_consume())
            return abort(LPB_RESULT_INTERNAL_ERROR,"[IO] Stored-direction stage failed.");
        uint8_t stage_hash[32]{};
        if(!output.capture_identity(verified.output_identity)||!output.compute_sha256(stage_hash)||std::memcmp(stage_hash,verified.output_sha256,32)||
            verified.output_identity.file_size!=verified.output_length||verified.output_identity.link_count!=1||
            !source.verify_same_identity(utf8_to_path(source_path)))return abort(LPB_RESULT_SOURCE_CHANGED,"[SourceChanged] Stored-direction source/stage changed.");
        if(lpb_context_check_cancelled(context)==LPB_RESULT_CANCELLED)return abort(LPB_RESULT_CANCELLED,"[Cancelled] Stored-direction resolution cancelled.");
        if(!output.publish_registered_no_replace(context,LPB_ARTIFACT_PRIMARY_IMAGE,destination))
            return abort(LPB_RESULT_INTERNAL_ERROR,"[IO] Stored-direction output cannot replace an existing object.");
        verified.proof_kind=3;verified.proof_flags=1;*proof=verified;return LPB_RESULT_OK;
    }catch(...){static_cast<void>(abort_owned_output_and_report(context,output,"Stored-direction resolution failed at the Native exception boundary."));return LPB_RESULT_INTERNAL_ERROR;}
}

lpb_result convert_image_file_v2(lpb_context* context,const char* source_path,const char* output_path,
    const lpb_image_conversion_request_v2* request,lpb_image_conversion_proof_v1* proof) noexcept {
    if(!proof)return LPB_RESULT_INVALID_ARGUMENT;
    const auto capacity=proof->struct_size;
    const uint32_t version=capacity>=8?proof->api_version:0;
    std::memset(proof,0,std::min<size_t>(capacity,sizeof(*proof)));
    if(capacity>=4)proof->struct_size=capacity;if(capacity>=8)proof->api_version=version;
    if(capacity!=sizeof(*proof))return LPB_RESULT_INVALID_ARGUMENT;
    if(!context||lpb_writer_callback_active(context))return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_writer_frame writer_frame(context);
    windows_owned_output output;
    try {
        if(version!=1||!request||request->struct_size!=sizeof(*request)||request->api_version!=2||
            request->quality<1||request->quality>100||request->orientation_policy!=1||request->reserved0||request->reserved1||
            !source_path||!source_path[0]||!output_path||!output_path[0]||paths_alias(source_path,output_path)) {
            set_error(context,"[Malformed] Bound image conversion request or proof layout is invalid.");return LPB_RESULT_INVALID_ARGUMENT;
        }
        if(request->source_container!=LPB_IMAGE_CONTAINER_JPEG||request->target_container!=LPB_IMAGE_CONTAINER_HEIC) {
            set_error(context,"[Unsupported] Bound ordinary image proof currently requires JPEG to HEIC; other semantic routes need their own complete mapping.");return LPB_RESULT_UNSUPPORTED;
        }
        windows_readonly_source source;lpb_file_identity identity{};std::vector<uint8_t> bytes;
        if(!source.open(utf8_to_path(source_path))||!source.capture_identity(identity)||!source.read_all_bounded(4,32ULL*1024*1024,bytes)) {
            set_error(context,"[IO] Bound image source cannot be pinned and read within32MiB.");return LPB_RESULT_INTERNAL_ERROR;
        }
        uint8_t source_hash[32]{};lpb::crypto::sha256_buffer(bytes.data(),bytes.size(),source_hash);
        if(identity.link_count!=1||!same_identity(identity,request->expected_identity)||std::memcmp(source_hash,request->source_sha256,32)) {
            set_error(context,"[SourceChanged] Bound image source identity or bytes differ from the request.");return LPB_RESULT_SOURCE_CHANGED;
        }
        if(lpb_context_check_cancelled(context)==LPB_RESULT_CANCELLED)return LPB_RESULT_CANCELLED;
        target_primary_observation facts;std::string error;
        if(!observe_target_primary(bytes,facts,error)||facts.container!=LPB_IMAGE_CONTAINER_JPEG||facts.orientation<1||facts.orientation>8) {
            set_error(context,"[Unsupported] Bound image source does not have a complete supported JPEG owner and known orientation.");return LPB_RESULT_UNSUPPORTED;
        }
        if(!facts.exif_payload.empty()&&observe_target_neutral_exif(facts.exif_payload)!=LPB_RESULT_OK) {
            set_error(context,"[Unsupported] Ordinary image source Exif retains a live pairing binding or ambiguous owner; Source-to-Neutral preparation is required.");
            return LPB_RESULT_UNSUPPORTED;
        }
        lpb_preservation_observation eligibility{};eligibility.struct_size=sizeof(eligibility);
        const auto inspected=lpb_media_capture_preservation_observation_bytes(context,bytes,LPB_SOURCE_PROTOCOL_UNKNOWN,LPB_IMAGE_CONTAINER_JPEG,&eligibility);
        if(inspected!=LPB_RESULT_OK)return inspected;
        if(eligibility.flags&(LPB_POBS_HAS_HEIC_AUX|LPB_POBS_HAS_GAINMAP_META|LPB_POBS_HAS_HEIC_GAINMAP_SEMANTIC|
            LPB_POBS_HEIC_AUX_AMBIGUOUS|LPB_POBS_CODESTREAM_ERROR)) {
            set_error(context,"[Unsupported] Bound ordinary image conversion cannot claim HDR/auxiliary preservation or ambiguous media eligibility.");
            return LPB_RESULT_UNSUPPORTED;
        }
        jpeg_rgb_image jpeg;auto status=decode_jpeg_rgb_bytes(context,bytes,jpeg);if(status!=LPB_RESULT_OK)return status;
        metadata mapped;
        if(jpeg.width!=facts.width||jpeg.height!=facts.height||!jpeg_metadata(bytes,facts,jpeg,mapped)||!map_exif(mapped,facts)) {
            set_error(context,"[Unsupported] Ordinary image metadata or thumbnail has no complete safe direction/container mapping.");return LPB_RESULT_UNSUPPORTED;
        }
        xmp_observation_failure why{};std::string mapped_xml;
        if(!rewrite_neutral_image_xmp(mapped.xmp,"image/jpeg","image/heic",facts.orientation,facts.width,facts.height,mapped_xml,why)) {
            set_error(context,"[Unsupported] Ordinary XMP cannot map primary MIME and physical orientation without losing ownership.");return LPB_RESULT_UNSUPPORTED;
        }
        mapped.xmp=std::move(mapped_xml);pixel_surface pixels;
        pixels.width=jpeg.width;pixels.height=jpeg.height;pixels.channels=3;pixels.signal_bit_depth=8;pixels.storage_bit_depth=8;
        pixels.stride=uint64_t(jpeg.width)*3;pixels.color.has_icc=!mapped.icc.empty();pixels.color.icc_profile=mapped.icc;pixels.pixels=std::move(jpeg.pixels);
        if(!normalize_pixel_orientation(pixels,facts.orientation))return LPB_RESULT_INVALID_ARGUMENT;
        std::vector<uint8_t> encoded;
        const auto tiff=mapped.exif.empty()?std::span<const uint8_t>{}:std::span<const uint8_t>(mapped.exif).subspan(6);
        status=encode_heic_primary_metadata_bytes(context,pixels,request->quality,tiff,
            std::span<const uint8_t>(reinterpret_cast<const uint8_t*>(mapped.xmp.data()),mapped.xmp.size()),encoded);
        if(status!=LPB_RESULT_OK)return status;
        if(!original_jpeg_metadata::attach(encoded,mapped.original_markers))return LPB_RESULT_INVALID_ARGUMENT;
        containers::heif_primary_properties properties;heic_image_facts actual;metadata observed;uint32_t primary{};
        if(!containers::observe_primary_item_properties(encoded,properties,error)||properties.orientation!=1||properties.has_clean_aperture||
            properties.width!=pixels.width||properties.height!=pixels.height||
            !containers::read_primary_xmp(encoded,primary,observed.xmp,error)||primary!=properties.item_id||
            inspect_heic_bytes(context,encoded,actual)!=LPB_RESULT_OK||!actual.auxiliaries.empty()||actual.hdr_relevant||actual.bit_depth!=8||
            actual.color.has_nclx||actual.color.has_icc!=!mapped.icc.empty()) {
            set_error(context,"[PostValidation] Actual HEIF owner/geometry/color graph differs from the mapped ordinary image.");return LPB_RESULT_INVALID_ARGUMENT;
        }
        observed.exif=properties.exif_payload;observed.icc=actual.color.icc_profile;
        if(!original_jpeg_metadata::observe(encoded,observed.original_markers)||observed.original_markers!=mapped.original_markers||
            observed.exif!=mapped.exif||observed.xmp!=mapped.xmp||observed.icc!=mapped.icc||validate_heic_bytes(context,encoded)!=LPB_RESULT_OK) {
            set_error(context,"[PostValidation] Actual HEIF Exif/XMP/ICC bytes or complete image decode differ from the mapped source.");return LPB_RESULT_INVALID_ARGUMENT;
        }
        lpb_image_conversion_proof_v1 verified{};verified.struct_size=sizeof(verified);verified.api_version=1;
        std::memcpy(verified.source_sha256,source_hash,32);lpb::crypto::sha256_buffer(encoded.data(),encoded.size(),verified.output_sha256);
        metadata_hash(mapped,verified.mapped_source_metadata_sha256);metadata_hash(observed,verified.output_metadata_sha256);
        verified.source_length=bytes.size();verified.output_length=encoded.size();verified.source_orientation=facts.orientation;verified.output_orientation=1;
        verified.operation=3;verified.metadata_count=static_cast<uint32_t>(!mapped.exif.empty())+static_cast<uint32_t>(!mapped.xmp.empty())+
            static_cast<uint32_t>(!mapped.icc.empty())+static_cast<uint32_t>(!mapped.original_markers.empty());
        verified.metadata_status=verified.metadata_count?1U:2U;verified.icc_status=mapped.icc.empty()?2U:1U;verified.orientation_status=1;
        verified.source_width=facts.width;verified.source_height=facts.height;verified.output_width=pixels.width;verified.output_height=pixels.height;
        const auto abort=[&](lpb_result result,const char* reason){return abort_owned_output_and_report(context,output,reason)?result:LPB_RESULT_INTERNAL_ERROR;};
        const auto destination=utf8_to_path(output_path);
        if(!output.create(destination,L"lpb-image-v2",L".tmp",true)||!output.write_all(encoded)||!output.flush()||!output.ready_to_consume())
            return abort(LPB_RESULT_INTERNAL_ERROR,"[IO] Bound image stage could not be created/written/flushed.");
        uint8_t stage_hash[32]{};
        if(!output.capture_identity(verified.output_identity)||!output.compute_sha256(stage_hash)||
            std::memcmp(stage_hash,verified.output_sha256,32)||verified.output_identity.file_size!=verified.output_length||
            verified.output_identity.link_count!=1||!source.verify_same_identity(utf8_to_path(source_path)))
            return abort(LPB_RESULT_SOURCE_CHANGED,"[SourceChanged] Bound source or exact owned stage changed before publish.");
        if(lpb_context_check_cancelled(context)==LPB_RESULT_CANCELLED)return abort(LPB_RESULT_CANCELLED,"[Cancelled] Bound image conversion cancelled before publish.");
        if(!output.publish_registered_no_replace(context,LPB_ARTIFACT_PRIMARY_IMAGE,destination))
            return abort(LPB_RESULT_INTERNAL_ERROR,"[IO] Bound image cannot publish without replacing an existing object.");
        verified.proof_kind=1;verified.proof_flags=1;*proof=verified;return LPB_RESULT_OK;
    }catch(...){
        static_cast<void>(abort_owned_output_and_report(context,output,"Bound image conversion failed at the Native exception boundary."));
        return LPB_RESULT_INTERNAL_ERROR;
    }
}
}
