#pragma once
#include "containers/original_jpeg_metadata.h"
#include "foundation/sha256_core.h"

namespace lpb::media::original_jpeg_padding {
// Historical source encoding padding, not current HEIF item/extent or live
// authority. This is separate from the frozen eight metadata ledger entries.
inline constexpr std::array<uint8_t,16> name{'L','i','v','e','P','h','o','t','o','B','o','x','J','P','v','1'};
using original_jpeg_metadata::integer;
using original_jpeg_metadata::append;
inline bool valid(std::span<const uint8_t> b) noexcept {
    if(b.size()<=80||b.size()>80+65536||!std::equal(name.begin(),name.end(),b.begin())||
        integer(b,16,4)!=1||integer(b,20,4))return false;
    const auto source_length=integer(b,24,8),offset=integer(b,64,8),length=integer(b,72,8);
    return source_length>=4&&source_length<=32ULL*1024*1024&&offset>=2&&offset<source_length&&
        length==source_length-offset&&length==b.size()-80&&length<=65536&&
        std::any_of(b.begin()+32,b.begin()+64,[](uint8_t v){return v!=0;})&&
        std::all_of(b.begin()+80,b.end(),[](uint8_t v){return v==0;});
}
inline bool encode(std::span<const uint8_t> source,uint64_t offset,std::vector<uint8_t>& record) {
    record.clear();if(offset>source.size())return false;if(offset==source.size())return true;
    if(source.size()>32ULL*1024*1024||offset<2||source.size()-offset>65536||
        source[offset-2]!=0xff||source[offset-1]!=0xd9||
        !std::all_of(source.begin()+static_cast<ptrdiff_t>(offset),source.end(),[](uint8_t b){return b==0;}))return false;
    uint8_t hash[32]{};lpb::crypto::sha256_buffer(source.data(),source.size(),hash);
    record.insert(record.end(),name.begin(),name.end());append(record,1,4);append(record,0,4);append(record,source.size(),8);
    record.insert(record.end(),std::begin(hash),std::end(hash));append(record,offset,8);append(record,source.size()-offset,8);
    record.insert(record.end(),source.begin()+static_cast<ptrdiff_t>(offset),source.end());
    if(!valid(record)){record.clear();return false;}return true;
}
inline bool attach(std::vector<uint8_t>& heif,std::span<const uint8_t> record) {
    if(record.empty())return true;if(!valid(record))return false;
    append(heif,record.size()+8,4);heif.insert(heif.end(),{'u','u','i','d'});heif.insert(heif.end(),record.begin(),record.end());return true;
}
inline bool observe(std::span<const uint8_t> heif,std::vector<uint8_t>& record) {
    record.clear();std::vector<uint8_t> found;size_t at=0;
    while(at<heif.size()) {
        isobmff_box_header box{};
        if(!try_read_box_header(heif.data(),at,heif.size(),box)||box.extends_to_end)return false;
        if(!std::memcmp(heif.data()+at+4,"uuid",4)) {
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
