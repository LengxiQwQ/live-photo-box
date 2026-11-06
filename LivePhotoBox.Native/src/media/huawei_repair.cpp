#include "livephotobox_native.h"
#include "foundation/internal.h"
#include "foundation/sha256.h"
#include "platform/windows_filesystem.h"
#include "media/media_inspector.h"
#include "media/jpeg_backend.h"
#include "media/jpeg_observation.h"
#include "media/xmp_observation.h"
#include "media/video_converter.h"
#include "media/repair_video_proof.h"
#include "containers/isobmff_mebx_transplant.h"
#include <cstring>

namespace {
using namespace lpb::media;
bool retained_metadata(const lpb_preservation_observation& a, const lpb_preservation_observation& b) {
    return !(b.flags & LPB_POBS_CODESTREAM_ERROR) &&
          std::strcmp(a.gps_sha256,b.gps_sha256)==0 && std::strcmp(a.icc_sha256,b.icc_sha256)==0 &&
          std::strcmp(a.makernote_nonlive_sha256,b.makernote_nonlive_sha256)==0 &&
          std::strcmp(a.xmp_nonprotocol_sha256,b.xmp_nonprotocol_sha256)==0;
}
bool stage_movie(lpb_context* context,const std::filesystem::path& path,std::span<const uint8_t> bytes) {
    windows_owned_output output;
    return output.create(path,L"lpb-huawei-video")&&output.write_all(bytes)&&
        output.publish_registered_no_replace(context,LPB_ARTIFACT_MOTION_VIDEO,path);
}
}

