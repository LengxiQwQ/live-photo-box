#pragma once
#include <algorithm>
#include <array>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <span>
#include <string_view>

namespace lpb::media {
// Private Copy-only grammar. Values are carried exactly, not interpreted as
// target binding/timing authority or applied to decoded image/video pixels.
inline bool copy_inline_transform_metadata(std::string_view bytes) noexcept {
    struct reader {
        std::string_view text;size_t at{};
        void ws() noexcept {while(at<text.size()&&(text[at]==' '||text[at]=='\t'||text[at]=='\r'||text[at]=='\n'))++at;}
        bool take(char value) noexcept {ws();if(at==text.size()||text[at]!=value)return false;++at;return true;}
        bool word(std::string_view& out) noexcept {
            if(!take('"'))return false;const size_t begin=at;
            while(at<text.size()&&text[at]!='"') {
                const auto ch=static_cast<unsigned char>(text[at]);if(ch<0x20U||ch>=0x7fU||ch=='\\')return false;++at;
            }
            if(at==text.size())return false;out=text.substr(begin,at-begin);++at;return true;
        }
        bool token(std::string_view& out,bool integer_only) noexcept {
            ws();const size_t begin=at;if(at<text.size()&&text[at]=='-'){if(integer_only)return false;++at;}
            if(at==text.size())return false;
            if(text[at]=='0')++at;
            else {if(text[at]<'1'||text[at]>'9')return false;while(at<text.size()&&text[at]>='0'&&text[at]<='9')++at;}
            if(!integer_only&&at<text.size()&&text[at]=='.') {
                ++at;const size_t first=at;while(at<text.size()&&text[at]>='0'&&text[at]<='9')++at;if(at==first)return false;
            }
            if(!integer_only&&at<text.size()&&(text[at]=='e'||text[at]=='E')) {
                ++at;if(at<text.size()&&(text[at]=='+'||text[at]=='-'))++at;
                const size_t first=at;while(at<text.size()&&text[at]>='0'&&text[at]<='9')++at;if(at==first)return false;
            }
            out=text.substr(begin,at-begin);return !out.empty();
        }
        bool number(double& value) noexcept {
            std::string_view raw;if(!token(raw,false))return false;
            const auto parsed=std::from_chars(raw.data(),raw.data()+raw.size(),value);
            return parsed.ec==std::errc{}&&parsed.ptr==raw.data()+raw.size()&&std::isfinite(value);
        }
        bool integer(uint64_t& value) noexcept {
            std::string_view raw;if(!token(raw,true))return false;
            const auto parsed=std::from_chars(raw.data(),raw.data()+raw.size(),value);
            return parsed.ec==std::errc{}&&parsed.ptr==raw.data()+raw.size();
        }
        bool numbers(std::span<double> values) noexcept {
            if(!take('['))return false;
            for(size_t i=0;i<values.size();++i)if((i&&!take(','))||!number(values[i]))return false;
            return take(']');
        }
    };
    constexpr std::string_view prefix="LivePhotoExtension";
    if(bytes.size()>16384U||!bytes.starts_with(prefix))return false;
    reader json{bytes.substr(prefix.size())};if(!json.take('{'))return false;
    constexpr std::array<std::string_view,11> names={"coverFramePts","desc","matrixCount","photoCropFactor","photoCropRect",
        "photoEisCropFactor","photoEisMatrix","subVideoScaleFactor","version","videoOrientation","videoSize"};
    uint32_t seen=0;
    for(;;) {
        std::string_view name;if(!json.word(name)||!json.take(':'))return false;
        const auto found=std::find(names.begin(),names.end(),name);if(found==names.end())return false;
        const auto index=static_cast<unsigned>(found-names.begin());const uint32_t bit=1U<<index;if(seen&bit)return false;seen|=bit;
        uint64_t value=0;double scalar=0;std::array<double,9> array{};
        switch(index) {
        case 0:if(!json.integer(value)||value>INT64_MAX)return false;break;
        case 1:{std::string_view description;if(!json.word(description)||description!="OppoMotionVideoExt")return false;break;}
        case 2:if(!json.integer(value)||value!=0)return false;break;
        case 3:if(!json.number(scalar)||scalar<0)return false;break;
        case 4:if(!json.numbers(std::span<double>(array).first(4))||
            !std::all_of(array.begin(),array.begin()+4,[](double n){return n>=0;}))return false;break;
        case 5:if(!json.numbers(std::span<double>(array).first(2))||array[0]<=0||array[1]<=0)return false;break;
        case 6:{
            if(!json.numbers(array))return false;
            const long double determinant=static_cast<long double>(array[0])*(static_cast<long double>(array[4])*array[8]-static_cast<long double>(array[5])*array[7])-
                static_cast<long double>(array[1])*(static_cast<long double>(array[3])*array[8]-static_cast<long double>(array[5])*array[6])+
                static_cast<long double>(array[2])*(static_cast<long double>(array[3])*array[7]-static_cast<long double>(array[4])*array[6]);
            if(!std::isfinite(determinant)||determinant==0)return false;break;
        }
        case 7:if(!json.number(scalar)||scalar<=0)return false;break;
        case 8:if(!json.integer(value)||value!=1)return false;break;
        case 9:if(!json.integer(value)||(value!=0&&value!=90&&value!=180&&value!=270))return false;break;
        case 10:
            if(!json.take('[')||!json.integer(value)||!value||value>65535||!json.take(',')||
                !json.integer(value)||!value||value>65535||!json.take(']'))return false;break;
        default:return false;
        }
        if(json.take('}'))break;if(!json.take(','))return false;
    }
    json.ws();return seen==0x7ffU&&json.at==json.text.size();
}
}
