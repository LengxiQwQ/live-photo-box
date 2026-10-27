#pragma once
#include <span>
#include <string>
#include <string_view>
#include <map>
#include <cstdint>

namespace lpb::media {
// Read-only syntax observation. Protocol policy is decided separately by each
// caller. The raw byte buffer remains owned by its ExifIFD tag.
inline bool observe_raw_camera_comment(std::span<const uint8_t> raw,
    std::map<std::string,std::string>& fields) {
    fields.clear();
    if(raw.size()<2||raw.size()>65500||raw.back()!=0)return false;
    for(size_t p=0;p+1<raw.size();) {
        const uint8_t first=raw[p++];
        if(!first||first=='\r'||(first<0x20&&first!='\n'&&first!='\t'))return false;
        if(first<0x80)continue;
        unsigned count=first>=0xc2&&first<=0xdf?1:first>=0xe0&&first<=0xef?2:first>=0xf0&&first<=0xf4?3:0;
        if(!count||count>raw.size()-1-p)return false;
        uint32_t scalar=first&((1u<<(6-count))-1);
        for(unsigned i=0;i<count;++i){const uint8_t next=raw[p++];if((next&0xc0)!=0x80)return false;scalar=(scalar<<6)|(next&63);}
        if((count==1&&scalar<0x80)||(count==2&&scalar<0x800)||(count==3&&scalar<0x10000)||scalar>0x10ffff||(scalar>=0xd800&&scalar<=0xdfff))return false;
    }
    std::string_view text(reinterpret_cast<const char*>(raw.data()),raw.size()-1);
    const auto trim=[](std::string_view v){while(!v.empty()&&(v.front()==' '||v.front()=='\n'||v.front()=='\t'))v.remove_prefix(1);while(!v.empty()&&(v.back()==' '||v.back()=='\n'||v.back()=='\t'))v.remove_suffix(1);return v;};
    while(!text.empty()) {
        const size_t end=text.find(';');if(end==text.npos)return false;
        const auto token=trim(text.substr(0,end));text.remove_prefix(end+1);
        const size_t colon=token.find(':');if(colon==token.npos)return false;
        const auto key=trim(token.substr(0,colon)),value=trim(token.substr(colon+1));
        if(key.empty()||!fields.emplace(std::string(key),std::string(value)).second)return false;
        text=trim(text);
    }
    return !fields.empty();
}
}
