#include "livephotobox_native.h"
#include "foundation/internal.h"
#include "foundation/sha256_core.h"
#include "platform/windows_filesystem.h"
#include "media/jpeg_backend.h"
#include "media/heic_backend.h"
#include "media/pixel_orientation.h"
#include "media/image_exif_mapping.h"
#include "media/jpeg_gainmap_mpf.h"
#include "media/media_inspector.h"
#include "media/target_primary_observation.h"
#include "media/video_converter.h"
#include "media/xmp_observation.h"
#include "metadata/jpeg.h"
#include "metadata/exif.h"
#include "protocols/samsung_inventory.h"
#include "containers/isobmff_mebx_transplant.h"
#include "containers/isobmff_video_profile.h"
#include "containers/heif_internal.h"
#include <algorithm>
#include <array>
#include <cstring>
#include <filesystem>
#include <string>
#include <vector>

namespace {
using namespace lpb::media;
uint32_t be32(const uint8_t* p) {return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3];}
void putbe(std::vector<uint8_t>& b,size_t p,uint32_t n){for(unsigned i=0;i<4;++i)b[p+i]=uint8_t(n>>((3-i)*8));}
void le32(std::vector<uint8_t>& b,uint32_t n){for(unsigned i=0;i<4;++i)b.push_back(uint8_t(n>>(i*8)));}
bool read(const std::filesystem::path& path,std::vector<uint8_t>& bytes) {
    windows_readonly_source source;return source.open(path)&&source.read_all_bounded(4,1024ULL*1024*1024,bytes);
}
bool stage(lpb_context* context,const std::filesystem::path& path,std::span<const uint8_t> bytes,int role) {
    windows_owned_output output;
    return output.create(path,L"lpb-samsung")&&output.write_all(bytes)&&output.publish_registered_no_replace(context,role,path);
}
struct source_layout {
    bool heic{},has_thumbnail{};size_t image_end{},main_offset{},main_length{};uint32_t orientation{},width{},height{};
    lpb::protocols::sef_inventory sef;uint16_t autoplay_marker{};
    std::string xml;std::vector<uint8_t> image;
    jpeg_gainmap_mpf::jpeg_info jpeg;
};
bool inspect(std::span<const uint8_t> bytes,source_layout& f,std::string& error) {
    f={};lpb::memory_random_access source{bytes};
    if(!lpb::protocols::observe_sef(source.view(),0,bytes.size(),f.sef,error))return false;
    const auto* motion=f.sef.find(0x0a30);if(!motion){error="Samsung main video owner is absent.";return false;}
    f.heic=!(bytes[0]==255&&bytes[1]==216);
    for(const auto& tag:f.sef.tags)if(tag.marker==0x0a33) {
        if(tag.name!="MotionPhoto_AutoPlay"){error="Samsung autoplay marker/name mismatch.";return false;}
        f.autoplay_marker=tag.marker;
    }
    if(!f.heic) {
        f.image_end=static_cast<size_t>(f.sef.begin);f.main_offset=static_cast<size_t>(motion->data_offset);f.main_length=static_cast<size_t>(motion->data_size);
        if(!jpeg_gainmap_mpf::parse_jpeg(bytes,f.jpeg)){error="Samsung JPEG/MPF is malformed.";return false;}
        lpb::memory_random_access primary{bytes.first(f.jpeg.eoi_end)};jpeg_observation observed;jpeg_observation_failure failure{};uint64_t bad{};
        if(!observe_jpeg(primary.view(),observed,failure,bad,{true,true,true,false})){error="Samsung JPEG metadata cannot establish orientation.";return false;}
        f.orientation=observed.orientation;f.width=observed.width;f.height=observed.height;f.xml=observed.xmp;
        if(!observed.exif_payload.empty()) {
            tiff_ifd root;
            if(!parse_ifd(observed.exif_payload.data(),observed.exif_payload.size(),6,observed.exif.ifd0_offset,observed.exif.big_endian,&root)){error="Samsung EXIF thumbnail directory is malformed.";return false;}
            f.has_thumbnail=root.next_ifd_offset!=0;
        }
    } else {
        size_t p=0;bool mpvd=false,sefd=false;
        while(p<bytes.size()) {
            isobmff_box_header b;if(!try_read_box_header(bytes.data(),p,bytes.size(),b)||b.extends_to_end){error="Samsung HEIC has malformed top-level ownership.";return false;}
            if(!std::memcmp(bytes.data()+p+4,"mpvd",4)) {
                if(mpvd||sefd){error="Samsung HEIC has ambiguous movie ownership.";return false;}mpvd=true;f.image_end=p;f.main_offset=p+b.header_size;f.main_length=b.size-b.header_size;
            }
            if(!std::memcmp(bytes.data()+p+4,"sefd",4)) {
                if(!mpvd||sefd||p+b.size!=bytes.size()||f.sef.begin!=p+b.header_size){error="Samsung SEFD is not a final complete owner.";return false;}sefd=true;
            }
            p+=b.size;
        }
        if(!mpvd||!sefd||motion->data_size!=12||std::memcmp(bytes.data()+motion->data_offset,"mpv2",4)||
            be32(bytes.data()+motion->data_offset+4)!=f.main_offset||be32(bytes.data()+motion->data_offset+8)!=f.main_length){error="Samsung HEIC video pointer disagrees with container.";return false;}
        lpb::containers::heif_primary_properties primary;uint32_t id{};
        if(!lpb::containers::observe_primary_item_properties(bytes.first(f.image_end),primary,error)||
            !lpb::containers::read_primary_xmp(bytes.first(f.image_end),id,f.xml,error))return false;
        f.orientation=primary.orientation;f.width=primary.width;f.height=primary.height;
    }
    if(!f.main_length||f.main_offset>bytes.size()||f.main_length>bytes.size()-f.main_offset||f.orientation<1||f.orientation>8){error="Samsung media extent/direction is unsupported.";return false;}
    target_xmp_observation xmp;xmp_observation_failure failure{};
    if(!observe_target_xmp(f.xml,xmp,failure)||!xmp.live_claim||!xmp.directory_present){error="Samsung motion XMP identity is ambiguous.";return false;}
    f.image.assign(bytes.begin(),bytes.begin()+f.image_end);return true;
}

