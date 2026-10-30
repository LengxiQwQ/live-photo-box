#include "media/target_video_observation.h"
#include "containers/remux_byte_proof.h"
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
  {
   bytes table(8);table[7]=1;put(table,2);put(table,0xfffffffdU);
   auto movie=atom("moov",atom("trak",atom("mdia",atom("minf",atom("stbl",atom("ctts",table))))));
   auto unchanged=movie;
   require(lpb::media::map_remux_composition_versions(movie,true,false),"QT signed-v0 to ISO signed-v1");
   // Six nested 8-byte headers: ctts fullbox version follows them.
   require(movie[48]==1,"ISO signed ctts v1 emitted");
   unchanged[48]=1;require(movie==unchanged,"only semantic representation version changes");
   movie[48]=0;require(!lpb::media::map_remux_composition_versions(movie,false,true),"unrepresentable ISO positive offset rejects");
   movie=unchanged;require(lpb::media::map_remux_composition_versions(movie,false,true)&&movie==unchanged,"signed v1 remains exact");
   movie=unchanged;movie[48]=0;movie[60]=0;movie[61]=0;movie[62]=0;movie[63]=3;
   auto positive=movie;
   require(lpb::media::map_remux_composition_versions(movie,true,false)&&movie==positive,"positive v0 stays exact across brands");
   movie=unchanged;movie[48]=2;
   require(!lpb::media::map_remux_composition_versions(movie,true,false),"unknown ctts version rejected");
   movie=unchanged;movie[49]=1;
   require(!lpb::media::map_remux_composition_versions(movie,true,false),"ctts reserved flags rejected");
   movie=unchanged;movie[55]=2;
   require(!lpb::media::map_remux_composition_versions(movie,true,false),"ctts entry count beyond table rejected");
   movie=unchanged;movie[55]=0;
   require(!lpb::media::map_remux_composition_versions(movie,true,false),"empty ctts table rejected");
  }
  {
   auto ftyp=atom("ftyp",txt("isom0000")), movie=atom("moov",atom("free",{1,2,3}));
   auto source=ftyp;append(source,movie);append(source,atom("mdat",{10,20,30,40}));append(source,atom("free",{9}));
   auto target=source;
   auto proof=[&](){lpb::memory_random_access a{source},b{target};return lpb::media::prove_remux_bytes(a.view(),b.view(),ftyp,movie);};
   require(proof(),"exact whole stream proof");
   target[target.size()-10]^=1; require(!proof(),"media payload corruption rejected");target=source;
   target.back()^=1;require(!proof(),"opaque/free payload corruption rejected");target=source;
   target[ftyp.size()+movie.size()-1]^=1;require(!proof(),"unexpected movie byte rejected");target=source;
   append(target,atom("free"));require(!proof(),"extra trailing box rejected");target=source;
   target.pop_back();require(!proof(),"truncated output rejected");target=source;
   lpb::memory_random_access a{source},b{target};
   require(!lpb::media::prove_remux_bytes(a.view(),b.view(),ftyp,movie,[](void*){return true;}),"cancelled proof rejected");
  }
  // Expected relocation/name bytes are constructed here, without calling
  // shift_chunk_offsets or the production handler mapper.
  for (bool wide:{false,true}) for (bool movie_first:{false,true}) for (bool grow:{false,true}) {
   const auto short_ftyp=atom("ftyp",txt("qt  0000qt  "));
   const auto long_ftyp=atom("ftyp",txt("mp420000mp42isom"));
   auto sf=grow?short_ftyp:long_ftyp, tf=grow?long_ftyp:short_ftyp;
   bytes st(8),tt(8);st[7]=1;tt[7]=1;
   const uint32_t source_address=movie_first?256U:128U;
   const uint32_t target_address=grow?source_address+4U:source_address-4U;
   if(wide){put(st,0);put(tt,0);}put(st,source_address);put(tt,target_address);
   auto sm=atom("moov",atom("trak",atom("mdia",atom("minf",atom("stbl",atom(wide?"co64":"stco",st))))));
   auto tm=atom("moov",atom("trak",atom("mdia",atom("minf",atom("stbl",atom(wide?"co64":"stco",tt))))));
   auto data=atom("mdat",{1,2,3,4,5});auto s=sf,t=tf;
   if(movie_first){append(s,sm);append(t,tm);append(s,data);append(t,data);}
   else{append(s,data);append(t,data);append(s,sm);append(t,tm);}
   lpb::memory_random_access a{s},b{t};
   require(lpb::media::prove_remux_bytes(a.view(),b.view(),tf,tm),"explicit positive/negative-delta expected transport");
   const size_t moov_start=movie_first?tf.size():tf.size()+data.size();
   t[moov_start+tm.size()-1]^=1;
   require(!lpb::media::prove_remux_bytes(a.view(),b.view(),tf,tm),"wrong stco/co64 relocation byte rejected");
  }
  require(check(atom("moov",atom("udta")),F::none),"empty ordinary udta");
  require(check(atom("moov",atom("trak",atom("udta",atom("tagc",txt("com.example.ordinary"))))),F::none),"bounded ordinary track label stays opaque");
  require(check(atom("moov",atom("udta",atom("xxxx",txt("com.apple.quicktime.content.identifier")))),F::none),"opaque value text grants no binding authority");
  auto broken_opaque=atom("moov",atom("udta",atom("xxxx",{1,2,3})));broken_opaque.pop_back();
  require(check(broken_opaque,F::malformed),"truncated opaque user data is still malformed");
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
  auto padded_meta=mdta("com.example.ordinary");
  bytes padded_body(padded_meta.begin()+8,padded_meta.end());append(padded_body,atom("free",txt("com.apple.quicktime.content.identifier")));append(padded_body,atom("free"));append(padded_body,atom("skip",{1,2,3}));
  require(check(atom("moov",atom("meta",padded_body)),F::none),"bounded metadata padding has no key authority");
  auto truncated_padding=atom("moov",atom("meta",padded_body));truncated_padding.pop_back();
  require(check(truncated_padding,F::malformed),"metadata padding must still be completely bounded");
  require(check(atom("moov",mdta("com.xiaomi.product.marketname")),F::none),"ordinary vendor model name stays opaque");
  require(check(atom("moov",mdta("copyright",true)),F::ambiguous),"duplicate metadata key");
  bytes two=mdta("copyright");append(two,mdta("comment"));require(check(atom("moov",two),F::ambiguous),"shadow meta");
  bytes comment{0,3,0,0,'a','b','c'};require(check(atom("moov",atom("udta",atom("\xa9" "cmt",comment))),F::none),"ordinary text atom");
  comment[1]=4;require(check(atom("moov",atom("udta",atom("\xa9" "cmt",comment))),F::malformed),"truncated ordinary text");
  bytes movie=atom("moov");bytes vivo=txt("vivoMediaExtInfo");vivo.resize(16);append(movie,atom("uuid",vivo));
  require(check(movie,F::unsupported),"truncated mixed UUID cannot be neutral");
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
  const std::string ordinary_json="{\"com.android.camera.xdr.meta\":[1,2],\"keep\":true}";
  bytes mixed=txt("vivoMediaExtInfovivo");append(mixed,txt(ordinary_json.c_str()));put(mixed,uint32_t(ordinary_json.size()));append(mixed,txt("cameralbum!"));put(mixed,19);
  append(mixed,{255,255,255,255,0x1b,0x2a,0x39,0x48,0x57,0x66,0x75,0x84,0x93,0xa2,0xb3});
  movie=atom("moov");append(movie,atom("uuid",mixed));require(check(movie,F::none),"full bounded neutral mixedUUID");
  append(movie,atom("uuid",mixed));require(check(movie,F::ambiguous),"duplicate top-level mixedUUID");
  require(check(atom("moov",atom("udta",atom("uuid",mixed))),F::malformed),"nested mixedUUID is not top-level authority");
  std::cout<<"Target video metadata observation cases passed\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
