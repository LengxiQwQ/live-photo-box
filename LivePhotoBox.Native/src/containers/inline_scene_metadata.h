#pragma once
#include <algorithm>
#include <array>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <span>
#include <string_view>

namespace lpb::media {
// Closed, inline-only movie/gnre JSON representation. Copy carries the complete
// box unchanged. No string, timestamp, id, or type becomes file/path, source live
// binding, target binding, playback, or deletion authority. Nested scene slices
// and unknown fields are deliberately unsupported until their graph is known.
inline bool copy_inline_scene_metadata(std::span<const uint8_t> bytes) noexcept {
    if(bytes.size()<17U||bytes.size()>16384U||
        !std::all_of(bytes.begin(),bytes.begin()+6,[](uint8_t n){return n==0;}))return false;
    size_t end=bytes.size();while(end>6U&&bytes[end-1U]==0U)--end;
    if(bytes.size()-end!=9U)return false;
    struct reader {
        std::string_view text;size_t at{};
        void ws() noexcept {while(at<text.size()&&(text[at]==' '||text[at]=='\t'||text[at]=='\r'||text[at]=='\n'))++at;}
        bool take(char c) noexcept {ws();if(at==text.size()||text[at]!=c)return false;++at;return true;}
        bool literal(std::string_view value) noexcept {ws();if(!text.substr(at).starts_with(value))return false;at+=value.size();return true;}
        bool word(std::string_view& out,size_t maximum=256U) noexcept {
            if(!take('"'))return false;const auto begin=at;
            while(at<text.size()&&text[at]!='"') {
                const auto n=static_cast<unsigned char>(text[at]);if(n<0x20U||n>=0x7fU||n=='\\'||at-begin>=maximum)return false;++at;
            }
            if(at==text.size())return false;out=text.substr(begin,at-begin);++at;return true;
        }
        bool token(std::string_view& out,bool integer_only) noexcept {
            ws();const auto begin=at;
            if(at==text.size())return false;
            if(text[at]=='0')++at;
            else {if(text[at]<'1'||text[at]>'9')return false;while(at<text.size()&&text[at]>='0'&&text[at]<='9')++at;}
            if(!integer_only&&at<text.size()&&text[at]=='.') {
                ++at;const auto first=at;while(at<text.size()&&text[at]>='0'&&text[at]<='9')++at;if(at==first)return false;
            }
            if(!integer_only&&at<text.size()&&(text[at]=='e'||text[at]=='E')) {
                ++at;if(at<text.size()&&(text[at]=='+'||text[at]=='-'))++at;
                const auto first=at;while(at<text.size()&&text[at]>='0'&&text[at]<='9')++at;if(at==first)return false;
            }
            out=text.substr(begin,at-begin);return !out.empty();
        }
        bool integer(uint64_t& n) noexcept {
            std::string_view raw;if(!token(raw,true))return false;
            const auto result=std::from_chars(raw.data(),raw.data()+raw.size(),n);
            return result.ec==std::errc{}&&result.ptr==raw.data()+raw.size()&&n<=INT64_MAX;
        }
        bool number() noexcept {
            std::string_view raw;if(!token(raw,false))return false;double n{};
            const auto result=std::from_chars(raw.data(),raw.data()+raw.size(),n);
            return result.ec==std::errc{}&&result.ptr==raw.data()+raw.size()&&std::isfinite(n)&&n>=0;
        }
        bool magic() noexcept {
            std::string_view key;
            return take('{')&&word(key)&&key=="sceneSlices"&&take(':')&&take('[')&&take(']')&&take('}');
        }
        bool lens() noexcept {
            if(!take('[')||!take('{'))return false;
            constexpr std::array<std::string_view,4> names{"endTime","lens","magicMoment","startTime"};
            uint32_t seen{};uint64_t start{},end{};
            for(;;) {
                std::string_view key;if(!word(key)||!take(':'))return false;
                const auto found=std::find(names.begin(),names.end(),key);if(found==names.end())return false;
                const auto index=static_cast<unsigned>(found-names.begin());const auto bit=1U<<index;
                if(seen&bit)return false;seen|=bit;
                if(index==0){if(!integer(end))return false;}
                else if(index==1){std::string_view value;if(!word(value,32U)||value.empty())return false;}
                else if(index==2){if(!magic())return false;}
                else if(!integer(start))return false;
                if(take('}'))break;if(!take(','))return false;
            }
            return seen==15U&&end>=start&&take(']');
        }
    };
    reader json{std::string_view(reinterpret_cast<const char*>(bytes.data()+6),end-6U)};
    constexpr std::array<std::string_view,8> names{"duration","effect","id","isSimilar","lensInformations","scene","score","type"};
    if(!json.take('{'))return false;uint32_t seen{};
    for(;;) {
        std::string_view key;if(!json.word(key)||!json.take(':'))return false;
        const auto found=std::find(names.begin(),names.end(),key);if(found==names.end())return false;
        const auto index=static_cast<unsigned>(found-names.begin());const auto bit=1U<<index;
        if(seen&bit)return false;seen|=bit;uint64_t value{};std::string_view word;
        switch(index) {
        case 0:case 1:if(!json.integer(value))return false;break;
        case 2:if(!json.word(word)||word.empty())return false;break;
        case 3:if(!json.literal("true")&&!json.literal("false"))return false;break;
        case 4:if(!json.lens())return false;break;
        case 5:if(!json.word(word,64U)||word.empty())return false;break;
        case 6:if(!json.number())return false;break;
        case 7:if(!json.word(word)||word!="livePhoto")return false;break;
        default:return false;
        }
        if(json.take('}'))break;if(!json.take(','))return false;
    }
    json.ws();return seen==255U&&json.at==json.text.size();
}
}
