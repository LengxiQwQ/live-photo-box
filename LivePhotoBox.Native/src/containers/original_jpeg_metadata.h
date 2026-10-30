#pragma once
#include "containers/isobmff.h"
#include <algorithm>
#include <array>
#include <cstring>
#include <span>
#include <vector>

namespace lpb::media::original_jpeg_metadata {
// Historical JPEG marker bytes retain their ORIGINAL container context. These
// offsets never identify a current HEIF item, file, executable path, auxiliary
// image, live binding, or mutation target. The normal Exif/XMP/ICC owners remain
// native HEIF metadata; this sealed record accounts for otherwise unrepresentable
// ordinary APP/COM and ICC segment padding. It is not an editing-support claim.
inline constexpr std::array<uint8_t,16> name{'L','i','v','e','P','h','o','t','o','B','o','x','I','M','v','1'};
struct marker {uint64_t offset{};std::vector<uint8_t> bytes;};
inline uint64_t integer(std::span<const uint8_t> b,size_t at,size_t n) noexcept {
    uint64_t value{};for(size_t i=0;i<n;++i)value=(value<<8)|b[at+i];return value;
}
inline void append(std::vector<uint8_t>& b,uint64_t n,size_t width) {
    for(size_t i=width;i>0;--i)b.push_back(static_cast<uint8_t>(n>>((i-1)*8)));
}
inline bool valid(std::span<const uint8_t> b) noexcept {
    if(b.size()<72||b.size()>32ULL*1024*1024||!std::equal(name.begin(),name.end(),b.begin())||
        integer(b,16,4)!=1||integer(b,20,4)||integer(b,68,4))return false;
    const auto length=integer(b,24,8),count=integer(b,64,4);
    if(length<4||length>32ULL*1024*1024||!count||count>512)return false;
    size_t at=72;uint64_t previous_end=2;
    for(uint64_t i=0;i<count;++i) {
        if(b.size()-at<16)return false;
        const auto offset=integer(b,at,8),size=integer(b,at+8,4);
        if(integer(b,at+12,4)||size<4||size>b.size()-at-16||offset<previous_end||offset>length||size>length-offset)return false;
        at+=16;const auto raw=b.subspan(at,static_cast<size_t>(size));
        size_t code=0;while(code<raw.size()&&raw[code]==0xff)++code;
        if(!code||raw.size()-code<3||!((raw[code]>=0xe0&&raw[code]<=0xef)||raw[code]==0xfe)||
            integer(raw,code+1,2)!=raw.size()-code-1)return false;
        previous_end=offset+size;at+=static_cast<size_t>(size);
    }
    return at==b.size();
}
inline bool encode(uint64_t length,std::span<const uint8_t,32> hash,const std::vector<marker>& markers,std::vector<uint8_t>& output) {
    output.clear();if(markers.empty())return true;
    output.insert(output.end(),name.begin(),name.end());append(output,1,4);append(output,0,4);append(output,length,8);
    output.insert(output.end(),hash.begin(),hash.end());append(output,markers.size(),4);append(output,0,4);
    for(const auto& m:markers) {
        if(m.bytes.size()>65539U||output.size()>32ULL*1024*1024-16-m.bytes.size()){output.clear();return false;}
        append(output,m.offset,8);append(output,m.bytes.size(),4);append(output,0,4);output.insert(output.end(),m.bytes.begin(),m.bytes.end());
    }
    if(!valid(output)){output.clear();return false;}return true;
}
inline bool attach(std::vector<uint8_t>& heif,std::span<const uint8_t> record) {
    if(record.empty())return true;if(!valid(record)||record.size()>UINT32_MAX-8ULL)return false;
    append(heif,record.size()+8,4);heif.insert(heif.end(),{'u','u','i','d'});heif.insert(heif.end(),record.begin(),record.end());return true;
}
// Framing only. Caller independently validates the complete HEIF owner graph.
inline bool observe(std::span<const uint8_t> heif,std::vector<uint8_t>& record) {
    record.clear();std::vector<uint8_t> found;size_t at=0;
    while(at<heif.size()) {
        isobmff_box_header box{};
        if(!try_read_box_header(heif.data(),at,heif.size(),box)||integer(heif,at,4)==0)return false;
        if(std::memcmp(heif.data()+at+4,"uuid",4)==0) {
            if(box.size-box.header_size<16)return false;
            const auto body=heif.subspan(at+box.header_size,box.size-box.header_size);
            if(std::equal(name.begin(),name.end(),body.begin())) {
                if(!found.empty()||!valid(body))return false;found.assign(body.begin(),body.end());
            }
        }
        at+=box.size;
    }
    if(at!=heif.size())return false;record=std::move(found);return true;
}
}