bool rewrite_jpeg_xmp(std::vector<uint8_t>& bytes,std::string_view xml,std::string& error) {
    jpeg_gainmap_mpf::jpeg_info before,after;
    if(!jpeg_gainmap_mpf::parse_jpeg(bytes,before))return false;
    std::vector<uint8_t> changed;
    if(lpb::metadata::replace_standard_jpeg_xmp_unchecked(bytes,{reinterpret_cast<const uint8_t*>(xml.data()),xml.size()},changed,error)!=LPB_RESULT_OK||
        !jpeg_gainmap_mpf::parse_jpeg(changed,after))return false;
    if(before.mpf_status==jpeg_gainmap_mpf::status::present) {
        if(before.mpf.image_count!=2||before.mpf.entries[1].absolute_offset!=before.eoi_end||
            !jpeg_gainmap_mpf::patch_layout(changed,after.mpf,after.eoi_end,after.eoi_end,before.mpf.entries[1].size))return false;
    }
    bytes=std::move(changed);return true;
}
bool normalize_jpeg(lpb_context* context,const source_layout& f,bool rotate,bool strip,std::vector<uint8_t>& image,bool& reencoded,std::string& error) {
    image=f.image;reencoded=false;
    if(!rotate&&!strip)return true;
    const uint32_t orientation=rotate?f.orientation:0;
    std::vector<uint8_t> primary,secondary;bool encoded{};
    if(normalize_repair_jpeg_bytes(context,std::span<const uint8_t>(f.image).first(f.jpeg.eoi_end),orientation,strip,primary,encoded)!=LPB_RESULT_OK){error="Samsung primary JPEG direction/metadata mapping failed.";return false;}
    reencoded|=encoded;
    if(f.jpeg.mpf_status==jpeg_gainmap_mpf::status::present) {
        if(f.jpeg.mpf.image_count!=2||f.jpeg.mpf.entries[1].absolute_offset!=f.jpeg.eoi_end||
            f.jpeg.eoi_end+f.jpeg.mpf.entries[1].size!=f.image.size()){error="Samsung MPF auxiliary ownership is unsupported.";return false;}
        if(normalize_repair_jpeg_bytes(context,std::span<const uint8_t>(f.image).subspan(f.jpeg.eoi_end),orientation,strip,secondary,encoded)!=LPB_RESULT_OK){error="Samsung HDR auxiliary JPEG normalization failed.";return false;}
        reencoded|=encoded;
        jpeg_gainmap_mpf::jpeg_info current;
        if(!jpeg_gainmap_mpf::parse_jpeg(primary,current)||!jpeg_gainmap_mpf::patch_layout(primary,current.mpf,primary.size(),primary.size(),secondary.size()))return false;
    } else if(f.jpeg.eoi_end!=f.image.size()){error="Unowned Samsung JPEG image trailer.";return false;}
    image=std::move(primary);image.insert(image.end(),secondary.begin(),secondary.end());return true;
}

