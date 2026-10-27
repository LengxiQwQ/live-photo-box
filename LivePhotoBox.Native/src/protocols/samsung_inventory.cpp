#include "protocols/samsung_inventory.h"
#include <algorithm>
#include <array>
#include <cstring>
#include <set>

namespace lpb::protocols {
namespace {
uint16_t le16(const uint8_t* p) {return static_cast<uint16_t>(p[0])|static_cast<uint16_t>(p[1]<<8);}
uint32_t le32(const uint8_t* p) {return uint32_t(p[0])|(uint32_t(p[1])<<8)|(uint32_t(p[2])<<16)|(uint32_t(p[3])<<24);}
}
const sef_tag* sef_inventory::find(uint16_t marker) const noexcept {
 for(const auto& tag:tags)if(tag.marker==marker)return &tag;
 return nullptr;
}
bool ordinary_sef(const sef_inventory& inventory) noexcept {
 return !inventory.find(0x0a30)&&!inventory.find(0x0a31);
}
bool retain_ordinary_sef(std::span<const uint8_t> input,const sef_inventory& inventory,std::vector<uint8_t>& output) noexcept {
 output.clear();
 try {
  std::vector<const sef_tag*> tags;
  for(const auto& tag:inventory.tags)if(tag.marker!=0x0a30&&tag.marker!=0x0a31)tags.push_back(&tag);
  if(tags.empty())return true;
  std::sort(tags.begin(),tags.end(),[](const auto* a,const auto* b){return a->offset<b->offset;});
  std::vector<uint32_t> offsets;
  for(const auto* tag:tags) {
   if(tag->offset>input.size()||tag->size>input.size()-tag->offset||output.size()>UINT32_MAX-tag->size)return false;
   offsets.push_back(static_cast<uint32_t>(output.size()));
   output.insert(output.end(),input.begin()+static_cast<ptrdiff_t>(tag->offset),input.begin()+static_cast<ptrdiff_t>(tag->offset+tag->size));
  }
  const uint32_t table=static_cast<uint32_t>(output.size());
  const auto u32=[&](uint32_t value){for(unsigned j=0;j<4;++j)output.push_back(static_cast<uint8_t>(value>>(j*8)));};
  output.insert(output.end(),{'S','E','F','H'});u32(107);u32(static_cast<uint32_t>(tags.size()));
  for(size_t i=0;i<tags.size();++i) {
   output.insert(output.end(),{0,0,static_cast<uint8_t>(tags[i]->marker),static_cast<uint8_t>(tags[i]->marker>>8)});
   u32(table-offsets[i]);u32(static_cast<uint32_t>(tags[i]->size));
  }
  u32(static_cast<uint32_t>(12+tags.size()*12));output.insert(output.end(),{'S','E','F','T'});return true;
 }catch(...){output.clear();return false;}
}
bool read_sef_version(const random_access_reader& reader,const sef_inventory& inventory,uint32_t& version) noexcept {
 version=0;const auto* tag=inventory.find(0x0a31);std::array<uint8_t,4> bytes{};
 if(!tag||tag->name!="MotionPhoto_Version"||tag->data_size!=4||!reader.read_exact(tag->data_offset,bytes))return false;
 if(!std::memcmp(bytes.data(),"mpv2",4))version=2;
 if(!std::memcmp(bytes.data(),"mpv3",4))version=3;
 return version!=0;
}
bool observe_sef(const random_access_reader& reader,uint64_t lower,uint64_t end,sef_inventory& output,std::string& error) noexcept {
 output={};error.clear();
 try {
  const auto reject=[&](const char* message){error=message;return false;};
  if(lower>end||end>reader.length||end-lower<20)return reject("SEF extent is too short or outside owner");
  std::array<uint8_t,12> bytes{};
  if(!reader.read_exact(end-8,std::span<uint8_t>(bytes.data(),8))||std::memcmp(bytes.data()+4,"SEFT",4))return reject("SEF final footer is missing");
  const uint64_t length=le32(bytes.data());
  if(length<12||length>end-lower-8)return reject("SEF footer length exceeds owner");
  const uint64_t table=end-8-length;
  if(!reader.read_exact(table,bytes)||std::memcmp(bytes.data(),"SEFH",4))return reject("SEFH is not at footer-declared table start");
  const uint32_t version=le32(bytes.data()+4),count=le32(bytes.data()+8);
  if(version!=107||!count||count>4096||length!=12ULL+uint64_t(count)*12)return reject("SEF version/count/12-byte table width mismatch");
  sef_inventory found;found.table=table;found.end=end;found.version=version;found.begin=table;
  std::set<uint16_t> markers;std::set<std::string> names;
  for(uint32_t i=0;i<count;++i) {
   if(!reader.read_exact(table+12+uint64_t(i)*12,bytes))return reject("SEF index read failed");
   const uint16_t prefix=le16(bytes.data()),marker=le16(bytes.data()+2);
   const uint64_t back=le32(bytes.data()+4),size=le32(bytes.data()+8);
   if(prefix||!marker||!markers.insert(marker).second||size<9||back>table-lower||size>back)return reject("SEF duplicate marker/prefix or entry outside owner");
   const uint64_t at=table-back;
   if(!reader.read_exact(at,std::span<uint8_t>(bytes.data(),8))||le16(bytes.data())!=prefix||le16(bytes.data()+2)!=marker)return reject("SEF indexed tag header mismatch");
   const uint32_t name_length=le32(bytes.data()+4);
   if(!name_length||name_length>1024||name_length>size-8)return reject("SEF name exceeds owned tag");
   std::string name(name_length,'\0');
   if(!reader.read_exact(at+8,std::span<uint8_t>(reinterpret_cast<uint8_t*>(name.data()),name.size())))return reject("SEF name read failed");
   if(name.back()=='\0')name.pop_back();
   if(name.empty()||name.find('\0')!=std::string::npos||!names.insert(name).second)return reject("SEF duplicate/shadow name");
   if((marker==0x0a30)!=(name=="MotionPhoto_Data")||(marker==0x0a31)!=(name=="MotionPhoto_Version"))return reject("SEF live marker/name identity mismatch");
   found.tags.push_back({marker,at,size,at+8+name_length,size-8-name_length,std::move(name)});
   found.begin=std::min(found.begin,at);
  }
  auto ordered=found.tags;std::sort(ordered.begin(),ordered.end(),[](const auto& a,const auto& b){return a.offset<b.offset;});
  uint64_t cursor=found.begin;
  for(const auto& tag:ordered){if(tag.offset!=cursor)return reject("SEF tags overlap or leave unowned gaps");cursor+=tag.size;}
  if(cursor!=table)return reject("SEF payload inventory does not end at SEFH");
  output=std::move(found);return true;
 }catch(...){output={};error="SEF inventory observation failed";return false;}
}
}
