#include "media/huawei_conformance.h"
#include <charconv>
#include <cmath>
#include <cstring>
#include <limits>
namespace lpb::media {
namespace {
bool number(std::string_view s,uint64_t& n) {
 if(s.empty())return false;for(char c:s)if(c<'0'||c>'9')return false;
 const auto p=std::from_chars(s.data(),s.data()+s.size(),n);return p.ec==std::errc{}&&p.ptr==s.data()+s.size();
}
bool field(std::span<const uint8_t> bytes,std::string_view& text) {
 size_t n=0;while(n<bytes.size()&&bytes[n]!=' ')++n;
 for(size_t i=n;i<bytes.size();++i)if(bytes[i]!=' ')return false;
 text={reinterpret_cast<const char*>(bytes.data()),n};return true;
}
}
bool observe_huawei_footer(std::span<const uint8_t> b,huawei_footer_observation& out) noexcept {
 out={};try {
  if(b.size()!=60)return false;std::string_view f,h,l;
  if(!field(b.first(20),f)||!field(b.subspan(20,20),h)||!field(b.last(20),l)||f.size()<5||l.size()<6)return false;
  if(f.starts_with("v6_f"))out.variant=1;else if(f.starts_with("v2_f"))out.variant=2;else return false;
  uint64_t frame=0,length=0;
  if(!number(f.substr(4),frame)||frame>UINT32_MAX||!l.starts_with("LIVE_")||!number(l.substr(5),length)||length<=20)return false;
  out.frame=static_cast<uint32_t>(frame);out.movie_length=length-20;out.history=h;return true;
 }catch(...){out={};return false;}
}
bool observe_huawei_covertime(const target_covertime_observation& properties,int64_t& us) noexcept {
 us=0;try {
  if(properties.properties.size()!=1)return false;const auto& p=properties.properties.front();
  if((p.owner!="moov/udta"&&p.owner!="moov")||p.locale)return false;
  if(p.type==1) {
   std::string_view value(reinterpret_cast<const char*>(p.value.data()),p.value.size());
   const auto dot=value.find('.');const auto integer=value.substr(0,dot);uint64_t ms=0;
   if(!number(integer,ms)||ms>static_cast<uint64_t>(INT64_MAX/1000))return false;
   uint64_t fraction=0;
   if(dot!=value.npos) {
    auto f=value.substr(dot+1);if(f.empty())return false;
    while(f.size()>3&&f.back()=='0')f.remove_suffix(1);
    if(f.size()>3||!number(f,fraction))return false;
    for(size_t i=f.size();i<3;++i)fraction*=10;
   }
   const auto whole=ms*1000;if(whole>static_cast<uint64_t>(INT64_MAX)-fraction)return false;
   us=static_cast<int64_t>(whole+fraction);return true;
  }
  if(p.type==0x17&&p.value.size()==4) {
   const uint32_t bits=(uint32_t(p.value[0])<<24)|(uint32_t(p.value[1])<<16)|(uint32_t(p.value[2])<<8)|p.value[3];
   float milliseconds=0;std::memcpy(&milliseconds,&bits,4);
   const double microseconds=static_cast<double>(milliseconds)*1000;
   // The observation is the nearest integral microsecond represented by the
   // actual float32 millisecond value. No epsilon admits a different frame.
   if(!std::isfinite(microseconds)||microseconds<0||microseconds>=static_cast<double>(INT64_MAX))return false;
   us=static_cast<int64_t>(std::round(microseconds));return true;
  }
  return false;
 }catch(...){return false;}
}
bool select_huawei_frame(const isobmff_video_presentation_timeline& t,int64_t us,uint32_t& frame,uint32_t& count) noexcept {
 frame=0;count=0;if(us<0||us>INT64_MAX/10)return false;unsigned hits=0;
 for(const auto& s:t.presentation_order)if(s.visible) {
  if(s.normalized_time_100ns<0||s.duration_100ns<=0)return false;
  const int64_t stamp=us*10;
  if(stamp>=s.normalized_time_100ns&&stamp-s.normalized_time_100ns<s.duration_100ns){frame=count;++hits;}
  if(count==UINT32_MAX)return false;++count;
 }
 return hits==1;
}
bool project_huawei_legacy_cover(const isobmff_video_presentation_timeline& t,uint32_t frame,int64_t& us,uint32_t& count) noexcept {
 us=0;count=0;bool found=false;
 for(const auto& sample:t.presentation_order)if(sample.visible) {
  if(count==frame) {
   if(sample.normalized_time_100ns<0)return false;
   us=sample.normalized_time_100ns/10+(sample.normalized_time_100ns%10!=0?1:0);found=true;
  }
  if(count==UINT32_MAX)return false;++count;
 }
 uint32_t selected=0,total=0;
 return found&&select_huawei_frame(t,us,selected,total)&&selected==frame&&total==count;
}
}
