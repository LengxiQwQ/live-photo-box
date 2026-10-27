#include "protocols/samsung_inventory.h"
#include <array>
#include <cstring>
#include <iostream>
#undef NDEBUG
#include <cassert>
using namespace lpb::protocols;
namespace {
void u32(std::vector<uint8_t>& b,uint32_t n){for(unsigned i=0;i<4;++i)b.push_back(static_cast<uint8_t>(n>>(i*8)));}
void put(std::vector<uint8_t>& b,size_t at,uint32_t n){for(unsigned i=0;i<4;++i)b[at+i]=static_cast<uint8_t>(n>>(i*8));}
std::vector<uint8_t> fixture(bool live) {
 std::vector<uint8_t> b(17,0x91);std::vector<std::array<uint32_t,3>> tags;
 const auto tag=[&](uint16_t marker,const char* name,const char* value) {
  const size_t at=b.size();b.insert(b.end(),{0,0,static_cast<uint8_t>(marker),static_cast<uint8_t>(marker>>8)});
  u32(b,static_cast<uint32_t>(std::strlen(name)));b.insert(b.end(),name,name+std::strlen(name));b.insert(b.end(),value,value+std::strlen(value));
  tags.push_back({marker,static_cast<uint32_t>(at),static_cast<uint32_t>(b.size()-at)});
 };
 tag(0xcc1,"Color_Display_P3","opaque\x01");
 if(live){tag(0xa30,"MotionPhoto_Data","movie");tag(0xa31,"MotionPhoto_Version","mpv3");}
 const auto table=static_cast<uint32_t>(b.size());b.insert(b.end(),{'S','E','F','H'});u32(b,107);u32(b,static_cast<uint32_t>(tags.size()));
 for(const auto& t:tags){b.insert(b.end(),{0,0,static_cast<uint8_t>(t[0]),static_cast<uint8_t>(t[0]>>8)});u32(b,table-t[1]);u32(b,t[2]);}
 u32(b,static_cast<uint32_t>(12+tags.size()*12));b.insert(b.end(),{'S','E','F','T'});return b;
}
bool observe(const std::vector<uint8_t>& b,sef_inventory& inventory){lpb::memory_random_access memory{b};std::string error;return observe_sef(memory.view(),17,b.size(),inventory,error);}
}
int main() {
 auto b=fixture(true);sef_inventory inventory;assert(observe(b,inventory));
 assert(inventory.begin==17&&inventory.version==107&&inventory.tags.size()==3&&!ordinary_sef(inventory));
 lpb::memory_random_access memory{b};uint32_t version=0;assert(read_sef_version(memory.view(),inventory,version)&&version==3);
 std::vector<uint8_t> retained;assert(retain_ordinary_sef(b,inventory,retained));
 auto expected=fixture(false);assert(std::vector<uint8_t>(expected.begin()+17,expected.end())==retained);
 const size_t table=static_cast<size_t>(inventory.table);
 for(unsigned which=0;which<11;++which) {
  auto bad=b;
  switch(which) {
   case 0:bad.back()^=1;break;
   case 1:put(bad,bad.size()-8,static_cast<uint32_t>(bad.size()));break;
   case 2:put(bad,table+4,106);break;
   case 3:put(bad,table+8,4097);break;
   case 4:put(bad,table+16,UINT32_MAX);break;
   case 5:put(bad,table+20,UINT32_MAX);break;
   case 6:bad[17]=1;break;
   case 7:put(bad,21,UINT32_MAX);break;
   case 8:bad[table+26]=bad[table+14];bad[table+27]=bad[table+15];break;
   case 9:put(bad,table+28,static_cast<uint32_t>(table-17));break;
   case 10:bad[static_cast<size_t>(inventory.find(0xa30)->offset)+8]^=1;break;
  }
  sef_inventory rejected;assert(!observe(bad,rejected));
 }
 std::cout<<"Samsung strict12B/backward-index/owner/name/version/overlap/ordinary-retention checks passed\n";
}
