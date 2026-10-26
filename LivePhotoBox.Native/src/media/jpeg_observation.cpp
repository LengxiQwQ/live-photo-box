#include "media/jpeg_observation.h"
#include "media/jpeg_marker.h"
#include "metadata/exif.h"
#include <cstring>
#include <set>
namespace lpb::media {
bool observe_plain_exif(std::span<const uint8_t> bytes,uint32_t& orientation,jpeg_observation_failure& failure) noexcept {
 failure=jpeg_observation_failure::malformed;
 try {
  if(bytes.size()<14||std::memcmp(bytes.data(),"Exif\0\0",6))return false;
  const size_t tiff=6,end=bytes.size();const bool big=bytes[tiff]=='M'&&bytes[tiff+1]=='M';
  if(!big&&!(bytes[tiff]=='I'&&bytes[tiff+1]=='I'))return false;
  const auto u16=[&](size_t at){return big?static_cast<uint16_t>((bytes[at]<<8)|bytes[at+1]):static_cast<uint16_t>(bytes[at]|(bytes[at+1]<<8));};
  const auto u32=[&](size_t at){return big?static_cast<uint32_t>((static_cast<uint32_t>(bytes[at])<<24)|(bytes[at+1]<<16)|(bytes[at+2]<<8)|bytes[at+3]):static_cast<uint32_t>(bytes[at]|(bytes[at+1]<<8)|(bytes[at+2]<<16)|(static_cast<uint32_t>(bytes[at+3])<<24));};
  if(u16(tiff+2)!=42||u32(tiff+4)<8)return false;
  std::vector<uint32_t> pending{u32(tiff+4)},seen;std::vector<std::pair<uint64_t,uint64_t>> occupied{{0,8}};
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
    if(!tags.insert(entry.tag).second)return false;
    if(entry.tag==0x927c||entry.tag==0x9286||entry.tag==0x014a||entry.tag==0xb000){failure=jpeg_observation_failure::unsupported;return false;}
    uint64_t width=0;switch(entry.type){case 1:case 2:case 6:case 7:width=1;break;case 3:case 8:width=2;break;case 4:case 9:case 11:width=4;break;case 5:case 10:case 12:width=8;break;default:failure=jpeg_observation_failure::unsupported;return false;}
    const uint64_t n=width*entry.count;if(!n||(n>4&&!claim(entry.value_offset,n)))return false;
    if(entry.tag==0x112){if(offset!=u32(tiff+4)||entry.type!=3||entry.count!=1)return false;orientation=u16(entry.absolute_pos+8);if(orientation<1||orientation>8)return false;}
    if(entry.tag==0x8769||entry.tag==0x8825||entry.tag==0xa005){if(entry.type!=4||entry.count!=1||entry.value_offset<8)return false;pending.push_back(entry.value_offset);}
   }
   pending.push_back(directory.next_ifd_offset);
  }
  failure=jpeg_observation_failure::none;return true;
 }catch(...){return false;}
}

bool observe_jpeg(const lpb::random_access_reader& source,jpeg_observation& out,
 jpeg_observation_failure& failure,uint64_t& bad) noexcept {
 out={};failure=jpeg_observation_failure::malformed;bad=0;
 try {
  bool sof=false;unsigned exif=0;uint8_t frame_marker=0;
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
    }else if(payload.size()>=6&&!std::memcmp(payload.data(),"Exif\0\0",6)){
     if(++exif!=1||!observe_plain_exif(payload,out.orientation,failure))return false;
    }
   }
   if(m.marker==0xe2&&payload.size()>=3&&!std::memcmp(payload.data(),"MPF",3))out.mpf=true;
   return true;
  });
  if(!valid||!sof||!out.saw_scan)return false;
  if(out.extended||out.mpf){failure=jpeg_observation_failure::unsupported;return false;}
  failure=jpeg_observation_failure::none;bad=UINT64_MAX;return true;
 }catch(...){return false;}
}
}