bool normalize_heic(lpb_context* context,const char* source_path,const source_layout& f,bool rotate,bool strip,
    std::vector<uint8_t>& image,bool& reencoded,bool& thumbnail_changed,std::string& error) {
    image=f.image;reencoded=false;thumbnail_changed=false;
    if(strip&&!lpb::containers::strip_primary_thumbnails(image,thumbnail_changed,error))return false;
    if(!rotate)return true;
    std::vector<uint32_t> ids;
    if(!lpb::containers::repair_display_item_ids(image,ids,error))return false;
    std::vector<lpb::containers::heif_repair_encoded_item> replacements;
    for(const auto id:ids) {
        lpb::containers::heif_primary_properties properties;
        if(!lpb::containers::observe_item_properties(image,id,properties,error))return false;
        if(properties.has_clean_aperture||!properties.stored_geometry_compatible){error="Samsung HEIC item geometry cannot be safely normalized.";return false;}
        if(properties.orientation==1)continue;
        pixel_surface surface;
        if(decode_heic_item_file(context,source_path,id,surface,true)!=LPB_RESULT_OK||
            !normalize_pixel_orientation(surface,properties.orientation)){error="Samsung HEIC owned item decode/rotation failed.";return false;}
        lpb::containers::heif_repair_encoded_item donor;donor.id=id;
        if(encode_heic_primary_metadata_bytes(context,surface,92,{},{},donor.encoded)!=LPB_RESULT_OK){error="Samsung HEIC owned item encoding failed.";return false;}
        replacements.push_back(std::move(donor));
    }
    lpb::containers::heif_primary_properties primary;
    if(!lpb::containers::observe_primary_item_properties(image,primary,error))return false;
    if(!primary.exif_payload.empty()) {
        target_primary_observation mapping;mapping.width=primary.width;mapping.height=primary.height;mapping.orientation=primary.orientation;
        if(!map_image_exif_orientation(primary.exif_payload,mapping)||
            !lpb::containers::replace_primary_exif_payload(image,primary.exif_payload,error)){error="Samsung HEIC primary EXIF direction mapping failed.";return false;}
    }
    if(!lpb::containers::replace_repair_encoded_items(image,replacements,error))return false;
    lpb::containers::heif_primary_properties after;
    if(!lpb::containers::observe_primary_item_properties(image,after,error)||after.orientation!=1||
        after.width!=(f.orientation>=5?f.height:f.width)||after.height!=(f.orientation>=5?f.width:f.height)){error="Samsung HEIC normalized dimensions/direction differ.";return false;}
    reencoded=!replacements.empty();return true;
}

bool normalize_movie(lpb_context* context,std::span<const uint8_t> source,const std::filesystem::path& workspace,
    const char* stem,bool apply,std::vector<uint8_t>& output,uint32_t& rotation,uint32_t& reflection,bool& encoded,bool& color_corrected,std::string& error) {
    const auto input=workspace/(std::string(stem)+"-source.mp4"),target=workspace/(std::string(stem)+"-encoded.mp4");
    std::vector<uint8_t> decoder_input;bool color_mapped{};
    if(!normalize_samsung_autoplay_color(source,decoder_input,color_mapped)){error="Samsung movie codec/color ownership is malformed.";return false;}
    if(!stage(context,input,decoder_input,LPB_ARTIFACT_MOTION_VIDEO)){error="Cannot stage Samsung video.";return false;}
    const auto input_name=path_to_utf8(input),target_name=path_to_utf8(target);
    lpb_video_item_facts facts{};facts.struct_size=sizeof(facts);
    if(probe_video_file(context,input_name.c_str(),&facts,&reflection)!=LPB_RESULT_OK){error=context->last_error;return false;}
    rotation=static_cast<uint32_t>(facts.rotation_degrees);encoded=false;color_corrected=false;output.assign(source.begin(),source.end());
    if(!apply||(!rotation&&!reflection))return true;
    std::vector<uint8_t> decoder_video;
    if(!extract_isobmff_main_video(decoder_input,decoder_video)){error="Samsung decoder view cannot bind the unique video and all original sample ranges.";return false;}
    const auto decoder_path=workspace/(std::string(stem)+"-video.mp4");
    if(!stage(context,decoder_path,decoder_video,LPB_ARTIFACT_MOTION_VIDEO)){error="Cannot stage the Samsung video-only decoder view.";return false;}
    const auto decoder_name=path_to_utf8(decoder_path);
    if(transcode_video_file(context,decoder_name.c_str(),target_name.c_str(),facts.container,facts.codec,18,nullptr,0,nullptr,nullptr,nullptr,true)!=LPB_RESULT_OK){error=context->last_error;return false;}
    std::vector<uint8_t> changed;
    if(!read(target,changed)||!replace_isobmff_main_video(source,changed,output)){error="Samsung video cannot safely retain its audio/data tracks after normalization.";return false;}
    const auto verified=workspace/(std::string(stem)+"-verified.mp4");
    if(!stage(context,verified,output,LPB_ARTIFACT_MOTION_VIDEO)){error="Cannot stage Samsung complete video.";return false;}
    lpb_video_item_facts after{};after.struct_size=sizeof(after);uint32_t flip{};const auto name=path_to_utf8(verified);
    if(probe_video_file(context,name.c_str(),&after,&flip)!=LPB_RESULT_OK||after.rotation_degrees||flip||facts.has_audio!=after.has_audio){error="Samsung complete video failed direction/audio validation.";return false;}
    encoded=true;color_corrected=color_mapped;return true;
}

