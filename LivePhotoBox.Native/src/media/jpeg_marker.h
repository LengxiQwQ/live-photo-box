#pragma once
#include "binary/portable_io.h"
#include <algorithm>
#include <array>
#include <cstdint>
#include <span>

namespace lpb::media {
struct jpeg_marker_range { uint8_t marker{}; uint64_t start{},payload{},length{}; bool after_scan{}; };
// One traversal for memory and file readers. Metadata visitors borrow one segment;
// entropy is streamed with FF/restart state retained across cache boundaries.
class jpeg_cursor {
    const lpb::random_access_reader& source_;
    std::array<uint8_t,65536> bytes_{};
    uint64_t base_{UINT64_MAX}; size_t count_{};
public:
    explicit jpeg_cursor(const lpb::random_access_reader& s):source_(s){}
    bool byte(uint64_t at,uint8_t& v) noexcept {
        if(at>=source_.length)return false;
        if(base_==UINT64_MAX||at<base_||at-base_>=count_) {
            base_=at;count_=static_cast<size_t>(std::min<uint64_t>(bytes_.size(),source_.length-at));
            if(!source_.read_exact(at,std::span<uint8_t>(bytes_.data(),count_))){base_=UINT64_MAX;return false;}
        }
        v=bytes_[static_cast<size_t>(at-base_)];return true;
    }
};
template<class Visitor> bool walk_jpeg_markers(const lpb::random_access_reader& source,
    uint64_t& eoi,Visitor&& visit) {
    eoi=0;jpeg_cursor cursor(source);uint8_t a=0,b=0;
    if(source.length<4||!cursor.byte(0,a)||!cursor.byte(1,b)||a!=0xff||b!=0xd8)return false;
    uint64_t p=2;bool inside=false,saw_scan=false;
    while(p<source.length) {
        if(inside) {
            bool found=false;
            while(p<source.length) {
                if(!cursor.byte(p,a))return false;
                if(a!=0xff){++p;continue;}
                uint64_t q=p+1;
                do {if(!cursor.byte(q,b))return false;++q;}while(b==0xff);
                if(b==0||(b>=0xd0&&b<=0xd7)){p=q;continue;}
                found=true;break;
            }
            if(!found)return false;inside=false;
        }
        const uint64_t start=p;
        if(!cursor.byte(p++,a)||a!=0xff)return false;
        do {if(!cursor.byte(p++,b))return false;}while(b==0xff);
        if(b==0xd9){eoi=p;return true;}
        if(b==0||b==0xd8||(b>=0xd0&&b<=0xd7))return false;
        if(b==1)continue;
        uint8_t hi=0,lo=0;
        if(!cursor.byte(p,hi)||!cursor.byte(p+1,lo))return false;
        const uint64_t length=(static_cast<uint64_t>(hi)<<8)|lo;
        if(length<2||length>source.length-p)return false;
        if(!visit(jpeg_marker_range{b,start,p+2,length-2,saw_scan}))return false;
        p+=length;
        if(b==0xda){inside=true;saw_scan=true;}
    }
    return false;
}
}
