#include "containers/isobmff_metadata_keys.h"
#include "containers/mp4_strip.h"
#include "foundation/residue_fingerprint.h"
#include "media/target_video_observation.h"
#include <algorithm>
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <utility>
#define REQUIRE(x) do {if(!(x))throw std::runtime_error(std::string("line ")+std::to_string(__LINE__)+": "+#x);}while(false)
namespace {
using bytes=std::vector<uint8_t>;
void put(bytes& b,uint32_t n){for(int i=3;i>=0;--i)b.push_back(static_cast<uint8_t>(n>>(i*8)));}
void append(bytes& b,const bytes& v){b.insert(b.end(),v.begin(),v.end());}
bytes box(uint32_t type,const bytes& body){bytes out;put(out,static_cast<uint32_t>(body.size()+8));put(out,type);append(out,body);return out;}
bytes box(const char* type,const bytes& body){uint32_t code=0;for(size_t i=0;i<4;++i)code=(code<<8)|static_cast<uint8_t>(type[i]);return box(code,body);}
bytes text(std::string_view s){return {s.begin(),s.end()};}
constexpr const char* live="com.apple.quicktime.live-photo-info";
bytes key(uint32_t id,std::string_view name,std::string_view space="mdta",const bytes& definitions={}) {
 auto value=text(space);append(value,text(name));auto declaration=box("keyd",value);append(declaration,definitions);return box(id,declaration);
}
struct fixture {bytes moov,source;};
fixture movie(const bytes& keys,uint32_t sample_id=1,std::string_view literal="value",const bytes& track_extra={},const bytes& entry_extra={},uint8_t reference_index=1,unsigned reference_mode=0,const bytes& packet={}) {
 const auto sample=packet.empty()?box(sample_id,text(literal)):packet;
 bytes entry(8);entry[7]=reference_index;append(entry,box("keys",keys));append(entry,entry_extra);bytes stsd(4);put(stsd,1);append(stsd,box("mebx",entry));
 bytes stsz(4);put(stsz,static_cast<uint32_t>(sample.size()));put(stsz,1);
 bytes stts(4);put(stts,1);put(stts,1);put(stts,1);
 bytes stsc(4);put(stsc,1);put(stsc,1);put(stsc,1);put(stsc,1);
 bytes stco(4);put(stco,1);put(stco,8);
 bytes stbl=box("stsd",stsd);append(stbl,box("stsz",stsz));append(stbl,box("stts",stts));append(stbl,box("stsc",stsc));append(stbl,box("stco",stco));
 bytes handler(8);append(handler,text("meta"));handler.resize(24);
 bytes ref(4);ref[3]=reference_mode==1?0:1;if(reference_mode==1)append(ref,text("external\0"));
 const auto url=box(reference_mode==4?"urn ":"url ",ref);bytes dref(4);put(dref,reference_mode==3?2:1);append(dref,url);if(reference_mode==3)append(dref,url);
 bytes information=box("nmhd",bytes(4));if(reference_mode!=2)append(information,box("dinf",box("dref",dref)));append(information,box("stbl",stbl));
 bytes mdhd(12);put(mdhd,1000);put(mdhd,1);mdhd.resize(24);
 auto mdia=box("mdhd",mdhd);append(mdia,box("hdlr",handler));append(mdia,box("minf",information));
 bytes header(12);put(header,3);header.resize(84);auto track=box("tkhd",header);append(track,box("mdia",mdia));append(track,track_extra);
 fixture out;out.moov=box("moov",box("trak",track));out.source=box("mdat",sample);append(out.source,out.moov);return out;
}
lpb_cleanup_action authority() {
 lpb_cleanup_action a{};a.struct_size=sizeof(a);a.owner_protocol=LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO;
 a.artifact_role=LPB_ARTIFACT_MOTION_VIDEO;a.structure_kind=LPB_RESIDUE_QUICKTIME_METADATA_TRACK;
 a.coordinate_space=LPB_COORD_STRUCTURED_SELECTOR;a.removal_mode=LPB_REMOVAL_DELETE;a.is_mandatory=1;
 strcpy_s(a.residue_id,"apple-vid-track-livephoto-info");strcpy_s(a.selector,live);strcpy_s(a.expected_semantic,"LivePhotoInfoTrack");
 const auto fp=lpb::crypto::compute_metadata_track_fingerprint("meta",live);strcpy_s(a.expected_fingerprint,fp.c_str());return a;
}
}
extern "C" LPB_API int LPB_CALL lpb_test_p9_metadata_keys() {
 using namespace lpb::media;
 try {
  const auto input=movie(key(1,live));const auto original=input.source;
  std::vector<isobmff_metadata_key_track> inventory;
  REQUIRE(inspect_isobmff_metadata_keys(input.source,inventory)&&inventory.size()==1);
  REQUIRE(has_isobmff_metadata_key(inventory,"mdta",live)&&inventory[0].keys.size()==1);
  REQUIRE(validate_isobmff_metadata_key_samples(input.source,inventory[0],input.source));
  REQUIRE(!validate_isobmff_metadata_key_samples(input.source,inventory[0],input.source,metadata_sample_scope::single_track_value));
  auto action=authority();const char* patterns[]={live};lpb::containers::Mp4StripSpec spec;
  spec.expected_protocol=action.owner_protocol;spec.track_patterns=patterns;spec.track_patterns_count=1;spec.actions=&action;spec.action_count=1;
  bytes output;bool modified=false;
  REQUIRE(lpb::containers::test_rebuild_moov_tracks(input.moov,input.source,spec,output,modified)&&modified&&output==box("moov",{}));
  REQUIRE(input.source==original);
  for(unsigned mutation=0;mutation<3;++mutation) {
   auto malformed=input.moov;REQUIRE(inspect_isobmff_metadata_keys(malformed,inventory)&&inventory.size()==1);
   size_t timing=SIZE_MAX;
   for(size_t at=inventory[0].sample_table_begin;at<inventory[0].sample_table_end;) {
    isobmff_box_header header{};REQUIRE(try_read_box_header(malformed.data(),at,inventory[0].sample_table_end,header));
    if(std::memcmp(malformed.data()+at+4,"stts",4)==0)timing=at+header.header_size;
    at+=header.size;
   }
   REQUIRE(timing!=SIZE_MAX);
   if(mutation==0)malformed[timing]=2;
   if(mutation==1)malformed[timing+3]=1;
   if(mutation==2)std::fill_n(malformed.begin()+static_cast<ptrdiff_t>(timing+12),4,0);
   output.clear();modified=false;
   REQUIRE(!lpb::containers::test_rebuild_moov_tracks(malformed,input.source,spec,output,modified)&&output.empty()&&!modified);
  }
  for(unsigned field=0;field<8;++field) {
   auto wrong=action;
   if(field==0)wrong.owner_protocol=LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO;
   if(field==1)wrong.selector[0]='x';if(field==2)wrong.expected_semantic[0]='x';
   if(field==3)wrong.residue_id[0]='x';if(field==4)wrong.removal_mode=LPB_REMOVAL_REBUILD_CONTAINER;
   if(field==5)wrong.coordinate_space=LPB_COORD_ORIGINAL_SOURCE_RANGE;
   if(field==6)wrong.expected_fingerprint[0]='x';if(field==7)wrong.artifact_role=LPB_ARTIFACT_PRIMARY_IMAGE;
   auto rejected=spec;rejected.actions=&wrong;output.clear();modified=false;
   REQUIRE(!lpb::containers::test_rebuild_moov_tracks(input.moov,input.source,rejected,output,modified)&&output.empty()&&!modified);
  }
  auto mixed=key(1,live);append(mixed,key(2,"com.example.ordinary"));const auto mixed_file=movie(mixed);
  output.clear();modified=false;
  REQUIRE(!lpb::containers::test_rebuild_moov_tracks(mixed_file.moov,mixed_file.source,spec,output,modified)&&output.empty()&&!modified);
  const auto ordinary=movie(key(1,"com.example.ordinary"),1,live);
  REQUIRE(inspect_isobmff_metadata_keys(ordinary.source,inventory)&&!has_isobmff_metadata_key(inventory,"mdta",live));
  REQUIRE(inventory.size()==1&&!validate_isobmff_metadata_key_samples(ordinary.source,inventory[0],ordinary.source,metadata_sample_scope::single_track_value));
  output.clear();modified=false;
  REQUIRE(lpb::containers::test_rebuild_moov_tracks(ordinary.moov,ordinary.source,spec,output,modified)&&!modified&&output==ordinary.moov);
  const auto foreign=movie(key(1,live,"free"));output.clear();modified=false;
  REQUIRE(lpb::containers::test_rebuild_moov_tracks(foreign.moov,foreign.source,spec,output,modified)&&!modified&&output==foreign.moov);
  const auto undeclared=movie(key(1,live),42);output.clear();modified=false;
  REQUIRE(!lpb::containers::test_rebuild_moov_tracks(undeclared.moov,undeclared.source,spec,output,modified)&&output.empty()&&!modified);
  // The source format's sole track key owns an opaque packet, including bytes
  // that are not QTFF atoms. No packet-content signature grants authority.
  constexpr const char* track_key="com.openharmony.timed_metadata.movingphoto";
  auto track_action=action;track_action.owner_protocol=LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO;
  strcpy_s(track_action.residue_id,"huawei-vid-track-movingphoto");strcpy_s(track_action.selector,track_key);
  strcpy_s(track_action.expected_semantic,track_key);
  const auto track_fp=lpb::crypto::compute_metadata_track_fingerprint("meta",track_key);
  strcpy_s(track_action.expected_fingerprint,track_fp.c_str());const char* track_patterns[]={track_key};
  auto track_spec=spec;track_spec.expected_protocol=track_action.owner_protocol;track_spec.actions=&track_action;track_spec.track_patterns=track_patterns;
  for(const auto& packet:{bytes{1,0,0,0,16,0,0,0,8,0,0,0,0xa2,0x22,0,0},bytes{0xf1,0,0x9a,0x80,0,3,2}}) {
   const auto opaque=movie(key(7,track_key),1,"",{},{},0,0,packet);
   REQUIRE(inspect_isobmff_metadata_keys(opaque.source,inventory)&&inventory.size()==1);
   REQUIRE(!validate_isobmff_metadata_key_samples(opaque.source,inventory[0],opaque.source));
   REQUIRE(validate_isobmff_metadata_key_samples(opaque.source,inventory[0],opaque.source,metadata_sample_scope::single_track_value));
   output.clear();modified=false;
   REQUIRE(lpb::containers::test_rebuild_moov_tracks(opaque.moov,opaque.source,track_spec,output,modified)&&modified&&output==box("moov",{}));
   auto unauthorized=track_spec;unauthorized.expected_protocol=LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO;output.clear();modified=false;
   REQUIRE(!lpb::containers::test_rebuild_moov_tracks(opaque.moov,opaque.source,unauthorized,output,modified)&&output.empty()&&!modified);
   auto mixed_keys=key(7,track_key);append(mixed_keys,key(8,"com.example.ordinary"));
   const auto opaque_mixed=movie(mixed_keys,1,"",{},{},0,0,packet);output.clear();modified=false;
   REQUIRE(!lpb::containers::test_rebuild_moov_tracks(opaque_mixed.moov,opaque_mixed.source,track_spec,output,modified)&&output.empty()&&!modified);
   const auto opaque_extra=movie(key(7,track_key),1,"",box("uuid",bytes(16,1)),{},0,0,packet);output.clear();modified=false;
   REQUIRE(!lpb::containers::test_rebuild_moov_tracks(opaque_extra.moov,opaque_extra.source,track_spec,output,modified)&&output.empty()&&!modified);
   for(unsigned mode=1;mode<=4;++mode) {
    const auto bad_ref=movie(key(7,track_key),1,"",{},{},0,mode,packet);output.clear();modified=false;
    REQUIRE(!lpb::containers::test_rebuild_moov_tracks(bad_ref.moov,bad_ref.source,track_spec,output,modified)&&output.empty()&&!modified);
   }
   auto bad_bounds=opaque.moov;
   REQUIRE(inspect_isobmff_metadata_keys(bad_bounds,inventory)&&inventory.size()==1);
   for(size_t at=inventory[0].sample_table_begin;at<inventory[0].sample_table_end;) {
    isobmff_box_header header{};REQUIRE(try_read_box_header(bad_bounds.data(),at,inventory[0].sample_table_end,header));
    if(std::memcmp(bad_bounds.data()+at+4,"stco",4)==0)std::fill_n(bad_bounds.begin()+static_cast<ptrdiff_t>(at+header.header_size+8),4,0xff);
    at+=header.size;
   }
   output.clear();modified=false;
   REQUIRE(!lpb::containers::test_rebuild_moov_tracks(bad_bounds,opaque.source,track_spec,output,modified)&&output.empty()&&!modified);
   const auto truncated_source=std::span<const uint8_t>(opaque.source).first(8+packet.size()-1);output.clear();modified=false;
   REQUIRE(!lpb::containers::test_rebuild_moov_tracks(opaque.moov,truncated_source,track_spec,output,modified)&&output.empty()&&!modified);
  }
  auto duplicate=key(1,live);append(duplicate,key(1,"com.example.ordinary"));
  for(const auto& malformed:{key(UINT32_MAX,live),duplicate,box(1,box("free",box("keyd",text(std::string("mdta")+live))))}) {
   const auto bad=movie(malformed);REQUIRE(!inspect_isobmff_metadata_keys(bad.source,inventory)&&inventory.empty());
  }
  for(const auto& ordinary_scope:{box("udta",box("\xa9" "cmt",{0,4,0,0,'k','e','e','p'})),box("uuid",bytes(16,1))}) {
   const auto additional=movie(key(1,live),1,"value",ordinary_scope);output.clear();modified=false;
   REQUIRE(!lpb::containers::test_rebuild_moov_tracks(additional.moov,additional.source,spec,output,modified)&&output.empty()&&!modified);
  }
  const auto entry_scope=movie(key(1,live),1,"value",{},box("uuid",bytes(16,1)));output.clear();modified=false;
  REQUIRE(!lpb::containers::test_rebuild_moov_tracks(entry_scope.moov,entry_scope.source,spec,output,modified)&&output.empty()&&!modified);
  for(unsigned mode=1;mode<=4;++mode) {
   const auto foreign_ref=movie(key(1,live),1,"value",{},{},1,mode);output.clear();modified=false;
   REQUIRE(!lpb::containers::test_rebuild_moov_tracks(foreign_ref.moov,foreign_ref.source,spec,output,modified)&&output.empty()&&!modified);
  }
  const auto out_of_range=movie(key(1,live),1,"value",{},{},2);output.clear();modified=false;
  REQUIRE(!lpb::containers::test_rebuild_moov_tracks(out_of_range.moov,out_of_range.source,spec,output,modified)&&output.empty()&&!modified);
  const auto implicit_local=movie(key(1,live),1,"value",{},{},0);output.clear();modified=false;
  REQUIRE(lpb::containers::test_rebuild_moov_tracks(implicit_local.moov,implicit_local.source,spec,output,modified)&&modified&&output==box("moov",{}));
  auto unused=key(1,live);append(unused,box(uint32_t{0},text("opaque unused definition")));const auto unused_file=movie(unused);
  REQUIRE(inspect_isobmff_metadata_keys(unused_file.source,inventory)&&inventory.size()==1&&inventory[0].keys.size()==1);
  output.clear();modified=false;REQUIRE(!lpb::containers::test_rebuild_moov_tracks(unused_file.moov,unused_file.source,spec,output,modified)&&output.empty()&&!modified);
  bytes definitions=box("dtyp",{0,0,0,0,0,0,0,1});append(definitions,box("sdpd",{1,2,3,4}));append(definitions,box("setu",{5,6}));append(definitions,box("ctps",{7}));
  const auto owned_definitions=movie(key(1,live,"mdta",definitions));output.clear();modified=false;
  REQUIRE(lpb::containers::test_rebuild_moov_tracks(owned_definitions.moov,owned_definitions.source,spec,output,modified)&&modified&&output==box("moov",{}));
  bytes extended;put(extended,1);append(extended,text("keyd"));put(extended,0);put(extended,static_cast<uint32_t>(20+std::strlen(live)));append(extended,text("mdta"));append(extended,text(live));
  const auto extended_file=movie(box(1,extended));output.clear();modified=false;
  REQUIRE(lpb::containers::test_rebuild_moov_tracks(extended_file.moov,extended_file.source,spec,output,modified)&&modified&&output==box("moov",{}));
  auto truncated=extended_file.source;truncated.pop_back();REQUIRE(!inspect_isobmff_metadata_keys(truncated,inventory)&&inventory.empty());
  bytes video_header(12);put(video_header,3);video_header.resize(84);
  bytes video_handler(8);append(video_handler,text("vide"));video_handler.resize(24);
  auto video_track=box("tkhd",video_header);append(video_track,box("mdia",box("hdlr",video_handler)));
  auto duplicate_ids=bytes(input.moov.begin()+8,input.moov.end());append(duplicate_ids,box("trak",video_track));
  REQUIRE(!inspect_isobmff_metadata_keys(box("moov",duplicate_ids),inventory)&&inventory.empty());
  bytes reference;put(reference,3);const auto dependent=movie(key(1,"com.example.ordinary"),1,"value",box("tref",box("cdsc",reference)));
  auto second_track=bytes(dependent.moov.begin()+8,dependent.moov.end());
  REQUIRE(second_track.size()>31&&second_track[31]==3);second_track[31]=4;
  auto movie_body=bytes(input.moov.begin()+8,input.moov.end());append(movie_body,second_track);
  const auto dependent_moov=box("moov",movie_body);auto dependent_source=bytes(input.source.begin(),input.source.end()-static_cast<ptrdiff_t>(input.moov.size()));append(dependent_source,dependent_moov);
  output.clear();modified=false;
  REQUIRE(!lpb::containers::test_rebuild_moov_tracks(dependent_moov,dependent_source,spec,output,modified)&&output.empty()&&!modified);
  // A value containing the same literal never becomes a source-key declaration.
  lpb::memory_random_access ordinary_reader{ordinary.source};target_video_diagnostic diagnostic;
  REQUIRE(observe_target_video_metadata(ordinary_reader.view(),diagnostic));
  lpb::memory_random_access live_reader{input.source};
  REQUIRE(!observe_target_video_metadata(live_reader.view(),diagnostic)&&diagnostic.failure==target_video_failure::source_binding);
  std::cout<<"MEBX declared-key inventory, exact authority, ordinary-value/mixed-key preservation gates passed\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
 catch(...){return 1;}
}
