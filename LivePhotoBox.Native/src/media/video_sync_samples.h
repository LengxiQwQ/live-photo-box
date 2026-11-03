#pragma once
#include "containers/isobmff.h"
#include "binary/endian.h"
#include <cstring>

namespace lpb::media {
// The ordinary source observer first validates the actual track graph. This
// additional target requirement checks the unique video track's sync table.
inline bool all_video_samples_sync(const std::vector<uint8_t>& moov) {
    const auto child=[&](size_t start,size_t end,const char* type,isobmff_box_header& found) {
        bool present=false;
        for(size_t at=start;at<end;) {
            isobmff_box_header box{};if(!try_read_box_header(moov.data(),at,end,box))return false;
            if(!std::memcmp(moov.data()+at+4,type,4)) {if(present)return false;found=box;present=true;}
            at+=box.size;
        }
        return present;
    };
    uint32_t videos=0;
    for(size_t at=8;at<moov.size();) {
        isobmff_box_header track{};if(!try_read_box_header(moov.data(),at,moov.size(),track))return false;
        at+=track.size;if(std::memcmp(moov.data()+track.start+4,"trak",4))continue;
        isobmff_box_header mdia{},handler{},minf{},stbl{},stsz{},stss{};
        if(!child(track.start+track.header_size,track.start+track.size,"mdia",mdia)||
           !child(mdia.start+mdia.header_size,mdia.start+mdia.size,"hdlr",handler)||handler.size<24)return false;
        if(std::memcmp(moov.data()+handler.start+handler.header_size+8,"vide",4))continue;
        ++videos;
        if(!child(mdia.start+mdia.header_size,mdia.start+mdia.size,"minf",minf)||
           !child(minf.start+minf.header_size,minf.start+minf.size,"stbl",stbl)||
           !child(stbl.start+stbl.header_size,stbl.start+stbl.size,"stsz",stsz)||stsz.size<20)return false;
        const auto count=read_be32u(moov.data()+stsz.start+stsz.header_size+8);
        bool has_sync=false;
        for(size_t p=stbl.start+stbl.header_size;p<stbl.start+stbl.size;) {
            isobmff_box_header entry{};if(!try_read_box_header(moov.data(),p,stbl.start+stbl.size,entry))return false;
            if(!std::memcmp(moov.data()+p+4,"stss",4)) {
                if(has_sync)return false;has_sync=true;stss=entry;
            }
            p+=entry.size;
        }
        if(!count)return false;
        if(has_sync) {
            const auto body=stss.start+stss.header_size;
            if(stss.size!=stss.header_size+8ULL+4ULL*count||read_be32u(moov.data()+body+4)!=count)return false;
            for(uint32_t i=0;i<count;++i)if(read_be32u(moov.data()+body+8+4ULL*i)!=i+1)return false;
        }
    }
    return videos==1;
}
}
