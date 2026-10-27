#include "protocols/huawei_target.h"
#include <algorithm>
#include <charconv>
#include <cstring>
#include <limits>
#include <stdexcept>

namespace lpb::target {
namespace {
using bytes=std::vector<uint8_t>;
uint32_t u32(const uint8_t* p) {return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3];}
void put32(bytes& b,size_t p,uint32_t n) {for(unsigned i=0;i<4;++i)b[p+i]=uint8_t(n>>((3-i)*8));}
struct box {size_t at,body,end;std::string type;};
bool children(const bytes& b,size_t first,size_t end,std::vector<box>& out) {
    for(size_t p=first;p<end;) {
        isobmff_box_header h{};
        if(!try_read_box_header(b.data(),p,end,h)||h.extends_to_end)return false;
        out.push_back({p,p+h.header_size,p+h.size,std::string(reinterpret_cast<const char*>(b.data()+p+4),4)});
        p+=h.size;
    }
    return true;
}
bytes wrap(const char* type,const bytes& body) {
    if(body.size()>UINT32_MAX-8)throw std::length_error("metadata box size");
    bytes out(8);put32(out,0,static_cast<uint32_t>(body.size()+8));
    std::memcpy(out.data()+4,type,4);out.insert(out.end(),body.begin(),body.end());return out;
}
void append(bytes& out,const bytes& in) {out.insert(out.end(),in.begin(),in.end());}
void raw(bytes& out,const bytes& in,size_t a,size_t e) {out.insert(out.end(),in.begin()+static_cast<ptrdiff_t>(a),in.begin()+static_cast<ptrdiff_t>(e));}
bytes property(uint32_t index,int64_t us) {
    // Exact decimal milliseconds: quotient plus exactly three decimal digits.
    const std::string value=std::to_string(us/1000)+"."+std::to_string(1000+us%1000).substr(1);
    bytes payload(8);put32(payload,0,1);payload.insert(payload.end(),value.begin(),value.end());
    bytes item=wrap("data",payload);bytes result(8);put32(result,0,static_cast<uint32_t>(8+item.size()));put32(result,4,index);append(result,item);return result;
}
bytes key() {
    constexpr std::string_view name="com.openharmony.covertime";
    bytes result(8);put32(result,0,static_cast<uint32_t>(8+name.size()));std::memcpy(result.data()+4,"mdta",4);
    result.insert(result.end(),name.begin(),name.end());return result;
}
bytes new_meta(int64_t us) {
    bytes hdlr(25);std::memcpy(hdlr.data()+8,"mdta",4);
    bytes keys(8);put32(keys,4,1);append(keys,key());
    bytes body(4);append(body,wrap("hdlr",hdlr));append(body,wrap("keys",keys));append(body,wrap("ilst",property(1,us)));
    return wrap("meta",body);
}
bool extend_meta(const bytes& in,const box& meta,int64_t us,bytes& out) {
    size_t first=meta.body;
    if(meta.end-first>=4&&u32(in.data()+first)==0)first+=4;
    std::vector<box> items;if(!children(in,first,meta.end,items))return false;
    const box *keys=nullptr,*ilst=nullptr,*handler=nullptr;
    for(const auto& c:items) {
        if(c.type=="keys")keys=&c;else if(c.type=="ilst")ilst=&c;else if(c.type=="hdlr")handler=&c;else return false;
    }
    // Input eligibility has already independently checked cardinality/ownership.
    // An empty meta or empty iTunes owner contains no ordinary property to lose.
    if(items.empty()||(handler&&!keys&&ilst&&ilst->body==ilst->end)) {out=new_meta(us);return true;}
    if(!keys||!ilst||!handler||keys->end-keys->body<8)return false;
    const uint32_t count=u32(in.data()+keys->body+4);if(count==UINT32_MAX)return false;
    bytes body;raw(body,in,meta.body,first);
    for(const auto& c:items) {
        if(c.type=="keys") {
            bytes k;raw(k,in,c.body,c.end);put32(k,4,count+1);append(k,key());append(body,wrap("keys",k));
        }else if(c.type=="ilst") {
            bytes values;raw(values,in,c.body,c.end);append(values,property(count+1,us));append(body,wrap("ilst",values));
        }else raw(body,in,c.at,c.end);
    }
    out=wrap("meta",body);return true;
}
bool extend_movie(const bytes& in,int64_t us,bytes& out) {
    isobmff_box_header h{};
    if(!try_read_box_header(in.data(),0,in.size(),h)||h.size!=in.size()||h.extends_to_end)return false;
    std::vector<box> list;if(!children(in,h.header_size,in.size(),list))return false;
    const box* direct=nullptr;const box* nested_owner=nullptr;
    const auto extensible=[&](const box& meta) {
        size_t first=meta.body;if(meta.end-first>=4&&u32(in.data()+first)==0)first+=4;
        std::vector<box> fields;if(!children(in,first,meta.end,fields))return false;
        return fields.empty()||std::any_of(fields.begin(),fields.end(),[](const auto& f){return f.type=="keys"||(f.type=="ilst"&&f.body==f.end);});
    };
    std::vector<box> udta_fields;
    for(const auto& c:list) {
        if(c.type=="meta"){if(direct)return false;direct=&c;}
        if(c.type=="udta") {
            if(!children(in,c.body,c.end,udta_fields))return false;
            for(const auto& n:udta_fields)if(n.type=="meta"){if(nested_owner)return false;nested_owner=&n;}
        }
    }
    const bool use_direct=(direct&&extensible(*direct))||(!direct&&nested_owner&&!extensible(*nested_owner));
    if(direct&&!extensible(*direct)&&nested_owner&&!extensible(*nested_owner))return false;
    bool udta_seen=false;bytes body;
    for(const auto& c:list) {
        if(use_direct&&c.type=="meta") {
            bytes rebuilt;if(!extend_meta(in,c,us,rebuilt))return false;append(body,rebuilt);continue;
        }
        if(use_direct&&c.type=="udta") {raw(body,in,c.at,c.end);udta_seen=true;continue;}
        if(c.type!="udta") {raw(body,in,c.at,c.end);continue;}
        if(udta_seen)return false;udta_seen=true;
        std::vector<box> nested;if(!children(in,c.body,c.end,nested))return false;
        bytes ub;bool meta_seen=false;
        for(const auto& n:nested) {
            if(n.type!="meta") {raw(ub,in,n.at,n.end);continue;}
            if(meta_seen)return false;meta_seen=true;bytes rebuilt;
            if(!extend_meta(in,n,us,rebuilt))return false;append(ub,rebuilt);
        }
        if(!meta_seen)append(ub,new_meta(us));append(body,wrap("udta",ub));
    }
    if(use_direct&&!direct)append(body,new_meta(us));
    if(!use_direct&&!udta_seen)append(body,wrap("udta",new_meta(us)));
    out=wrap("moov",body);return true;
}
}
bool huawei_movie_plan::read(const random_access_reader& input,uint64_t at,std::span<uint8_t> out) const noexcept {
    if(input.length!=input_length||at>output_length||out.size()>output_length-at)return false;
    while(!out.empty()) {
        uint64_t stop=output_length;const uint8_t* replacement=nullptr;uint64_t original=at;
        if(at<ftyp.size()) {stop=ftyp.size();replacement=ftyp.data()+static_cast<size_t>(at);}
        else if(at<moov_offset)stop=moov_offset;
        else if(at<moov_offset+moov.size()) {stop=moov_offset+moov.size();replacement=moov.data()+static_cast<size_t>(at-moov_offset);}
        else original=at-moov.size()+old_moov_length;
        const size_t n=static_cast<size_t>(std::min<uint64_t>(out.size(),stop-at));
        if(!n)return false;
        if(replacement)std::memcpy(out.data(),replacement,n);
        else if(!input.read_exact(original,out.first(n)))return false;
        at+=n;out=out.subspan(n);
    }
    return true;
}
bool prepare_huawei_movie(const random_access_reader& input,int64_t cover_us,huawei_movie_plan& out,std::string& error) noexcept {
    out={};error.clear();
    const auto fail=[&](const char* why){error=why;out={};return false;};
    try {
        media::target_video_observation observed;media::target_video_diagnostic diagnostic;
        if(!media::observe_target_video(input,true,observed,diagnostic)){error=diagnostic.reason;return false;}
        if(observed.media.quicktime||(observed.media.video_codec!=2&&observed.media.video_codec!=3)||observed.media.sample_tables.size()!=2)
            return fail("Huawei requires one H264/HEVC video and one independently validated AAC audio track");
        if(cover_us<0||cover_us>INT64_MAX/10)return fail("Unknown or out-of-range Huawei cover");
        uint32_t selected=0,count=0,hits=0;
        for(const auto& s:observed.media.timeline.presentation_order)if(s.visible) {
            if(s.normalized_time_100ns<0||s.duration_100ns<=0)return fail("Invalid visible Huawei timeline");
            const auto stamp=cover_us*10;
            if(stamp>=s.normalized_time_100ns&&stamp-s.normalized_time_100ns<s.duration_100ns){selected=count;++hits;}
            ++count;
        }
        if(hits!=1)return fail("Huawei cover must select one unambiguous visible presentation frame");
        const auto& top=observed.media.top_level_boxes;
        const auto movie=std::find_if(top.begin(),top.end(),[](const auto& b){return !std::memcmp(b.type,"moov",4);});
        if(top.empty()||movie==top.end()||std::memcmp(top.front().type,"ftyp",4)||top.front().size>1024*1024)
            return fail("Huawei movie requires a bounded explicit ftyp and moov");
        out.ftyp.resize(static_cast<size_t>(top.front().size));
        if(!input.read_exact(0,out.ftyp))return fail("Huawei ftyp read failed");
        if(u32(out.ftyp.data())==0)return fail("Implicit-size ftyp cannot be relocated");
        std::memcpy(out.ftyp.data()+top.front().header_size,"mp42",4);
        auto original=observed.media.moov_bytes;
        bytes rebuilt;if(!extend_movie(original,cover_us,rebuilt))return fail("Huawei metadata owner cannot be extended without loss");
        const int64_t delta=static_cast<int64_t>(rebuilt.size())-static_cast<int64_t>(original.size());
        // Patch the exact observed original table locations BEFORE metadata
        // reconstruction changes their snapshot positions. Every chunk remains
        // in the same mdat at the same relative coordinate, including multi-mdat.
        for(const auto& table:observed.media.sample_tables)for(size_t i=0;i<table.chunks.size();++i) {
            const auto& chunk=table.chunks[i];const isobmff_media_data_range* owner=nullptr;
            for(const auto& m:observed.media.mdats)if(chunk.offset>=m.start&&chunk.offset<=m.end&&chunk.size<=m.end-chunk.offset) {
                if(owner)return fail("Ambiguous Huawei chunk owner");owner=&m;
            }
            if(!owner)return fail("Huawei chunk has no observed mdat owner");
            uint64_t shifted=chunk.offset;
            if(owner->start>=movie->offset+movie->size) {
                if(delta>=0) {if(shifted>UINT64_MAX-static_cast<uint64_t>(delta))return fail("Huawei chunk offset overflow");shifted+=static_cast<uint64_t>(delta);}
                else {const auto n=static_cast<uint64_t>(-delta);if(shifted<n)return fail("Huawei chunk offset underflow");shifted-=n;}
            }
            const size_t width=table.chunk_offset_width,at=table.chunk_offsets_entries_start+i*width;
            if((width!=4&&width!=8)||at>original.size()||width>original.size()-at||(width==4&&shifted>UINT32_MAX))
                return fail("Huawei relocated offset cannot fit its existing table representation");
            for(size_t j=0;j<width;++j)original[at+j]=static_cast<uint8_t>(shifted>>((width-1-j)*8));
        }
        if(!extend_movie(original,cover_us,out.moov))return fail("Huawei rebuilt metadata failed");
        out.input_length=input.length;out.moov_offset=movie->offset;out.old_moov_length=movie->size;
        if(input.length<movie->size||input.length-movie->size>UINT64_MAX-out.moov.size())return fail("Huawei movie size overflow");
        out.output_length=input.length-movie->size+out.moov.size();out.cover_frame=selected;out.total_frames=count;
        return true;
    }catch(...){return fail("Huawei movie rewrite resource failure");}
}
bool make_huawei_footer(uint32_t variant,uint32_t frame,uint32_t count,uint64_t length,std::array<uint8_t,60>& out,std::string& error) noexcept {
    out.fill(' ');error.clear();
    try {
        if((variant!=1&&variant!=2)||!count||frame>=count||length>UINT64_MAX-20){error="Invalid Huawei footer semantics";return false;}
        const std::array<std::string,3> fields{std::string(variant==1?"v6_f":"v2_f")+std::to_string(frame),
            std::to_string(frame)+":"+std::to_string(count),"LIVE_"+std::to_string(length+20)};
        // The documented fixed field positions are 0,20,40; each available
        // field ends at the next boundary, never by snprintf minimum width.
        for(size_t i=0;i<3;++i) {
            if(fields[i].size()>20){error="Huawei footer field overflow";out.fill(' ');return false;}
            std::copy(fields[i].begin(),fields[i].end(),out.begin()+static_cast<ptrdiff_t>(i*20));
        }
        return true;
    }catch(...){error="Huawei footer resource failure";return false;}
}
} // namespace lpb::target
