#include "media/jpeg_observation.h"
#include "media/jpeg_marker.h"
#include "metadata/exif.h"
#include "foundation/md5_core.h"
#include "media/jpeg_gainmap_mpf.h"
#include <cstring>
#include <set>
namespace lpb::media {
bool observe_plain_exif(std::span<const uint8_t> bytes,uint32_t& orientation,jpeg_observation_failure& failure) noexcept {
 exif_observation ignored;return observe_exif(bytes,orientation,ignored,failure,false);
}
bool observe_exif(std::span<const uint8_t> bytes,uint32_t& orientation,exif_observation& out,jpeg_observation_failure& failure,bool allow_opaque,bool allow_unknown_orientation) noexcept {
 out={};
 failure=jpeg_observation_failure::malformed;
 try {
  if(bytes.size()<14||std::memcmp(bytes.data(),"Exif\0\0",6))return false;
  const size_t tiff=6,end=bytes.size();const bool big=bytes[tiff]=='M'&&bytes[tiff+1]=='M';
  if(!big&&!(bytes[tiff]=='I'&&bytes[tiff+1]=='I'))return false;
  const auto u16=[&](size_t at){return big?static_cast<uint16_t>((bytes[at]<<8)|bytes[at+1]):static_cast<uint16_t>(bytes[at]|(bytes[at+1]<<8));};
  const auto u32=[&](size_t at){return big?static_cast<uint32_t>((static_cast<uint32_t>(bytes[at])<<24)|(bytes[at+1]<<16)|(bytes[at+2]<<8)|bytes[at+3]):static_cast<uint32_t>(bytes[at]|(bytes[at+1]<<8)|(bytes[at+2]<<16)|(static_cast<uint32_t>(bytes[at+3])<<24));};
  if(u16(tiff+2)!=42||u32(tiff+4)<8)return false;
  out.big_endian=big;out.ifd0_offset=u32(tiff+4);
  size_t maker_count=0;bool apple_maker=false;
  std::vector<uint32_t> pending{u32(tiff+4)},seen;std::vector<std::pair<uint64_t,uint64_t>> occupied{{0,8}};
  std::set<uint32_t> image_ifds{u32(tiff+4)};
  const auto claim=[&](uint64_t start,uint64_t length){
   if(start>end-tiff||length>end-tiff-start)return false;const uint64_t finish=start+length;
   for(const auto& range:occupied)if(start<range.second&&range.first<finish)return false;
   occupied.emplace_back(start,finish);return true;
  };
  while(!pending.empty()) {
   const uint32_t offset=pending.back();pending.pop_back();if(!offset)continue;
   if(offset<8||offset>end-tiff||seen.size()>=32||std::find(seen.begin(),seen.end(),offset)!=seen.end())return false;
   seen.push_back(offset);tiff_ifd directory{};if(!parse_ifd(bytes.data(),end,tiff,offset,big,&directory)||!claim(offset,2+static_cast<uint64_t>(directory.entries.size())*12+4))return false;
   std::set<uint16_t> tags;
   for(const auto& entry:directory.entries){
    // Huawei/Honor legitimately retain several opaque vendor MakerNotes in
    // the formal ExifIFD. They grant no target authority. All pointers, ranges
    // and other tags remain unique; any Apple-shaped owner plus another note
    // is a pairing shadow and is rejected below as well as by the CID reader.
    if(!tags.insert(entry.tag).second&&!(allow_opaque&&entry.tag==0x927c))return false;
    if(entry.tag==0x014a||entry.tag==0xb000||(!allow_opaque&&(entry.tag==0x927c||entry.tag==0x9286))){failure=jpeg_observation_failure::unsupported;return false;}
    uint64_t width=0;switch(entry.type){case 1:case 2:case 6:case 7:width=1;break;case 3:case 8:width=2;break;case 4:case 9:case 11:width=4;break;case 5:case 10:case 12:width=8;break;default:failure=jpeg_observation_failure::unsupported;return false;}
    const uint64_t n=width*entry.count;if(!n||(n>4&&!claim(entry.value_offset,n)))return false;
    if(entry.tag==0x927c||entry.tag==0x9286){
     // These tags are owned by the unique ExifIFD, never IFD0/GPS/Interop/thumbnail.
     if(!out.exif_ifd_offset||offset!=out.exif_ifd_offset||
        (entry.tag==0x927c?entry.type!=7:(entry.type!=7&&entry.type!=2)))return false;
     out.opaque_metadata=true;
     if(entry.tag==0x927c) {
      ++maker_count;const size_t at=n<=4?entry.absolute_pos+8:tiff+entry.value_offset;
      if(n>=9&&std::memcmp(bytes.data()+at,"Apple iOS",9)==0)apple_maker=true;
      if(maker_count>1&&apple_maker)return false;
     }
     if(entry.tag==0x9286){
      if(out.has_user_comment)return false;out.has_user_comment=true;
      out.user_comment_entry=entry.absolute_pos-tiff;
      out.user_comment_offset=n<=4?entry.absolute_pos+8-tiff:entry.value_offset;
      out.user_comment.assign(bytes.begin()+tiff+out.user_comment_offset,bytes.begin()+tiff+out.user_comment_offset+static_cast<size_t>(n));
     }
    }
    if(entry.tag==0x112){
     if(!image_ifds.count(offset))return false;
     const uint32_t value=entry.type==3&&entry.count==1?u16(entry.absolute_pos+8):0;
     if(value<1||value>8){
      if(!allow_unknown_orientation)return false;
      if(offset==out.ifd0_offset)orientation=0;
     }else if(offset==out.ifd0_offset)orientation=value;
    }
    if(entry.tag==0x8769||entry.tag==0x8825||entry.tag==0xa005){
     if(entry.type!=4||entry.count!=1||entry.value_offset<8)return false;
     if(allow_opaque){
      if((entry.tag==0x8769||entry.tag==0x8825)&&offset!=out.ifd0_offset)return false;
      if(entry.tag==0xa005&&offset!=out.exif_ifd_offset)return false;
     }
     if(entry.tag==0x8769){if(out.exif_ifd_offset)return false;out.exif_ifd_offset=entry.value_offset;out.exif_pointer_entry=entry.absolute_pos-tiff;}
     pending.push_back(entry.value_offset);
    }
   }
   if(directory.next_ifd_offset&&image_ifds.count(offset))image_ifds.insert(directory.next_ifd_offset);
   pending.push_back(directory.next_ifd_offset);
  }
  failure=jpeg_observation_failure::none;return true;
 }catch(...){return false;}
}

bool observe_jpeg(const lpb::random_access_reader& source,jpeg_observation& out,
 jpeg_observation_failure& failure,uint64_t& bad,jpeg_observation_options options) noexcept {
 out={};out.orientation=1;failure=jpeg_observation_failure::malformed;bad=0;
 try {
  bool sof=false;unsigned exif=0;uint8_t frame_marker=0;
  struct chunk_data { extended_xmp_chunk facts;std::vector<uint8_t> bytes; };
  std::vector<chunk_data> chunks;uint32_t extension_total=0;
  std::array<bool,4> quant{},dc{},ac{};
  std::array<int,256> components{};components.fill(-1);
  const bool valid=walk_jpeg_markers(source,out.eoi_end,[&](const jpeg_marker_range& m){
   bad=m.start;std::vector<uint8_t> payload(static_cast<size_t>(m.length));
   if(!source.read_exact(m.payload,payload)){failure=jpeg_observation_failure::io;return false;}
   if(m.marker==0xdb){
    for(size_t p=0;p<payload.size();) {
     const uint8_t field=payload[p++],precision=field>>4,index=field&15;
     if(precision>1||index>3||64U*(precision+1)>payload.size()-p)return false;
     for(size_t q=0;q<64;++q){const unsigned value=precision?static_cast<unsigned>((payload[p]<<8)|payload[p+1]):payload[p];if(!value)return false;p+=precision+1;}
     quant[index]=true;
    }
   }
   if(m.marker==0xc4){
    for(size_t p=0;p<payload.size();) {
     if(payload.size()-p<17)return false;const uint8_t field=payload[p++];const unsigned kind=field>>4,index=field&15;
     if(kind>1||index>3)return false;unsigned total=0;int slots=1;
     for(unsigned bits=0;bits<16;++bits){slots=slots*2-payload[p+bits];if(slots<0)return false;total+=payload[p+bits];}
     p+=16;if(!total||total>256||total>payload.size()-p)return false;
     p+=total;(kind?ac:dc)[index]=true;
    }
   }
   if(m.marker==0xdd&&payload.size()!=2)return false;
   if(m.marker==0xda){
    if(payload.size()<4||payload[0]==0||payload.size()!=static_cast<size_t>(payload[0])*2+4||!sof||payload[0]>4)return false;
    const size_t tail=1+static_cast<size_t>(payload[0])*2;const uint8_t first=payload[tail],last=payload[tail+1],approx=payload[tail+2];
    if(frame_marker!=0xc2&&(first||last!=63||approx))return false;
    if(frame_marker==0xc2&&(first>last||last>63||(first==0&&last!=0)||(first>0&&payload[0]!=1)||(approx>>4)>13||(approx&15)>13))return false;
    std::array<bool,256> selected{};
    for(size_t c=0;c<payload[0];++c){const uint8_t id=payload[1+c*2],table=payload[2+c*2];
     if(selected[id]||components[id]<0||!quant[static_cast<size_t>(components[id])]||(table>>4)>3||(table&15)>3)return false;
     selected[id]=true;if((first==0&&!dc[table>>4])||(last>0&&!ac[table&15]))return false;
    }
    out.saw_scan=true;
   }
   if((m.marker>=0xc0&&m.marker<=0xcf)&&m.marker!=0xc4&&m.marker!=0xc8&&m.marker!=0xcc){
    if(sof||payload.size()<6||payload[0]!=8||payload[5]==0||payload.size()!=6+static_cast<size_t>(payload[5])*3)return false;
    if(m.marker!=0xc0&&m.marker!=0xc1&&m.marker!=0xc2){failure=jpeg_observation_failure::unsupported;return false;}
    out.height=(payload[1]<<8)|payload[2];out.width=(payload[3]<<8)|payload[4];if(!out.width||!out.height||payload[5]>4)return false;
    for(size_t c=0;c<payload[5];++c){const uint8_t id=payload[6+c*3],sampling=payload[7+c*3],q=payload[8+c*3];
     if(components[id]!=-1||q>3||!(sampling>>4)||!(sampling&15)||(sampling>>4)>4||(sampling&15)>4)return false;components[id]=q;
    }
    frame_marker=m.marker;sof=true;
   }
   constexpr char xmp[]="http://ns.adobe.com/xap/1.0/";
   constexpr char extended[]="http://ns.adobe.com/xmp/extension/";
   if(m.marker==0xe1){
    out.metadata_after_scan=out.metadata_after_scan||m.after_scan;
    if(payload.size()>=sizeof(xmp)&&!std::memcmp(payload.data(),xmp,sizeof(xmp))){
     if(++out.standard_packets!=1)return false;out.xmp_offset=m.payload+sizeof(xmp);
     out.xmp.assign(reinterpret_cast<const char*>(payload.data()+sizeof(xmp)),payload.size()-sizeof(xmp));
    }else if(payload.size()>=sizeof(extended)&&!std::memcmp(payload.data(),extended,sizeof(extended))){
     if(payload.size()<sizeof(extended)+40)return false;out.extended=true;
     if(options.allow_extended_xmp){
      const size_t head=sizeof(extended);std::string guid(reinterpret_cast<const char*>(payload.data()+head),32);
      for(char c:guid)if(!((c>='0'&&c<='9')||(c>='A'&&c<='F')))return false;
      const auto be32=[&](size_t at){return (uint32_t(payload[at])<<24)|(uint32_t(payload[at+1])<<16)|(uint32_t(payload[at+2])<<8)|payload[at+3];};
      const uint32_t total=be32(head+32),offset=be32(head+36);const size_t length=payload.size()-head-40;
      if(!total||!length||offset>total||length>total-offset)return false;
      if(chunks.empty()){out.extended_guid=guid;extension_total=total;}
      else if(guid!=out.extended_guid||total!=extension_total)return false;
      chunks.push_back({{m.start,m.payload+head+40,offset,static_cast<uint32_t>(length)},std::vector<uint8_t>(payload.begin()+head+40,payload.end())});
     }
    }else if(payload.size()>=6&&!std::memcmp(payload.data(),"Exif\0\0",6)){
     if(++exif!=1||!observe_exif(payload,out.orientation,out.exif,failure,options.allow_opaque_exif,options.allow_unknown_orientation))return false;
     failure=jpeg_observation_failure::malformed;
     out.exif_payload=payload;out.exif_marker_start=m.start;out.exif_segment_end=m.payload+m.length;
    }
   }
   if(m.marker==0xe2&&payload.size()>=3&&!std::memcmp(payload.data(),"MPF",3)){
    if(out.mpf)return false;out.mpf=true;
    jpeg_gainmap_mpf::relationship index{};bool indexed=false;
    if(!jpeg_gainmap_mpf::detail::parse_mpf_segment(payload,0,payload.size(),index,indexed))return false;
    out.mpf_index=indexed;
    if(indexed&&index.image_count==1){
     out.singleton_mpf=true;out.mpf_primary_size=index.entries[0].size;
    }
   }
   return true;
  });
  if(!valid||!sof||!out.saw_scan)return false;
  if((out.extended&&!options.allow_extended_xmp)||(out.mpf&&!options.allow_mpf)){failure=jpeg_observation_failure::unsupported;return false;}
  if(out.extended){
   if(out.standard_packets!=1)return false;
   std::sort(chunks.begin(),chunks.end(),[](const auto& a,const auto& b){return a.facts.offset<b.facts.offset;});
   uint64_t covered=0;for(const auto& chunk:chunks){if(chunk.facts.offset!=covered)return false;covered+=chunk.bytes.size();}
   if(covered!=extension_total)return false;
   // Allocate only after exact physical coverage proves the advertised size.
   out.extended_xmp.reserve(extension_total);
   for(const auto& chunk:chunks){out.extended_xmp.append(reinterpret_cast<const char*>(chunk.bytes.data()),chunk.bytes.size());out.extended_chunks.push_back(chunk.facts);}
   if(lpb::md5_hex_upper(std::span<const uint8_t>(reinterpret_cast<const uint8_t*>(out.extended_xmp.data()),out.extended_xmp.size()))!=out.extended_guid)return false;
  }
  failure=jpeg_observation_failure::none;bad=UINT64_MAX;return true;
 }catch(...){return false;}
}
}
