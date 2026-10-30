#pragma once
#include "binary/portable_io.h"
#include "containers/isobmff.h"
#include <array>
#include <algorithm>
#include <span>
#include <cstring>

namespace lpb::media {
// QuickTime legacy ctts v0 is signed; ISO ctts v0 is unsigned. Preserve the
// actual offset meaning across the brand change by using ISO's signed v1.
// The reverse positive >INT32_MAX case has no signed 32-bit representation.
inline bool map_remux_composition_versions(std::span<uint8_t> movie,bool source_qt,bool target_qt) noexcept {
    const auto walk=[&](auto&& self,size_t begin,size_t end,bool sample_table,size_t depth)->bool {
        if(depth>8)return false;
        for(size_t at=begin;at<end;) {
            isobmff_box_header box{};
            if(!try_read_box_header(movie.data(),at,end,box))return false;
            const auto type=[&](const char* name){return std::memcmp(movie.data()+at+4,name,4)==0;};
            const size_t body=at+box.header_size,limit=at+box.size;
            if(type("ctts")&&sample_table) {
                if(box.size<box.header_size+8||movie[body]>1||movie[body+1]||movie[body+2]||movie[body+3])return false;
                uint32_t count=0;for(size_t i=0;i<4;++i)count=(count<<8)|movie[body+4+i];
                if(count==0||count>(limit-body-8)/8||limit-body-8!=static_cast<size_t>(count)*8)return false;
                bool high=false;
                for(size_t i=0;i<count;++i)if(movie[body+8+i*8+4]&0x80)high=true;
                if(movie[body]==0&&high&&source_qt!=target_qt) {
                    if(!source_qt)return false;
                    movie[body]=1;
                }
            } else if(type("moov")||type("trak")||type("mdia")||type("minf")||type("stbl")) {
                if(!self(self,body,limit,type("stbl"),depth+1))return false;
            }
            at=limit;
        }
        return true;
    };
    return walk(walk,0,movie.size(),false,0);
}

// Additional transport proof, not a parser/authority substitute. The caller
// supplies its structurally derived expected ftyp and relocated/mapped moov,
// including an equivalent signed composition-offset version where required.
// Every other complete top-level box (including all mdat/free/unknown bytes)
// must remain byte-identical and in the same order.
inline bool prove_remux_bytes(const lpb::random_access_reader& source,
    const lpb::random_access_reader& output, std::span<const uint8_t> expected_ftyp,
    std::span<const uint8_t> expected_moov, bool (*cancelled)(void*) = nullptr,
    void* state = nullptr) noexcept {
    struct box { uint64_t at{}, size{}; std::array<uint8_t,4> type{}; };
    const auto read_box = [](const lpb::random_access_reader& r, uint64_t at, box& b) noexcept {
        std::array<uint8_t,16> h{};
        if (at >= r.length || !r.read_exact(at, std::span<uint8_t>(h).first(8))) return false;
        uint64_t size = 0; for (size_t i=0; i<4; ++i) size=(size<<8)|h[i];
        uint64_t header=8;
        if (size==1) {
            if (!r.read_exact(at+8, std::span<uint8_t>(h).subspan(8))) return false;
            size=0; for (size_t i=8; i<16; ++i) size=(size<<8)|h[i]; header=16;
        } else if (size==0) size=r.length-at;
        if (size<header || size>r.length-at) return false;
        b={at,size,{h[4],h[5],h[6],h[7]}}; return true;
    };
    if (expected_ftyp.size()<8 || expected_moov.size()<8) return false;
    uint64_t a=0,b=0; unsigned ftyp=0,moov=0;
    std::array<uint8_t,65536> x{},y{};
    while (a<source.length && b<output.length) {
        if (cancelled && cancelled(state)) return false;
        box s{},t{}; if (!read_box(source,a,s)||!read_box(output,b,t)||s.type!=t.type) return false;
        std::span<const uint8_t> expected;
        if (std::memcmp(s.type.data(),"ftyp",4)==0) {
            if (++ftyp!=1 || a!=0 || b!=0) return false; expected=expected_ftyp;
        } else if (std::memcmp(s.type.data(),"moov",4)==0) {
            if (++moov!=1) return false; expected=expected_moov;
        } else if (s.size!=t.size) return false;
        if (!expected.empty() && t.size!=expected.size()) return false;
        uint64_t offset=0;
        while (offset<t.size) {
            if (cancelled && cancelled(state)) return false;
            const size_t n=static_cast<size_t>(std::min<uint64_t>(x.size(),t.size-offset));
            if (!output.read_exact(t.at+offset,std::span<uint8_t>(y).first(n))) return false;
            if (expected.empty()) {
                if (!source.read_exact(s.at+offset,std::span<uint8_t>(x).first(n)) ||
                    std::memcmp(x.data(),y.data(),n)!=0) return false;
            } else if (std::memcmp(expected.data()+static_cast<size_t>(offset),y.data(),n)!=0) return false;
            offset+=n;
        }
        a+=s.size; b+=t.size;
    }
    return a==source.length && b==output.length && ftyp==1 && moov==1;
}
}
