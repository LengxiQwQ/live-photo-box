#pragma once
#include "containers/isobmff.h"
#include "containers/isobmff_video_profile.h"
#include "media/video_sei_preservation.h"
#include <algorithm>
#include <cstring>

namespace lpb::media {
// Compare the presentation schedule and source-owned SEI, rather than packet
// order: an encoder may use a different B-frame decoding order.
inline bool verify_repair_video(std::span<const uint8_t> a,std::span<const uint8_t> b) {
    lpb::memory_random_access ar{a},br{b};
    isobmff_video_profile ap{},bp{};video_profile_failure error{};
    const auto codec=[](uint32_t c){return c==0x68766331u||c==0x68657631u?1:c==0x61766331u||c==0x61766333u?2:0;};
    if(!inspect_isobmff_video_profile(ar.view(),ap,error)||!inspect_isobmff_video_profile(br.view(),bp,error)||
       !codec(ap.codec_fourcc)||codec(ap.codec_fourcc)!=codec(bp.codec_fourcc)||ap.bit_depth_luma!=bp.bit_depth_luma||ap.bit_depth_chroma!=bp.bit_depth_chroma||
       ap.chroma_format!=bp.chroma_format||ap.color_primaries!=bp.color_primaries||
       ap.transfer_characteristics!=bp.transfer_characteristics||ap.matrix_coefficients!=bp.matrix_coefficients||
       ap.full_range!=bp.full_range)return false;
    isobmff_video_presentation_timeline at{},bt{};
    if(!inspect_isobmff_video_presentation_timeline(a,at)||!inspect_isobmff_video_presentation_timeline(b,bt)||
       at.presentation_order.size()!=bt.presentation_order.size())return false;
    for(size_t i=0;i<at.presentation_order.size();++i) {
        const auto& x=at.presentation_order[i];const auto& y=bt.presentation_order[i];
        const int64_t tick=std::max((10000000LL+at.media_timescale-1)/at.media_timescale,(10000000LL+bt.media_timescale-1)/bt.media_timescale);
        if(x.visible!=y.visible||x.normalized_time_100ns!=y.normalized_time_100ns||
           std::abs(x.duration_100ns-y.duration_100ns)>tick)return false;
    }
    using messages=std::vector<preserved_sei_message>;
    const auto collect=[](std::span<const uint8_t> data,const auto& timeline,std::vector<messages>& result,uint32_t encoder_transfer) {
        const auto u32=[](const uint8_t* p){return uint32_t(p[0])<<24|uint32_t(p[1])<<16|uint32_t(p[2])<<8|p[3];};
        // All addresses come from bounded boxes and the strict sample table.
        const auto child=[&](size_t start,size_t end,const char* type,isobmff_box_header& found) {
            found={};for(size_t p=start;p<end;) {isobmff_box_header h{};
                if(!try_read_box_header(data.data(),p,end,h))return false;
                if(!std::memcmp(data.data()+p+4,type,4)){if(found.size)return false;found=h;}p+=h.size;}
            return found.size!=0;
        };
        isobmff_box_header moov{},video{};
        if(!child(0,data.size(),"moov",moov))return false;
        for(size_t p=moov.start+moov.header_size;p<moov.start+moov.size;) {
            isobmff_box_header h{};if(!try_read_box_header(data.data(),p,moov.start+moov.size,h))return false;
            if(!std::memcmp(data.data()+p+4,"trak",4)) {
                isobmff_box_header md{},hd{};
                if(!child(p+h.header_size,p+h.size,"mdia",md)||!child(md.start+md.header_size,md.start+md.size,"hdlr",hd)||hd.size<hd.header_size+12)return false;
                if(!std::memcmp(data.data()+hd.start+hd.header_size+8,"vide",4)){if(video.size)return false;video=md;}
            }p+=h.size;
        }
        isobmff_box_header mi{},st{},sd{},sz{},entry{},config{};
        if(!video.size||!child(video.start+video.header_size,video.start+video.size,"minf",mi)||
           !child(mi.start+mi.header_size,mi.start+mi.size,"stbl",st)||
           !child(st.start+st.header_size,st.start+st.size,"stsd",sd)||sd.size<sd.header_size+8||u32(data.data()+sd.start+sd.header_size+4)!=1||
           !try_read_box_header(data.data(),sd.start+sd.header_size+8,sd.start+sd.size,entry)||entry.size<86)return false;
        const bool hevc=!std::memcmp(data.data()+entry.start+4,"hvc1",4)||!std::memcmp(data.data()+entry.start+4,"hev1",4);
        if(!hevc&&std::memcmp(data.data()+entry.start+4,"avc1",4)&&std::memcmp(data.data()+entry.start+4,"avc3",4))return false;
        if(!child(entry.start+86,entry.start+entry.size,hevc?"hvcC":"avcC",config))return false;
        const size_t body=config.start+config.header_size;
        if(config.size<config.header_size+(hevc?23u:5u))return false;
        const unsigned length=(data[body+(hevc?21:4)]&3)+1;
        messages configuration;
        if(hevc&&!collect_hevc_configuration_sei(data.subspan(body,config.size-config.header_size),configuration))return false;
        lpb::memory_random_access memory{data};isobmff_reader_observation observed{};isobmff_reader_diagnostic diagnostic{};
        if(!inspect_isobmff_reader(memory.view(),true,observed,diagnostic))return false;
        isobmff_sample_table_layout table{};uint64_t bad{};
        if(!inspect_isobmff_strict_sample_table(data,st.start,st.start+st.size,observed.mdats,0,table,bad)||
           !child(st.start+st.header_size,st.start+st.size,"stsz",sz)||sz.size<sz.header_size+12)return false;
        const size_t sb=sz.start+sz.header_size;const uint32_t fixed=u32(data.data()+sb+4),count=u32(data.data()+sb+8);
        if(count!=table.sample_count||count!=timeline.sample_count||(!fixed&&sz.size!=sz.header_size+12+uint64_t(count)*4))return false;
        std::vector<messages> decode(count);
        for(const auto& chunk:table.chunks) {
            uint64_t p=chunk.offset;
            for(uint32_t j=0;j<chunk.sample_count;++j) {
                const uint32_t index=chunk.first_sample+j;
                if(index>=count)return false;
                const uint32_t n=fixed?fixed:u32(data.data()+sb+12+size_t(index)*4);
                if(p>data.size()||n>data.size()-p||!collect_packet_sei(data.subspan(size_t(p),n),length,hevc,decode[index],false,encoder_transfer))return false;
                // Configuration SEI applies to each frame; compare its semantics
                // together with sample-owned messages, independent of placement.
                decode[index].insert(decode[index].end(),configuration.begin(),configuration.end());p+=n;
            }
            if(p!=chunk.offset+chunk.size)return false;
        }
        messages persistent;
        for(const auto& sample:timeline.presentation_order) {
            auto current=std::move(decode[sample.sample_index]);
            // Mastering display and light level persist until replaced. The
            // muxer may move these from hvcC to the first access unit.
            for(uint32_t type:{137u,144u}) {
                auto value=std::find_if(current.begin(),current.end(),[&](const auto& x){return x.type==type;});
                auto prior=std::find_if(persistent.begin(),persistent.end(),[&](const auto& x){return x.type==type;});
                if(value!=current.end()){if(prior==persistent.end())persistent.push_back(*value);else *prior=*value;}
                else if(prior!=persistent.end())current.push_back(*prior);
            }
            result.push_back(std::move(current));
        }
        return true;
    };
    std::vector<messages> am,bm;if(!collect(a,at,am,UINT32_MAX)||!collect(b,bt,bm,bp.transfer_characteristics))return false;
    for(size_t i=0;i<am.size();++i) {
        const auto equal=[](const auto& x,const auto& y){return x.type==y.type&&x.payload==y.payload;};
        auto remaining=bm[i];
        for(const auto& m:am[i]){auto it=std::find_if(remaining.begin(),remaining.end(),[&](const auto& x){return equal(x,m);});if(it==remaining.end())return false;remaining.erase(it);}
        // Encoders may add their own unregistered identification message.
        for(const auto& m:remaining)if(m.type!=5)return false;
    }
    return true;
}
}
