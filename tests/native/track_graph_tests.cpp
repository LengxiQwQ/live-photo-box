#include "containers/isobmff_track_graph.h"
#include "livephotobox_native.h"
#include "containers/isobmff.h"
#include "containers/isobmff_metadata_keys.h"
#include "containers/pcm_config.h"
#include "media/target_video_observation.h"
#include <algorithm>
#include <bit>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <memory>
#include <stdexcept>
#include <utility>
#define REQUIRE(x) do {if(!(x))throw std::runtime_error(std::string("line ")+std::to_string(__LINE__)+": "+#x);}while(false)
namespace {
using bytes=std::vector<uint8_t>;
void put(bytes& out,uint32_t value){for(int n=3;n>=0;--n)out.push_back(static_cast<uint8_t>(value>>(n*8)));}
void set(bytes& out,size_t at,uint32_t value){for(size_t n=0;n<4;++n)out[at+n]=static_cast<uint8_t>(value>>((3-n)*8));}
void append(bytes& out,const bytes& value){out.insert(out.end(),value.begin(),value.end());}
bytes atom(const char* kind,bytes body){bytes out;put(out,static_cast<uint32_t>(body.size()+8));out.insert(out.end(),kind,kind+4);append(out,body);return out;}
bytes track(uint32_t id,const char* role,std::vector<std::pair<std::string,std::vector<uint32_t>>> references={}) {
 bytes tkhd(84);set(tkhd,12,id);bytes handler(24);std::copy_n(role,4,handler.begin()+8);
 auto out=atom("tkhd",tkhd);append(out,atom("mdia",atom("hdlr",handler)));
 if(!references.empty()) {
  bytes edges;for(const auto& [kind,targets]:references){bytes ids;for(const auto value:targets)put(ids,value);append(edges,atom(kind.c_str(),ids));}
  append(out,atom("tref",edges));
 }return atom("trak",out);
}
bytes movie(std::initializer_list<bytes> tracks){bytes out;for(const auto& value:tracks)append(out,value);return atom("moov",out);}
bytes pcm() {
 bytes out(64);out[7]=1;out[9]=2;out[17]=3;out[19]=16;out[20]=0xff;out[21]=0xfe;
 set(out,24,65536);set(out,28,72);
 const uint64_t rate=std::bit_cast<uint64_t>(48000.0);set(out,32,static_cast<uint32_t>(rate>>32));set(out,36,static_cast<uint32_t>(rate));
 set(out,40,1);set(out,44,0x7f000000);set(out,48,16);set(out,52,12);set(out,56,2);set(out,60,1);return out;
}
struct failing_reader {
 std::span<const uint8_t> data;uint64_t denied{};
 static bool read(void* state,uint64_t at,std::span<uint8_t> output) noexcept {
  const auto& self=*static_cast<failing_reader*>(state);
  if(at==self.denied||at>self.data.size()||output.size()>self.data.size()-at)return false;
  std::memcpy(output.data(),self.data.data()+at,output.size());return true;
 }
 lpb::random_access_reader view(){return {data.size(),this,read};}
};
}
extern "C" LPB_API int LPB_CALL lpb_test_p9_track_graph() {
 using namespace lpb::media;try {
  isobmff_track_graph graph;
  const auto primary=track(1,"vide"),auxiliary=track(2,"auxv",{{"vmap",{1}}}),metadata=track(3,"meta",{{"cdsc",{1}},{"cdep",{1}}});
  REQUIRE(inspect_isobmff_track_graph(movie({primary,auxiliary,metadata}),graph)&&graph.tracks.size()==3&&graph.opaque_relationships);
  for(const auto& malformed:{movie({primary,track(1,"soun")}),movie({track(0,"vide")}),
   movie({primary,track(2,"meta",{{"cdsc",{0}}})}),movie({primary,track(2,"meta",{{"cdsc",{9}}})}),
   movie({primary,track(2,"auxv",{{"cdsc",{1}}})}),movie({primary,track(2,"meta",{{"cdsc",{3}}}),track(3,"meta")}),
   movie({primary,track(2,"meta",{{"cdsc",{1,1}}})}),movie({primary,track(2,"meta",{{"cdsc",{1}},{"cdsc",{1}}})}),
   movie({primary,track(2,"auxv",{{"vmap",{1,1}}})}),movie({primary,track(2,"auxv",{{"vmap",{3}}})}),
   movie({track(1,"vide",{{"cdep",{2}}}),track(2,"auxv",{{"cdep",{1}}})}),movie({track(1,"vide",{{"cdep",{1}}})})}) {
   REQUIRE(!inspect_isobmff_track_graph(malformed,graph)&&graph.tracks.empty());
  }
  auto broken=movie({primary,auxiliary});broken.pop_back();REQUIRE(!inspect_isobmff_track_graph(broken,graph));
  lpcm_v2_layout audio;const auto valid=pcm();REQUIRE(inspect_lpcm_v2(valid,audio)&&audio.rate==48000&&audio.channels==1&&audio.bytes_per_packet==2);
  for(const auto [offset,value]:std::initializer_list<std::pair<size_t,uint32_t>>{{28,71},{28,UINT32_MAX},{32,0x7ff00000},{32,0x7ff80000},
   {40,0},{40,UINT32_MAX},{48,20},{52,13},{52,0x2c},{52,0x1c},{52,0x8000000c},{56,3},{60,UINT32_MAX}}) {
   auto bad=valid;set(bad,offset,value);REQUIRE(!inspect_lpcm_v2(bad,audio));
  }
  auto packed24=valid;set(packed24,48,24);set(packed24,56,3);REQUIRE(inspect_lpcm_v2(packed24,audio)&&audio.bits==24);
  set(packed24,56,4);REQUIRE(!inspect_lpcm_v2(packed24,audio));
  char* raw_root=nullptr;size_t root_size=0;
  REQUIRE(_dupenv_s(&raw_root,&root_size,"LPB_P9R8_TRACK_INPUT_ROOT")==0);
  std::unique_ptr<char,decltype(&std::free)> root(raw_root,&std::free);REQUIRE(root&&root_size>1);
  for(const auto& [name,expected_aux]:std::initializer_list<std::pair<std::filesystem::path,size_t>>{{L"苹果-双文件.JPG",4},{L"苹果双文件.HEIC",0}}) {
   std::ifstream stream(std::filesystem::path(root.get())/name/"asset-1.mov",std::ios::binary);REQUIRE(stream.good());
   bytes data((std::istreambuf_iterator<char>(stream)),{});lpb::memory_random_access input{data};
   target_video_observation observed;target_video_diagnostic diagnostic;
   const bool accepted=observe_target_video(input.view(),false,observed,diagnostic);
   if(!accepted)std::cerr<<diagnostic.reason<<'\n';
   REQUIRE(accepted);REQUIRE(observed.media.auxiliary_video_tracks.size()==expected_aux);
   REQUIRE(observed.media.timeline.sample_count==61&&observed.media.video_codec==3);
   if(expected_aux) {
    REQUIRE(observed.media.video_width==1920&&observed.media.video_height==1440);
    bool different_schedule=false;
    for(const auto& aux:observed.media.auxiliary_video_tracks){REQUIRE(aux.width<observed.media.video_width&&aux.codec==3);if(aux.sample_count!=61)different_schedule=true;}
    REQUIRE(different_schedule);
   }
   std::vector<isobmff_metadata_key_track> key_tracks;
   REQUIRE(inspect_isobmff_metadata_keys(data,key_tracks)&&!key_tracks.empty());
   const auto& metadata_track=key_tracks[0];
   std::vector<isobmff_media_data_range> mdats;
   for(const auto& top:scan_top_level_boxes(input.view()))if(std::memcmp(top.type,"mdat",4)==0)mdats.push_back({top.offset+top.header_size,top.offset+top.size});
   isobmff_sample_table_layout table;uint64_t bad_offset{};
   REQUIRE(inspect_isobmff_strict_sample_table(data,metadata_track.sample_table_offset,metadata_track.sample_table_end,mdats,0,table,bad_offset)&&!table.chunks.empty());
   const uint64_t sample_at=table.chunks[0].offset;failing_reader failure{data,sample_at};
   isobmff_reader_observation ignored;isobmff_reader_diagnostic error;
   REQUIRE(!inspect_isobmff_reader(failure.view(),false,ignored,error)&&error.failure==isobmff_reader_failure::io&&error.offset==sample_at);
   auto malformed=data;REQUIRE(sample_at<=malformed.size()&&malformed.size()-sample_at>=4);
   std::fill_n(malformed.begin()+static_cast<ptrdiff_t>(sample_at),4,0xff);lpb::memory_random_access bad_input{malformed};
   REQUIRE(!inspect_isobmff_reader(bad_input.view(),false,ignored,error)&&error.failure==isobmff_reader_failure::malformed);
  }
  std::cout<<"Track graph/LPCM negatives and two original-source Neutral media observations passed\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