bool rebuild(lpb_context*,std::span<const uint8_t> original,const source_layout& f,std::vector<uint8_t> image,
    std::span<const uint8_t> main,std::span<const uint8_t> autoplay,bool rotated,std::vector<uint8_t>& output,std::string& error) {
    struct tag {uint16_t marker;std::vector<uint8_t> bytes;size_t offset{};};std::vector<tag> tags;
    size_t seflen=20+f.sef.tags.size()*12;size_t main_relative{},padding{},ordinary_size{};
    auto ordered=f.sef.tags;std::sort(ordered.begin(),ordered.end(),[](const auto&a,const auto&b){return a.offset<b.offset;});
    for(const auto& owner:ordered) {
        tag item{owner.marker,{original.begin()+owner.offset,original.begin()+owner.data_offset}};
        const auto payload=owner.marker==0x0a30&&!f.heic?main:owner.marker==f.autoplay_marker?autoplay:
            original.subspan(static_cast<size_t>(owner.data_offset),static_cast<size_t>(owner.data_size));
        item.bytes.insert(item.bytes.end(),payload.begin(),payload.end());
        if(owner.marker==0x0a30&&!f.heic)main_relative=ordinary_size+static_cast<size_t>(owner.data_offset-owner.offset);
        ordinary_size+=item.bytes.size();seflen+=item.bytes.size();tags.push_back(std::move(item));
    }
    const uint64_t resource=f.heic?main.size()+8+seflen:seflen-main_relative;
    padding=f.heic?8:main_relative;
    uint64_t gainmap_length{};
    if(!f.heic){jpeg_gainmap_mpf::jpeg_info current;if(!jpeg_gainmap_mpf::parse_jpeg(image,current))return false;if(current.mpf.image_count==2)gainmap_length=current.mpf.entries[1].size;}
    std::string xml;xmp_observation_failure failure{};
    if(!rewrite_samsung_repair_xmp(f.xml,resource,padding,gainmap_length,rotated?f.orientation:1,f.width,f.height,xml,failure)){error="Samsung XMP repair layout mapping failed.";return false;}
    if(f.heic) {
        std::vector<uint8_t> changed;
        if(!lpb::containers::rewrite_primary_xmp(image,xml,changed,error))return false;
        image=std::move(changed);
    }else if(!rewrite_jpeg_xmp(image,xml,error))return false;
    if(image.size()+main.size()+seflen+16>UINT32_MAX){error="Samsung repair exceeds 32-bit pointer limits.";return false;}
    output=std::move(image);
    const size_t main_at=output.size()+(f.heic?8:main_relative);
    if(f.heic){const size_t at=output.size();output.resize(at+8);putbe(output,at,static_cast<uint32_t>(main.size()+8));std::memcpy(output.data()+at+4,"mpvd",4);output.insert(output.end(),main.begin(),main.end());
        const size_t sefd=output.size();output.resize(sefd+8);putbe(output,sefd,static_cast<uint32_t>(seflen+8));std::memcpy(output.data()+sefd+4,"sefd",4);}
    for(auto& item:tags) {
        item.offset=output.size();
        if(f.heic&&item.marker==0x0a30){if(item.bytes.size()<12)return false;putbe(item.bytes,item.bytes.size()-8,static_cast<uint32_t>(main_at));putbe(item.bytes,item.bytes.size()-4,static_cast<uint32_t>(main.size()));}
        output.insert(output.end(),item.bytes.begin(),item.bytes.end());
    }
    const size_t table=output.size();output.insert(output.end(),{'S','E','F','H'});le32(output,f.sef.version);le32(output,static_cast<uint32_t>(tags.size()));
    for(const auto& item:tags){output.insert(output.end(),{0,0,uint8_t(item.marker),uint8_t(item.marker>>8)});le32(output,static_cast<uint32_t>(table-item.offset));le32(output,static_cast<uint32_t>(item.bytes.size()));}
    le32(output,static_cast<uint32_t>(12+tags.size()*12));output.insert(output.end(),{'S','E','F','T'});
    source_layout after;if(!inspect(output,after,error))return false;
    if((rotated&&(after.orientation!=1||after.width!=(f.orientation>=5?f.height:f.width)||after.height!=(f.orientation>=5?f.width:f.height)))||
        after.heic!=f.heic||after.autoplay_marker!=f.autoplay_marker||after.sef.tags.size()!=f.sef.tags.size()){error="Samsung output identity differs.";return false;}
    for(const auto& owner:f.sef.tags)if(owner.marker!=0x0a30&&owner.marker!=f.autoplay_marker){const auto* retained=after.sef.find(owner.marker);
        if(!retained||retained->size!=owner.size||!std::equal(original.begin()+owner.offset,original.begin()+owner.offset+owner.size,output.begin()+retained->offset)){error="Samsung ordinary record differs after repair.";return false;}}
    return true;
}
}