LPB_API lpb_result LPB_CALL lpb_repair_huawei_v1(lpb_context* context,const char* path,const char* output_path,
    const lpb_huawei_repair_request_v1* request,lpb_huawei_repair_result_v1* result) {
    if(!context||!path||!request||request->struct_size!=sizeof(*request)||request->api_version!=1||request->reserved||
       (request->actions&~15u)||!result||result->struct_size!=sizeof(*result)||result->api_version!=1||
       (output_path&&paths_alias(path,output_path)))return LPB_RESULT_INVALID_ARGUMENT;
    *result={};result->struct_size=sizeof(*result);result->api_version=1;
    if(lpb_writer_callback_active(context))return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_writer_frame writer_frame(context);
    const auto fail=[&](const char* why){set_error(context,why);return LPB_RESULT_UNSUPPORTED;};
    try {
        windows_readonly_source source;lpb_file_identity identity{};std::vector<uint8_t> original;
        if(!source.open(utf8_to_path(path))||!source.capture_identity(identity)||
           !source.read_all_bounded(4,1024ULL*1024*1024,original))return fail("Cannot read Huawei source.");
        uint8_t hash[32];lpb::crypto::sha256_buffer(original.data(),original.size(),hash);
        const auto& expected=request->expected_identity;
        if(identity.volume_serial!=expected.volume_serial||identity.file_index!=expected.file_index||
           identity.file_size!=expected.file_size||identity.link_count!=expected.link_count||std::memcmp(hash,request->source_sha256,32))
            return LPB_RESULT_SOURCE_CHANGED;
        lpb_source_media_facts facts{};facts.struct_size=sizeof(facts);
        const auto inspected=inspect_source(context,path,nullptr,&facts);
        if(inspected!=LPB_RESULT_OK)return inspected;
        if(facts.protocol!=LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO)return fail("Huawei repair requires a confirmed Huawei dynamic photo.");
        const auto image_length=facts.primary_image.file_range.length;
        const auto video=facts.motion_video.file_range;
        if(facts.primary_image.file_range.offset||!image_length||image_length>original.size()||
           video.offset<image_length||video.offset>original.size()||video.length>original.size()-video.offset)
            return fail("Huawei media ranges are inconsistent.");
        std::vector<uint8_t> image(original.begin(),original.begin()+static_cast<size_t>(image_length));
        lpb_preservation_observation before{};before.struct_size=sizeof(before);
        auto status=lpb_media_capture_preservation_observation_bytes(context,image,facts.protocol,facts.primary_image.container,&before);
        if(status!=LPB_RESULT_OK)return status;
        // A preservation fingerprint limitation does not make a valid source
        // corrupt. Known safe actions may still run; unproved output is isolated.
        if(before.flags&(LPB_POBS_CODESTREAM_ERROR|LPB_POBS_EXIF_PARSE_ERROR))result->pending|=request->actions&7u;
        result->image_orientation=before.orientation;
        std::string details;
        std::vector<uint8_t> repaired_movie;
        if(request->actions&8u) {
            lpb::memory_random_access movie{std::span<const uint8_t>(original).subspan(static_cast<size_t>(video.offset),static_cast<size_t>(video.length))};
            lpb_video_item_facts observed{};observed.struct_size=sizeof(observed);uint32_t reflection{};
            status=probe_video_reader(context,movie.view(),&observed,&reflection);
            if(status==LPB_RESULT_CANCELLED)return status;
            if(status!=LPB_RESULT_OK) {result->pending|=8u;details+="Embedded video direction could not be checked; video bytes retained. ";}
            else {
                result->main_rotation=observed.rotation_degrees;result->reflection=reflection;
                if(observed.rotation_degrees||reflection) {
                    result->changed|=8u;
                    if(output_path) {
                        const auto workspace=utf8_to_path(output_path).parent_path();
                        const auto input=workspace/"huawei-source.mp4",encoded=workspace/"huawei-encoded.mp4",verified=workspace/"huawei-verified.mp4";
                        const auto original_movie=std::span<const uint8_t>(original).subspan(size_t(video.offset),size_t(video.length));
                        const auto in_name=path_to_utf8(input),out_name=path_to_utf8(encoded);
                        if(!stage_movie(context,input,original_movie))return fail("Cannot stage Huawei embedded video.");
                        status=transcode_video_file(context,in_name.c_str(),out_name.c_str(),observed.container,observed.codec,18,nullptr,0,nullptr,nullptr,nullptr,true);
                        if(status==LPB_RESULT_CANCELLED)return status;
                        if(status!=LPB_RESULT_OK&&status!=LPB_RESULT_UNSUPPORTED)return status;
                        windows_readonly_source encoded_file;std::vector<uint8_t> encoded_bytes;
                        if(status!=LPB_RESULT_OK||!encoded_file.open(encoded)||!encoded_file.read_all_bounded(4,1024ULL*1024*1024,encoded_bytes)||
                           !replace_isobmff_main_video(original_movie,encoded_bytes,repaired_movie)||!verify_repair_video(original_movie,repaired_movie)) {
                            // Keep independently repairable image actions available.
                            result->changed&=~8u;result->pending|=8u;repaired_movie.clear();
                            details+="Embedded video direction repair could not retain its verified timing/color/SEI/tracks; original video retained. ";
                        }else {
                            if(!stage_movie(context,verified,repaired_movie))return fail("Cannot stage Huawei verified video.");
                            lpb::memory_random_access check{repaired_movie};lpb_video_item_facts after_video{};after_video.struct_size=sizeof(after_video);uint32_t flip{};
                            if(probe_video_reader(context,check.view(),&after_video,&flip)!=LPB_RESULT_OK||after_video.rotation_degrees||flip||
                               after_video.has_audio!=observed.has_audio)return fail("Huawei repaired video direction/audio validation failed.");
                        }
                    }
                }
            }
        }
        const bool heic=facts.primary_image.container==LPB_IMAGE_CONTAINER_HEIC;
        // Attempt metadata actions separately. Unknown thumbnail ownership must
        // not prevent a separately verifiable orientation alignment, or vice versa.
        for(uint32_t action:{1u,2u}) {
            const uint32_t requested=action==1u?2u:4u;
            if(!(request->actions&requested)||(action==2u&&!heic))continue;
            auto candidate=image;uint32_t changed{};
            status=repair_image_exif_bytes(context,candidate,action,changed);
            if(status==LPB_RESULT_CANCELLED)return status;
            if(status!=LPB_RESULT_OK&&status!=LPB_RESULT_UNSUPPORTED)return status;
            if(status!=LPB_RESULT_OK) {result->pending|=requested;details+=action==1u?"Thumbnail cleanup was not completed; original data retained. ":"HEIF orientation alignment was not completed; original data retained. ";continue;}
            if(changed){image=std::move(candidate);result->changed|=requested;result->metadata_changes|=changed;}
        }
        bool rotated=false;
        if(!heic&&(request->actions&1u)&&before.orientation>1) {
            lpb::memory_random_access memory{image};jpeg_observation jpeg;jpeg_observation_failure why{};uint64_t bad{};
            std::vector<uint8_t> transformed;bool reencoded{};
            // A single bounded JPEG with no competing geometry can use the
            // existing perfect lossless operation. Opaque appended/MPF media is
            // retained rather than guessed or passed to a lossy fallback.
            const bool supported=observe_jpeg(memory.view(),jpeg,why,bad,{true,true,true,true})&&
                jpeg.eoi_end==image.size()&&!jpeg.mpf&&before.orientation<=8&&
                ((jpeg.xmp.empty()&&jpeg.extended_xmp.empty())||
                 validate_jpeg_repair_xmp(jpeg.xmp,jpeg.extended_xmp,jpeg.extended_guid,jpeg.orientation,jpeg.width,jpeg.height));
            if(supported&&normalize_repair_jpeg_bytes(context,image,before.orientation,false,transformed,reencoded,true)==LPB_RESULT_OK&&!reencoded) {
                image=std::move(transformed);result->changed|=1u;rotated=true;
            }else {result->pending|=1u;details+="JPEG direction could not be repaired losslessly with its attached metadata; original direction retained. ";}
        }
        lpb_preservation_observation after{};after.struct_size=sizeof(after);
        status=lpb_media_capture_preservation_observation_bytes(context,image,facts.protocol,facts.primary_image.container,&after);
        if(status!=LPB_RESULT_OK)return status;
        if((result->changed&7u)&&(!retained_metadata(before,after)||
           (!rotated&&std::strcmp(before.image_codestream_sha256,after.image_codestream_sha256))||
           (rotated&&after.orientation!=1)))return fail("Huawei repaired photo did not retain its verified media and metadata.");
        for(uint32_t action:{1u,2u})if(result->metadata_changes&action) {
            auto check=image;uint32_t remaining{};
            if(repair_image_exif_bytes(context,check,action,remaining)!=LPB_RESULT_OK||remaining)
                return fail("Huawei selected metadata repair did not complete.");
        }
        details="Huawei dynamic photo: primary image, complete embedded MP4 and 60-byte trailer identified. "+details;
        if(result->changed&8u)details+=output_path?"Video direction repaired by re-encoding (CRF 18); codec, color depth, HDR/SEI and presentation timing verified. Original audio/data tracks retained. ":"Embedded video requires direction repair; analysis only, no encoding performed. ";
        else details+="Embedded video bytes retained unchanged. ";
        if(result->changed&1u)details+="JPEG direction repaired losslessly. ";
        if(result->changed&2u)details+="Confirmed thumbnails/previews removed. ";
        if(result->changed&4u)details+="EXIF orientation aligned with HEIF display transform. ";
        strncpy_s(result->details,details.c_str(),_TRUNCATE);
        if(!output_path||!result->changed)return LPB_RESULT_OK;
        // Metadata helpers retain offsets. A JPEG lossless transform may change
        // image length; LIVE_ is movie length +20, so it needs no rewriting.
        std::vector<uint8_t> assembled=std::move(image);
        if(repaired_movie.empty())assembled.insert(assembled.end(),original.begin()+static_cast<size_t>(image_length),original.end());
        else {
            if(video.offset+video.length+60!=original.size())return fail("Huawei video/trailer ownership differs.");
            const size_t gap=size_t(video.offset-image_length);
            if(gap!=0&&gap!=8&&gap!=16)return fail("Huawei video wrapper cannot be resized safely.");
            if(gap) {
                isobmff_box_header wrapper{};
                if(!try_read_box_header(original.data(),size_t(image_length),original.size(),wrapper)||wrapper.header_size!=gap||
                   std::memcmp(original.data()+image_length+4,"free",4)||wrapper.size!=original.size()-image_length)
                    return fail("Huawei video wrapper ownership differs.");
                const size_t start=assembled.size();assembled.insert(assembled.end(),original.begin()+size_t(image_length),original.begin()+size_t(video.offset));
                const uint64_t length=gap+repaired_movie.size()+60;
                if(gap==8&&length>UINT32_MAX)return fail("Huawei wrapper exceeds its size field.");
                if(!wrapper.extends_to_end)for(size_t i=0;i<(gap==8?4u:8u);++i)assembled[start+(gap==8?0:8)+i]=uint8_t(length>>((gap==8?3:7)-i)*8);
            }
            assembled.insert(assembled.end(),repaired_movie.begin(),repaired_movie.end());
            assembled.insert(assembled.end(),original.end()-60,original.end()-20);
            std::string live="LIVE_"+std::to_string(repaired_movie.size()+20);if(live.size()>20)return fail("Huawei LIVE length exceeds its field.");
            live.resize(20,' ');assembled.insert(assembled.end(),live.begin(),live.end());
        }
        if(!source.verify_same_identity(utf8_to_path(path)))return LPB_RESULT_SOURCE_CHANGED;
        if(lpb_context_check_cancelled(context)!=LPB_RESULT_OK)return LPB_RESULT_CANCELLED;
        windows_owned_output output;const auto destination=utf8_to_path(output_path);
        if(!output.create(destination,L"lpb-huawei-repair")||!output.write_all(assembled)||!output.ready_to_consume())
            return fail("Cannot stage Huawei repaired photo.");
        lpb_source_media_facts verified{};verified.struct_size=sizeof(verified);const auto staged=path_to_utf8(output.path());
        if(inspect_source(context,staged.c_str(),nullptr,&verified,nullptr,true)!=LPB_RESULT_OK||
           verified.protocol!=facts.protocol||verified.timing.cover_timestamp_us!=facts.timing.cover_timestamp_us||
           verified.motion_video.file_range.length!=(repaired_movie.empty()?video.length:repaired_movie.size())||
           verified.primary_image.width!=(rotated&&before.orientation>=5?facts.primary_image.height:facts.primary_image.width)||
           verified.primary_image.height!=(rotated&&before.orientation>=5?facts.primary_image.width:facts.primary_image.height))
            return fail("Huawei repaired output did not retain its dynamic photo structure and cover time.");
        const auto& retained=verified.motion_video.file_range;
        if(retained.offset>assembled.size()||retained.length>assembled.size()-retained.offset||
           (repaired_movie.empty()&&!std::equal(original.begin()+static_cast<size_t>(video.offset),original.end(),assembled.begin()+static_cast<size_t>(retained.offset),assembled.end()))||
           (!repaired_movie.empty()&&!std::equal(repaired_movie.begin(),repaired_movie.end(),assembled.begin()+size_t(retained.offset))))
            return fail("Huawei video or trailer changed during photo repair.");
        if(!output.publish_registered_no_replace(context,LPB_ARTIFACT_PRIMARY_IMAGE,destination))return fail("Cannot publish Huawei repaired staging photo.");
        return LPB_RESULT_OK;
    }catch(...){set_error(context,"Unexpected internal Huawei repair failure.");return LPB_RESULT_INTERNAL_ERROR;}
}
