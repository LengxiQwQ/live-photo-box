#pragma once
#include "containers/isobmff.h"
#include <span>
#include <vector>
#include <string_view>
#include <set>
#include <algorithm>
#include <cstring>

namespace lpb::media {
struct empty_audio_placeholder { size_t begin{},end{};uint32_t track_id{};bool found{}; };
namespace empty_audio_detail {
struct box {size_t begin,body,end;std::string_view type;};
inline uint32_t u32(const uint8_t* p){return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3];}
inline bool children(std::span<const uint8_t> b,size_t first,size_t last,std::vector<box>& output){
 for(size_t p=first;p<last;){isobmff_box_header h{};if(!try_read_box_header(b.data(),p,last,h)||h.extends_to_end)return false;output.push_back({p,p+h.header_size,p+h.size,{reinterpret_cast<const char*>(b.data()+p+4),4}});p+=h.size;}return true;
}
inline bool only(const std::vector<box>& boxes,std::initializer_list<std::string_view> allowed){std::set<std::string_view> seen;for(const auto& b:boxes)if(std::find(allowed.begin(),allowed.end(),b.type)==allowed.end()||!seen.insert(b.type).second)return false;return true;}
inline const box* one(const std::vector<box>& boxes,std::string_view name){const box* out=nullptr;for(const auto& b:boxes)if(b.type==name){if(out)return nullptr;out=&b;}return out;}
inline bool classified_track_tree(std::span<const uint8_t> b,size_t first,size_t last,std::string_view parent,unsigned depth=0){
 if(depth>8)return false;std::vector<box> nodes;if(!children(b,first,last,nodes))return false;
 std::set<std::string_view> seen;
 for(const auto& node:nodes){if(!seen.insert(node.type).second)return false;
  bool allowed=false;
  if(parent=="trak")allowed=node.type=="tkhd"||node.type=="mdia"||node.type=="edts"||node.type=="tref";
  else if(parent=="mdia")allowed=node.type=="mdhd"||node.type=="hdlr"||node.type=="minf";
  else if(parent=="minf")allowed=node.type=="vmhd"||node.type=="smhd"||node.type=="nmhd"||node.type=="dinf"||node.type=="stbl";
  else if(parent=="dinf")allowed=node.type=="dref";
  else if(parent=="edts")allowed=node.type=="elst";
  else if(parent=="stbl")allowed=node.type=="stsd"||node.type=="stts"||node.type=="ctts"||node.type=="stsc"||node.type=="stsz"||node.type=="stz2"||node.type=="stco"||node.type=="co64"||node.type=="stss";
  if(!allowed)return false;
  if(node.type=="mdia"||node.type=="minf"||node.type=="stbl"||node.type=="dinf"||node.type=="edts")
   if(!classified_track_tree(b,node.body,node.end,node.type,depth+1))return false;
 }
 return true;
}
inline bool android_version_meta(std::span<const uint8_t> bytes,const box& meta){
 std::vector<box> fields;size_t begin=meta.body;
 if(meta.end-begin>=4&&!u32(bytes.data()+begin))begin+=4;
 if(!children(bytes,begin,meta.end,fields)||!only(fields,{"hdlr","keys","ilst"}))return false;
 const auto* h=one(fields,"hdlr");const auto* k=one(fields,"keys");const auto* i=one(fields,"ilst");
 if(!h||!k||!i||h->end-h->body<24||u32(bytes.data()+h->body)||u32(bytes.data()+h->body+4)||std::memcmp(bytes.data()+h->body+8,"mdta",4))return false;
 for(size_t p=h->body+12;p<h->end;++p)if(bytes[p])return false;
 constexpr char name[]="com.android.version";
 if(k->end-k->body!=8+8+sizeof(name)-1||u32(bytes.data()+k->body)||u32(bytes.data()+k->body+4)!=1||u32(bytes.data()+k->body+8)!=8+sizeof(name)-1||std::memcmp(bytes.data()+k->body+12,"mdta",4)||std::memcmp(bytes.data()+k->body+16,name,sizeof(name)-1))return false;
 std::vector<box> indexed;if(!children(bytes,i->body,i->end,indexed)||indexed.size()!=1||u32(bytes.data()+indexed[0].begin+4)!=1)return false;
 std::vector<box> values;if(!children(bytes,indexed[0].body,indexed[0].end,values)||values.size()!=1||values[0].type!="data")return false;
 const auto& value=values[0];if(value.end-value.body<9||value.end-value.body>24||u32(bytes.data()+value.body)!=1||u32(bytes.data()+value.body+4))return false;
 for(size_t p=value.body+8;p<value.end;++p)if(bytes[p]<'0'||bytes[p]>'9')return false;
 return true;
}
}
// User-authorized finite repair model. Absence of an audio sample table is
// established from legal ownership; arbitrary malformed/nonempty tracks reject.
inline bool observe_empty_audio_placeholder(std::span<const uint8_t> b,empty_audio_placeholder& result){
 using namespace empty_audio_detail;result={};std::vector<box> top;if(!children(b,0,b.size(),top))return false;
 const auto moovs=std::count_if(top.begin(),top.end(),[](const auto& v){return v.type=="moov";});
 // This optional repair inventory never grants generic media validity. A
 // source structural probe without a movie owner has no placeholder action;
 // Writer/Validator still require their complete independent movie model.
 if(!moovs)return true;if(moovs!=1)return false;const auto* moov=one(top,"moov");
 std::vector<box> movie;if(!children(b,moov->body,moov->end,movie))return false;std::set<uint32_t> ids;std::vector<uint32_t> references;bool unclassified_graph=false;
 for(const auto& track:movie)if(track.type=="trak"){
  std::vector<box> parts;if(!children(b,track.body,track.end,parts))return false;const auto* header=one(parts,"tkhd");const auto* mdia=one(parts,"mdia");if(!header||!mdia||header->end-header->body<24)return false;
  const auto version=b[header->body];if(version>1)return false;const size_t id_at=header->body+(version?20:12);if(id_at+4>header->end)return false;const auto id=u32(b.data()+id_at);if(!id||!ids.insert(id).second)return false;
  if(!classified_track_tree(b,track.body,track.end,"trak"))unclassified_graph=true;
  for(const auto& part:parts)if(part.type!="tkhd"&&part.type!="mdia"&&part.type!="tref"&&part.type!="edts")unclassified_graph=true;
  if(std::count_if(parts.begin(),parts.end(),[](const auto& v){return v.type=="tref";})>1)return false;
  if(const auto* tref=one(parts,"tref")){std::vector<box> refs;if(!children(b,tref->body,tref->end,refs))return false;for(const auto& ref:refs){if((ref.end-ref.body)%4)return false;for(size_t p=ref.body;p<ref.end;p+=4)references.push_back(u32(b.data()+p));}}
  std::vector<box> media;if(!children(b,mdia->body,mdia->end,media))return false;const auto* handler=one(media,"hdlr");if(!handler||handler->end-handler->body<24)return false;if(std::memcmp(b.data()+handler->body+8,"soun",4))continue;
  const auto* minf=one(media,"minf");if(!minf)return false;std::vector<box> info;if(!children(b,minf->body,minf->end,info))return false;
  const auto* stbl=one(info,"stbl");
  if(stbl&&stbl->end!=stbl->body)continue;
  if(result.found||!only(parts,{"tkhd","mdia"})||!only(media,{"mdhd","hdlr","minf"})||!one(media,"mdhd")||!only(info,{"smhd","dinf","stbl"}))return false;
  if(header->end-header->body!=(version?96u:84u))return false;
  if((u32(b.data()+header->body)&0x00ffffff)>15)return false;
  const auto* clock=one(media,"mdhd");if(clock->end-clock->body<4)return false;const auto clock_version=b[clock->body];
  if(clock->end-clock->body!=(clock_version==0?24u:clock_version==1?36u:0u)||u32(b.data()+clock->body)%0x1000000||!u32(b.data()+clock->body+(clock_version?20:12)))return false;
  if(u32(b.data()+handler->body)||u32(b.data()+handler->body+4))return false;
  for(size_t p=handler->body+12;p<handler->body+24;++p)if(b[p])return false;

  const auto* sound=one(info,"smhd");const auto* dinf=one(info,"dinf");if(!sound||sound->end-sound->body!=8||!dinf)return false;
  for(size_t p=sound->body;p<sound->end;++p)if(b[p])return false;
  std::vector<box> data;if(!children(b,dinf->body,dinf->end,data)||data.size()!=1||data[0].type!="dref")return false;
  const auto& ref=data[0];if(ref.end-ref.body<8||u32(b.data()+ref.body)||u32(b.data()+ref.body+4)!=1)return false;
  std::vector<box> urls;if(!children(b,ref.body+8,ref.end,urls)||urls.size()!=1||urls[0].type!="url "||urls[0].end-urls[0].body!=4||u32(b.data()+urls[0].body)!=1)return false;
  result={track.begin,track.end,id,true};
 }
 if(result.found){if(unclassified_graph||std::count_if(movie.begin(),movie.end(),[](const auto& v){return v.type=="mvhd";})!=1)return false;if(std::count_if(movie.begin(),movie.end(),[](const auto& v){return v.type=="meta";})>1)return false;for(const auto& child:movie)if(child.type!="mvhd"&&child.type!="trak"&&child.type!="free"&&!(child.type=="meta"&&android_version_meta(b,child)))return false;for(const auto id:references)if(id==result.track_id)return false;}
 return true;
}
}
