#pragma once
#include "containers/isobmff.h"
#include <span>
#include <string>
#include <string_view>
#include <vector>
#include <set>
#include <array>
#include <algorithm>
#include <cstring>

namespace lpb::media {
struct vivo_json_member { std::string key,value;size_t begin{},end{},value_begin{};bool string_value{}; };
struct vivo_modern_inventory {
 size_t box_begin{},box_end{},json_begin{},json_end{},album{},record{};
 std::vector<vivo_json_member> members;
 std::string pairing;
 bool binding{},found{};
};
namespace vivo_json_detail {
inline uint32_t be32(const uint8_t* p){return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3];}
struct parser {
 std::span<const uint8_t> b;size_t p{},limit{};unsigned depth{};std::vector<vivo_json_member> root;
 void ws(){while(p<limit&&(b[p]==' '||b[p]=='\n'||b[p]=='\r'||b[p]=='\t'))++p;}
 bool take(uint8_t c){ws();if(p==limit||b[p]!=c)return false;++p;return true;}
 static void utf8(std::string& out,uint32_t n){
  if(n<0x80)out.push_back(char(n));else if(n<0x800){out.push_back(char(0xc0|(n>>6)));out.push_back(char(0x80|(n&63)));}
  else if(n<0x10000){out.push_back(char(0xe0|(n>>12)));out.push_back(char(0x80|((n>>6)&63)));out.push_back(char(0x80|(n&63)));}
  else {out.push_back(char(0xf0|(n>>18)));out.push_back(char(0x80|((n>>12)&63)));out.push_back(char(0x80|((n>>6)&63)));out.push_back(char(0x80|(n&63)));}
 }
 bool hex(uint32_t& n){n=0;for(unsigned i=0;i<4;++i){if(p==limit)return false;const uint8_t c=b[p++];const int v=c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:c>='A'&&c<='F'?c-'A'+10:-1;if(v<0)return false;n=(n<<4)|v;}return true;}
 bool string(std::string& out){
  out.clear();if(!take('"'))return false;
  while(p<limit){const uint8_t c=b[p++];if(c=='"')return true;if(c<0x20)return false;
   if(c=='\\'){
    if(p==limit)return false;const uint8_t e=b[p++];
    if(e=='"'||e=='\\'||e=='/')out.push_back(char(e));
    else if(e=='b'||e=='f'||e=='n'||e=='r'||e=='t')out.push_back(e=='b'?'\b':e=='f'?'\f':e=='n'?'\n':e=='r'?'\r':'\t');
    else if(e=='u'){uint32_t n;if(!hex(n))return false;if(n>=0xd800&&n<=0xdbff){if(limit-p<6||b[p++]!='\\'||b[p++]!='u')return false;uint32_t low;if(!hex(low)||low<0xdc00||low>0xdfff)return false;n=0x10000+((n-0xd800)<<10)+(low-0xdc00);}else if(n>=0xdc00&&n<=0xdfff)return false;utf8(out,n);}
    else return false;
   }else if(c<0x80)out.push_back(char(c));
   else {unsigned n=c>=0xc2&&c<=0xdf?1:c>=0xe0&&c<=0xef?2:c>=0xf0&&c<=0xf4?3:0;if(!n||n>limit-p)return false;uint32_t scalar=c&((1u<<(6-n))-1);for(unsigned i=0;i<n;++i){const auto v=b[p++];if((v&0xc0)!=0x80)return false;scalar=(scalar<<6)|(v&63);}if((n==1&&scalar<0x80)||(n==2&&scalar<0x800)||(n==3&&scalar<0x10000)||scalar>0x10ffff||(scalar>=0xd800&&scalar<=0xdfff))return false;utf8(out,scalar);}
  }return false;
 }
 bool value(std::string* text=nullptr,bool* is_string=nullptr){
  ws();if(p==limit||depth>=32)return false;
  if(b[p]=='"'){std::string v;if(!string(v))return false;if(text)*text=std::move(v);if(is_string)*is_string=true;return true;}
  if(b[p]=='{')return object(false);
  if(b[p]=='['){++p;++depth;ws();if(p<limit&&b[p]==']'){++p;--depth;return true;}for(;;){if(!value())return false;ws();if(p==limit)return false;if(b[p++]==']'){--depth;return true;}if(b[p-1]!=',')return false;}}
  for(std::string_view literal:{"true","false","null"})if(limit-p>=literal.size()&&!std::memcmp(b.data()+p,literal.data(),literal.size())){p+=literal.size();return true;}
  const size_t start=p;if(b[p]=='-')++p;if(p==limit)return false;
  if(b[p]=='0')++p;else {if(b[p]<'1'||b[p]>'9')return false;while(p<limit&&b[p]>='0'&&b[p]<='9')++p;}
  if(p<limit&&b[p]=='.'){++p;const auto a=p;while(p<limit&&b[p]>='0'&&b[p]<='9')++p;if(p==a)return false;}
  if(p<limit&&(b[p]=='e'||b[p]=='E')){++p;if(p<limit&&(b[p]=='+'||b[p]=='-'))++p;const auto a=p;while(p<limit&&b[p]>='0'&&b[p]<='9')++p;if(p==a)return false;}
  return p>start;
 }
 bool object(bool capture){
  if(!take('{')||depth>=32)return false;++depth;std::set<std::string> names;ws();if(p<limit&&b[p]=='}'){++p;--depth;return true;}
  for(;;){ws();vivo_json_member member;member.begin=p;if(!string(member.key)||!names.insert(member.key).second||!take(':'))return false;
   if(!capture&&(member.key=="com.android.camera.livephoto"||member.key=="com.android.camera.imageTime"||member.key=="com.android.camera.livePhoto.firstFrame"))return false;
   ws();member.value_begin=p;if(!value(&member.value,&member.string_value))return false;member.end=p;if(capture)root.push_back(std::move(member));ws();if(p==limit)return false;if(b[p++]=='}'){--depth;return true;}if(b[p-1]!=',')return false;}
 }
};
inline bool live_key(std::string_view key){return key=="com.android.camera.livephoto"||key=="com.android.camera.imageTime"||key=="com.android.camera.livePhoto.firstFrame";}
}
// Observes only legal top-level UUID ownership, never arbitrary string hits.
inline bool observe_vivo_modern(std::span<const uint8_t> movie,vivo_modern_inventory& out) {
 out={};
 for(size_t p=0;p<movie.size();) {
  isobmff_box_header h{};if(!try_read_box_header(movie.data(),p,movie.size(),h)||h.extends_to_end)return false;
  if(!std::memcmp(movie.data()+p+4,"uuid",4)&&h.size>=h.header_size+16&&
     !std::memcmp(movie.data()+p+h.header_size,"vivoMediaExtInfo",16)) {
   if(out.found||h.size>1024*1024)return false;out.found=true;out.box_begin=p;out.box_end=p+h.size;
   const size_t prefix=p+h.header_size+16;if(out.box_end-prefix<4||std::memcmp(movie.data()+prefix,"vivo",4))return false;
   out.json_begin=prefix+4;vivo_json_detail::parser json{movie,out.json_begin,out.box_end};if(!json.object(true))return false;out.json_end=json.p;out.members=std::move(json.root);
   const size_t count=out.json_end-out.json_begin;if(out.box_end-out.json_end<4+11+19||vivo_json_detail::be32(movie.data()+out.json_end)!=count)return false;
   out.album=out.json_end+4;if(std::memcmp(movie.data()+out.album,"cameralbum!",11))return false;out.record=out.album+11;
   const uint32_t record=vivo_json_detail::be32(movie.data()+out.record);if(record<19||record!=out.box_end-out.record)return false;
   const size_t id_size=record-19;const auto id=movie.subspan(out.record+4,id_size);
   constexpr std::array<uint8_t,15> tail{255,255,255,255,0x1b,0x2a,0x39,0x48,0x57,0x66,0x75,0x84,0x93,0xa2,0xb3};
   if(!std::equal(tail.begin(),tail.end(),movie.begin()+static_cast<ptrdiff_t>(out.box_end-tail.size())))return false;
   bool pair=false;
   for(const auto& m:out.members)if(vivo_json_detail::live_key(m.key)){
    out.binding=true;
    if(m.key=="com.android.camera.livephoto"){if(!m.string_value||m.value.empty()||m.value.size()>127)return false;out.pairing=m.value;pair=true;}
    else {
     if(m.string_value||m.value_begin==m.end)return false;uint64_t value=0;
     for(size_t i=m.value_begin;i<m.end;++i){const auto c=movie[i];if(c<'0'||c>'9'||value>(UINT64_MAX-(c-'0'))/10)return false;value=value*10+c-'0';}
    }
   }
   if(out.binding&&(!pair||id.size()!=out.pairing.size()||!std::equal(id.begin(),id.end(),out.pairing.begin())))return false;
   if(!out.binding&&id_size)return false;
  }
  p+=h.size;
 }
 return true;
}
inline bool rebuild_vivo_neutral_movie(std::span<const uint8_t> movie,const vivo_modern_inventory& facts,std::vector<uint8_t>& output) {
 output.clear();vivo_modern_inventory actual;
 if(!facts.found||!facts.binding||facts.box_end!=movie.size()||!observe_vivo_modern(movie,actual)||!actual.found||!actual.binding||actual.box_begin!=facts.box_begin||actual.box_end!=facts.box_end)return false;
 std::vector<uint8_t> replacement(movie.begin()+facts.box_begin,movie.begin()+facts.json_begin);replacement.push_back('{');bool first=true;
 for(const auto& m:actual.members)if(!vivo_json_detail::live_key(m.key)){if(!first)replacement.push_back(',');first=false;replacement.insert(replacement.end(),movie.begin()+m.begin,movie.begin()+m.end);}
 replacement.push_back('}');const size_t length=replacement.size()-(facts.json_begin-facts.box_begin);
 const auto put=[&](uint32_t n){for(int i=3;i>=0;--i)replacement.push_back(uint8_t(n>>(8*i)));};
 put(static_cast<uint32_t>(length));replacement.insert(replacement.end(),movie.begin()+facts.album,movie.begin()+facts.album+11);put(19);replacement.insert(replacement.end(),movie.begin()+facts.box_end-15,movie.begin()+facts.box_end);
 // Source variants with an extended UUID header require a separately modelled rewrite.
 if(vivo_json_detail::be32(movie.data()+facts.box_begin)==1||replacement.size()>UINT32_MAX)return false;
 for(size_t i=0;i<4;++i)replacement[i]=uint8_t(replacement.size()>>(8*(3-i)));
 output.insert(output.end(),movie.begin(),movie.begin()+facts.box_begin);output.insert(output.end(),replacement.begin(),replacement.end());output.insert(output.end(),movie.begin()+facts.box_end,movie.end());
 vivo_modern_inventory checked;return observe_vivo_modern(output,checked)&&checked.found&&!checked.binding;
}
}
