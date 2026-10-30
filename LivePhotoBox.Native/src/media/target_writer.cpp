#include "media/target_writer.h"
#include "media/media_inspector.h"
#include "media/target_video_observation.h"
#include "media/target_primary_observation.h"
#include "media/heic_backend.h"
#include "media/compound_primary.h"
#include "media/jpeg_gainmap_mpf.h"
#include "media/jpeg_mpf_vendor.h"
#include "media/ordinary_metadata_container.h"
#include "containers/heif_internal.h"
#include "foundation/internal.h"
#include "foundation/sha256_core.h"
#include "platform/windows_filesystem.h"
#include "protocols/target_structure.h"
#include "containers/isobmff.h"
#include "containers/isobmff_video_profile.h"
#include "protocols/google_v2_writer.h"
#include "protocols/huawei_target.h"
#include "protocols/samsung_target.h"
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
static_assert(sizeof(lpb_target_auxiliary_v2)==1568);
static_assert(offsetof(lpb_target_auxiliary_v2,materialized)==456);
static_assert(sizeof(lpb_target_writer_request_v2)==1248);
static_assert(offsetof(lpb_target_writer_request_v2,auxiliaries)==1104);

struct lpb_target_writer_session {
    lpb_context* owner{};
    uint64_t token{},generation{};
    lpb_target_writer_request_v1 request{};
    std::array<lpb_target_writer_artifact_v1,5> artifacts{};
    std::array<windows_readonly_source,5> inputs;
    std::array<lpb_file_identity,5> observed{};
    std::array<std::array<uint8_t,32>,5> hashes{};
    uint32_t input_count{2};
    bool extended{},primary_timestamp_present{};
    int64_t primary_timestamp_us{};
    std::string primary_identity;
    std::vector<lpb_target_auxiliary_v2> auxiliaries;
    bool ordinary_present{};
    lpb_target_ordinary_metadata_v1 ordinary{};
    uint32_t ordinary_input_index{UINT32_MAX};
    std::array<uint32_t,2> auxiliary_input_indices{UINT32_MAX,UINT32_MAX};
    lpb::media::target_primary_observation primary_facts;
    lpb::target::huawei_movie_plan huawei_movie;
    lpb::media::user_comment_mapping comment_mapping;
    std::vector<uint8_t> samsung_suffix;
    std::shared_ptr<lpb::media::compound_primary_proof> compound;
    std::array<uint8_t,32> fingerprint{};
    uint32_t state{},failure{};
    bool release_requested{};
    uint32_t active_reads{};
    std::mutex stage_io;
    std::unique_ptr<windows_owned_output> stage;
    lpb_target_writer_output_v1 output{};
    void* directory{};
    ~lpb_target_writer_session() noexcept { if(stage)stage->abort_and_confirm();if(directory)CloseHandle(directory); }
};
bool lpb_abort_target_writer_stages(lpb_context* context) noexcept {
    if(!context)return false;
    bool confirmed=true;
    try {
        // Finalization is exclusive: active_operations is zero. No callbacks.
        for(const auto& session:context->target_writer_plans)
            if(session->stage&&!session->stage->abort_and_confirm())confirmed=false;
        context->target_writer_plans.clear();
    }catch(...){return false;}
    return confirmed;
}
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
template<size_t N> bool fixed_text(const char (&value)[N]) noexcept {
    return std::find(value,value+N,'\0')!=value+N&&value[0]&&fixed_utf8(value,N);
}
bool zero_bytes(const void* value,size_t size) noexcept {
    const auto* bytes=static_cast<const uint8_t*>(value);
    return std::all_of(bytes,bytes+size,[](uint8_t v){return v==0;});
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
lpb_result observe_extended_primary(lpb_context* context,lpb_target_writer_session& session,
    const std::vector<uint8_t>& primary) {
    std::string error;
    auto& facts=session.primary_facts;
    if(!(session.compound?lpb::media::validate_compound_primary(primary,*session.compound,facts,error):
        lpb::media::observe_target_primary(primary,facts,error))) {
        set_error(context,error.c_str());return LPB_RESULT_INVALID_ARGUMENT;
    }
    if(static_cast<uint32_t>(facts.container)!=session.artifacts[0].container||
        lpb::media::target_primary_has_live_binding(facts)||facts.xmp.foreign_claim) {
        set_error(context,"Prepared primary container differs or retains source live binding.");return LPB_RESULT_UNSUPPORTED;
    }
    if(!facts.exif_payload.empty()) {
        const auto neutral=lpb::media::observe_target_neutral_exif(facts.exif_payload);
        if(neutral!=LPB_RESULT_OK)return neutral;
    }
    if(facts.container==LPB_IMAGE_CONTAINER_HEIC) {
        const auto valid=lpb::media::validate_heic_bytes(context,primary);
        if(valid!=LPB_RESULT_OK)return valid;
    }
    if(session.auxiliaries.size()>2)return LPB_RESULT_INVALID_ARGUMENT;
    if(session.request.target_profile>=LPB_TARGET_GOOGLE_V1&&session.request.target_profile<=LPB_TARGET_OPPO&&
        std::any_of(session.auxiliaries.begin(),session.auxiliaries.end(),[](const auto& a){return std::string_view(a.semantic)=="MpfAuxiliaryImage";})) {
        lpb::media::mpf_vendor_primary vendor;
        if(session.auxiliaries.size()!=1||session.auxiliaries[0].representation!=1||facts.container!=LPB_IMAGE_CONTAINER_JPEG||
            facts.xmp.items.size()>1||!lpb::media::observe_cross_target_mpf_primary(primary,vendor,error)) {
            set_error(context,("Cross-target MPF ownership: "+error).c_str());return LPB_RESULT_UNSUPPORTED;
        }
    }
    std::vector<uint32_t> selected;
    for(size_t i=0;i<session.auxiliaries.size();++i) {
        auto& requested=session.auxiliaries[i];
        const std::string_view semantic=requested.semantic;
        if(requested.representation==1||requested.representation==3) {
            const lpb::media::target_primary_auxiliary* match=nullptr;
            for(size_t j=0;j<facts.auxiliaries.size();++j) {
                const auto& actual=facts.auxiliaries[j];
                const bool resolve=requested.selector_kind==0&&requested.selector_id==0&&actual.semantic==semantic&&
                    actual.relationship==requested.relationship&&actual.range.offset==requested.embedded_offset&&actual.range.length==requested.byte_length;
                if(resolve||(actual.selector_kind==requested.selector_kind&&actual.selector_id==requested.selector_id)) {
                    if(match||std::find(selected.begin(),selected.end(),static_cast<uint32_t>(j))!=selected.end())return LPB_RESULT_INVALID_ARGUMENT;
                    match=&actual;selected.push_back(static_cast<uint32_t>(j));
                }
            }
            if(!match||match->semantic!=semantic||match->relationship!=requested.relationship||
                match->range.offset!=requested.embedded_offset||match->range.length!=requested.byte_length||
                match->range.offset>primary.size()||match->range.length>primary.size()-match->range.offset) {
                set_error(context,"Auxiliary request does not match the actual owner/selector/semantic graph.");return LPB_RESULT_INVALID_ARGUMENT;
            }
            requested.selector_kind=match->selector_kind;requested.selector_id=match->selector_id;
            uint8_t hash[32]{};lpb::crypto::sha256_ctx digest;
            digest.update(primary.data()+static_cast<size_t>(match->range.offset),static_cast<size_t>(match->range.length));digest.finalize(hash);
            if(std::memcmp(hash,requested.sha256,32))return LPB_RESULT_SOURCE_CHANGED;
        }else {
            for(const auto& actual:facts.auxiliaries)if(actual.semantic==semantic) {
                set_error(context,"Detached auxiliary would duplicate an embedded semantic asset.");return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
        const uint32_t index=session.auxiliary_input_indices[i];
        if(index!=UINT32_MAX) {
            if(session.inputs[index].size()!=requested.byte_length||std::memcmp(session.hashes[index].data(),requested.sha256,32))return LPB_RESULT_SOURCE_CHANGED;
            // GainMap is one neutral image. Original is an immutable scoped
            // image asset whose historical metadata is not primary authority.
            std::vector<uint8_t> bytes;
            if(!session.inputs[index].read_all_bounded(4,32ULL*1024*1024,bytes))return LPB_RESULT_UNSUPPORTED;
            if(semantic=="Original") {
                if(!lpb::media::observe_original_jpeg_asset(bytes,error)) {
                    set_error(context,error.c_str());return LPB_RESULT_INVALID_ARGUMENT;
                }
                continue;
            }
            if(semantic=="GainMap") {
                if(!lpb::media::observe_gainmap_jpeg_asset(bytes,error)) {
                    set_error(context,error.c_str());return LPB_RESULT_INVALID_ARGUMENT;
                }
                continue;
            }
            lpb::media::target_primary_observation auxiliary;
            if(!lpb::media::observe_target_primary(bytes,auxiliary,error)) {
                set_error(context,("Materialized auxiliary observation: "+error).c_str());return LPB_RESULT_INVALID_ARGUMENT;
            }
            if(lpb::media::target_primary_has_live_binding(auxiliary)||auxiliary.xmp.foreign_claim||
                !auxiliary.auxiliaries.empty()) {
                set_error(context,"Materialized auxiliary retains a live binding or an unrepresented nested auxiliary graph.");return LPB_RESULT_INVALID_ARGUMENT;
            }
            if(!auxiliary.exif_payload.empty()) {
                const auto neutral=lpb::media::observe_target_neutral_exif(auxiliary.exif_payload);if(neutral!=LPB_RESULT_OK)return neutral;
            }
        }
    }
    const bool strict = (session.request.reserved0 == 0);
    if(strict && selected.size()!=facts.auxiliaries.size()) {
        set_error(context,"Prepared auxiliary graph has unrepresented assets; Strict cannot discard them.");return LPB_RESULT_UNSUPPORTED;
    }
    if(strict && facts.xmp.hdr_claim&&std::none_of(session.auxiliaries.begin(),session.auxiliaries.end(),
        [](const auto& asset){return std::string_view(asset.semantic)=="GainMap";})) {
        set_error(context,"HDR metadata has no represented GainMap semantic asset.");return LPB_RESULT_UNSUPPORTED;
    }
    return LPB_RESULT_OK;
}
lpb_result observe_inputs(lpb_context* context,lpb_target_writer_session& session) {
    std::vector<uint8_t> primary;
    if(!session.inputs[0].read_all_bounded(4,32ULL*1024*1024,primary)) {
        set_error(context,"[Unsupported] Writer primary rewrite snapshot exceeds32MiB; video remains streaming.");
        return LPB_RESULT_UNSUPPORTED;
    }
    auto result=session.extended?observe_extended_primary(context,session,primary):
        lpb::media::observe_target_writer_primary(context,primary);
    if(result!=LPB_RESULT_OK)return result;
    if(session.ordinary_present) {
        std::vector<uint8_t> raw;lpb::media::ordinary_metadata::observed_capsule existing;
        if(session.ordinary_input_index>=session.input_count||
            !session.inputs[session.ordinary_input_index].read_all_bounded(11,32ULL*1024*1024,raw)||
            !lpb::media::ordinary_metadata::complete_record_block(raw)) {
            set_error(context,"[Malformed] Ordinary metadata artifact is not one complete self-relative record block.");return LPB_RESULT_INVALID_ARGUMENT;
        }
        const auto presence=session.primary_facts.container==LPB_IMAGE_CONTAINER_HEIC
            ?lpb::media::ordinary_metadata::observe_heif_capsule(primary,existing)
            :lpb::media::ordinary_metadata::observe_jpeg_capsule(primary,existing);
        if(presence!=lpb::media::ordinary_metadata::observation::absent) {
            set_error(context,"[Unsupported] Ordinary metadata is already embedded or has a malformed namespace owner.");return LPB_RESULT_UNSUPPORTED;
        }
    }
    if(!session.extended&&session.artifacts[0].container!=LPB_IMAGE_CONTAINER_JPEG) {
        set_error(context,"[Malformed] Writer observed JPEG differs from requested image container.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const uint32_t format=session.request.media_format;
    const bool huawei=session.request.target_profile==LPB_TARGET_HUAWEI;
    const bool samsung=session.request.target_profile==LPB_TARGET_SAMSUNG;
    if(huawei&&(!session.extended||(format==4&&session.artifacts[1].video_codec!=3))) {
        set_error(context,"[Unsupported] Explicit Huawei HEVC profile requires prepared HEVC/AAC.");return LPB_RESULT_UNSUPPORTED;
    }
    if(format>1&&(!session.extended||!((format==3&&session.request.target_profile==2)||(format==2&&samsung)||((format==2||format==4)&&huawei))||
        session.primary_facts.container!=LPB_IMAGE_CONTAINER_HEIC))return LPB_RESULT_UNSUPPORTED;
    const bool mp4=session.artifacts[1].container==LPB_VIDEO_CONTAINER_MP4;
    if(session.artifacts[1].container!=static_cast<uint32_t>((format==0||format==2||format==4)?LPB_VIDEO_CONTAINER_MP4:LPB_VIDEO_CONTAINER_MOV)) {
        set_error(context,"[Malformed] Requested target format and prepared video container differ.");return LPB_RESULT_INVALID_ARGUMENT;
    }
    lpb::media::target_video_observation video;
    lpb::media::target_video_diagnostic diagnostic;
    if(!lpb::media::observe_target_video(session.inputs[1].reader(),mp4,video,diagnostic)) {
        set_error(context,diagnostic.reason.c_str());
        return diagnostic.failure==lpb::media::target_video_failure::unsupported||
            diagnostic.failure==lpb::media::target_video_failure::source_binding?LPB_RESULT_UNSUPPORTED:
            diagnostic.failure==lpb::media::target_video_failure::io?LPB_RESULT_INTERNAL_ERROR:LPB_RESULT_INVALID_ARGUMENT;
    }
    if(video.media.quicktime!=(format==1||format==3)||video.media.video_codec!=session.artifacts[1].video_codec) {
        set_error(context,"[Malformed] Actual observed video container/codec differs from prepared expectation.");return LPB_RESULT_INVALID_ARGUMENT;
    }
    if(video.profile.classification!=lpb::media::video_profile_class::ordinary_sdr&&
        (!session.extended||video.profile.classification!=lpb::media::video_profile_class::preservation_critical)) {
        set_error(context,"[Unsupported] HDR video profile integration pending.");return LPB_RESULT_UNSUPPORTED;
    }
    const auto& timeline=video.media.timeline;
    if(!timeline.visible_sample_count)return LPB_RESULT_INVALID_ARGUMENT;
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
    if(session.extended&&session.primary_timestamp_present) {
        if(session.primary_timestamp_us<0||session.primary_timestamp_us>INT64_MAX/10)return LPB_RESULT_INVALID_ARGUMENT;
        const auto stamp=session.primary_timestamp_us*10;
        if(std::none_of(timeline.presentation_order.begin(),timeline.presentation_order.end(),[&](const auto& sample){
            return sample.visible&&sample.normalized_time_100ns>=0&&sample.duration_100ns>0&&
                stamp>=sample.normalized_time_100ns&&stamp-sample.normalized_time_100ns<sample.duration_100ns;
        }))return LPB_RESULT_INVALID_ARGUMENT;
    }
    if(session.request.target_profile==LPB_TARGET_OPPO&&(!session.extended||!session.primary_timestamp_present)) {
        set_error(context,"OPlus original capture timestamp is unknown; cannot invent one from current cover.");return LPB_RESULT_UNSUPPORTED;
    }
    if(huawei) {
        if(video.media.sample_tables.size()!=2) {
            set_error(context,"[Unsupported] Huawei requires one video and one AAC audio track.");return LPB_RESULT_UNSUPPORTED;
        }
        if(!session.request.cover_timestamp_present||session.primary_timestamp_present) {
            set_error(context,"[Unsupported] Huawei requires a known current cover without source capture history.");return LPB_RESULT_UNSUPPORTED;
        }
        if(std::any_of(session.auxiliaries.begin(),session.auxiliaries.end(),[](const auto& a){return a.representation!=1&&a.representation!=3;})) {
            set_error(context,"[Unsupported] Detached Huawei image auxiliaries require explicit primary representation.");return LPB_RESULT_UNSUPPORTED;
        }
        std::string error;
        if(!lpb::target::prepare_huawei_movie(session.inputs[1].reader(),session.request.cover_timestamp_us,session.huawei_movie,error)) {
            set_error(context,error.c_str());return LPB_RESULT_INVALID_ARGUMENT;
        }
    }
    if(samsung&&(!session.extended||session.primary_timestamp_present||std::any_of(session.auxiliaries.begin(),session.auxiliaries.end(),
        [](const auto& a){return a.representation!=1&&a.representation!=3;}))) {
        set_error(context,"[Unsupported] Samsung requires an explicitly bound embedded primary auxiliary graph.");return LPB_RESULT_UNSUPPORTED;
    }
    return LPB_RESULT_OK;
}
void fingerprint(lpb_target_writer_session& session) noexcept {
    lpb::crypto::sha256_ctx hash;
    const auto integer=[&](uint64_t value){uint8_t bytes[8]{};for(unsigned i=0;i<8;++i)bytes[i]=static_cast<uint8_t>(value>>(i*8));hash.update(bytes,8);};
    integer(session.request.target_profile);integer(session.request.target_version);integer(session.request.media_format);
    integer(session.request.cover_timestamp_present);integer(static_cast<uint64_t>(session.request.cover_timestamp_us));
    integer(session.extended);integer(session.primary_timestamp_present);integer(static_cast<uint64_t>(session.primary_timestamp_us));
    hash.update(reinterpret_cast<const uint8_t*>(session.primary_identity.data()),session.primary_identity.size());
    if(session.compound) {
        integer(session.compound->token);integer(session.compound->known_length);integer(session.compound->opaque_length);
        hash.update(session.compound->opaque_sha256.data(),32);
    }
    for(const auto& auxiliary:session.auxiliaries)hash.update(reinterpret_cast<const uint8_t*>(&auxiliary),sizeof(auxiliary));
    if(session.ordinary_present) {
        integer(0x4c50424d00000001ULL);integer(session.ordinary.kind);integer(session.ordinary.owner_index);
        hash.update(reinterpret_cast<const uint8_t*>(session.ordinary.stable_identity),sizeof(session.ordinary.stable_identity));
        hash.update(reinterpret_cast<const uint8_t*>(session.ordinary.owner_identity),sizeof(session.ordinary.owner_identity));
        hash.update(reinterpret_cast<const uint8_t*>(session.ordinary.semantic),sizeof(session.ordinary.semantic));
        hash.update(reinterpret_cast<const uint8_t*>(session.ordinary.relationship),sizeof(session.ordinary.relationship));
    }
    for(size_t i=0;i<session.input_count;++i) {
        integer(session.artifacts[i].role);integer(session.artifacts[i].container);integer(session.artifacts[i].video_codec);
        integer(session.observed[i].volume_serial);integer(session.observed[i].file_index);
        integer(session.observed[i].file_size);integer(session.observed[i].link_count);
        hash.update(session.hashes[i].data(),32);
    }
    hash.finalize(session.fingerprint.data());
}
lpb_result revalidate(lpb_context* context,lpb_target_writer_session& session) {
    if(session.compound&&session.compound->revoked)return LPB_RESULT_AUTHORITY_VIOLATION;
    for(size_t i=0;i<session.input_count;++i) {
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
bool expand_owned_mpf(std::vector<uint8_t>& primary,const std::vector<lpb::media::target_xmp_item>& auxiliaries,uint64_t padding) {
    using namespace lpb::media::jpeg_gainmap_mpf;
    jpeg_info before;
    if(!parse_jpeg(primary,before))return false;
    const bool need_gainmap=std::any_of(auxiliaries.begin(),auxiliaries.end(),[](const auto& a){return a.semantic=="GainMap";});
    if(before.mpf_status==status::absent&&!need_gainmap)return true;
    if(auxiliaries.empty())return true;
    const size_t count=auxiliaries.size()+1;
    if(count>max_mpf_image_count)return false;
    if(before.mpf_status==status::present&&before.mpf.image_count==count)return true;
    // Called only after observe_extended_primary bound the old graph and every
    // newly appended detached semantic member. Expand its single owned index,
    // retaining all existing entry flags/dependencies and unknown MPF fields.
    std::vector<uint8_t> payload;
    size_t marker_start=0,marker_end=0;
    if(before.mpf_status==status::present) {
        uint64_t eoi=0;lpb::memory_random_access memory{primary};
        if(!lpb::media::walk_jpeg_markers(memory.view(),eoi,[&](const lpb::media::jpeg_marker_range& marker){
            if(marker.marker==0xe2&&marker.length>=4&&std::memcmp(primary.data()+marker.payload,"MPF\0",4)==0) {
                if(!payload.empty())return false;
                marker_start=static_cast<size_t>(marker.start);marker_end=static_cast<size_t>(marker.payload+marker.length);
                payload.assign(primary.begin()+static_cast<ptrdiff_t>(marker.payload),primary.begin()+static_cast<ptrdiff_t>(marker_end));
            }return true;
        })||payload.empty()||before.mpf.image_count>count)return false;
    }else {
        // Big-endian baseline MP Index, one formal IFD and no unknown fields.
        payload={'M','P','F',0,'M','M',0,42,0,0,0,8,0,3,
            0xb0,0,0,7,0,0,0,4,'0','1','0','0',
            0xb0,1,0,4,0,0,0,1,0,0,0,1,
            0xb0,2,0,7,0,0,0,16,0,0,0,50,0,0,0,0,
            0,3,0,0,0,0,0,1,0,0,0,0,0,0,0,0};
        marker_start=marker_end=2;
    }
    const size_t old_payload_start=before.mpf_status==status::present?before.mpf.tiff_header_offset-4:0;
    const bool little=before.mpf_status==status::present&&before.mpf.little_endian;
    const size_t table=payload.size();payload.resize(table+count*16,0);
    for(size_t i=0;i<count;++i) {
        const uint32_t attributes=i==0?0x00030000U:0U;
        write_u32(payload,table+i*16,attributes,little);
        if(before.mpf_status==status::present&&i<before.mpf.image_count) {
            const size_t source=before.mpf.entries_array_offset+i*16;
            std::copy_n(primary.data()+source,16,payload.data()+table+i*16);
        }
    }
    const size_t image_count_field=before.mpf_status==status::present?before.mpf.image_count_value_field_offset-old_payload_start:34;
    const size_t byte_count_field=before.mpf_status==status::present?before.mpf.entries_byte_count_field_offset-old_payload_start:42;
    const size_t table_field=before.mpf_status==status::present?before.mpf.entries_value_field_offset-old_payload_start:46;
    write_u32(payload,image_count_field,static_cast<uint32_t>(count),little);
    write_u32(payload,byte_count_field,static_cast<uint32_t>(count*16),little);
    write_u32(payload,table_field,static_cast<uint32_t>(table-4),little);
    if(payload.size()>65533)return false;
    std::vector<uint8_t> segment{0xff,0xe2,static_cast<uint8_t>((payload.size()+2)>>8),static_cast<uint8_t>(payload.size()+2)};
    segment.insert(segment.end(),payload.begin(),payload.end());
    const uint64_t new_eoi=before.eoi_end-(marker_end-marker_start)+segment.size();
    const uint64_t tiff=marker_start+8;
    if(new_eoi>UINT32_MAX)return false;
    write_u32(segment,4+table+4,static_cast<uint32_t>(new_eoi),little);
    write_u32(segment,4+table+8,0,little);
    uint64_t offset=new_eoi+padding;
    for(size_t i=0;i<auxiliaries.size();++i) {
        const auto& asset=auxiliaries[i];
        if(asset.length>UINT32_MAX||offset<tiff||offset-tiff>UINT32_MAX)return false;
        write_u32(segment,4+table+(i+1)*16+4,static_cast<uint32_t>(asset.length),little);
        write_u32(segment,4+table+(i+1)*16+8,static_cast<uint32_t>(offset-tiff),little);
        offset+=asset.length;
    }
    if(offset!=primary.size()-(marker_end-marker_start)+segment.size())return false;
    primary.erase(primary.begin()+static_cast<ptrdiff_t>(marker_start),primary.begin()+static_cast<ptrdiff_t>(marker_end));
    primary.insert(primary.begin()+static_cast<ptrdiff_t>(marker_start),segment.begin(),segment.end());
    return true;
}
lpb_result serialize_extended_primary(lpb_context* context,lpb_target_writer_session& session,std::vector<uint8_t>& serialized) {
    std::vector<uint8_t> primary;
    if(!session.inputs[0].read_all_bounded(4,32ULL*1024*1024,primary))return LPB_RESULT_UNSUPPORTED;
    uint64_t encoding_boundary=session.primary_facts.encoding_boundary;
    const bool best_effort = (session.request.reserved0 == 1);
    if(best_effort && session.primary_facts.container==LPB_IMAGE_CONTAINER_JPEG) {
        lpb::media::jpeg_gainmap_mpf::jpeg_info info;
        if(lpb::media::jpeg_gainmap_mpf::parse_jpeg(primary, info) && info.mpf_status==lpb::media::jpeg_gainmap_mpf::status::present) {
            if(!lpb::media::jpeg_gainmap_mpf::singleton_primary(info) && (info.mpf.image_count > (session.auxiliaries.empty() ? 1U : 2U) || session.auxiliaries.empty())) {
                if(encoding_boundary>0 && encoding_boundary<primary.size()) {
                    primary.resize(static_cast<size_t>(encoding_boundary));
                }
                const auto tiff = info.mpf.tiff_header_offset;
                if(tiff >= 8 && primary.size() >= tiff) {
                    const size_t marker_start = tiff - 8;
                    const size_t marker_length = 2 + (static_cast<size_t>(primary[tiff - 6]) << 8 | primary[tiff - 5]);
                    if(marker_start + marker_length <= primary.size() && primary[marker_start] == 0xff && primary[marker_start + 1] == 0xe2) {
                        primary.erase(primary.begin() + static_cast<ptrdiff_t>(marker_start), primary.begin() + static_cast<ptrdiff_t>(marker_start + marker_length));
                        if(encoding_boundary >= marker_length) encoding_boundary -= marker_length;
                    }
                }
                session.primary_facts.encoding_boundary = primary.size();
                session.auxiliaries.clear();
            }
        }
    }
    if(session.ordinary_present) {
        std::vector<uint8_t> raw,decorated;
        if(!session.inputs[session.ordinary_input_index].read_all_bounded(11,32ULL*1024*1024,raw))return LPB_RESULT_UNSUPPORTED;
        const bool heif=session.primary_facts.container==LPB_IMAGE_CONTAINER_HEIC;
        const bool inserted=heif?lpb::media::ordinary_metadata::insert_heif_capsule(primary,raw,decorated)
            :lpb::media::ordinary_metadata::insert_jpeg_capsule(primary,raw,decorated);
        if(!inserted){set_error(context,"[Unsupported] Ordinary metadata cannot be represented without losing original container ownership.");return LPB_RESULT_UNSUPPORTED;}
        primary=std::move(decorated);
        if(!heif){lpb::media::jpeg_gainmap_mpf::jpeg_info jpeg;if(!lpb::media::jpeg_gainmap_mpf::parse_jpeg(primary,jpeg))return LPB_RESULT_INVALID_ARGUMENT;encoding_boundary=jpeg.eoi_end;}
    }
    lpb::target::google_xmp_request request;
    request.target=session.request.target_profile;request.format=session.request.media_format;
    request.video_length=session.inputs[1].size();request.cover_present=session.request.cover_timestamp_present!=0;
    request.cover_us=session.request.cover_timestamp_us;
    request.primary_cover_present=session.primary_timestamp_present;request.primary_cover_us=session.primary_timestamp_us;
    request.primary_mime=session.primary_facts.container==LPB_IMAGE_CONTAINER_HEIC?"image/heic":"image/jpeg";
    request.hdr_relationship_validated=std::any_of(session.auxiliaries.begin(),session.auxiliaries.end(),
        [](const auto& asset){return std::string_view(asset.semantic)=="GainMap";});
    request.best_effort = best_effort;
    if(session.primary_facts.container==LPB_IMAGE_CONTAINER_HEIC) {
        std::vector<uint32_t> resolved_ids;
        resolved_ids.reserve(session.auxiliaries.size());
        for(size_t i=0;i<session.auxiliaries.size();++i) {
            const auto& asset=session.auxiliaries[i];uint32_t resolved=asset.selector_id;
            if(asset.representation==2) {
                if(std::string_view(asset.semantic)!="Original") {
                    set_error(context,"[Unsupported] Detached HEIF image conversion has no proved coded representation.");return LPB_RESULT_UNSUPPORTED;
                }
                const auto index=session.auxiliary_input_indices[i];std::vector<uint8_t> bytes,decorated;std::string error;
                if(index==UINT32_MAX||!session.inputs[index].read_all_bounded(4,32ULL*1024*1024,bytes))return LPB_RESULT_UNSUPPORTED;
                if(!lpb::media::observe_original_jpeg_asset(bytes,error)) {set_error(context,error.c_str());return LPB_RESULT_INVALID_ARGUMENT;}
                if(bytes.size()!=asset.byte_length)return LPB_RESULT_SOURCE_CHANGED;
                uint8_t hash[32]{};lpb::crypto::sha256_ctx digest;digest.update(bytes.data(),bytes.size());digest.finalize(hash);
                if(std::memcmp(hash,asset.sha256,32))return LPB_RESULT_SOURCE_CHANGED;
                if(!lpb::containers::append_original_file_resource(primary,bytes,decorated,resolved,error)) {
                    set_error(context,error.c_str());return LPB_RESULT_INVALID_ARGUMENT;
                }
                primary=std::move(decorated);
            }
            resolved_ids.push_back(resolved);
        }
        if(request.video_length>UINT64_MAX-16)return LPB_RESULT_INVALID_ARGUMENT;
        const uint64_t header=request.video_length<=UINT32_MAX-8ULL?8ULL:16ULL;
        request.primary_padding=header;
        std::string xml,error;
        std::string target_input=session.primary_facts.standard_xml;
        const auto& layout=session.primary_facts.xmp;
        if(layout.directory_present&&layout.items.size()==2&&layout.items[1].semantic=="GainMap"&&layout.items[1].mime=="image/heic") {
            const auto maps=std::count_if(session.primary_facts.auxiliaries.begin(),session.primary_facts.auxiliaries.end(),
                [](const auto& asset){return asset.semantic=="GainMap"&&asset.mime=="image/heic"&&asset.selector_kind==2;});
            lpb::media::xmp_observation_failure failure{};std::string reduced;
            if(maps!=1||!request.hdr_relationship_validated||!lpb::media::reduce_embedded_heif_gainmap_layout(target_input,reduced,failure)) {
                set_error(context,"[TargetXmpMapping] Existing embedded GainMap layout has unrepresented content or no unique bound image.");return LPB_RESULT_UNSUPPORTED;
            }
            target_input=std::move(reduced);
        }
        auto result=lpb::target::build_google_xmp(target_input,request,xml);
        if(result!=LPB_RESULT_OK) {
            set_error(context,"[TargetXmpMapping] Prepared HEIF XMP inventory cannot be represented by the requested target directory without changing an existing image asset description.");
            return result;
        }
        if(!lpb::containers::rewrite_primary_xmp(primary,xml,serialized,error)) {
            set_error(context,error.c_str());return LPB_RESULT_INVALID_ARGUMENT;
        }
        // Re-observe the rewritten prefix before framing. Existing encoded
        // image/auxiliary identities remain in their original item graph.
        lpb::media::target_primary_observation observed;
        if(!lpb::media::observe_target_primary(serialized,observed,error))return LPB_RESULT_INVALID_ARGUMENT;
        if(observed.auxiliaries.size()!=session.auxiliaries.size())return LPB_RESULT_INVALID_ARGUMENT;
        for(size_t i=0;i<session.auxiliaries.size();++i) {
            const auto& asset=session.auxiliaries[i];
            const auto found=std::find_if(observed.auxiliaries.begin(),observed.auxiliaries.end(),[&](const auto& actual){
                return actual.selector_kind==(asset.representation==2?2:asset.selector_kind)&&
                    actual.selector_id==resolved_ids[i]&&actual.semantic==asset.semantic&&actual.relationship==asset.relationship;
            });
            if(found==observed.auxiliaries.end()||found->range.length!=asset.byte_length||
                found->range.offset>serialized.size()||found->range.length>serialized.size()-found->range.offset)return LPB_RESULT_INVALID_ARGUMENT;
            uint8_t hash[32]{};lpb::crypto::sha256_ctx digest;
            digest.update(serialized.data()+static_cast<size_t>(found->range.offset),static_cast<size_t>(found->range.length));digest.finalize(hash);
            if(std::memcmp(hash,asset.sha256,32))return LPB_RESULT_SOURCE_CHANGED;
        }
        const uint64_t total=request.video_length+header;
        if(header==8)for(int i=3;i>=0;--i)serialized.push_back(static_cast<uint8_t>(total>>(i*8)));
        else serialized.insert(serialized.end(),{0,0,0,1});
        serialized.insert(serialized.end(),{'m','p','v','d'});
        if(header==16)for(int i=7;i>=0;--i)serialized.push_back(static_cast<uint8_t>(total>>(i*8)));
        return LPB_RESULT_OK;
    }
    if(session.primary_facts.xmp.directory_present)request.primary_padding=session.primary_facts.xmp.items[0].padding;
    const bool ordinary_mpf=std::any_of(session.primary_facts.auxiliaries.begin(),session.primary_facts.auxiliaries.end(),
        [](const auto& a){return a.semantic=="MpfAuxiliaryImage";});
    if(ordinary_mpf||std::any_of(session.primary_facts.auxiliaries.begin(),session.primary_facts.auxiliaries.end(),
        [](const auto& a){return a.selector_kind==3;})) {
        // MiCamera's JPEG/PNG assets retain their native XMPMeta graph and
        // placement. They are primary padding, never invented Google items.
        request.primary_padding=primary.size()-encoding_boundary;
    }
    // Existing embedded assets stay exactly once in their current order. A
    // detached semantic member is appended only after its explicit identity
    // and no-embedded-duplicate proof in observe_extended_primary.
    std::array<std::string_view,2> order{"GainMap","Original"};
    for(const auto semantic:order) {
        const auto found=std::find_if(session.auxiliaries.begin(),session.auxiliaries.end(),[&](const auto& asset){return std::string_view(asset.semantic)==semantic;});
        if(found==session.auxiliaries.end())continue;
        const size_t index=static_cast<size_t>(found-session.auxiliaries.begin());
        if(found->representation==2) {
            // Appending before an existing later item would reorder ownership;
            // that layout needs an explicit reflow, never a silent permutation.
            if(semantic=="GainMap"&&std::any_of(session.primary_facts.auxiliaries.begin(),session.primary_facts.auxiliaries.end(),
                [](const auto& actual){return actual.semantic=="Original";}))return LPB_RESULT_UNSUPPORTED;
            std::vector<uint8_t> bytes;
            if(!session.inputs[session.auxiliary_input_indices[index]].read_all_bounded(4,32ULL*1024*1024,bytes))return LPB_RESULT_UNSUPPORTED;
            primary.insert(primary.end(),bytes.begin(),bytes.end());
        }
        request.auxiliary_items.push_back({std::string(semantic),"image/jpeg",found->byte_length,0,true,false});
    }
    if(!ordinary_mpf&&!expand_owned_mpf(primary,request.auxiliary_items,request.primary_padding)) {
        set_error(context, "[Stage] expand_owned_mpf failed.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    return lpb::target::build_google_primary(context,primary,request,serialized,&session.comment_mapping);
}
lpb_result finish(lpb_context* context,const std::shared_ptr<lpb_target_writer_session>& session,lpb_result result) {
    std::scoped_lock lock(context->plan_mutex);
    if(session->release_requested&&result==LPB_RESULT_OK)result=LPB_RESULT_CANCELLED;
    if(result!=LPB_RESULT_OK && session->stage) {
        if(!session->stage->abort_and_confirm()) {
            result=LPB_RESULT_INTERNAL_ERROR;session->failure=6;
            set_error(context,"[CleanupFailed] Writer exact-owned stage cleanup failed.");
        }
        session->stage.reset();session->output={};
    }
    session->state=result==LPB_RESULT_OK?2U:result==LPB_RESULT_CANCELLED?4U:3U;
    if(session->failure!=6)session->failure=result==LPB_RESULT_OK?0U:result==LPB_RESULT_UNSUPPORTED?failure_unsupported:
        result==LPB_RESULT_SOURCE_CHANGED?failure_identity:result==LPB_RESULT_INVALID_ARGUMENT?failure_malformed:failure_io;
    if(session->release_requested) {
        auto& plans=context->target_writer_plans;
        plans.erase(std::remove(plans.begin(),plans.end(),session),plans.end());
    }
    return result;
}

bool reentry(lpb_context* context) noexcept {
    if(!context||lpb_writer_callback_active(context)) {
        set_error(context,"[AuthorityViolation] Writer callback reentry is not authorized.");return true;
    }
    return false;
}
}

static lpb_result prepare_target_writer(lpb_context* context,const lpb_target_writer_request_v1* request,
    lpb_target_writer_plan** output,const lpb_target_writer_request_v2* extended,uint64_t compound_token=0,
    const lpb_target_writer_request_v4* ordinary=nullptr) {
    if(output)*output=nullptr;
    if(reentry(context))return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
    lpb_writer_frame frame(context);
    try {
        if(!request||!output||request->struct_size!=sizeof(*request)||request->version!=1||
            request->artifact_count!=2||!request->artifacts||request->reserved0>1||request->reserved1||request->reserved2||
            request->cover_timestamp_present>1||(!request->cover_timestamp_present&&request->cover_timestamp_us!=0)||
            !fixed_utf8(request->staging_directory_utf8,sizeof(request->staging_directory_utf8)))return LPB_RESULT_INVALID_ARGUMENT;
        const bool samsung=request->target_profile==LPB_TARGET_SAMSUNG;
        if((samsung?(request->target_version!=2&&request->target_version!=3):(request->target_version!=1&&!(request->target_profile==LPB_TARGET_HUAWEI&&request->target_version==2)))||
            !lpb::target::profile_accepts(request->target_profile,request->media_format))return LPB_RESULT_UNSUPPORTED;
        auto session=std::make_shared<lpb_target_writer_session>();
        session->owner=context;session->request=*request;session->request.artifacts=nullptr;
        if(compound_token) {
            if(!extended||request->target_profile!=LPB_TARGET_HUAWEI||request->media_format!=0)return LPB_RESULT_UNSUPPORTED;
            session->compound=lpb::media::find_compound_primary(compound_token);
            if(!session->compound||session->compound->revoked)return LPB_RESULT_AUTHORITY_VIOLATION;
        }
        if(extended) {
            if(extended->struct_size!=sizeof(*extended)||extended->version!=2||extended->reserved||
                extended->primary_timestamp_present>1||(!extended->primary_timestamp_present&&extended->primary_timestamp_us)||
                extended->auxiliary_count>2||(extended->auxiliary_count&&!extended->auxiliaries)||
                !fixed_text(extended->primary_identity))return LPB_RESULT_INVALID_ARGUMENT;
            session->extended=true;session->primary_timestamp_present=extended->primary_timestamp_present!=0;
            session->primary_timestamp_us=extended->primary_timestamp_us;session->primary_identity=extended->primary_identity;
            for(uint32_t i=0;i<extended->auxiliary_count;++i) {
                const auto& auxiliary=extended->auxiliaries[i];
                if(auxiliary.struct_size!=sizeof(auxiliary)||auxiliary.version!=2||auxiliary.owner_index||
                    auxiliary.representation<1||auxiliary.representation>3||!auxiliary.byte_length||
                    !fixed_text(auxiliary.stable_identity)||!fixed_text(auxiliary.owner_identity)||
                    !fixed_text(auxiliary.semantic)||!fixed_text(auxiliary.relationship)||
                    std::string_view(auxiliary.owner_identity)!=session->primary_identity||
                    (std::string_view(auxiliary.semantic)!="GainMap"&&std::string_view(auxiliary.semantic)!="Original"&&
                     std::string_view(auxiliary.semantic)!="MiCameraSubimage"&&std::string_view(auxiliary.semantic)!="MiCameraLensWatermark"&&
                     std::string_view(auxiliary.semantic)!="MpfAuxiliaryImage"))return LPB_RESULT_INVALID_ARGUMENT;
                if((std::string_view(auxiliary.semantic)=="MiCameraSubimage"||std::string_view(auxiliary.semantic)=="MiCameraLensWatermark"||std::string_view(auxiliary.semantic)=="MpfAuxiliaryImage")&&
                    auxiliary.representation!=1)return LPB_RESULT_INVALID_ARGUMENT;
                const std::string_view semantic=auxiliary.semantic;
                if(semantic=="MpfAuxiliaryImage"&&request->target_profile!=LPB_TARGET_HUAWEI&&
                    !((request->target_profile>=LPB_TARGET_GOOGLE_V1&&request->target_profile<=LPB_TARGET_OPPO)&&(request->media_format==0||request->media_format==1))) {
                    set_error(context,"[Unsupported] MPF auxiliary image passthrough is bounded to Huawei/Honor targets.");return LPB_RESULT_UNSUPPORTED;
                }
                const std::string_view relationship=semantic=="GainMap"?"gain-map":semantic=="Original"?"Original":
                    semantic=="MpfAuxiliaryImage"?"mpf-image":semantic=="MiCameraSubimage"?"micamera-xmpmeta-subimage":"micamera-xmpmeta-lenswatermark";
                // An embedded HEIF GainMap carries the observed auxC/auxl
                // relationship URI. It is bound below to the exact item graph,
                // selector, range and hash, independently of target selection.
                const auto* primary_artifact=std::find_if(request->artifacts,request->artifacts+request->artifact_count,
                    [](const auto& artifact){return artifact.role==0;});
                const bool observed_heif_gainmap=semantic=="GainMap"&&primary_artifact!=request->artifacts+request->artifact_count&&
                    primary_artifact->container==LPB_IMAGE_CONTAINER_HEIC&&
                    auxiliary.representation!=2&&auxiliary.selector_kind==2;
                if(std::string_view(auxiliary.relationship)!=relationship&&!observed_heif_gainmap) {
                    set_error(context,"Auxiliary semantic/relationship identity mismatch before mutation.");return LPB_RESULT_INVALID_ARGUMENT;
                }
                for(const auto& prior:session->auxiliaries)
                    if(std::string_view(prior.stable_identity)==auxiliary.stable_identity||
                       std::string_view(prior.semantic)==auxiliary.semantic)return LPB_RESULT_INVALID_ARGUMENT;
                if(auxiliary.representation==2) {
                    if(auxiliary.selector_kind||auxiliary.selector_id||auxiliary.embedded_offset)return LPB_RESULT_INVALID_ARGUMENT;
                }else if(auxiliary.selector_kind>4||(!auxiliary.selector_kind&&auxiliary.selector_id)||
                    (auxiliary.selector_kind>=2&&!auxiliary.selector_id))return LPB_RESULT_INVALID_ARGUMENT;
                if(auxiliary.representation==1) {
                    if(!zero_bytes(&auxiliary.materialized,sizeof(auxiliary.materialized)))return LPB_RESULT_INVALID_ARGUMENT;
                }else {
                    const auto& materialized=auxiliary.materialized;
                    if(materialized.struct_size!=sizeof(materialized)||materialized.version!=1||materialized.role||
                        materialized.container!=LPB_IMAGE_CONTAINER_JPEG||materialized.video_codec||materialized.reserved||
                        !fixed_utf8(materialized.path_utf8,sizeof(materialized.path_utf8)))return LPB_RESULT_INVALID_ARGUMENT;
                    const uint32_t index=session->input_count++;
                    session->artifacts[index]=materialized;session->auxiliary_input_indices[i]=index;
                }
                session->auxiliaries.push_back(auxiliary);
            }
        }
        if(ordinary) {
            static_assert(sizeof(lpb_target_ordinary_metadata_v1)==1520);
            static_assert(sizeof(lpb_target_writer_request_v4)==1304);
            if(!extended||compound_token||ordinary->metadata_count!=1||!ordinary->metadata||ordinary->reserved||ordinary->reserved_tail||
                request->target_profile<LPB_TARGET_GOOGLE_V1||request->target_profile>LPB_TARGET_OPPO)return LPB_RESULT_UNSUPPORTED;
            const auto& metadata=*ordinary->metadata;const auto& artifact=metadata.materialized;
            if(metadata.struct_size!=sizeof(metadata)||metadata.version!=1||metadata.kind!=1||metadata.owner_index||metadata.flags||metadata.reserved||
                !fixed_text(metadata.stable_identity)||!fixed_text(metadata.owner_identity)||!fixed_text(metadata.semantic)||!fixed_text(metadata.relationship)||
                std::string_view(metadata.owner_identity)!=session->primary_identity||std::string_view(metadata.semantic)!="OpaqueCameraRecords"||
                std::string_view(metadata.relationship)!="self-relative-record-directory-v1"||
                artifact.struct_size!=sizeof(artifact)||artifact.version!=1||artifact.role!=LPB_ARTIFACT_METADATA_CARRIER||
                artifact.container||artifact.video_codec||artifact.reserved||!fixed_utf8(artifact.path_utf8,sizeof(artifact.path_utf8))||
                !artifact.expected_identity.file_size||artifact.expected_identity.file_size>32ULL*1024*1024)return LPB_RESULT_INVALID_ARGUMENT;
            for(const auto& auxiliary:session->auxiliaries)if(std::string_view(auxiliary.stable_identity)==metadata.stable_identity)return LPB_RESULT_INVALID_ARGUMENT;
            session->ordinary_present=true;session->ordinary=metadata;session->ordinary_input_index=session->input_count++;
            session->artifacts[session->ordinary_input_index]=artifact;
        }
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
        for(size_t i=0;i<session->input_count;++i) {
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
        for(size_t i=0;i<session->input_count;++i)for(size_t j=0;j<i;++j)
            if(session->observed[i].volume_serial==session->observed[j].volume_serial&&session->observed[i].file_index==session->observed[j].file_index)
                return LPB_RESULT_AUTHORITY_VIOLATION;
        const auto observed=observe_inputs(context,*session);if(observed!=LPB_RESULT_OK)return observed;
        if(session->compound) {
            if(!identity_equal(session->compound->identity,session->observed[0])||session->compound->sha256!=session->hashes[0])return LPB_RESULT_SOURCE_CHANGED;
        }
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
lpb_result LPB_CALL lpb_target_writer_prepare_v1(lpb_context* context,const lpb_target_writer_request_v1* request,lpb_target_writer_plan** output) {
    return prepare_target_writer(context,request,output,nullptr);
}
lpb_result LPB_CALL lpb_target_writer_prepare_v2(lpb_context* context,const lpb_target_writer_request_v2* request,lpb_target_writer_plan** output) {
    return prepare_target_writer(context,request?&request->base:nullptr,output,request);
}
lpb_result LPB_CALL lpb_target_writer_prepare_v3(lpb_context* context,const lpb_target_writer_request_v3* request,lpb_target_writer_plan** output) {
    if(output)*output=nullptr;
    if(!request||request->struct_size!=sizeof(*request)||request->version!=3||request->reserved||!request->compound_primary_token)
        return LPB_RESULT_INVALID_ARGUMENT;
    return prepare_target_writer(context,&request->base.base,output,&request->base,request->compound_primary_token);
}
lpb_result LPB_CALL lpb_target_writer_prepare_v4(lpb_context* context,const lpb_target_writer_request_v4* request,lpb_target_writer_plan** output) {
    if(output)*output=nullptr;
    if(!request||request->struct_size!=sizeof(*request)||request->version!=4||request->reserved||request->reserved_tail||
        request->base.struct_size!=sizeof(request->base)||request->base.version!=3||request->base.reserved||request->base.compound_primary_token)
        return LPB_RESULT_INVALID_ARGUMENT;
    return prepare_target_writer(context,&request->base.base.base,output,&request->base.base,0,request);
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
            if((session->request.target_profile!=LPB_TARGET_GOOGLE_V2&&session->request.target_profile!=LPB_TARGET_GOOGLE_V1&&
                (!session->extended||(session->request.target_profile!=LPB_TARGET_OPPO&&session->request.target_profile!=LPB_TARGET_VIVO_MODERN&&session->request.target_profile!=LPB_TARGET_HUAWEI&&session->request.target_profile!=LPB_TARGET_SAMSUNG)))||
                (session->request.media_format>1&&(!session->extended||!((session->request.target_profile==2&&session->request.media_format==3)||
                    (session->request.target_profile==LPB_TARGET_SAMSUNG&&session->request.media_format==2)||
                    (session->request.target_profile==LPB_TARGET_HUAWEI&&(session->request.media_format==2||session->request.media_format==4)))))) {
                set_error(context,"[Unsupported] Target serializer is not implemented; Writer attempt consumed with no outputs.");
                result=LPB_RESULT_UNSUPPORTED;
            } else {
                std::vector<uint8_t> primary,serialized;
                const bool huawei=session->request.target_profile==LPB_TARGET_HUAWEI;
                const bool samsung=session->request.target_profile==LPB_TARGET_SAMSUNG;
                if(samsung) {
                    std::string error;
                    if(!session->inputs[0].read_all_bounded(4,32ULL*1024*1024,primary))result=LPB_RESULT_UNSUPPORTED;
                    else if(!lpb::target::prepare_samsung_target(context,primary,session->primary_facts,session->inputs[1].size(),session->request.target_version,
                        session->request.cover_timestamp_present!=0,session->request.cover_timestamp_us,serialized,session->samsung_suffix,error)) {
                        set_error(context,error.c_str());result=LPB_RESULT_INVALID_ARGUMENT;
                    }
                }else if(huawei) {
                    if(!session->inputs[0].read_all_bounded(4,32ULL*1024*1024,serialized))result=LPB_RESULT_INTERNAL_ERROR;
                }
                else if(session->extended)result=serialize_extended_primary(context,*session,serialized);
                else if(!session->inputs[0].read_all_bounded(4,32ULL*1024*1024,primary))result=LPB_RESULT_INTERNAL_ERROR;
                else result=lpb::target::build_google_v2_primary(context,primary,session->inputs[1].size(),
                    session->request.media_format,session->request.cover_timestamp_present!=0,session->request.cover_timestamp_us,serialized,session->request.target_profile);
                if(result==LPB_RESULT_OK&&session->primary_facts.singleton_mpf&&!session->compound) {
                    // Reconcile the redundant self-size on every newly written
                    // JPEG target. Source bytes stay pinned and unchanged; only
                    // this derived field in the staged primary is normalized.
                    lpb::media::jpeg_gainmap_mpf::jpeg_info observed;
                    const bool parsed = lpb::media::jpeg_gainmap_mpf::parse_jpeg(serialized, observed);
                    const bool singleton = parsed && lpb::media::jpeg_gainmap_mpf::singleton_primary(observed);
                    if(!parsed || !singleton || observed.eoi_end > UINT32_MAX) {
                        const std::string diag = "[Stage] Singleton MPF normalization failed: parsed=" + std::to_string(parsed) +
                            " singleton=" + std::to_string(singleton) +
                            " mpf_status=" + std::to_string(static_cast<int>(observed.mpf_status)) +
                            " count=" + (parsed ? std::to_string(observed.mpf.image_count) : "na") +
                            " attr=" + (parsed && observed.mpf.image_count > 0 ? std::to_string(observed.mpf.entries[0].attributes) : "na") +
                            " eoi=" + (parsed ? std::to_string(observed.eoi_end) : "na") +
                            " ser_sz=" + std::to_string(serialized.size());
                        set_error(context, diag.c_str());
                        result=LPB_RESULT_INVALID_ARGUMENT;
                    }
                    else lpb::media::jpeg_gainmap_mpf::write_u32(serialized,observed.mpf.entries[0].size_field_offset,
                        static_cast<uint32_t>(observed.eoi_end),observed.mpf.little_endian);
                }
                if(result==LPB_RESULT_OK) {
                    session->stage=std::make_unique<windows_owned_output>();
                    const auto destination=utf8_to_path(session->request.staging_directory_utf8)/L"target-stage.bin";
                    if(!session->stage->create(destination,L"lpb-target-",L".stage",true)||!session->stage->write_all(serialized))result=LPB_RESULT_INTERNAL_ERROR;
                    std::array<uint8_t,65536> buffer{};
                    const uint64_t movie_length=huawei?session->huawei_movie.output_length:session->inputs[1].size();
                    for(uint64_t at=0;result==LPB_RESULT_OK&&at<movie_length;) {
                        result=check_cancel(context);if(result!=LPB_RESULT_OK)break;
                        const size_t n=static_cast<size_t>(std::min<uint64_t>(buffer.size(),movie_length-at));
                        const bool read=huawei?session->huawei_movie.read(session->inputs[1].reader(),at,std::span<uint8_t>(buffer.data(),n)):
                            session->inputs[1].read_at(at,std::span<uint8_t>(buffer.data(),n));
                        if(!read||
                            !session->stage->write_all(std::span<const uint8_t>(buffer.data(),n)))result=LPB_RESULT_INTERNAL_ERROR;
                        at+=n;
                    }
                    if(samsung&&result==LPB_RESULT_OK&&!session->stage->write_all(session->samsung_suffix))result=LPB_RESULT_INTERNAL_ERROR;
                    if(huawei&&result==LPB_RESULT_OK) {
                        std::array<uint8_t,60> tail{};std::string error;
                        if(!lpb::target::make_huawei_footer(session->request.target_version,session->huawei_movie.cover_frame,session->huawei_movie.total_frames,movie_length,tail,error)) {
                            set_error(context,error.c_str());result=LPB_RESULT_INVALID_ARGUMENT;
                        }else if(!session->stage->write_all(tail))result=LPB_RESULT_INTERNAL_ERROR;
                    }
                    if(session->compound&&session->compound->revoked)result=LPB_RESULT_AUTHORITY_VIOLATION;
                    if(result==LPB_RESULT_OK)result=check_cancel(context);
                    auto& output=session->output;
                    output={sizeof(output),1,0,0,session->request.target_profile,session->request.target_version,session->generation};
                    if(result==LPB_RESULT_OK&&(!session->stage->ready_to_consume()||
                        !session->stage->capture_identity(output.identity)||!session->stage->size(output.byte_length)||
                        !session->stage->compute_sha256(output.sha256)||!lpb_generate_unique_plan_token(output.stage_id)))result=LPB_RESULT_INTERNAL_ERROR;
                    if(result==LPB_RESULT_OK)std::memcpy(output.request_fingerprint,session->fingerprint.data(),32);
                }
            }
        }
        return finish(context,session,result);
    } catch(...) {
        if(session)static_cast<void>(finish(context,session,LPB_RESULT_INTERNAL_ERROR));
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
        const uint32_t total=session->state==2&&!session->release_requested&&session->stage?1U:0U;
        *status={sizeof(*status),1,session->state,session->failure,session->request.target_profile,total,session->generation};
        *count=total;
        if(total&&capacity) {
            if(outputs[0].struct_size!=sizeof(outputs[0])||outputs[0].version!=1){*count=0;return LPB_RESULT_INVALID_ARGUMENT;}
            outputs[0]=session->output;
        }
        return LPB_RESULT_OK;
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
        std::shared_ptr<lpb_target_writer_session> session;
        {
            std::scoped_lock lock(context->plan_mutex);session=find_locked(context,plan);
            if(!session)return LPB_RESULT_AUTHORITY_VIOLATION;
            if(session->state!=2||session->release_requested||!session->stage)return LPB_RESULT_UNSUPPORTED;
            if(stage!=session->output.stage_id)return LPB_RESULT_AUTHORITY_VIOLATION;
            if(offset>session->output.byte_length||capacity>session->output.byte_length-offset)return LPB_RESULT_INVALID_ARGUMENT;
            ++session->active_reads;
        }
        lpb_result result=check_cancel(context);
        {
            std::scoped_lock io(session->stage_io);
            if(result==LPB_RESULT_OK&&session->compound&&session->compound->revoked)result=LPB_RESULT_AUTHORITY_VIOLATION;
            lpb_file_identity actual{};
            if(result==LPB_RESULT_OK&&(!session->stage->capture_identity(actual)||!identity_equal(actual,session->output.identity)))result=LPB_RESULT_SOURCE_CHANGED;
            if(result==LPB_RESULT_OK&&capacity&&!session->stage->reader().read_exact(offset,std::span<uint8_t>(output,capacity)))result=LPB_RESULT_INTERNAL_ERROR;
        }
        if(result==LPB_RESULT_OK)result=check_cancel(context);
        if(result==LPB_RESULT_OK&&session->compound&&session->compound->revoked)result=LPB_RESULT_AUTHORITY_VIOLATION;
        bool cleanup=false;
        {
            std::scoped_lock lock(context->plan_mutex);--session->active_reads;
            cleanup=session->active_reads==0&&session->release_requested;
            if(cleanup){auto& plans=context->target_writer_plans;plans.erase(std::remove(plans.begin(),plans.end(),session),plans.end());}
        }
        if(cleanup&&session->stage&&!session->stage->abort_and_confirm()) {
            set_error(context,"[CleanupFailed] Released Writer read stage cleanup failed.");result=LPB_RESULT_INTERNAL_ERROR;
        }
        if(result==LPB_RESULT_OK)*count=capacity;
        return result;
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
            if(session->state==1||session->state==5||session->active_reads) {session->release_requested=true;session->state=5;return LPB_RESULT_OK;}
            auto& plans=context->target_writer_plans;
            plans.erase(std::remove(plans.begin(),plans.end(),session),plans.end());
        }
        if(session->stage&&!session->stage->abort_and_confirm()) {
            set_error(context,"[CleanupFailed] Writer exact-owned stage cleanup failed during release.");return LPB_RESULT_INTERNAL_ERROR;
        }
        return LPB_RESULT_OK;
    } catch(...) {return LPB_RESULT_INTERNAL_ERROR;}
}
