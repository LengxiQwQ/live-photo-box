#include "media/neutral_timing.h"
#include "media/huawei_conformance.h"
#include "media/media_inspector.h"
#include "media/video_presentation_timing.h"
#include "foundation/internal.h"
#include "foundation/sha256.h"
#include "containers/isobmff.h"
#include "binary/binary_io.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstring>
#include <limits>
#include <set>
#include <string>
#include <string_view>
#include <vector>

namespace {
using timeline = isobmff_video_presentation_timeline;
static_assert(sizeof(lpb_neutral_timing_observation_v1) == 144);
static_assert(offsetof(lpb_neutral_timing_observation_v1, final_video_sha256) == 112);

struct fixed_file {
    HANDLE handle{INVALID_HANDLE_VALUE};
    std::vector<uint8_t> bytes;
    ~fixed_file() noexcept { if(handle!=INVALID_HANDLE_VALUE)CloseHandle(handle); }
    bool open(lpb_context* context,const char* path) {
        if(!path || !*path)return false;
        const auto p=utf8_to_path(path);
        handle=CreateFileW(p.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(handle==INVALID_HANDLE_VALUE)return false;
        LARGE_INTEGER size{};
        if(!GetFileSizeEx(handle,&size) || size.QuadPart<0 || static_cast<uint64_t>(size.QuadPart)>1024ULL*1024*1024)return false;
        bytes.resize(static_cast<size_t>(size.QuadPart));
        size_t offset=0;
        while(offset<bytes.size()) {
            if(lpb_context_check_cancelled(context)==LPB_RESULT_CANCELLED)return false;
            DWORD read=0; const DWORD request=static_cast<DWORD>(std::min<size_t>(bytes.size()-offset,1024*1024));
            if(!ReadFile(handle,bytes.data()+offset,request,&read,nullptr)||read==0)return false;
            offset+=read;
        }
        return true;
    }
};

bool box_type(std::span<const uint8_t> data,size_t p,const char* name) noexcept {
    return p<=data.size()&&data.size()-p>=8&&std::memcmp(data.data()+p+4,name,4)==0;
}
bool child(std::span<const uint8_t> data,size_t start,size_t end,const char* name,isobmff_box_header& out) noexcept {
    out={}; if(start>end||end>data.size())return false;
    for(size_t p=start;p<end;) {
        isobmff_box_header box{}; if(!try_read_box_header(data.data(),p,end,box))return false;
        if(box_type(data,p,name)) {if(out.size)return false;out=box;}
        p+=box.size;
    }
    return true;
}

// One strict keys/ilst reader for protocol-owned typed scalars. Select exactly
// one movie metadata hierarchy; an ambiguous graph never becomes a fallback.
bool mdta_meta_scalar(std::span<const uint8_t> data,const isobmff_box_header& meta,
    std::string_view key,bool& present,int64_t& microseconds) {
    present=false; microseconds=0;
    size_t start=meta.start+meta.header_size;
    // ISO FullBox meta and QuickTime plain meta have different child starts.
    if(meta.size-meta.header_size>=4&&read_be32u(data.data()+start)==0)start+=4;
    isobmff_box_header keys{},ilst{},handler{};
    if(!child(data,start,meta.start+meta.size,"keys",keys)||!child(data,start,meta.start+meta.size,"ilst",ilst)||
        !child(data,start,meta.start+meta.size,"hdlr",handler)||handler.size<handler.header_size+12)return false;
    const bool keyed_handler=std::memcmp(data.data()+handler.start+handler.header_size+8,"mdta",4)==0;
    const bool legacy_handler=std::memcmp(data.data()+handler.start+handler.header_size+8,"mdir",4)==0;
    // A legacy mdir/ilst hierarchy (e.g. encoder ©too) can coexist with movie
    // mdta, but cannot own any mdta keys. Do not mistake it for a shadow.
    if(!keys.size)return legacy_handler;
    if(!keyed_handler)return false;
    if(keys.size<keys.header_size+8||!ilst.size)return false;
    const size_t kb=keys.start+keys.header_size;
    if(read_be32u(data.data()+kb)!=0)return false;
    uint32_t count=read_be32u(data.data()+kb+4),target=0;
    size_t kp=kb+8;
    if(count>(keys.start+keys.size-kp)/8)return false;
    std::set<std::string> names;
    for(uint32_t index=1;index<=count;++index) {
        isobmff_box_header item{};
        if(!try_read_box_header(data.data(),kp,keys.start+keys.size,item))return false;
        if(box_type(data,kp,"mdta")) {
            const std::string name(reinterpret_cast<const char*>(data.data()+kp+item.header_size),item.size-item.header_size);
            if(!names.insert(name).second)return false;
            if(name==key)target=index;
        }
        kp+=item.size;
    }
    if(kp!=keys.start+keys.size)return false;
    std::set<uint32_t> indexes;
    for(size_t ip=ilst.start+ilst.header_size;ip<ilst.start+ilst.size;) {
        isobmff_box_header item{},value{};
        if(!try_read_box_header(data.data(),ip,ilst.start+ilst.size,item))return false;
        const uint32_t index=read_be32u(data.data()+ip+4);
        if(index==0||index>count||!indexes.insert(index).second)return false;
        if(index==target) {
            if(!child(data,ip+item.header_size,ip+item.size,"data",value)||value.size<value.header_size+8||
                value.start!=ip+item.header_size||value.size!=item.size-item.header_size)return false;
            const size_t vb=value.start+value.header_size;
            if(read_be32u(data.data()+vb+4)!=0)return false;
            const uint32_t type=read_be32u(data.data()+vb);
            const size_t length=value.size-value.header_size-8;
            const uint8_t* payload=data.data()+vb+8;
            double milliseconds=0;
            if(type==0x17&&length==4) {
                uint32_t bits=read_be32u(payload);float f=0;std::memcpy(&f,&bits,4);milliseconds=f;
            } else if(type==1&&length>0&&length<=64) {
                const char* first=reinterpret_cast<const char*>(payload);
                auto parsed=std::from_chars(first,first+length,milliseconds);
                if(parsed.ec!=std::errc{}||parsed.ptr!=first+length)return false;
            } else return false;
            if(!std::isfinite(milliseconds)||milliseconds<0||milliseconds>static_cast<double>(INT64_MAX/1000))return false;
            const double rounded=std::round(milliseconds*1000);
            if(!std::isfinite(rounded)||rounded>=static_cast<double>(INT64_MAX))return false;
            microseconds=static_cast<int64_t>(rounded);present=true;
        }
        ip+=item.size;
    }
    return target==0||present;
}

bool mdta_scalar(std::span<const uint8_t> data,std::string_view key,bool& present,int64_t& microseconds) {
    present=false;microseconds=0;
    isobmff_box_header moov{},meta{},udta{},nested{};
    if(!child(data,0,data.size(),"moov",moov)||!moov.size)return false;
    if(!child(data,moov.start+moov.header_size,moov.start+moov.size,"meta",meta)||
       !child(data,moov.start+moov.header_size,moov.start+moov.size,"udta",udta))return false;
    if(udta.size&&!child(data,udta.start+udta.header_size,udta.start+udta.size,"meta",nested))return false;
    for(const auto& candidate:{meta,nested})if(candidate.size) {
        bool found=false;int64_t value=0;
        if(!mdta_meta_scalar(data,candidate,key,found,value))return false;
        if(found){if(present)return false;present=true;microseconds=value;}
    }
    return true;
}

// A bounded JSON lexical reader, shared for the two numeric fields in the
// already structurally selected vivo UUID. Never searches opaque media bytes.
struct json_reader {
    std::string_view s;size_t p{};
    void ws(){while(p<s.size()&&(s[p]==' '||s[p]=='\r'||s[p]=='\n'||s[p]=='\t'))++p;}
    bool text(std::string& out) {
        ws();out.clear();if(p>=s.size()||s[p++]!='"')return false;
        while(p<s.size()) {
            unsigned char c=static_cast<unsigned char>(s[p++]);
            if(c=='"')return true;if(c<32)return false;
            if(c=='\\') {
                if(p>=s.size())return false;c=static_cast<unsigned char>(s[p++]);
                if(c=='u') {
                    if(s.size()-p<4)return false;unsigned v=0;
                    for(int i=0;i<4;++i){char h=s[p++];v*=16;if(h>='0'&&h<='9')v+=h-'0';else if(h>='a'&&h<='f')v+=h-'a'+10;else if(h>='A'&&h<='F')v+=h-'A'+10;else return false;}
                    if(v>127)return false;c=static_cast<unsigned char>(v);
                } else if(c=='b')c='\b';else if(c=='f')c='\f';else if(c=='n')c='\n';else if(c=='r')c='\r';else if(c=='t')c='\t';
                else if(c!='"'&&c!='\\'&&c!='/')return false;
            }
            out.push_back(static_cast<char>(c));
        }return false;
    }
    bool value(unsigned depth=0) {
        ws();if(depth>32||p>=s.size())return false;
        if(s[p]=='"'){std::string ignored;return text(ignored);}
        if(s[p]=='{'||s[p]=='[') {
            const bool object=s[p++]=='{';const char end=object?'}':']';ws();
            if(p<s.size()&&s[p]==end){++p;return true;}
            do {if(object){std::string name;if(!text(name))return false;ws();if(p>=s.size()||s[p++]!=':')return false;}
                if(!value(depth+1))return false;ws();if(p>=s.size())return false;if(s[p]==end){++p;return true;}
            }while(s[p++]==',');return false;
        }
        for(auto literal:{std::string_view("true"),std::string_view("false"),std::string_view("null")})
            if(s.substr(p,literal.size())==literal){p+=literal.size();return true;}
        size_t begin=p;if(s[p]=='-')++p;if(p>=s.size())return false;
        if(s[p]=='0')++p;else {if(s[p]<'1'||s[p]>'9')return false;while(p<s.size()&&s[p]>='0'&&s[p]<='9')++p;}
        if(p<s.size()&&s[p]=='.'){++p;size_t at=p;while(p<s.size()&&s[p]>='0'&&s[p]<='9')++p;if(at==p)return false;}
        if(p<s.size()&&(s[p]=='e'||s[p]=='E')){++p;if(p<s.size()&&(s[p]=='+'||s[p]=='-'))++p;size_t at=p;while(p<s.size()&&s[p]>='0'&&s[p]<='9')++p;if(at==p)return false;}
        return p>begin;
    }
};

bool vivo_positions(std::span<const uint8_t> data,bool& has_frame,int64_t& frame,bool& has_cover,int64_t& cover) {
    has_frame=false;has_cover=false;bool found=false;
    for(size_t p=0;p<data.size();) {
        isobmff_box_header box{};if(!try_read_box_header(data.data(),p,data.size(),box))return false;
        if(box_type(data,p,"uuid")&&box.size>=box.header_size+16&&std::memcmp(data.data()+p+box.header_size,"vivoMediaExtInfo",16)==0) {
            if(found)return false;found=true;
            std::string_view payload(reinterpret_cast<const char*>(data.data()+p+box.header_size+16),box.size-box.header_size-16);
            if(!payload.starts_with("vivo{"))return false;
            json_reader r{payload.substr(4)};r.p=1;std::set<std::string> names;std::string pairing;
            for(;;) {
                r.ws();if(r.p>=r.s.size())return false;if(r.s[r.p]=='}'){++r.p;break;}
                std::string name;if(!r.text(name)||!names.insert(name).second)return false;
                r.ws();if(r.p>=r.s.size()||r.s[r.p++]!=':')return false;r.ws();const size_t begin=r.p;
                if(!r.value())return false;const size_t end=r.p;
                if(name=="com.android.camera.imageTime"||name=="com.vivo.gallery.livePhoto.newCoverTime") {
                    int64_t number=0;auto parsed=std::from_chars(r.s.data()+begin,r.s.data()+end,number);
                    if(parsed.ec!=std::errc{}||parsed.ptr!=r.s.data()+end||number<0)return false;
                    if(name=="com.android.camera.imageTime"){has_frame=true;frame=number;}
                    else {if(number>INT64_MAX/1000)return false;has_cover=true;cover=number*1000;}
                }
                if(name=="com.android.camera.livephoto") {
                    json_reader id{r.s.substr(begin,end-begin)};
                    if(!id.text(pairing)||id.p!=id.s.size())return false;
                }
                r.ws();if(r.p>=r.s.size())return false;if(r.s[r.p]=='}'){++r.p;break;}
                if(r.s[r.p++]!=',')return false;
                r.ws();if(r.p<r.s.size()&&r.s[r.p]=='}')return false;
            }
            // The captured format optionally appends a length-indexed legacy
            // footer inside the UUID. Validate the complete footer and paired
            // ID; arbitrary bytes / another JSON object are not padding.
            const size_t json_length=r.p;
            r.ws();
            const auto tail=r.s.substr(r.p);
            if(!tail.empty()&&!std::all_of(tail.begin(),tail.end(),[](char c){return c=='\0';})) {
                constexpr uint8_t signature[]{0x1b,0x2a,0x39,0x48,0x57,0x66,0x75,0x84,0x93,0xa2,0xb3};
                const auto* t=reinterpret_cast<const uint8_t*>(tail.data());
                if(pairing.empty()||tail.size()!=4+11+4+pairing.size()+4+sizeof(signature)||
                    read_be32u(t)!=json_length||std::memcmp(t+4,"cameralbum!",11)!=0||
                    read_be32u(t+15)!=19+pairing.size()||
                    std::memcmp(t+19,pairing.data(),pairing.size())!=0||
                    read_be32u(t+19+pairing.size())!=0xffffffffu||
                    std::memcmp(t+23+pairing.size(),signature,sizeof(signature))!=0)return false;
            }
        }
        p+=box.size;
    }
    return found;
}

std::vector<const isobmff_video_presentation_sample*> visible(const timeline& t) {
    std::vector<const isobmff_video_presentation_sample*> result;
    for(const auto& sample:t.presentation_order)if(sample.visible)result.push_back(&sample);
    return result;
}
int64_t ceiling_microseconds(int64_t time_100ns) noexcept {
    return time_100ns/10+(time_100ns%10!=0?1:0);
}
bool time_index_100ns(const std::vector<const isobmff_video_presentation_sample*>& samples,int64_t t,size_t& index) {
    if(samples.empty()||t<0)return false;
    const auto* last=samples.back();
    if(last->normalized_time_100ns>INT64_MAX-last->duration_100ns||t>=last->normalized_time_100ns+last->duration_100ns||t<samples.front()->normalized_time_100ns)return false;
    index=0;for(size_t i=1;i<samples.size()&&samples[i]->normalized_time_100ns<=t;++i)index=i;
    return true;
}
bool time_index(const std::vector<const isobmff_video_presentation_sample*>& samples,int64_t us,size_t& index) {
    return us>=0&&us<=INT64_MAX/10&&time_index_100ns(samples,us*10,index);
}
bool schedules_equal(const std::vector<const isobmff_video_presentation_sample*>& a,
    const std::vector<const isobmff_video_presentation_sample*>& b) {
    if(a.size()!=b.size())return false;
    for(size_t i=0;i<a.size();++i)if(a[i]->normalized_time_100ns!=b[i]->normalized_time_100ns||a[i]->duration_100ns!=b[i]->duration_100ns)return false;
    return true;
}

bool footer_frame(const std::vector<uint8_t>& data,int64_t& frame) {
    if(data.size()<60)return false;
    const char* start=reinterpret_cast<const char*>(data.data()+data.size()-60);
    if(start[0]!='v'||start[1]<'0'||start[1]>'9'||start[2]!='_'||start[3]!='f')return false;
    const char* end=start+4;while(end<start+20&&*end>='0'&&*end<='9')++end;
    if(end==start+4)return false;
    if(!std::all_of(end,start+20,[](char c){return c==' '||c=='\0';}))return false;
    auto result=std::from_chars(start+4,end,frame);return result.ec==std::errc{}&&result.ptr==end&&frame>=0;
}
}

bool lpb::media::observe_huawei_source_timing(std::span<const uint8_t> movie,
    uint32_t legacy_frame,int64_t& cover_us,uint32_t& total_frames) noexcept {
    cover_us=-1;total_frames=0;
    try {
        bool present=false;int64_t modern=0;
        if(!mdta_scalar(movie,"com.openharmony.covertime",present,modern))return false;
        // LIVE_ identification has no mandatory cover. Unknown is explicit;
        // malformed owned timing above never becomes an absent timestamp.
        if(!present&&legacy_frame==UINT32_MAX)return true;
        timeline observed{};
        if(!inspect_isobmff_video_presentation_timeline(movie,observed))return false;
        const auto samples=visible(observed);
        if(samples.empty()||samples.size()>UINT32_MAX)return false;
        total_frames=static_cast<uint32_t>(samples.size());
        if(present) {
            if(modern<0||modern>INT64_MAX/10)return false;
            uint32_t index=0,count=0;if(!select_huawei_frame(observed,modern,index,count))return false;
            cover_us=modern;
        }else {
            if(!project_huawei_legacy_cover(observed,legacy_frame,cover_us,total_frames))return false;
        }
        return true;
    }catch(...){return false;}
}

LPB_API lpb_result LPB_CALL lpb_observe_neutral_timing_v1(lpb_context* context,
    const char* primary_path,const char* secondary_path,const char* final_video_path,
    lpb_neutral_timing_observation_v1* out) {
    if(!context||!out||out->struct_size!=sizeof(*out)||!primary_path||!final_video_path)return LPB_RESULT_INVALID_ARGUMENT;
    *out={};out->struct_size=sizeof(*out);
    if(lpb_context_check_cancelled(context)==LPB_RESULT_CANCELLED)return LPB_RESULT_CANCELLED;
    try {
        fixed_file primary,secondary,final;
        const bool has_secondary=secondary_path&&*secondary_path;
        if(!primary.open(context,primary_path)||(has_secondary&&!secondary.open(context,secondary_path))||!final.open(context,final_video_path)) {
            if(lpb_context_check_cancelled(context)==LPB_RESULT_CANCELLED)return LPB_RESULT_CANCELLED;
            set_error(context,"Neutral timing could not acquire immutable source/final file snapshots.");return LPB_RESULT_INVALID_ARGUMENT;
        }
        lpb_neutral_timing_observation_v1 result{};result.struct_size=sizeof(result);
        const auto publish=[&]() -> lpb_result {
            if(lpb_context_check_cancelled(context)==LPB_RESULT_CANCELLED)return LPB_RESULT_CANCELLED;
            *out=result;return LPB_RESULT_OK;
        };
        lpb::crypto::sha256_buffer(primary.bytes.data(),primary.bytes.size(),result.primary_sha256);
        lpb::crypto::sha256_buffer(final.bytes.data(),final.bytes.size(),result.final_video_sha256);
        if(has_secondary){result.presence_flags|=LPB_NEUTRAL_TIMING_SECONDARY_PRESENT;lpb::crypto::sha256_buffer(secondary.bytes.data(),secondary.bytes.size(),result.secondary_sha256);}
        timeline final_timeline{};
        const bool final_valid=inspect_isobmff_video_presentation_timeline(final.bytes,final_timeline);
        std::vector<const isobmff_video_presentation_sample*> final_samples;
        if(final_valid) {
            final_samples=visible(final_timeline);if(final_samples.empty())return LPB_RESULT_INVALID_ARGUMENT;
            const auto* last=final_samples.back();
            if(final_samples.size()>INT32_MAX||last->normalized_time_100ns>INT64_MAX-last->duration_100ns)return LPB_RESULT_INVALID_ARGUMENT;
            result.total_frames=static_cast<int32_t>(final_samples.size());
            result.final_duration_us=(last->normalized_time_100ns+last->duration_100ns)/10;
            result.presence_flags|=LPB_NEUTRAL_TIMING_TOTAL_FRAMES_PRESENT|LPB_NEUTRAL_TIMING_DURATION_PRESENT;
        }
        lpb_source_media_facts facts{};facts.struct_size=sizeof(facts);
        const auto inspected=lpb::media::inspect_source(context,primary_path,secondary_path,&facts);
        if(inspected!=LPB_RESULT_OK)return inspected;
        if(std::memcmp(facts.primary_sha256,result.primary_sha256,32)!=0||
            (has_secondary&&std::memcmp(facts.secondary_sha256,result.secondary_sha256,32)!=0)) {
            set_error(context,"Neutral timing source inspection identity differs from fixed source bytes.");return LPB_RESULT_INVALID_ARGUMENT;
        }
        if(facts.protocol==LPB_SOURCE_PROTOCOL_NON_LIVE) {
            result.source_evidence=LPB_NEUTRAL_TIMING_NOT_APPLICABLE;
            result.final_applicability=LPB_NEUTRAL_TIMING_NOT_APPLICABLE;return publish();
        }
        const auto& bytes=facts.motion_video.source_index==1?secondary.bytes:primary.bytes;
        const auto range=facts.motion_video.file_range;
        if(!facts.motion_video.is_present||range.offset>bytes.size()||range.length>bytes.size()-static_cast<size_t>(range.offset))return LPB_RESULT_INVALID_ARGUMENT;
        const std::span<const uint8_t> source_video(bytes.data()+static_cast<size_t>(range.offset),static_cast<size_t>(range.length));
        timeline source{};
        if(!inspect_isobmff_video_presentation_timeline(source_video,source)||!final_valid)return publish();
        const auto source_samples=visible(source);
        if(source_samples.empty())return LPB_RESULT_INVALID_ARGUMENT;
        bool has_cover=false,has_primary=false,cover_by_frame=false,primary_by_frame=false,default_midpoint=false;
        int64_t cover=0,primary_time=0,source_frame=0;
        bool valid=true;
        switch(facts.protocol) {
        case LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO:
            valid=inspect_isobmff_still_image_time(source_video,has_cover,cover);break;
        case LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL: {
            bool has_frame=false;int64_t frame=0;
            valid=vivo_positions(source_video,has_frame,frame,has_cover,cover);
            if(has_frame){has_primary=true;primary_by_frame=true;primary_time=frame;}
            if(!has_cover&&has_frame){has_cover=true;cover_by_frame=true;source_frame=frame;}
            break;
        }
        case LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO:
        case LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO:
            valid=mdta_scalar(source_video,"com.openharmony.covertime",has_cover,cover);
            if(valid&&!has_cover&&footer_frame(primary.bytes,source_frame)){has_cover=true;cover_by_frame=true;}break;
        default: {
            const bool micro=facts.protocol==LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1;
            const int state=lpb::media::observe_source_xmp_integer(context,primary.bytes,facts.primary_image.container,false,micro,cover);
            valid=state>=0;has_cover=state==1;
            // V2 default is explicit protocol behavior, not fabricated zero.
            if(valid&&!micro&&(state==0||cover==-1)){has_cover=true;default_midpoint=true;}
            if(facts.protocol==LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO) {
                const int ps=lpb::media::observe_source_xmp_integer(context,primary.bytes,facts.primary_image.container,true,false,primary_time);
                valid=valid&&ps>=0;has_primary=ps==1;
            }
            break;
        }}
        if(!valid){set_error(context,"Malformed or ambiguous protocol timing evidence.");return LPB_RESULT_INVALID_ARGUMENT;}
        size_t cover_index=0,primary_index=0;
        if(default_midpoint) {
            const auto* last=source_samples.back();
            if(last->normalized_time_100ns>INT64_MAX-last->duration_100ns)return LPB_RESULT_INVALID_ARGUMENT;
            const int64_t midpoint=(last->normalized_time_100ns+last->duration_100ns)/2;
            if(!time_index_100ns(source_samples,midpoint,cover_index))return LPB_RESULT_INVALID_ARGUMENT;
            cover_by_frame=true;source_frame=static_cast<int64_t>(cover_index);
        }
        if(has_cover) {
            if(facts.protocol==LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL&&!cover_by_frame) {
                const auto* last=source_samples.back();
                if(last->normalized_time_100ns>INT64_MAX-last->duration_100ns)return LPB_RESULT_INVALID_ARGUMENT;
                const int64_t source_end_ns=last->normalized_time_100ns+last->duration_100ns;
                // The editing field is integer milliseconds. Its documented
                // end sentinel is floor(duration milliseconds), per protocol.
                if(cover%1000==0 && cover/1000==source_end_ns/10000)
                    {cover_by_frame=true;source_frame=static_cast<int64_t>(source_samples.size()-1);}
            }
            if(cover_by_frame) {
                if(source_frame<0||static_cast<uint64_t>(source_frame)>=source_samples.size())return LPB_RESULT_INVALID_ARGUMENT;
                cover_index=static_cast<size_t>(source_frame);cover=ceiling_microseconds(source_samples[cover_index]->normalized_time_100ns);
            } else if(!time_index(source_samples,cover,cover_index)){
                if(cover>INT64_MAX/10||cover<0)return LPB_RESULT_INVALID_ARGUMENT;
                return publish();
            }
        }
        if(has_primary) {
            if(primary_by_frame) {
                if(primary_time<0||static_cast<uint64_t>(primary_time)>=source_samples.size())return LPB_RESULT_INVALID_ARGUMENT;
                primary_index=static_cast<size_t>(primary_time);primary_time=ceiling_microseconds(source_samples[primary_index]->normalized_time_100ns);
            } else if(!time_index(source_samples,primary_time,primary_index)){
                if(primary_time>INT64_MAX/10||primary_time<0)return LPB_RESULT_INVALID_ARGUMENT;
                return publish();
            }
        }
        if(has_cover||has_primary)result.source_evidence=default_midpoint?LPB_NEUTRAL_TIMING_PROTOCOL_DEFAULT:LPB_NEUTRAL_TIMING_EXPLICIT;
        uint32_t failing=0;
        if(!lpb::media::presentation_timing_within_source_frame_intervals(source,final_timeline,failing))return publish();
        const bool equal=schedules_equal(source_samples,final_samples);
        if(has_cover||has_primary)result.final_applicability=equal?LPB_NEUTRAL_TIMING_VERIFIED_PRESERVED:LPB_NEUTRAL_TIMING_MAPPED;
        if(has_cover) {
            result.cover_timestamp_us=equal?cover:ceiling_microseconds(final_samples[cover_index]->normalized_time_100ns);
            result.cover_frame_index=static_cast<int32_t>(cover_index);
            result.presence_flags|=LPB_NEUTRAL_TIMING_COVER_PRESENT|LPB_NEUTRAL_TIMING_COVER_FRAME_PRESENT;
        }
        if(has_primary){result.primary_timestamp_us=equal?primary_time:ceiling_microseconds(final_samples[primary_index]->normalized_time_100ns);result.presence_flags|=LPB_NEUTRAL_TIMING_PRIMARY_PRESENT;}
        return publish();
    } catch(...) {
        *out={};out->struct_size=sizeof(*out);set_error(context,"Neutral timing observation failed safely.");return LPB_RESULT_INTERNAL_ERROR;
    }
}
