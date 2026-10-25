#include "media/target_writer.h"
#include "media/media_inspector.h"
#include "foundation/internal.h"
#include "foundation/sha256_core.h"
#include "platform/windows_filesystem.h"
#include "protocols/target_structure.h"
#include "containers/isobmff.h"
#include "containers/isobmff_video_profile.h"
#include <array>
#include <string_view>

static_assert(sizeof(lpb_target_writer_artifact_v1)==1112);
static_assert(offsetof(lpb_target_writer_artifact_v1,expected_identity)==24);
static_assert(offsetof(lpb_target_writer_artifact_v1,sha256)==56);
static_assert(offsetof(lpb_target_writer_artifact_v1,path_utf8)==88);
static_assert(sizeof(lpb_target_writer_request_v1)==1080);
static_assert(offsetof(lpb_target_writer_request_v1,artifacts)==32);
static_assert(offsetof(lpb_target_writer_request_v1,cover_timestamp_us)==40);
static_assert(offsetof(lpb_target_writer_request_v1,staging_directory_utf8)==56);
static_assert(sizeof(lpb_target_writer_output_v1)==144);
static_assert(sizeof(lpb_target_writer_status_v1)==32);

struct lpb_target_writer_session {
    lpb_context* owner{};
    uint64_t token{},generation{};
    lpb_target_writer_request_v1 request{};
    std::array<lpb_target_writer_artifact_v1,2> artifacts{};
    std::array<windows_readonly_source,2> inputs;
    std::array<lpb_file_identity,2> observed{};
    std::array<std::array<uint8_t,32>,2> hashes{};
    std::array<uint8_t,32> fingerprint{};
    uint32_t state{},failure{};
    bool release_requested{};
    void* directory{};
    ~lpb_target_writer_session() noexcept { if(directory)CloseHandle(directory); }
};
namespace {
constexpr uint32_t failure_unsupported=1,failure_malformed=2,failure_identity=4,failure_io=5;
bool fixed_utf8(const char* value,size_t capacity) noexcept {
    const size_t n=static_cast<size_t>(std::find(value,value+capacity,'\0')-value);
    if(!n||n==capacity||n>static_cast<size_t>(INT_MAX))return false;
    for(size_t i=n+1;i<capacity;++i)if(value[i])return false;
    return MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value,static_cast<int>(n),nullptr,0)>0;
}
bool identity_equal(const lpb_file_identity& a,const lpb_file_identity& b) noexcept {
    return a.volume_serial==b.volume_serial&&a.file_index==b.file_index&&
        a.file_size==b.file_size&&a.link_count==b.link_count;
}
uint64_t token_of(lpb_target_writer_plan* value) noexcept {
    return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(value));
}
std::shared_ptr<lpb_target_writer_session> find_locked(lpb_context* context,lpb_target_writer_plan* plan) {
    for(const auto& record:context->target_writer_plans)
        if(record->token==token_of(plan)&&record->owner==context)return record;
    return {};
}
bool destruction_requested(lpb_context* context) {
    std::scoped_lock lock(context->lifetime_mutex);
    return context->destroy_requested;
}
lpb_result check_cancel(lpb_context* context) {
    const auto result=lpb_context_check_cancelled(context);
    if(result!=LPB_RESULT_OK)return result;
    if(destruction_requested(context))return LPB_RESULT_CANCELLED;
    return LPB_RESULT_OK;
}
lpb_result hash_reader(lpb_context* context,const lpb::random_access_reader& reader,uint8_t output[32]) {
    lpb::crypto::sha256_ctx hash;
    std::vector<uint8_t> buffer(1024*1024);
    for(uint64_t p=0;p<reader.length;) {
        const auto cancel=check_cancel(context);if(cancel!=LPB_RESULT_OK)return cancel;
        const size_t count=static_cast<size_t>(std::min<uint64_t>(buffer.size(),reader.length-p));
        if(!reader.read_exact(p,std::span<uint8_t>(buffer.data(),count)))return LPB_RESULT_INTERNAL_ERROR;
        hash.update(buffer.data(),count);p+=count;
    }
    hash.finalize(output);return LPB_RESULT_OK;
}
// Existing bounded box parser supplies hierarchy. Unsupported metadata owners
// are rejected, not searched as arbitrary strings or silently declared neutral.
lpb_result plain_video_graph(std::span<const uint8_t> bytes,size_t first,size_t end,unsigned depth=0) {
    if(depth>16)return LPB_RESULT_INVALID_ARGUMENT;
    for(size_t p=first;p<end;) {
        isobmff_box_header box{};
        if(!try_read_box_header(bytes.data(),p,end,box))return LPB_RESULT_INVALID_ARGUMENT;
        const std::string_view type(reinterpret_cast<const char*>(bytes.data()+p+4),4);
        if(type=="uuid"||type=="meta"||type=="udta"||type=="meco"||type=="tref"||type=="mpvd"||type=="sefd")
            return LPB_RESULT_UNSUPPORTED;
        if(type=="hdlr") {
            const size_t b=p+box.header_size;
            if(box.size<box.header_size+12)return LPB_RESULT_INVALID_ARGUMENT;
            const std::string_view handler(reinterpret_cast<const char*>(bytes.data()+b+8),4);
            if(handler!="vide"&&handler!="soun"&&handler!="alis")return LPB_RESULT_UNSUPPORTED;
        }
        if(type=="moov"||type=="trak"||type=="mdia"||type=="minf"||type=="stbl"||type=="edts"||type=="dinf") {
            const auto result=plain_video_graph(bytes,p+box.header_size,p+box.size,depth+1);
            if(result!=LPB_RESULT_OK)return result;
        }
        p+=box.size;
    }
    return LPB_RESULT_OK;
}
lpb_result observe_inputs(lpb_context* context,lpb_target_writer_session& session) {
    std::vector<uint8_t> primary,video;
    if(!session.inputs[0].read_all_bounded(4,32ULL*1024*1024,primary)||
        !session.inputs[1].read_all_bounded(8,256ULL*1024*1024,video)) {
        set_error(context,"[Unsupported] Writer v1 structural snapshot limits are 32MiB image and 256MiB video.");
        return LPB_RESULT_UNSUPPORTED;
    }
    auto result=lpb::media::observe_target_writer_primary(context,primary);
    if(result!=LPB_RESULT_OK)return result;
    if(session.artifacts[0].container!=LPB_IMAGE_CONTAINER_JPEG) {
        set_error(context,"[Malformed] Writer observed JPEG primary does not match the requested image container.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const auto container=lpb::media::detect_video_container(video);
    if(static_cast<uint32_t>(container)!=session.artifacts[1].container) {
        set_error(context,"[Malformed] Writer observed video container does not match the requested video container.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const bool valid=container==LPB_VIDEO_CONTAINER_MP4 ?
        is_valid_isobmff_media_range(video.data(),video.size(),0,video.size()):
        container==LPB_VIDEO_CONTAINER_MOV&&is_valid_mov_media_range(video.data(),video.size(),0,video.size());
    if(!valid){set_error(context,"[Malformed] Writer video hierarchy or sample tables are invalid.");return LPB_RESULT_INVALID_ARGUMENT;}
    result=plain_video_graph(video,0,video.size());
    if(result!=LPB_RESULT_OK) {
        set_error(context,"[Unsupported] Writer v1 cannot establish Neutral eligibility for this video metadata/track graph.");
        return result;
    }
    lpb::media::isobmff_video_profile profile{};
    lpb::media::video_profile_failure failure{};
    lpb::media::video_profile_diagnostic diagnostic{};
    if(!lpb::media::inspect_isobmff_video_profile(session.inputs[1].reader(),profile,failure,&diagnostic)) {
        const std::string message="Writer video profile inspection failed: category="+std::to_string(static_cast<int>(failure))+
            ", stage="+std::to_string(static_cast<int>(diagnostic))+".";
        set_error(context,message.c_str());
        return failure==lpb::media::video_profile_failure::unsupported?LPB_RESULT_UNSUPPORTED:LPB_RESULT_INVALID_ARGUMENT;
    }
    if(profile.classification!=lpb::media::video_profile_class::ordinary_sdr) {
        set_error(context,"[Unsupported] Writer v1 requires an observed ordinary SDR video profile.");
        return LPB_RESULT_UNSUPPORTED;
    }
    const uint32_t codec=profile.codec_fourcc==0x61766331U||profile.codec_fourcc==0x61766333U ?
        LPB_VIDEO_CODEC_H264 : profile.codec_fourcc==0x68766331U||profile.codec_fourcc==0x68657631U ? LPB_VIDEO_CODEC_HEVC:LPB_VIDEO_CODEC_UNKNOWN;
    if(codec!=session.artifacts[1].video_codec) {
        set_error(context,"[Malformed] Writer observed video codec does not match the requested video codec.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const uint32_t format=session.request.media_format;
    if(((format==0||format==2||format==4) && container!=LPB_VIDEO_CONTAINER_MP4)||
        ((format==1||format==3) && container!=LPB_VIDEO_CONTAINER_MOV)) {
        set_error(context,"[Malformed] Writer target media format does not match the observed video container.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if(format>=2) {
        set_error(context,"[Unsupported] Writer v1 HEIF primary/auxiliary integration is pending.");
        return LPB_RESULT_UNSUPPORTED;
    }
    isobmff_video_presentation_timeline timeline{};
    if(!inspect_isobmff_video_presentation_timeline(video,timeline)||!timeline.visible_sample_count) {
        set_error(context,"[Malformed] Writer video presentation timeline is invalid or has no visible samples.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if(session.request.cover_timestamp_present) {
        const auto requested=session.request.cover_timestamp_us;
        if(requested<0||requested>INT64_MAX/10) {
            set_error(context,"[Malformed] Writer cover timestamp is outside the supported nonnegative time range.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        bool found=false;
        for(const auto& sample:timeline.presentation_order)if(sample.visible&&sample.normalized_time_100ns>=0&&sample.duration_100ns>0) {
            const int64_t stamp=requested*10;
            if(stamp>=sample.normalized_time_100ns && stamp-sample.normalized_time_100ns<sample.duration_100ns)found=true;
        }
        if(!found) {
            set_error(context,"[Malformed] Writer cover timestamp does not select an observed visible video sample.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }
    return LPB_RESULT_OK;
}
void fingerprint(lpb_target_writer_session& session) noexcept {
    lpb::crypto::sha256_ctx hash;
    const auto integer=[&](uint64_t value){uint8_t bytes[8]{};for(unsigned i=0;i<8;++i)bytes[i]=static_cast<uint8_t>(value>>(i*8));hash.update(bytes,8);};
    integer(session.request.target_profile);integer(session.request.target_version);integer(session.request.media_format);
    integer(session.request.cover_timestamp_present);integer(static_cast<uint64_t>(session.request.cover_timestamp_us));
    for(size_t i=0;i<2;++i) {
        integer(session.artifacts[i].role);integer(session.artifacts[i].container);integer(session.artifacts[i].video_codec);
        integer(session.observed[i].volume_serial);integer(session.observed[i].file_index);
        integer(session.observed[i].file_size);integer(session.observed[i].link_count);
        hash.update(session.hashes[i].data(),32);
    }
    hash.finalize(session.fingerprint.data());
}
lpb_result revalidate(lpb_context* context,lpb_target_writer_session& session) {
    for(size_t i=0;i<2;++i) {
        lpb_file_identity current{};
        if(!session.inputs[i].capture_identity(current)||!identity_equal(current,session.observed[i])||
            !session.inputs[i].verify_same_identity(utf8_to_path(session.artifacts[i].path_utf8)))return LPB_RESULT_SOURCE_CHANGED;
        std::array<uint8_t,32> hash{};
        const auto result=hash_reader(context,session.inputs[i].reader(),hash.data());
        if(result!=LPB_RESULT_OK)return result;
        if(hash!=session.hashes[i])return LPB_RESULT_SOURCE_CHANGED;
    }
    return observe_inputs(context,session);
}
void finish(lpb_context* context,const std::shared_ptr<lpb_target_writer_session>& session,lpb_result result) {
    std::scoped_lock lock(context->plan_mutex);
    session->state=result==LPB_RESULT_CANCELLED?4U:3U;
    session->failure=result==LPB_RESULT_UNSUPPORTED?failure_unsupported:
        result==LPB_RESULT_SOURCE_CHANGED?failure_identity:result==LPB_RESULT_INVALID_ARGUMENT?failure_malformed:failure_io;
    if(session->release_requested) {
        auto& plans=context->target_writer_plans;
        plans.erase(std::remove(plans.begin(),plans.end(),session),plans.end());
    }
}
bool reentry(lpb_context* context) noexcept {
    if(!context||lpb_writer_callback_active(context)) {
        set_error(context,"[AuthorityViolation] Writer callback reentry is not authorized.");return true;
    }
    return false;
}
}

lpb_result LPB_CALL lpb_target_writer_prepare_v1(lpb_context* context,const lpb_target_writer_request_v1* request,lpb_target_writer_plan** output) {
    if(output)*output=nullptr;
    if(reentry(context))return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_writer_frame frame(context);
    try {
        if(!request||!output||request->struct_size!=sizeof(*request)||request->version!=1||
            request->artifact_count!=2||!request->artifacts||request->reserved0||request->reserved1||request->reserved2||
            request->cover_timestamp_present>1||(!request->cover_timestamp_present&&request->cover_timestamp_us!=0)||
            !fixed_utf8(request->staging_directory_utf8,sizeof(request->staging_directory_utf8)))return LPB_RESULT_INVALID_ARGUMENT;
        if(request->target_version!=1||!lpb::target::profile_accepts(request->target_profile,request->media_format))return LPB_RESULT_UNSUPPORTED;
        auto session=std::make_shared<lpb_target_writer_session>();
        session->owner=context;session->request=*request;session->request.artifacts=nullptr;
        bool roles[2]{};
        for(size_t i=0;i<2;++i) {
            const auto& artifact=request->artifacts[i];
            if(artifact.struct_size!=sizeof(artifact)||artifact.version!=1||artifact.role>1||roles[artifact.role]||artifact.reserved||
                !fixed_utf8(artifact.path_utf8,sizeof(artifact.path_utf8)))return LPB_RESULT_INVALID_ARGUMENT;
            if(artifact.role==0 && (artifact.container<1||artifact.container>2||artifact.video_codec))return LPB_RESULT_INVALID_ARGUMENT;
            if(artifact.role==1 && (artifact.container<1||artifact.container>2||artifact.video_codec<2||artifact.video_codec>3))return LPB_RESULT_INVALID_ARGUMENT;
            roles[artifact.role]=true;session->artifacts[artifact.role]=artifact;
        }
        const auto cancel=check_cancel(context);if(cancel!=LPB_RESULT_OK)return cancel;
        if(!lpb_platform_pin_directory(utf8_to_path(session->request.staging_directory_utf8),session->directory)) {
            set_error(context,"[IO] Writer cannot pin staging directory.");return LPB_RESULT_INTERNAL_ERROR;
        }
        for(size_t i=0;i<2;++i) {
            if(!session->inputs[i].open(utf8_to_path(session->artifacts[i].path_utf8),true)||
                !session->inputs[i].capture_identity(session->observed[i])) {
                set_error(context,"[IO] Writer cannot pin input with exclusive reader ownership.");return LPB_RESULT_INTERNAL_ERROR;
            }
            if(!identity_equal(session->observed[i],session->artifacts[i].expected_identity)||session->observed[i].link_count!=1)
                return LPB_RESULT_SOURCE_CHANGED;
            const auto result=hash_reader(context,session->inputs[i].reader(),session->hashes[i].data());
            if(result!=LPB_RESULT_OK)return result;
            if(std::memcmp(session->hashes[i].data(),session->artifacts[i].sha256,32))return LPB_RESULT_SOURCE_CHANGED;
        }
        if(session->observed[0].volume_serial==session->observed[1].volume_serial&&session->observed[0].file_index==session->observed[1].file_index)
            return LPB_RESULT_AUTHORITY_VIOLATION;
        const auto observed=observe_inputs(context,*session);if(observed!=LPB_RESULT_OK)return observed;
        fingerprint(*session);
        if(check_cancel(context)!=LPB_RESULT_OK)return LPB_RESULT_CANCELLED;
        if(!lpb_generate_unique_plan_token(session->token))return LPB_RESULT_INTERNAL_ERROR;
        {
            std::scoped_lock lock(context->plan_mutex);
            if(!context->next_target_writer_generation||context->next_target_writer_generation==UINT64_MAX)return LPB_RESULT_INTERNAL_ERROR;
            session->generation=context->next_target_writer_generation++;
            context->target_writer_plans.push_back(session);
        }
        *output=reinterpret_cast<lpb_target_writer_plan*>(static_cast<uintptr_t>(session->token));
        return LPB_RESULT_OK;
    } catch(...) {set_error(context,"Writer prepare failed at exception boundary.");return LPB_RESULT_INTERNAL_ERROR;}
}
lpb_result LPB_CALL lpb_target_writer_stage_v1(lpb_context* context,lpb_target_writer_plan* plan) {
    if(reentry(context))return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_writer_frame frame(context);
    std::shared_ptr<lpb_target_writer_session> session;
    try {
        {
            std::scoped_lock lock(context->plan_mutex);session=find_locked(context,plan);
            if(!session||!session->generation)return LPB_RESULT_AUTHORITY_VIOLATION;
            if(session->state!=0)return LPB_RESULT_PLAN_REPLAYED;
            session->state=1;
        }
        auto result=check_cancel(context);
        if(result==LPB_RESULT_OK)result=revalidate(context,*session);
        if(result==LPB_RESULT_OK) {
            // No family serializer has been connected yet. Never mint target
            // bytes or target-success from a validated neutral input session.
            set_error(context,"[Unsupported] Target serializer is not implemented; Writer attempt consumed with no outputs.");
            result=LPB_RESULT_UNSUPPORTED;
        }
        finish(context,session,result);return result;
    } catch(...) {
        if(session)finish(context,session,LPB_RESULT_INTERNAL_ERROR);
        set_error(context,"Writer stage failed at exception boundary.");return LPB_RESULT_INTERNAL_ERROR;
    }
}
lpb_result LPB_CALL lpb_target_writer_get_outputs_v1(lpb_context* context,lpb_target_writer_plan* plan,
    lpb_target_writer_output_v1* outputs,uint32_t capacity,uint32_t* count,lpb_target_writer_status_v1* status) {
    if(count)*count=0;
    if(reentry(context))return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_writer_frame frame(context);
    try {
        if(!count||!status||status->struct_size!=sizeof(*status)||status->version!=1||(capacity&&!outputs))return LPB_RESULT_INVALID_ARGUMENT;
        std::scoped_lock lock(context->plan_mutex);const auto session=find_locked(context,plan);
        if(!session)return LPB_RESULT_AUTHORITY_VIOLATION;
        *status={sizeof(*status),1,session->state,session->failure,session->request.target_profile,0,session->generation};
        return LPB_RESULT_OK; // Query succeeded, status never claims target success.
    } catch(...) {return LPB_RESULT_INTERNAL_ERROR;}
}
lpb_result LPB_CALL lpb_target_writer_read_stage_v1(lpb_context* context,lpb_target_writer_plan* plan,
    uint64_t stage,uint64_t offset,uint8_t* output,uint32_t capacity,uint32_t* count) {
    if(count)*count=0;
    if(reentry(context))return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_writer_frame frame(context);
    try {
        if(!count||(capacity&&!output))return LPB_RESULT_INVALID_ARGUMENT;
        std::scoped_lock lock(context->plan_mutex);
        if(!find_locked(context,plan))return LPB_RESULT_AUTHORITY_VIOLATION;
        static_cast<void>(stage);static_cast<void>(offset);
        return LPB_RESULT_UNSUPPORTED; // No sealed target object exists in R1.
    } catch(...) {return LPB_RESULT_INTERNAL_ERROR;}
}
lpb_result LPB_CALL lpb_target_writer_release_v1(lpb_context* context,lpb_target_writer_plan* plan) {
    if(reentry(context))return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_writer_frame frame(context);
    try {
        std::shared_ptr<lpb_target_writer_session> session;
        {
            std::scoped_lock lock(context->plan_mutex);session=find_locked(context,plan);
            if(!session)return LPB_RESULT_AUTHORITY_VIOLATION;
            if(session->state==1||session->state==5) {session->release_requested=true;session->state=5;return LPB_RESULT_OK;}
            auto& plans=context->target_writer_plans;
            plans.erase(std::remove(plans.begin(),plans.end(),session),plans.end());
        }
        return LPB_RESULT_OK;
    } catch(...) {return LPB_RESULT_INTERNAL_ERROR;}
}