LPB_API lpb_result LPB_CALL lpb_repair_samsung_v1(lpb_context* context,const char* source_path,const char* workspace_path,
    const char* output_path,const lpb_samsung_repair_request_v1* request,lpb_samsung_repair_result_v1* result) {
    if(!context||!source_path||!workspace_path||!request||request->struct_size!=sizeof(*request)||request->api_version!=1||request->reserved||
        request->actions&~15u||!result||result->struct_size!=sizeof(*result)||result->api_version!=1||
        (output_path&&paths_alias(source_path,output_path)))return LPB_RESULT_INVALID_ARGUMENT;
    if(lpb_writer_callback_active(context))return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_writer_frame writer_frame(context);
    *result={};result->struct_size=sizeof(*result);result->api_version=1;
    const auto fail=[&](const std::string& error){set_error(context,error.c_str());return lpb_context_check_cancelled(context)==LPB_RESULT_CANCELLED?LPB_RESULT_CANCELLED:LPB_RESULT_UNSUPPORTED;};
    try {
        windows_readonly_source source;lpb_file_identity identity{};std::vector<uint8_t> bytes;
        if(!source.open(utf8_to_path(source_path))||!source.capture_identity(identity)||!source.read_all_bounded(4,1024ULL*1024*1024,bytes))return fail("Cannot pin Samsung source.");
        uint8_t hash[32];lpb::crypto::sha256_buffer(bytes.data(),bytes.size(),hash);
        const auto& expected=request->expected_identity;
        if(identity.link_count!=1||identity.volume_serial!=expected.volume_serial||identity.file_index!=expected.file_index||identity.file_size!=expected.file_size||identity.link_count!=expected.link_count||std::memcmp(hash,request->source_sha256,32))return LPB_RESULT_SOURCE_CHANGED;
        lpb_source_media_facts source_facts{};source_facts.struct_size=sizeof(source_facts);
        if(inspect_source(context,source_path,nullptr,&source_facts)!=LPB_RESULT_OK)return fail(context->last_error);
        if(source_facts.protocol!=LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG&&source_facts.protocol!=LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC)return fail("Samsung repair requires confirmed Samsung source identity.");
        source_layout f;std::string error;if(!inspect(bytes,f,error))return fail(error);
        const auto workspace=utf8_to_path(workspace_path);const auto* auto_tag=f.sef.find(f.autoplay_marker);
        std::vector<uint8_t> main,autoplay;uint32_t main_flip{},auto_flip{};bool main_encoded{},auto_encoded{},main_color{},auto_color{};
        if(!normalize_movie(context,std::span<const uint8_t>(bytes).subspan(f.main_offset,f.main_length),workspace,"main",output_path&&(request->actions&8),main,result->main_rotation,main_flip,main_encoded,main_color,error))return fail(error);
        if(auto_tag&&!normalize_movie(context,std::span<const uint8_t>(bytes).subspan(static_cast<size_t>(auto_tag->data_offset),static_cast<size_t>(auto_tag->data_size)),workspace,"autoplay",output_path&&(request->actions&8),autoplay,result->autoplay_rotation,auto_flip,auto_encoded,auto_color,error))return fail(error);
        result->image_orientation=f.orientation;result->flags=(auto_tag?1u:0u)|(f.heic?2u:0u)|((main_color||auto_color)?4u:0u);
        const bool rotate_image=f.orientation>1&&(request->actions&(f.heic?4u:1u));
        result->changed=(rotate_image?(f.heic?4u:1u):0u)|(((request->actions&8)&&(result->main_rotation||main_flip||result->autoplay_rotation||auto_flip))?8u:0u);
        std::vector<uint8_t> image=f.image;bool image_encoded{},image_rotated=rotate_image;
        std::string image_warning;
        const auto retain_original_image=[&]() {
            if(output_path ? !(main_encoded||auto_encoded) : !(result->changed&8))return false;
            image=f.image;image_encoded=false;image_rotated=false;result->changed&=~7u;result->flags|=256;
            image_warning=error;error.clear();return true;
        };
        if(!f.heic&&(rotate_image||(request->actions&2))) {
            if(!normalize_jpeg(context,f,rotate_image,request->actions&2,image,image_encoded,error)&&!retain_original_image())return fail(error);
            if(image_warning.empty()&&f.has_thumbnail&&(request->actions&2)&&image!=f.image)result->changed|=2;
        }
        if(f.heic) {
            bool thumbnail_changed{};
            if(output_path) {
                if(!normalize_heic(context,source_path,f,rotate_image,request->actions&2,image,image_encoded,thumbnail_changed,error)&&!retain_original_image())return fail(error);
            } else if((request->actions&2)&&!lpb::containers::strip_primary_thumbnails(image,thumbnail_changed,error))return fail(error);
            if(image_warning.empty()&&thumbnail_changed)result->changed|=2;
        }
        result->reencoded=(image_encoded?1u:0u)|(main_encoded?8u:0u)|(auto_encoded?16u:0u);
        const std::string details="Samsung image direction="+std::to_string(f.orientation)+"; main video="+std::to_string(result->main_rotation)+"; autoplay video="+std::to_string(result->autoplay_rotation)+". Available selected direction repairs are applied; source audio/data tracks and ordinary SEF records are retained. JPEG prefers perfect lossless transform, otherwise quality 92; HEIC quality 92; video CRF 18."+
            ((main_color||auto_color)?" Conflicting Samsung autoplay SPS color declarations were aligned with its explicit BT.709 container display.":"")+
            (image_warning.empty()?"":" Image repair was not completed; original image retained. Isolated output required. "+image_warning);
        strncpy_s(result->details,details.c_str(),_TRUNCATE);
        if(!output_path||!result->changed)return LPB_RESULT_OK;
        if(!source.verify_same_identity(utf8_to_path(source_path)))return LPB_RESULT_SOURCE_CHANGED;
        std::vector<uint8_t> assembled;if(!rebuild(context,bytes,f,std::move(image),main,autoplay,image_rotated,assembled,error))return fail(error);
        if(lpb_context_check_cancelled(context)!=LPB_RESULT_OK)return LPB_RESULT_CANCELLED;
        windows_owned_output output;const auto destination=utf8_to_path(output_path);
        if(!output.create(destination,L"lpb-samsung-repair")||!output.write_all(assembled)||!output.ready_to_consume())return fail("Cannot stage Samsung repaired file.");
        lpb_source_media_facts verified{};verified.struct_size=sizeof(verified);const auto temp=path_to_utf8(output.path());
        if(inspect_source(context,temp.c_str(),nullptr,&verified,nullptr,true)!=LPB_RESULT_OK||verified.protocol!=source_facts.protocol)return fail("Samsung repaired structure did not retain source protocol.");
        if(!output.publish_registered_no_replace(context,LPB_ARTIFACT_PRIMARY_IMAGE,destination))return fail("Cannot publish Samsung repaired staging file.");
        return LPB_RESULT_OK;
    }catch(...){set_error(context,"Unexpected internal Samsung repair failure.");return LPB_RESULT_INTERNAL_ERROR;}
}
