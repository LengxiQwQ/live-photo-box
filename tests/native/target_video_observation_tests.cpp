#include "media/target_video_observation.h"
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <vector>
namespace {
using bytes=std::vector<uint8_t>;
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
void put(bytes& b,uint32_t n){for(int i=3;i>=0;--i)b.push_back(static_cast<uint8_t>(n>>(i*8)));}
bytes atom(const char* type,bytes body={}){bytes b;put(b,static_cast<uint32_t>(body.size()+8));b.insert(b.end(),type,type+4);b.insert(b.end(),body.begin(),body.end());return b;}
void append(bytes& into,const bytes& value){into.insert(into.end(),value.begin(),value.end());}
bytes txt(const char* value){return bytes(value,value+std::strlen(value));}
bytes mdta(const char* key,bool duplicate=false){
 bytes h(8);append(h,txt("mdta"));h.resize(24);bytes k(4);put(k,duplicate?2:1);
 bytes entry;put(entry,static_cast<uint32_t>(8+std::strlen(key)));append(entry,txt("mdta"));append(entry,txt(key));append(k,entry);if(duplicate)append(k,entry);
 bytes value(8);value[3]=1;append(value,txt("ordinary literal"));
 bytes item=atom("data",value);bytes indexed;put(indexed,static_cast<uint32_t>(item.size()+8));put(indexed,1);append(indexed,item);
 bytes m(4);append(m,atom("hdlr",h));append(m,atom("keys",k));append(m,atom("ilst",indexed));return atom("meta",m);
}
bool check(bytes b,lpb::media::target_video_failure expected){
 lpb::memory_random_access memory{b};lpb::media::target_video_diagnostic d;
 const bool ok=lpb::media::observe_target_video_metadata(memory.view(),d);
 if(d.failure!=expected){std::cerr<<"reason="<<d.reason<<" actual="<<static_cast<int>(d.failure)<<'\n';return false;}
 return ok==(expected==lpb::media::target_video_failure::none);
}
}
int main(){
 using F=lpb::media::target_video_failure;
 try {
  require(check(atom("moov",atom("udta")),F::none),"empty ordinary udta");
  auto opaque_top=atom("moov");append(opaque_top,atom("comm",{1,2,3,4}));
  require(check(opaque_top,F::none),"unknown bounded vendor data remains opaque");
  for(const char* binding:{"mpvd","sefd"}) {
   auto source=atom("moov");append(source,atom(binding,{1,2,3,4}));
   require(check(source,F::source_binding),"known source live top-level box still rejects");
  }
  auto orphan=atom("moov");append(orphan,atom("keys",bytes(8)));
  require(check(orphan,F::malformed),"top-level keys do not gain metadata authority");
  auto ordinary_top=atom("moov");append(ordinary_top,mdta("comment"));
  require(check(ordinary_top,F::none),"ordinary top-level metadata is structurally observed");
  for(const char* key:{"com.apple.quicktime.content.identifier","com.huawei.movingphoto"}){
   auto direct=atom("moov");append(direct,mdta(key));
   require(check(direct,F::source_binding),"top-level meta cannot hide source live key");
   auto nested=atom("moov");append(nested,atom("udta",mdta(key)));
   require(check(nested,F::source_binding),"top-level udta cannot hide source live key");
  }
  auto duplicate_top=ordinary_top;append(duplicate_top,mdta("copyright"));
  require(check(duplicate_top,F::ambiguous),"duplicate top-level metadata owner");
  auto shadow_top=atom("moov");append(shadow_top,mdta("comment",true));
  require(check(shadow_top,F::ambiguous),"top-level duplicate mdta key");
  bytes empty_handler(8);append(empty_handler,txt("mdir"));append(empty_handler,txt("appl"));empty_handler.resize(25);
  bytes empty_meta(4);append(empty_meta,atom("hdlr",empty_handler));append(empty_meta,atom("ilst"));
  require(check(atom("moov",atom("udta",atom("meta",empty_meta))),F::none),"ordinary empty mdir owner");
  empty_handler[12]='x';empty_meta=bytes(4);append(empty_meta,atom("hdlr",empty_handler));append(empty_meta,atom("ilst"));
  require(check(atom("moov",atom("udta",atom("meta",empty_meta))),F::unsupported),"unknown empty owner is not mdta authority");
  require(check(atom("moov",mdta("com.apple.quicktime.copyright")),F::none),"ordinary copyright key");
  require(check(atom("moov",mdta("com.apple.quicktime.comment")),F::none),"ordinary comment key");
  for(const char* key:{"com.android.version","com.android.manufacturer","com.android.model","com.android.build","com.android.capture.fps","com.android.video.temporal_layers_count"})
   require(check(atom("moov",mdta(key)),F::none),"ordinary Android recording metadata");
  for(const char* key:{"com.apple.quicktime.content.identifier","com.apple.quicktime.still-image-time","com.apple.quicktime.live-photo-info","com.openharmony.covertime","com.openharmony.timed_metadata.movingphoto","com.huawei.movingphoto"})
   require(check(atom("moov",mdta(key)),F::source_binding),"known source binding key");
  require(check(atom("moov",mdta("com.example.unknown")),F::none),"N9 unknown owned mdta stays opaque");
  require(check(atom("moov",mdta("com.xiaomi.product.marketname")),F::none),"ordinary vendor model name stays opaque");
  require(check(atom("moov",mdta("copyright",true)),F::ambiguous),"duplicate metadata key");
  bytes two=mdta("copyright");append(two,mdta("comment"));require(check(atom("moov",two),F::ambiguous),"shadow meta");
  bytes comment{0,3,0,0,'a','b','c'};require(check(atom("moov",atom("udta",atom("\xa9" "cmt",comment))),F::none),"ordinary text atom");
  comment[1]=4;require(check(atom("moov",atom("udta",atom("\xa9" "cmt",comment))),F::malformed),"truncated ordinary text");
  bytes movie=atom("moov");bytes vivo=txt("vivoMediaExtInfo");vivo.resize(16);append(movie,atom("uuid",vivo));
  require(check(movie,F::source_binding),"actual UUID usertype binding");
  movie=atom("moov");append(movie,atom("uuid",bytes(16,0)));require(check(movie,F::none),"N9 unknown UUID stays opaque");
  bytes spherical{0xff,0xcc,0x82,0x63,0xf8,0x55,0x4a,0x93,0x88,0x14,0x58,0x7a,0x02,0x52,0x1f,0xdd};
  require(check(atom("moov",atom("trak",atom("uuid",spherical))),F::none),"ordinary track-owned spherical UUID");
  bytes duplicate_uuid=atom("uuid",spherical);append(duplicate_uuid,atom("uuid",spherical));
  require(check(atom("moov",atom("trak",duplicate_uuid)),F::ambiguous),"duplicate UUID ownership");
  movie=atom("moov");append(movie,atom("uuid",spherical));require(check(movie,F::unsupported),"registered spherical UUID wrong owner");
  bytes handler(8);append(handler,txt("mdta"));handler.resize(24);
  require(check(atom("moov",atom("trak",atom("mdia",atom("hdlr",handler)))),F::unsupported),"metadata track not silently neutral");
  require(check(atom("moov",atom("keys",bytes(8))),F::malformed),"shadow keys outside owner");
  bytes tkhd(16);tkhd[15]=1;bytes ref;put(ref,1);bytes tr=atom("tkhd",tkhd);append(tr,atom("tref",atom("chap",ref)));
  require(check(atom("moov",atom("trak",tr)),F::none),"ordinary existing track reference");
  ref.clear();put(ref,2);tr=atom("tkhd",tkhd);append(tr,atom("tref",atom("chap",ref)));
  require(check(atom("moov",atom("trak",tr)),F::malformed),"dangling track relationship");
  bytes damaged=atom("moov",atom("udta"));damaged.pop_back();require(check(damaged,F::malformed),"truncated box graph");
  std::cout<<"Target video metadata observation cases passed\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
