#pragma once

#include <algorithm>
#include <charconv>
#include <cstdint>
#include <limits>
#include <span>
#include <string>
#include <string_view>
#include <vector>
#include "containers/isobmff.h"

namespace lpb::media {

// Ordinary byte records with an EOF-owned, self-relative directory. Record
// names and payloads confer no live-binding, file-access or mutation authority.
struct opaque_camera_record {
    std::string name;
    uint64_t offset{}, length{}; // relative to the start of the captured block
};
struct opaque_camera_record_inventory {
    uint64_t block_offset{}, block_length{}, directory_offset{}, directory_length{};
    std::vector<opaque_camera_record> records;
};

namespace opaque_record_detail {
class directory_reader {
    std::string_view input_;
    size_t at_{};
    void space() { while(at_<input_.size() && (input_[at_]==' '||input_[at_]=='\t'||input_[at_]=='\n'||input_[at_]=='\r')) ++at_; }
    bool take(char c) { space();if(at_==input_.size()||input_[at_]!=c)return false;++at_;return true; }
    bool text(std::string& value) {
        if(!take('"'))return false;
        const auto begin=at_;
        while(at_<input_.size() && input_[at_]!='"') {
            const auto c=static_cast<unsigned char>(input_[at_]);
            if(!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='.'||c=='_'||c=='-'))return false;
            if(++at_-begin>95)return false;
        }
        if(at_==input_.size()||at_==begin)return false;
        value.assign(input_.substr(begin,at_-begin));++at_;return true;
    }
    bool integer(uint64_t& value) {
        space();const auto begin=at_;
        while(at_<input_.size()&&input_[at_]>='0'&&input_[at_]<='9')++at_;
        if(at_==begin || (at_-begin>1 && input_[begin]=='0'))return false;
        const auto result=std::from_chars(input_.data()+begin,input_.data()+at_,value);
        return result.ec==std::errc{} && result.ptr==input_.data()+at_;
    }
public:
    explicit directory_reader(std::string_view input):input_(input){}
    bool read(std::vector<opaque_camera_record>& output) {
        if(!take('['))return false;
        for(;;) {
            if(output.size()==64 || !take('{'))return false;
            opaque_camera_record record;uint32_t seen=0;uint64_t version=0;
            for(;;) {
                std::string key;if(!text(key)||!take(':'))return false;
                uint32_t bit=0;
                if(key=="name") {bit=1;if(!text(record.name))return false;}
                else if(key=="offset") {bit=2;if(!integer(record.offset))return false;}
                else if(key=="length") {bit=4;if(!integer(record.length))return false;}
                else if(key=="version") {bit=8;if(!integer(version))return false;}
                else return false;
                if(seen&bit)return false;seen|=bit;
                if(take('}'))break;
                if(!take(','))return false;
            }
            if(seen!=15 || version!=1 || !record.length || record.offset<record.length ||
                std::any_of(output.begin(),output.end(),[&](const auto& r){return r.name==record.name;}))return false;
            output.push_back(std::move(record));
            if(take(']'))break;
            if(!take(','))return false;
        }
        space();return at_==input_.size();
    }
};
}

// The input end is supplied by an authoritative source/container boundary.
// Never searches for a footer or JSON string at arbitrary byte positions.
inline bool observe_opaque_camera_records(std::span<const uint8_t> bytes,
    opaque_camera_record_inventory& output) noexcept {
    output={};
    try {
        if(bytes.size()<11)return false;
        const auto n=bytes.size();
        const uint32_t directory_length=uint32_t(bytes[n-4])|(uint32_t(bytes[n-3])<<8)|
            (uint32_t(bytes[n-2])<<16)|(uint32_t(bytes[n-1])<<24);
        if(directory_length<11 || directory_length>65536 || directory_length>n || bytes[n-9]!=0)return false;
        const std::string_view marker(reinterpret_cast<const char*>(bytes.data()+n-8),4);
        if(marker!="jxrs"&&marker!="wtmk")return false;
        const size_t directory=n-directory_length;
        std::vector<opaque_camera_record> records;
        if(!opaque_record_detail::directory_reader(std::string_view(
            reinterpret_cast<const char*>(bytes.data()+directory),directory_length-9)).read(records))return false;
        uint64_t area_length=0;
        for(const auto& record:records)area_length=std::max(area_length,record.offset);
        if(!area_length || area_length>directory || area_length>32ULL*1024*1024-directory_length)return false;
        for(auto& record:records)record.offset=area_length-record.offset;
        std::sort(records.begin(),records.end(),[](const auto& a,const auto& b){return a.offset<b.offset;});
        uint64_t cursor=0;
        for(const auto& record:records) {
            if(record.offset!=cursor || record.length>area_length-cursor)return false;
            cursor+=record.length;
        }
        if(cursor!=area_length)return false;
        output.block_offset=directory-area_length;
        output.block_length=area_length+directory_length;
        output.directory_offset=area_length;output.directory_length=directory_length;
        output.records=std::move(records);return true;
    }catch(...){output={};return false;}
}

// Source-side linkage only. A terminal record block must be outside the
// declared video or begin at its unique final explicitly sized comm box.
// The directory may cross that box's end. No downstream brand dispatch.
inline bool bind_opaque_camera_records_source(std::span<const uint8_t> bytes,
    uint64_t video_offset,uint64_t video_length,opaque_camera_record_inventory& output,
    uint64_t& extracted_video_length) noexcept {
    output={};extracted_video_length=0;
    opaque_camera_record_inventory observed;
    if(video_offset>bytes.size()||!video_length||video_length>bytes.size()-video_offset ||
        video_offset+video_length==bytes.size()||!observe_opaque_camera_records(bytes,observed))return false;
    const uint64_t end=video_offset+video_length;
    if(observed.block_offset==end) {
        output=std::move(observed);extracted_video_length=video_length;return true;
    }
    if(observed.block_offset<=video_offset||observed.block_offset>=end)return false;
    size_t at=static_cast<size_t>(video_offset);size_t comm_count=0;
    while(at<end) {
        isobmff_box_header header{};
        if(!try_read_box_header(bytes.data(),at,static_cast<size_t>(end),header)||header.extends_to_end)return false;
        if(std::string_view(reinterpret_cast<const char*>(bytes.data()+at+4),4)=="comm") {
            if(++comm_count!=1||at!=observed.block_offset||header.size!=end-at)return false;
        }
        at+=header.size;
    }
    if(comm_count!=1||at!=end)return false;
    extracted_video_length=observed.block_offset-video_offset;output=std::move(observed);return true;
}
}
