#include "media/target_video_observation.h"
#include "media/vivo_modern_inventory.h"
#include "containers/isobmff_metadata_keys.h"
#include "containers/isobmff_track_graph.h"
#include <algorithm>
#include <array>
#include <cstring>
#include <set>
#include <string_view>
#include <utility>

namespace lpb::media {
namespace {
uint32_t u32(const uint8_t* p) { return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3]; }
struct box { size_t at,end,body; std::string type; };
struct inspector {
    target_video_diagnostic& d;
    uint64_t origin{};
    std::span<const uint8_t> data;
    std::set<uint32_t> tracks;
    std::vector<uint32_t> references;
    bool complete_graph_required{};
    target_covertime_observation* covertime{};
    bool fail(target_video_failure f,size_t at,std::string why) {
        d={f,target_video_stage::metadata,origin+at,std::move(why)};return false;
    }
    bool children(size_t first,size_t last,std::vector<box>& out) {
        for(size_t at=first;at<last;) {
            isobmff_box_header h{};
            if(!try_read_box_header(data.data(),at,last,h)||h.extends_to_end)
                return fail(target_video_failure::malformed,at,"Invalid or implicit-size metadata box");
            out.push_back({at,at+h.size,at+h.header_size,std::string(reinterpret_cast<const char*>(data.data()+at+4),4)});
            at+=h.size;
        }
        return true;
    }
    std::string_view text(size_t first,size_t last) const {
        return {reinterpret_cast<const char*>(data.data()+first),last-first};
    }
    bool fullzero(const box& b,size_t n=4) {
        if(b.end-b.body<n)return fail(target_video_failure::malformed,b.at,"Truncated full box");
        if(u32(data.data()+b.body))return fail(target_video_failure::unsupported,b.at,"Unsupported full-box version or flags");
        return true;
    }
    static bool live(std::string_view key) {
        return is_source_live_metadata_key("mdta",key);
    }
    bool uuid(const box& b,std::string_view parent={}) {
        if(b.end-b.body<16)return fail(target_video_failure::malformed,b.at,"Truncated UUID user type");
        // The protocol's ASCII user type is shorter than 16 bytes; compare its
        // documented identifier at the actual UUID user-type field only.
        constexpr std::string_view vivo="vivoMediaExtInfo";
        if(text(b.body,b.body+vivo.size())==vivo) {
            if(!parent.empty())return fail(target_video_failure::malformed,b.at,"Modern vivo mixed UUID has no legal top-level owner");
            vivo_modern_inventory mixed;
            if(!observe_vivo_modern(data.subspan(b.at,b.end-b.at),mixed)||!mixed.found)
                return fail(target_video_failure::unsupported,b.at,"Unclassified vivo mixed UUID ownership");
            if(mixed.binding)return fail(target_video_failure::source_binding,b.at,"vivo UUID live binding remains");
            return true;
        }
        // Registered Google spherical-video v1 UUID owns projection metadata,
        // not a Live/Motion Photo binding. Preserve its opaque payload in place.
        constexpr std::array<uint8_t,16> spherical{0xff,0xcc,0x82,0x63,0xf8,0x55,0x4a,0x93,0x88,0x14,0x58,0x7a,0x02,0x52,0x1f,0xdd};
        if(std::equal(spherical.begin(),spherical.end(),data.begin()+static_cast<ptrdiff_t>(b.body))) {
            if(parent=="trak")return true;
            return fail(target_video_failure::unsupported,b.at,"Registered spherical metadata has an unsupported owner");
        }
        // N9: unclassified user data is retained as opaque bytes. It grants
        // no target placement/timing authority. Known live UUIDs above reject.
        return true;
    }
    bool meta(const box& b,std::string_view owner) {
        size_t start=b.body;
        if(b.end-start>=4&&u32(data.data()+start)==0)start+=4; // ISO FullBox; QT omits it.
        std::vector<box> list;if(!children(start,b.end,list))return false;
        const box *handler=nullptr,*keys=nullptr,*ilst=nullptr;
        for(const auto& c:list) {
            // Padding is legal container scaffolding, including repeated free
            // space. Its bounded opaque bytes do not declare metadata keys.
            if(c.type=="free"||c.type=="skip")continue;
            const box** slot=c.type=="hdlr"?&handler:c.type=="keys"?&keys:c.type=="ilst"?&ilst:nullptr;
            if(!slot)return fail(target_video_failure::unsupported,c.at,"Unknown meta child ownership: "+c.type);
            if(*slot)return fail(target_video_failure::ambiguous,c.at,"Duplicate meta child: "+c.type);
            *slot=&c;
        }
        if(!handler&&!keys&&!ilst)return true;
        // Ordinary iTunes text owns literal data values, not mdta key indices.
        // Huawei's encoder owner uses manufacturer 'ohav'; preserve its ©too
        // bytes while independently inspecting any sibling movie mdta owner.
        if(handler&&!keys&&ilst&&handler->end-handler->body>=24&&text(handler->body+8,handler->body+12)=="mdir") {
            const auto mfr=text(handler->body+12,handler->body+16);if(mfr!="appl"&&mfr!="ohav"&&mfr!=std::string_view("\0\0\0\0",4)&&mfr!="    ")
                return fail(target_video_failure::unsupported,handler->at,"Unclassified iTunes metadata handler");
            if(!fullzero(*handler,24)||u32(data.data()+handler->body+4)||
               u32(data.data()+handler->body+16)||u32(data.data()+handler->body+20))
                return fail(target_video_failure::malformed,handler->at,"Invalid iTunes metadata handler");
            for(size_t p=handler->body+24;p<handler->end;++p)if(data[p])return fail(target_video_failure::unsupported,p,"Unknown iTunes handler name");
            std::vector<box> ordinary;if(!children(ilst->body,ilst->end,ordinary))return false;
            std::set<std::string> tags;
            for(const auto& item:ordinary) {
                const bool known=item.type==std::string("\xa9" "too",4)||item.type==std::string("\xa9" "nam",4)||
                    item.type==std::string("\xa9" "cmt",4)||item.type==std::string("\xa9" "cpy",4)||item.type==std::string("\xa9" "ART",4);
                if(!known||!tags.insert(item.type).second)return fail(target_video_failure::unsupported,item.at,"Unknown or duplicate iTunes property");
                std::vector<box> values;if(!children(item.body,item.end,values)||values.size()!=1||values[0].type!="data"||
                    values[0].end-values[0].body<8||u32(data.data()+values[0].body)!=1||u32(data.data()+values[0].body+4))
                    return fail(target_video_failure::malformed,item.at,"Invalid ordinary iTunes text ownership");
            }
            return true;
        }
        // A QuickTime/iTunes mdir handler with an actually empty ilst has no
        // key/value properties or live binding. FFmpeg's preserved ordinary
        // audio fixture uses this legal empty owner, not mdta+keys.
        if(handler&&!keys&&ilst&&ilst->body==ilst->end) {
            if(!fullzero(*handler,24))return false;
            if(u32(data.data()+handler->body+4)||text(handler->body+8,handler->body+12)!="mdir"||
               (text(handler->body+12,handler->body+16)!="appl"&&text(handler->body+12,handler->body+16)!=std::string_view("\0\0\0\0",4)&&text(handler->body+12,handler->body+16)!="    ")||
               u32(data.data()+handler->body+16)||u32(data.data()+handler->body+20))
                return fail(target_video_failure::unsupported,handler->at,"Unclassified empty metadata handler");
            for(size_t p=handler->body+24;p<handler->end;++p)if(data[p])
                return fail(target_video_failure::unsupported,handler->at,"Unclassified metadata handler name");
            return true;
        }
        if(!handler||!keys||!ilst)return fail(target_video_failure::malformed,b.at,"Incomplete mdta keys/ilst ownership");
        if(!fullzero(*handler,12)||!fullzero(*keys,8))return false;
        if(text(handler->body+8,handler->body+12)!="mdta")return fail(target_video_failure::unsupported,handler->at,"Unsupported metadata handler");
        const uint32_t count=u32(data.data()+keys->body+4);
        if(count>65536)return fail(target_video_failure::unsupported,keys->at,"Metadata key resource limit");
        size_t p=keys->body+8;std::set<std::string> names;uint32_t cover_index=0;
        for(uint32_t i=0;i<count;++i) {
            if(keys->end-p<8)return fail(target_video_failure::malformed,p,"Truncated metadata key");
            const uint32_t n=u32(data.data()+p);
            if(n<9||n>keys->end-p)return fail(target_video_failure::malformed,p,"Invalid metadata key length");
            if(text(p+4,p+8)!="mdta")return fail(target_video_failure::unsupported,p,"Unknown metadata key namespace");
            const auto name=text(p+8,p+n);
            if(name.find('\0')!=name.npos)return fail(target_video_failure::malformed,p,"NUL metadata key");
            if(!names.emplace(name).second)return fail(target_video_failure::ambiguous,p,"Duplicate metadata key");
            if(name=="com.openharmony.covertime"&&covertime)cover_index=i+1;
            else if(live(name))return fail(target_video_failure::source_binding,p,"Live metadata key remains: "+std::string(name));
            // N9 preserves unknown metadata. The complete mdta keys/ilst
            // hierarchy is checked below; only confirmed live keys grant a
            // source-binding diagnosis, never opaque value text or key names.
            p+=n;
        }
        if(p!=keys->end)return fail(target_video_failure::malformed,p,"Trailing metadata keys bytes");
        std::vector<box> items;if(!children(ilst->body,ilst->end,items))return false;
        std::set<uint32_t> indices;
        for(const auto& item:items) {
            const uint32_t index=u32(data.data()+item.at+4);
            if(!index||index>count)return fail(target_video_failure::malformed,item.at,"Metadata item key index out of range");
            if(!indices.insert(index).second)return fail(target_video_failure::ambiguous,item.at,"Duplicate metadata item index");
            std::vector<box> values;if(!children(item.body,item.end,values))return false;
            if(values.size()!=1||values[0].type!="data"||values[0].end-values[0].body<8)
                return fail(target_video_failure::malformed,item.at,"Metadata data ownership is missing or shadowed");
            // Value bytes are opaque ordinary data, not another box hierarchy.
            if(data[values[0].body]!=0)return fail(target_video_failure::unsupported,values[0].at,"Unsupported metadata data version");
            if(index==cover_index&&covertime) {
                const auto& v=values[0];target_covertime_property property;
                property.owner=owner;property.type=u32(data.data()+v.body);property.locale=u32(data.data()+v.body+4);
                property.value.assign(data.begin()+static_cast<ptrdiff_t>(v.body+8),data.begin()+static_cast<ptrdiff_t>(v.end));
                covertime->properties.push_back(std::move(property));
            }
        }
        if(cover_index&&!indices.contains(cover_index))return fail(target_video_failure::malformed,b.at,"Covertime key has no indexed value");
        return true;
    }
    bool walk(size_t first,size_t last,unsigned depth=0,std::string_view parent={},std::string path="moov") {
        if(depth>16)return fail(target_video_failure::unsupported,first,"Metadata nesting resource limit");
        std::vector<box> list;if(!children(first,last,list))return false;
        std::set<std::string> singleton;
        std::set<std::string> uuid_types;
        for(const auto& b:list) {
            if(b.type=="meta"||b.type=="udta"||b.type=="tref"||b.type=="hdlr"||b.type=="tkhd")
                if(!singleton.insert(b.type).second)return fail(target_video_failure::ambiguous,b.at,"Duplicate metadata owner: "+b.type);
            if(b.type=="meta") {if(!meta(b,path))return false;}
            else if(b.type=="uuid") {
                if(b.end-b.body>=16&&!uuid_types.emplace(text(b.body,b.body+16)).second)
                    return fail(target_video_failure::ambiguous,b.at,"Duplicate UUID ownership");
                if(!uuid(b,parent))return false;
            }
            else if(b.type=="udta") {if(!walk(b.body,b.end,depth+1,"udta",path+"/udta"))return false;}
            else if(b.type=="tref") {
                if(parent!="trak")return fail(target_video_failure::malformed,b.at,"Track reference has no track owner");
                std::vector<box> refs;if(!children(b.body,b.end,refs))return false;
                std::set<std::string> kinds;
                for(const auto& r:refs) {
                    if(!kinds.insert(r.type).second)return fail(target_video_failure::ambiguous,r.at,"Duplicate track reference owner");
                    if(r.type!="chap"&&r.type!="hint")complete_graph_required=true;
                    if(r.end==r.body||(r.end-r.body)%4)return fail(target_video_failure::malformed,r.at,"Invalid track reference list");
                    std::set<uint32_t> ids;
                    for(size_t p=r.body;p<r.end;p+=4){const uint32_t id=u32(data.data()+p);if(!id||!ids.insert(id).second)return fail(target_video_failure::ambiguous,p,"Duplicate or zero track reference");references.push_back(id);}
                }
            } else if(b.type=="tkhd") {
                if(parent!="trak")return fail(target_video_failure::malformed,b.at,"Track header has no track owner");
                if(b.end-b.body<4)return fail(target_video_failure::malformed,b.at,"Truncated track header");
                const size_t delta=data[b.body]==0?12:data[b.body]==1?20:SIZE_MAX;
                if(delta==SIZE_MAX||b.end-b.body<delta+4)return fail(target_video_failure::malformed,b.at,"Invalid track header version");
                const uint32_t id=u32(data.data()+b.body+delta);
                if(!id||!tracks.insert(id).second)return fail(target_video_failure::ambiguous,b.at,"Duplicate or zero track identity");
            } else if(b.type=="hdlr") {
                if(parent!="mdia"&&parent!="minf")return fail(target_video_failure::malformed,b.at,"Media handler has no legal owner");
                if(b.end-b.body<12)return fail(target_video_failure::malformed,b.at,"Truncated media handler");
                const auto type=text(b.body+8,b.body+12);
                if(parent=="mdia"&&type=="auxv")complete_graph_required=true;
                if(parent=="mdia"&&type!="vide"&&type!="soun"&&type!="meta"&&type!="auxv")
                    return fail(target_video_failure::unsupported,b.at,"Non-audio/video metadata track requires explicit Neutral ownership: "+std::string(type));
                if(parent=="minf"&&type!="vide"&&type!="soun"&&type!="alis"&&
                    !(text(b.body+4,b.body+8)=="dhlr"&&type=="url "))
                    return fail(target_video_failure::unsupported,b.at,"Unknown media data-handler ownership: "+std::string(type));
            } else if(b.type=="moov"||b.type=="trak"||b.type=="mdia"||b.type=="minf"||b.type=="stbl"||b.type=="edts"||b.type=="dinf") {
                const std::string_view required=b.type=="trak"?"moov":b.type=="mdia"||b.type=="edts"?"trak":
                    b.type=="minf"?"mdia":b.type=="stbl"||b.type=="dinf"?"minf":"";
                if(required.empty()||parent!=required)return fail(target_video_failure::malformed,b.at,"Known container has no legal parent");
                if(!walk(b.body,b.end,depth+1,b.type,path+"/"+b.type))return false;
            } else if(b.type=="meco"||b.type=="mpvd"||b.type=="sefd")
                return fail(target_video_failure::unsupported,b.at,"Unexpected ownership container: "+b.type);
            else if(b.type=="keys"||b.type=="ilst")
                return fail(target_video_failure::malformed,b.at,"Metadata key/value owner outside meta");
            else if(parent=="udta") {
                // Samsung camera user data is retained as bounded ordinary
                // metadata. No bytes here authorize a live range or timing.
                if(b.type=="SDLN"||b.type=="smrd") {
                    if(path!="moov/udta"||b.body==b.end)return fail(target_video_failure::malformed,b.at,"Samsung ordinary data has wrong owner or empty payload");
                    continue;
                }
                if(b.type=="smta") {
                    if(path!="moov/udta"||!fullzero(b))return fail(target_video_failure::malformed,b.at,"Samsung smta has wrong owner/full-box header");
                    std::vector<box> members;if(!children(b.body+4,b.end,members))return false;
                    if(members.size()!=1||members.front().type!="mdln"||members.front().body==members.front().end)
                        return fail(target_video_failure::unsupported,b.at,"Samsung smta ordinary child ownership unsupported");
                    continue;
                }
                // QuickTime text atoms have a known literal layout. Other
                // bounded user-data children remain opaque under N9: their
                // bytes are preserved and grant no binding or media authority.
                const bool text_atom=b.type==std::string("\xa9" "nam",4)||b.type==std::string("\xa9" "cmt",4)||
                    b.type==std::string("\xa9" "cpy",4)||b.type==std::string("\xa9" "too",4)||b.type==std::string("\xa9" "ART",4)||b.type==std::string("\xa9" "xyz",4);
                if(!text_atom)continue;
                if(b.end-b.body<4)return fail(target_video_failure::malformed,b.at,"Truncated QuickTime text atom");
                const size_t n=(size_t(data[b.body])<<8)|data[b.body+1];
                if(n!=b.end-b.body-4)return fail(target_video_failure::malformed,b.at,"QuickTime text length mismatch");
            }
        }
        return true;
    }
};
}
bool observe_target_video_metadata(const lpb::random_access_reader& reader,target_video_diagnostic& d,target_covertime_observation* covertime) noexcept {
    d={};
    if(covertime)covertime->properties.clear();
    try {
        const auto boxes=scan_top_level_boxes(reader);
        if(boxes.empty()){d={target_video_failure::malformed,target_video_stage::metadata,0,"Invalid top-level box graph"};return false;}
        size_t moovs=0,top_metas=0,top_udtas=0,modern_uuids=0;uint64_t end=0;
        for(const auto& b:boxes) {
            end=b.offset+b.size;
            if(!std::memcmp(b.type,"mpvd",4)||!std::memcmp(b.type,"sefd",4)) {
                d={target_video_failure::source_binding,target_video_stage::metadata,b.offset,"Known source live container remains at video top level"};return false;
            }
            constexpr std::string_view child_types[]={"keys","ilst","trak","stbl","tref","hdlr","mvhd","tkhd","mdhd",
                "mdia","minf","dinf","stsd","stts","ctts","stsc","stsz","stz2","stco","co64","stss","elst","edts","avcC","hvcC"};
            if(std::any_of(std::begin(child_types),std::end(child_types),[&](auto name){return !std::memcmp(b.type,name.data(),4);})) {
                d={target_video_failure::malformed,target_video_stage::metadata,b.offset,"Known ownership-bearing child has no legal top-level parent"};return false;
            }
            const bool top_meta=!std::memcmp(b.type,"meta",4),top_udta=!std::memcmp(b.type,"udta",4);
            if((top_meta&&++top_metas>1)||(top_udta&&++top_udtas>1)) {
                d={target_video_failure::ambiguous,target_video_stage::metadata,b.offset,"Duplicate top-level metadata owner"};return false;
            }
            if(std::memcmp(b.type,"moov",4)&&std::memcmp(b.type,"uuid",4)&&!top_meta&&!top_udta)continue;
            if(!std::memcmp(b.type,"moov",4)&&++moovs!=1){d={target_video_failure::ambiguous,target_video_stage::metadata,b.offset,"Duplicate moov owner"};return false;}
            uint64_t size=b.size;
            if(!std::memcmp(b.type,"uuid",4)) {
                std::array<uint8_t,16> user{};
                if(b.size<b.header_size+16||!reader.read_exact(b.offset+b.header_size,user)){d={target_video_failure::malformed,target_video_stage::metadata,b.offset,"Truncated top-level UUID user type"};return false;}
                const bool modern=!std::memcmp(user.data(),"vivoMediaExtInfo",16);
                if(modern&&(++modern_uuids>1||b.size>1024*1024)){d={target_video_failure::ambiguous,target_video_stage::metadata,b.offset,"Duplicate or unbounded modern mixed UUID"};return false;}
                if(!modern)size=b.header_size+16;
            }
            if(size>64ULL*1024*1024){d={target_video_failure::unsupported,target_video_stage::metadata,b.offset,"Metadata owner exceeds 64MiB structural resource limit"};return false;}
            std::vector<uint8_t> bytes(static_cast<size_t>(size));
            if(!reader.read_exact(b.offset,bytes)){d={target_video_failure::io,target_video_stage::metadata,b.offset,"Metadata read failed"};return false;}
            inspector inspect{d,b.offset,bytes};
            inspect.covertime=covertime;
            if(!std::memcmp(b.type,"uuid",4)) {if(!inspect.uuid({0,bytes.size(),b.header_size,"uuid"}))return false;}
            else if(top_meta) {if(!inspect.meta({0,bytes.size(),b.header_size,"meta"},"top"))return false;}
            else if(top_udta) {if(!inspect.walk(b.header_size,bytes.size(),0,"udta","top/udta"))return false;}
            else {
                std::vector<isobmff_metadata_key_track> key_tracks;
                if(!inspect_isobmff_metadata_keys(bytes,key_tracks))return inspect.fail(target_video_failure::malformed,0,"Malformed metadata-track key hierarchy");
                for(const auto& track:key_tracks)for(const auto& key:track.keys)
                    if(is_source_live_metadata_key(key.name_space,key.name))return inspect.fail(target_video_failure::source_binding,key.keyd_offset,"Known live binding in an actual MEBX key declaration");
                if(!inspect.walk(b.header_size,bytes.size(),0,"moov"))return false;
                for(const auto id:inspect.references)if(!inspect.tracks.contains(id))return inspect.fail(target_video_failure::malformed,0,"Dangling track reference");
                if(inspect.complete_graph_required) {
                    isobmff_track_graph graph;
                    if(!inspect_isobmff_track_graph(bytes,graph))return inspect.fail(target_video_failure::malformed,0,"Invalid complete track identity/relationship graph");
                }
            }
        }
        if(end!=reader.length||moovs!=1){d={target_video_failure::malformed,target_video_stage::metadata,end,"Incomplete or missing movie ownership"};return false;}
        return true;
    }catch(...){d={target_video_failure::unsupported,target_video_stage::metadata,UINT64_MAX,"Metadata observation resource failure"};return false;}
}
bool observe_target_video(const lpb::random_access_reader& reader,bool require_ftyp,target_video_observation& out,target_video_diagnostic& d,target_covertime_observation* covertime) noexcept {
    out={};if(!observe_target_video_metadata(reader,d,covertime))return false;
    isobmff_reader_diagnostic media;
    if(!inspect_isobmff_reader(reader,require_ftyp,out.media,media)) {
        const auto f=media.failure==isobmff_reader_failure::io?target_video_failure::io:media.failure==isobmff_reader_failure::unsupported?target_video_failure::unsupported:target_video_failure::malformed;
        d={f,target_video_stage::generic_media,media.offset,"Independent generic media structure rejected at byte "+std::to_string(media.offset)};return false;
    }
    video_profile_failure profile;video_profile_diagnostic why;
    if(!inspect_isobmff_video_profile(reader,out.profile,profile,&why)) {
        const auto f=profile==video_profile_failure::io?target_video_failure::io:profile==video_profile_failure::unsupported?target_video_failure::unsupported:profile==video_profile_failure::ambiguous?target_video_failure::ambiguous:target_video_failure::malformed;
        d={f,target_video_stage::profile,UINT64_MAX,"Independent video profile rejected at stage "+std::to_string(static_cast<unsigned>(why))};return false;
    }
    d={};return true;
}
}
