#include "metadata/target_exif.h"
#include "metadata/exif.h"
#include "media/xmp_observation.h"
#include <algorithm>
#include <array>
#include <limits>

namespace lpb::metadata {
namespace {
bool write_comment(std::span<const uint8_t> jpeg,std::span<const uint8_t> comment,
 const media::preserved_user_comment* expected,std::vector<uint8_t>& output,media::jpeg_observation_failure& failure) noexcept {
 output.clear();failure=media::jpeg_observation_failure::malformed;
 try {
  if(comment.empty()||comment.size()>65500){failure=media::jpeg_observation_failure::unsupported;return false;}
  lpb::memory_random_access memory{jpeg};media::jpeg_observation observed;uint64_t bad{};
  if(!media::observe_jpeg(memory.view(),observed,failure,bad,{true,true,true,true}))return false;
  failure=media::jpeg_observation_failure::malformed;
  if(expected) {
   if(!expected->present||!observed.exif.has_user_comment||
      media::comment_tuple(observed.exif.user_comment_type,observed.exif.user_comment_count,observed.exif.user_comment)!=*expected)return false;
   media::target_xmp_observation record;media::xmp_observation_failure why{};
   if(!media::observe_target_xmp_packets(observed.xmp,observed.extended_xmp,observed.extended_guid,record,why)||record.preserved_comment!=*expected)return false;
  }else if(observed.exif.has_user_comment){failure=media::jpeg_observation_failure::unsupported;return false;}
  std::vector<uint8_t> payload=observed.exif_payload;
  if(payload.empty())payload={'E','x','i','f',0,0,'I','I',42,0,8,0,0,0,0,0,0,0,0,0};
  const bool big=observed.exif.big_endian;const size_t tiff=6;
  const auto put16=[&](size_t at,uint16_t value){payload[at]=static_cast<uint8_t>(value>>(big?8:0));payload[at+1]=static_cast<uint8_t>(value>>(big?0:8));};
  const auto put32=[&](size_t at,uint32_t value){for(size_t i=0;i<4;++i)payload[at+i]=static_cast<uint8_t>(value>>(8*(big?3-i:i)));};
  const auto align=[&](){if((payload.size()-tiff)&1)payload.push_back(0);};
  tiff_ifd root{},exif{};const auto root_offset=observed.exif_payload.empty()?8:observed.exif.ifd0_offset;
  if(!parse_ifd(payload.data(),payload.size(),tiff,root_offset,big,&root))return false;
  if(observed.exif.exif_ifd_offset&&!parse_ifd(payload.data(),payload.size(),tiff,observed.exif.exif_ifd_offset,big,&exif))return false;
  if(exif.entries.size()>=65535||(!observed.exif.exif_ifd_offset&&root.entries.size()>=65535)){
   failure=media::jpeg_observation_failure::unsupported;return false;
  }
  // Copy directory entries before vector growth invalidates their storage.
  const auto copy_entries=[&](const tiff_ifd& directory){
   std::vector<std::array<uint8_t,12>> entries;
   for(const auto& entry:directory.entries){std::array<uint8_t,12> bytes{};std::copy_n(payload.data()+entry.absolute_pos,12,bytes.data());entries.push_back(bytes);}return entries;
  };
  auto exif_entries=copy_entries(exif),root_entries=copy_entries(root);
  if(expected) {
   std::erase_if(exif_entries,[&](const auto& entry){return (big?uint16_t((entry[0]<<8)|entry[1]):uint16_t(entry[0]|(entry[1]<<8)))==0x9286;});
   if(exif_entries.size()+1!=exif.entries.size())return false;
  }
  align();uint32_t comment_offset=0;
  if(comment.size()>4){comment_offset=static_cast<uint32_t>(payload.size()-tiff);payload.insert(payload.end(),comment.begin(),comment.end());align();}
  const auto new_exif_offset=static_cast<uint32_t>(payload.size()-tiff);
  payload.resize(payload.size()+2+(exif_entries.size()+1)*12+4);
  size_t at=tiff+new_exif_offset;put16(at,static_cast<uint16_t>(exif_entries.size()+1));at+=2;
  // Maintain ascending tag order without rewriting any existing entry bytes.
  bool inserted=false;
  const auto emit_comment=[&](){put16(at,0x9286);put16(at+2,7);put32(at+4,static_cast<uint32_t>(comment.size()));put32(at+8,comment_offset);if(comment.size()<=4)std::copy(comment.begin(),comment.end(),payload.begin()+at+8);at+=12;};
  for(const auto& entry:exif_entries){const uint16_t tag=big?uint16_t((entry[0]<<8)|entry[1]):uint16_t(entry[0]|(entry[1]<<8));if(!inserted&&tag>0x9286){emit_comment();inserted=true;}std::copy(entry.begin(),entry.end(),payload.begin()+at);at+=12;}
  if(!inserted)emit_comment();put32(at,exif.next_ifd_offset);
  if(observed.exif.exif_ifd_offset){put32(tiff+observed.exif.exif_pointer_entry+8,new_exif_offset);}
  else {
   align();const auto new_root_offset=static_cast<uint32_t>(payload.size()-tiff);
   payload.resize(payload.size()+2+(root_entries.size()+1)*12+4);at=tiff+new_root_offset;put16(at,static_cast<uint16_t>(root_entries.size()+1));at+=2;inserted=false;
   const auto emit_pointer=[&](){put16(at,0x8769);put16(at+2,4);put32(at+4,1);put32(at+8,new_exif_offset);at+=12;};
   for(const auto& entry:root_entries){const uint16_t tag=big?uint16_t((entry[0]<<8)|entry[1]):uint16_t(entry[0]|(entry[1]<<8));if(!inserted&&tag>0x8769){emit_pointer();inserted=true;}std::copy(entry.begin(),entry.end(),payload.begin()+at);at+=12;}
   if(!inserted)emit_pointer();put32(at,root.next_ifd_offset);put32(tiff+4,new_root_offset);
  }
  if(payload.size()>65533){failure=media::jpeg_observation_failure::unsupported;return false;}
  // Reparse the complete candidate graph before producing any output.
  uint32_t orientation{};media::exif_observation check;
  if(!media::observe_exif(payload,orientation,check,failure,true,true)||!check.has_user_comment||check.user_comment!=std::vector<uint8_t>(comment.begin(),comment.end()))return false;
  const size_t start=observed.exif_payload.empty()?2:static_cast<size_t>(observed.exif_marker_start);
  const size_t end=observed.exif_payload.empty()?2:static_cast<size_t>(observed.exif_segment_end);
  std::vector<uint8_t> candidate;candidate.reserve(jpeg.size()-(end-start)+payload.size()+4);
  candidate.insert(candidate.end(),jpeg.begin(),jpeg.begin()+start);const size_t length=payload.size()+2;
  candidate.insert(candidate.end(),{0xff,0xe1,static_cast<uint8_t>(length>>8),static_cast<uint8_t>(length)});
  candidate.insert(candidate.end(),payload.begin(),payload.end());candidate.insert(candidate.end(),jpeg.begin()+end,jpeg.end());
  if(expected) {
   lpb::memory_random_access verified{candidate};media::jpeg_observation final;uint64_t final_bad{};
   media::target_xmp_observation record;media::xmp_observation_failure why{};
   if(!media::observe_jpeg(verified.view(),final,failure,final_bad,{true,true,true,true})||
      final.exif.user_comment_type!=7||final.exif.user_comment_count!=comment.size()||
      final.exif.user_comment!=std::vector<uint8_t>(comment.begin(),comment.end())||
      !media::observe_target_xmp_packets(final.xmp,final.extended_xmp,final.extended_guid,record,why)||record.preserved_comment!=*expected)return false;
  }
  output=std::move(candidate);failure=media::jpeg_observation_failure::none;return true;
 }catch(...){output.clear();return false;}
}
}
bool add_target_user_comment(std::span<const uint8_t> jpeg,std::span<const uint8_t> comment,
 std::vector<uint8_t>& output,media::jpeg_observation_failure& failure) noexcept {
 return write_comment(jpeg,comment,nullptr,output,failure);
}
bool map_target_user_comment(std::span<const uint8_t> jpeg,std::span<const uint8_t> comment,
 const media::preserved_user_comment& expected,std::vector<uint8_t>& output,media::jpeg_observation_failure& failure) noexcept {
 return write_comment(jpeg,comment,&expected,output,failure);
}
}
