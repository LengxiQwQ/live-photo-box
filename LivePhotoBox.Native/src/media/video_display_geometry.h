#pragma once
#include "containers/isobmff.h"
#include <array>
#include <cstring>

namespace lpb::media {
// Only structurally owned sample-entry clap boxes grant crop authority.
// Fractional pixel apertures are reported as unsupported, never rounded.
struct video_display_geometry {
    uint32_t width{}, height{}, top{}, bottom{}, left{}, right{};
    bool has_aperture{};
};
inline bool inspect_video_display_geometry(std::span<const uint8_t> bytes, video_display_geometry& out) {
    out = {};
    const auto u32 = [](const uint8_t* p) { return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3]; };
    const auto child = [&](const isobmff_box_header& parent, const char* type, isobmff_box_header& found) {
        unsigned count=0;
        for(size_t p=parent.start+parent.header_size;p<parent.start+parent.size;) {
            isobmff_box_header b{};
            if(!try_read_box_header(bytes.data(),p,parent.start+parent.size,b)) return false;
            if(std::memcmp(bytes.data()+p+4,type,4)==0) {found=b;++count;}
            p+=b.size;
        }
        return count==1;
    };
    isobmff_box_header root{0,bytes.size(),0}, moov{};
    if(!child(root,"moov",moov)) return false;
    unsigned videos=0;
    for(size_t p=moov.start+moov.header_size;p<moov.start+moov.size;) {
        isobmff_box_header trak{};
        if(!try_read_box_header(bytes.data(),p,moov.start+moov.size,trak)) return false;
        p+=trak.size;
        if(std::memcmp(bytes.data()+trak.start+4,"trak",4)!=0) continue;
        isobmff_box_header mdia{},hdlr{},minf{},stbl{},stsd{},entry{};
        if(!child(trak,"mdia",mdia)||!child(mdia,"hdlr",hdlr)||hdlr.size<hdlr.header_size+12) return false;
        if(std::memcmp(bytes.data()+hdlr.start+hdlr.header_size+8,"vide",4)!=0) continue;
        if(++videos!=1 || !child(mdia,"minf",minf)||!child(minf,"stbl",stbl)||!child(stbl,"stsd",stsd)||
            stsd.size<stsd.header_size+8 || u32(bytes.data()+stsd.start+stsd.header_size+4)!=1 ||
            !try_read_box_header(bytes.data(),stsd.start+stsd.header_size+8,stsd.start+stsd.size,entry)||
            entry.size<entry.header_size+78) return false;
        const auto* visual=bytes.data()+entry.start+entry.header_size;
        out.width=(uint32_t(visual[24])<<8)|visual[25]; out.height=(uint32_t(visual[26])<<8)|visual[27];
        for(size_t q=entry.start+entry.header_size+78;q<entry.start+entry.size;) {
            if(entry.start+entry.size-q==4 && u32(bytes.data()+q)==0) break;
            isobmff_box_header box{};
            if(!try_read_box_header(bytes.data(),q,entry.start+entry.size,box)) return false;
            q+=box.size;
            if(std::memcmp(bytes.data()+box.start+4,"clap",4)!=0) continue;
            if(out.has_aperture || box.size!=box.header_size+32) return false;
            out.has_aperture=true;
            const auto* a=bytes.data()+box.start+box.header_size;
            const uint32_t wn=u32(a),wd=u32(a+4),hn=u32(a+8),hd=u32(a+12),xd=u32(a+20),yd=u32(a+28);
            if(!wd||!hd||!xd||!yd||wn%wd||hn%hd) return false;
            const uint32_t w=wn/wd,h=hn/hd;
            if(!w||!h||w>out.width||h>out.height) return false;
            const int64_t lx=int64_t(out.width-w)*xd+2*int64_t(int32_t(u32(a+16)));
            const int64_t ty=int64_t(out.height-h)*yd+2*int64_t(int32_t(u32(a+24)));
            const int64_t dx=2*int64_t(xd),dy=2*int64_t(yd);
            if(lx<0||ty<0||lx%dx||ty%dy||lx/dx>out.width-w||ty/dy>out.height-h) return false;
            out.left=uint32_t(lx/dx);out.right=out.width-w-out.left;
            out.top=uint32_t(ty/dy);out.bottom=out.height-h-out.top;
        }
    }
    return videos==1 && out.width && out.height;
}
}
