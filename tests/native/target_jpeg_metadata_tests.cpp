#include "media/jpeg_observation.h"
#include "metadata/target_exif.h"
#include "foundation/md5_core.h"
#include <algorithm>
#include <iostream>
#include <stdexcept>
#include <string_view>
namespace {
using bytes=std::vector<uint8_t>;
#define REQUIRE(x) do{if(!(x))throw std::runtime_error(std::string("line ")+std::to_string(__LINE__)+": "+#x);}while(false)
void segment(bytes& jpeg,uint8_t marker,const bytes& payload){size_t n=payload.size()+2;jpeg.insert(jpeg.end(),{0xff,marker,uint8_t(n>>8),uint8_t(n)});jpeg.insert(jpeg.end(),payload.begin(),payload.end());}
bytes image(const std::vector<bytes>& app1={}){
 bytes b{0xff,0xd8};for(const auto& p:app1)segment(b,0xe1,p);
 bytes q(65,1);q[0]=0;segment(b,0xdb,q);
 bytes h(18,0);h[1]=1;segment(b,0xc4,h);h[0]=0x10;segment(b,0xc4,h);
 segment(b,0xc0,{8,0,1,0,1,1,1,0x11,0});segment(b,0xda,{1,1,0,0,63,0});b.insert(b.end(),{0x11,0xff,0xd9});return b;
}
bool observe(const bytes& b,lpb::media::jpeg_observation& out,bool permissive=true){
 lpb::memory_random_access m{b};lpb::media::jpeg_observation_failure f{};uint64_t bad{};
 return lpb::media::observe_jpeg(m.view(),out,f,bad,permissive?lpb::media::jpeg_observation_options{true,true,true}:lpb::media::jpeg_observation_options{});
}
void put16(bytes& p,size_t at,uint16_t n,bool big){p[at]=uint8_t(n>>(big?8:0));p[at+1]=uint8_t(n>>(big?0:8));}
void put32(bytes& p,size_t at,uint32_t n,bool big){for(size_t i=0;i<4;++i)p[at+i]=uint8_t(n>>(8*(big?3-i:i)));}
bytes exif(bool big,bool comment=false){
 bytes p(6+64,0);std::copy_n("Exif\0\0",6,p.begin());p[6]=p[7]=big?'M':'I';put16(p,8,42,big);put32(p,10,8,big);
 put16(p,14,1,big);put16(p,16,0x8769,big);put16(p,18,4,big);put32(p,20,1,big);put32(p,24,26,big);
 put16(p,32,1,big);put16(p,34,comment?0x9286:0x927c,big);put16(p,36,7,big);put32(p,38,8,big);put32(p,42,50,big);
 std::copy_n("opaque!!",8,p.begin()+56);return p;
}
bytes standard(){constexpr char prefix[]="http://ns.adobe.com/xap/1.0/";bytes p(prefix,prefix+sizeof(prefix));p.push_back('x');return p;}
bytes extension(std::string_view xml,std::string_view guid,uint32_t offset,uint32_t count){
 constexpr char prefix[]="http://ns.adobe.com/xmp/extension/";bytes p(prefix,prefix+sizeof(prefix));p.insert(p.end(),guid.begin(),guid.end());
 size_t at=p.size();p.resize(at+8);put32(p,at,static_cast<uint32_t>(xml.size()),true);put32(p,at+4,offset,true);p.insert(p.end(),xml.begin()+offset,xml.begin()+offset+count);return p;
}
std::string md5(std::string_view s){return lpb::md5_hex_upper({reinterpret_cast<const uint8_t*>(s.data()),s.size()});}
}
int main(){
 using namespace lpb::media;using lpb::metadata::add_target_user_comment;
 REQUIRE(md5("")=="D41D8CD98F00B204E9800998ECF8427E");REQUIRE(md5("abc")=="900150983CD24FB0D6963F7D28E17F72");
 REQUIRE(md5(std::string(1000,'a'))=="CABE45DCC9AE5B66BA86600CCA6B8BA8");
 jpeg_observation out;jpeg_observation_failure failure;bytes result;
 for(bool big:{false,true}){
  auto thumbnail=exif(big);
  // A next image IFD owns its own orientation; it does not override IFD0.
  thumbnail.resize(6+82,0);put32(thumbnail,28,64,big);
  put16(thumbnail,70,1,big);put16(thumbnail,72,0x112,big);put16(thumbnail,74,3,big);
  put32(thumbnail,76,1,big);put16(thumbnail,80,6,big);
  REQUIRE(observe(image({thumbnail}),out)&&out.orientation==1);
  put16(thumbnail,80,0,big);REQUIRE(!observe(image({thumbnail}),out));
 }
 for(bool big:{false,true}) {
  auto ascii=exif(big);put16(ascii,36,2,big);put32(ascii,38,2,big);ascii[42]='0';ascii[43]=0;
  REQUIRE(observe(image({ascii}),out)&&out.exif_payload==ascii);
  auto no_nul=ascii;no_nul[43]='X';REQUIRE(!observe(image({no_nul}),out));
  auto apple=exif(big);put16(apple,36,2,big);put32(apple,38,10,big);apple.resize(66);std::copy_n("Apple iOS",9,apple.begin()+56);apple[65]=0;
  REQUIRE(!observe(image({apple}),out));
 }
 const bytes marker{'o','p','l','u','s','_','1','0','4','8','5','7','9','2',0};
 for(bool big:{false,true}) {
  bytes vendor(6+128,0);std::copy_n("Exif\0\0",6,vendor.begin());vendor[6]=vendor[7]=big?'M':'I';
  put16(vendor,8,42,big);put32(vendor,10,8,big);put16(vendor,14,1,big);
  put16(vendor,16,0x8769,big);put16(vendor,18,4,big);put32(vendor,20,1,big);put32(vendor,24,26,big);
  put16(vendor,32,2,big);
  for(size_t i=0;i<2;++i){const size_t at=34+i*12;put16(vendor,at,0x927c,big);put16(vendor,at+2,7,big);put32(vendor,at+4,12,big);put32(vendor,at+8,static_cast<uint32_t>(80+i*20),big);}
  std::copy_n("HUAWEI_NOTE",11,vendor.begin()+86);std::copy_n("HONOR_NOTE_",11,vendor.begin()+106);
  REQUIRE(observe(image({vendor}),out)&&out.exif_payload==vendor);
  auto apple_shadow=vendor;std::copy_n("Apple iOS",9,apple_shadow.begin()+86);
  REQUIRE(!observe(image({apple_shadow}),out));
  auto overlapping=vendor;put32(overlapping,54,80,big);REQUIRE(!observe(image({overlapping}),out));
 }
 for(bool big:{false,true}){
  bytes unknown(32,0);std::copy_n("Exif\0\0",6,unknown.begin());unknown[6]=unknown[7]=big?'M':'I';
  put16(unknown,8,42,big);put32(unknown,10,8,big);put16(unknown,14,1,big);
  put16(unknown,16,0x112,big);put16(unknown,18,4,big);put32(unknown,20,1,big);
  const auto encoded=image({unknown});lpb::memory_random_access memory{encoded};uint64_t bad=0;
  REQUIRE(!observe(encoded,out));
  REQUIRE(observe_jpeg(memory.view(),out,failure,bad,{true,true,true,true})&&out.orientation==0);
  REQUIRE(out.exif_payload==unknown);
  // Ordinary malformed TIFF bounds are still rejected under the new policy.
  put32(unknown,20,100,big);
  const auto broken_bytes=image({unknown});lpb::memory_random_access broken_memory{broken_bytes};
  REQUIRE(!observe_jpeg(broken_memory.view(),out,failure,bad,{true,true,true,true}));
 }
 REQUIRE(observe(image(),out,false));REQUIRE(add_target_user_comment(image(),marker,result,failure));
 REQUIRE(observe(result,out)&&out.exif.user_comment==marker);
 for(bool big:{false,true}){
  const auto payload=exif(big);const auto jpeg=image({payload});REQUIRE(!observe(jpeg,out,false));REQUIRE(observe(jpeg,out));
  REQUIRE(out.exif.opaque_metadata&&out.exif.big_endian==big&&!out.exif.has_user_comment);
  REQUIRE(add_target_user_comment(jpeg,marker,result,failure));REQUIRE(observe(result,out)&&out.exif.user_comment==marker);
  // Original TIFF bytes, except only the four bytes redirecting ExifIFD, remain exact.
  for(size_t i=0;i<payload.size();++i)if(i<24||i>=28)REQUIRE(out.exif_payload[i]==payload[i]);
  const auto with_comment=result;REQUIRE(!add_target_user_comment(with_comment,marker,result,failure)&&result.empty());
  const auto conflict=image({exif(big,true)});REQUIRE(!add_target_user_comment(conflict,marker,result,failure)&&result.empty()&&failure==jpeg_observation_failure::unsupported);
  auto malformed=payload;put32(malformed,42,63,big);REQUIRE(!observe(image({malformed}),out));
  auto cycle=payload;put32(cycle,28,8,big);REQUIRE(!observe(image({cycle}),out));
  REQUIRE(!observe(image({payload,payload}),out));
  auto duplicate=payload;put16(duplicate,32,2,big);const bytes duplicate_entry(payload.begin()+34,payload.begin()+46);duplicate.insert(duplicate.begin()+46,duplicate_entry.begin(),duplicate_entry.end());REQUIRE(!observe(image({duplicate}),out));
 }
 const std::string xml="<extended>ordinary</extended>",guid=md5(xml);const auto first=extension(xml,guid,0,10),last=extension(xml,guid,10,static_cast<uint32_t>(xml.size()-10));
 const auto complete=image({standard(),last,first});REQUIRE(!observe(complete,out,false));REQUIRE(observe(complete,out));REQUIRE(out.extended_xmp==xml&&out.extended_guid==guid&&out.extended_chunks.size()==2);
 REQUIRE(!observe(image({standard(),first}),out));REQUIRE(!observe(image({standard(),first,first,last}),out));
 REQUIRE(!observe(image({last,first}),out));
 auto corrupt=last;corrupt.back()^=1;REQUIRE(!observe(image({standard(),first,corrupt}),out));
 auto bad_guid=guid;bad_guid[0]=bad_guid[0]=='A'?'B':'A';REQUIRE(!observe(image({standard(),extension(xml,bad_guid,0,static_cast<uint32_t>(xml.size()))}),out));
 auto mixed=last;mixed[sizeof("http://ns.adobe.com/xmp/extension/")]=bad_guid[0];REQUIRE(!observe(image({standard(),first,mixed}),out));
 std::cout<<"P9R3_JpegMetadata: opaque Exif append preservation, conflict/graph rejection, Extended XMP coverage/MD5 transport PASS\n";
}
