#include "containers/isobmff_metadata_keys.h"
#include "containers/isobmff.h"
#include <algorithm>
#include <cstring>
#include <set>
#include <string_view>
namespace lpb::media {
namespace {
uint32_t u32(const uint8_t* p) {return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3];}
struct node {size_t at{},body{},end{};uint32_t type{};};
constexpr uint32_t code(const char* s) {return (uint32_t(uint8_t(s[0]))<<24)|(uint32_t(uint8_t(s[1]))<<16)|(uint32_t(uint8_t(s[2]))<<8)|uint8_t(s[3]);}
bool children(std::span<const uint8_t> b,size_t begin,size_t end,std::vector<node>& out) {
 if(begin>end||end>b.size())return false;
 for(size_t at=begin;at<end;) {
  isobmff_box_header box{};if(!try_read_box_header(b.data(),at,end,box)||box.size<box.header_size||!box.size)return false;
  out.push_back({at,at+box.header_size,at+box.size,u32(b.data()+at+4)});at+=box.size;
 }return true;
}
bool unique(std::span<const node> nodes,uint32_t type,const node*& out,bool required=true) {
 out=nullptr;for(const auto& n:nodes)if(n.type==type){if(out)return false;out=&n;}
 return out||!required;
}
bool walk_path(std::span<const uint8_t> b,const node& parent,uint32_t type,node& out) {
 std::vector<node> rows;const node* found{};
 if(!children(b,parent.body,parent.end,rows)||!unique(rows,type,found))return false;out=*found;return true;
}
bool read_keys(std::span<const uint8_t> b,const node& entry,uint32_t ordinal,isobmff_metadata_key_track& track) {
 if(entry.end-entry.body<8)return false;
 // SampleEntry reserved bytes cannot encode an alternative ownership path.
 for(size_t at=entry.body;at<entry.body+6;++at)if(b[at])return false;
 track.data_reference_index=static_cast<uint16_t>((b[entry.body+6]<<8)|b[entry.body+7]);
 std::vector<node> fields;const node* keys{};
 if(!children(b,entry.body+8,entry.end,fields)||!unique(fields,code("keys"),keys))return false;
 for(const auto& field:fields)if(field.type!=code("keys")) {
  if(field.type!=code("btrt")||field.end-field.body!=12)track.whole_track_owned_shape=false;
 }
 std::vector<node> items;if(!children(b,keys->body,keys->end,items)||items.empty())return false;
 std::set<uint32_t> ids;std::set<std::pair<std::string,std::string>> names;
 for(const auto& item:items) {
  if(item.type==UINT32_MAX)return false;
  // QTFF reserves ID zero as an unused key atom. Its contents have no key
  // authority, but remain ordinary opaque content that cannot be discarded.
  if(!item.type){track.whole_track_owned_shape=false;continue;}
  if(!ids.insert(item.type).second)return false;
  std::vector<node> declarations;const node* keyd{};
  if(!children(b,item.body,item.end,declarations)||!unique(declarations,code("keyd"),keyd)||keyd->end-keyd->body<=4)return false;
  isobmff_metadata_key key;key.description_index=ordinal;key.key_id=item.type;
  key.name_space.assign(reinterpret_cast<const char*>(b.data()+keyd->body),4);
  key.name.assign(reinterpret_cast<const char*>(b.data()+keyd->body+4),keyd->end-keyd->body-4);
  // keyd is a byte-array key, not a NUL-terminated name or a nested box.
  if((key.name_space=="mdta"&&key.name.find('\0')!=std::string::npos)||!names.emplace(key.name_space,key.name).second)return false;
  // QTFF Metadata key atom defines every bounded direct subatom here as
  // structure/datatype/locale information for this unique key. Unknown
  // definition bytes are not another property or a source of extra ranges.
  key.keyd_offset=keyd->at;key.keyd_length=keyd->end-keyd->at;track.keys.push_back(std::move(key));
 }return true;
}
}
bool inspect_isobmff_metadata_keys(std::span<const uint8_t> b,std::vector<isobmff_metadata_key_track>& out) noexcept {
 out.clear();
 try {
  std::vector<isobmff_metadata_key_track> result;
  std::vector<node> top;const node* moov{};
  if(!children(b,0,b.size(),top)||!unique(top,code("moov"),moov,false))return false;
  if(!moov)return true;
  std::vector<node> movie;if(!children(b,moov->body,moov->end,movie))return false;
  std::set<uint32_t> track_ids;
  for(const auto& track:movie)if(track.type==code("trak")) {
   std::vector<node> members,media;const node *mdia{},*hdlr{},*tkhd{};
   if(!children(b,track.body,track.end,members)||!unique(members,code("mdia"),mdia,false)||!unique(members,code("tkhd"),tkhd,false))return false;
   uint32_t id=0;
   if(tkhd) {
    if(tkhd->end-tkhd->body<4||b[tkhd->body]>1)return false;
    const size_t id_offset=tkhd->body+(b[tkhd->body]?20:12);
    if(id_offset>tkhd->end||tkhd->end-id_offset<4)return false;
    id=u32(b.data()+id_offset);if(!id||!track_ids.insert(id).second)return false;
   }
   if(!mdia)continue;
   if(!children(b,mdia->body,mdia->end,media)||!unique(media,code("hdlr"),hdlr,false))return false;
   if(!hdlr)continue;
   if(hdlr->end-hdlr->body<12)return false;
   if(u32(b.data()+hdlr->body+8)!=code("meta"))continue;
   if(!tkhd||!id)return false;
   node minf{},stbl{},stsd{};
   if(!walk_path(b,*mdia,code("minf"),minf)||!walk_path(b,minf,code("stbl"),stbl)||!walk_path(b,stbl,code("stsd"),stsd)||
      stsd.end-stsd.body<8||u32(b.data()+stsd.body))return false;
   std::vector<node> entries;if(!children(b,stsd.body+8,stsd.end,entries)||entries.empty()||entries.size()!=u32(b.data()+stsd.body+4))return false;
   isobmff_metadata_key_track facts;facts.track_id=id;facts.offset=track.at;facts.length=track.end-track.at;
   facts.sample_table_begin=stbl.body;facts.sample_table_end=stbl.end;
   facts.sample_table_offset=stbl.at;
   facts.media_info_begin=minf.body;facts.media_info_end=minf.end;
   const auto unique_types=[](const auto& values){std::set<uint32_t> types;for(const auto& value:values)if(!types.insert(value.type).second)return false;return true;};
   if(!unique_types(members)||!unique_types(media))facts.whole_track_owned_shape=false;
   for(const auto& member:members)if(member.type!=code("tkhd")&&member.type!=code("edts")&&member.type!=code("tref")&&member.type!=code("mdia"))facts.whole_track_owned_shape=false;
   for(const auto& member:members)if(member.type==code("edts")||member.type==code("tref")) {
    std::vector<node> scoped;if(!children(b,member.body,member.end,scoped))return false;
    std::set<uint32_t> kinds;
    for(const auto& child:scoped) {
     if(!kinds.insert(child.type).second)facts.whole_track_owned_shape=false;
     if(member.type==code("edts")&&child.type!=code("elst"))facts.whole_track_owned_shape=false;
     if(member.type==code("tref")&&child.type!=code("cdsc")&&child.type!=code("cdep"))facts.whole_track_owned_shape=false;
    }
   }
   for(const auto& member:media)if(member.type!=code("mdhd")&&member.type!=code("hdlr")&&member.type!=code("minf"))facts.whole_track_owned_shape=false;
   std::vector<node> information,tables;
   if(!children(b,minf.body,minf.end,information)||!children(b,stbl.body,stbl.end,tables))return false;
   if(!unique_types(information))facts.whole_track_owned_shape=false;
   for(const auto& member:information)if(member.type!=code("nmhd")&&member.type!=code("gmhd")&&member.type!=code("hdlr")&&member.type!=code("dinf")&&member.type!=code("stbl"))facts.whole_track_owned_shape=false;
   for(const auto& member:information)if(member.type==code("gmhd")) {
    std::vector<node> generic;if(!children(b,member.body,member.end,generic))return false;
    if(generic.size()!=1||generic.front().type!=code("gmin")||generic.front().end-generic.front().body!=16)facts.whole_track_owned_shape=false;
   }
   constexpr uint32_t structural_tables[]={code("stsd"),code("stsz"),code("stsc"),code("stco"),code("co64"),code("stts"),code("ctts"),code("stss")};
   std::set<uint32_t> table_kinds;
   for(const auto& member:tables)if(!table_kinds.insert(member.type).second||std::find(std::begin(structural_tables),std::end(structural_tables),member.type)==std::end(structural_tables))facts.whole_track_owned_shape=false;
   facts.description_count=static_cast<uint32_t>(entries.size());facts.all_descriptions_mebx=true;
   for(size_t index=0;index<entries.size();++index) {
    if(entries[index].type!=code("mebx")){facts.all_descriptions_mebx=false;continue;}
    if(!read_keys(b,entries[index],static_cast<uint32_t>(index+1),facts))return false;
   }
   if(!facts.keys.empty())result.push_back(std::move(facts));
  }
  out=std::move(result);return true;
 }catch(...){out.clear();return false;}
}
bool has_isobmff_metadata_key(std::span<const isobmff_metadata_key_track> tracks,std::string_view space,std::string_view name) noexcept {
 for(const auto& track:tracks)for(const auto& key:track.keys)if(key.name_space==space&&key.name==name)return true;return false;
}
bool is_source_live_metadata_key(std::string_view space,std::string_view name) noexcept {
 if(space!="mdta")return false;
 constexpr std::string_view keys[]={"com.apple.quicktime.content.identifier","com.apple.quicktime.live-photo",
  "com.apple.quicktime.still-image-time","com.apple.quicktime.live-photo-info",
  "com.apple.quicktime.live-photo-still-image-transform","com.apple.quicktime.live-photo-still-image-transform-reference-dimensions",
  "com.openharmony.covertime","com.openharmony.movingphoto","com.huawei.movingphoto","com.openharmony.timed_metadata.movingphoto"};
 return std::find(std::begin(keys),std::end(keys),name)!=std::end(keys);
}
metadata_sample_scope declared_metadata_sample_scope(std::string_view space,std::string_view name) noexcept {
 // OpenHarmony's timed-meta muxer writes stream packets verbatim. This exact
 // declared key owns the whole packet; it is not a QTFF keyed sample atom.
 // No source identity, filename, packet signature or parse-failure fallback.
 return space=="mdta"&&name=="com.openharmony.timed_metadata.movingphoto"
  ?metadata_sample_scope::single_track_value:metadata_sample_scope::keyed_atoms;
}
bool validate_isobmff_metadata_key_samples(std::span<const uint8_t> metadata,
 const isobmff_metadata_key_track& track,std::span<const uint8_t> source,metadata_sample_scope scope,metadata_validation_purpose purpose) noexcept {
 lpb::memory_random_access bytes{source};
 return validate_isobmff_metadata_key_samples_reader(metadata,track,bytes.view(),scope,purpose);
}
bool validate_isobmff_metadata_key_samples_reader(std::span<const uint8_t> metadata,
 const isobmff_metadata_key_track& track,const lpb::random_access_reader& source,metadata_sample_scope scope,metadata_validation_purpose purpose,
 metadata_sample_diagnostic* diagnostic) noexcept {
 if(diagnostic)*diagnostic={};
 const auto read=[&](uint64_t at,std::span<uint8_t> buffer) {
  if(source.read_exact(at,buffer))return true;
  if(diagnostic)*diagnostic={true,at};return false;
 };
 try {
  if(purpose!=metadata_validation_purpose::whole_track_removal&&purpose!=metadata_validation_purpose::read_only_media)return false;
  if(!track.all_descriptions_mebx||(purpose==metadata_validation_purpose::whole_track_removal&&!track.whole_track_owned_shape)||track.description_count!=1||track.keys.empty()||track.data_reference_index>1)return false;
  if(scope!=metadata_sample_scope::keyed_atoms&&scope!=metadata_sample_scope::single_track_value)return false;
  if(scope==metadata_sample_scope::single_track_value&&track.keys.size()!=1)return false;
  for(const auto& key:track.keys)if(declared_metadata_sample_scope(key.name_space,key.name)!=scope)return false;
  isobmff_box_header track_box{};
  if(!try_read_box_header(metadata.data(),track.offset,metadata.size(),track_box)||track_box.size!=track.length)return false;
  const node owner{track.offset,track.offset+track_box.header_size,track.offset+track_box.size,code("trak")};
  node tkhd{},mdia{},mdhd{};
  if(!walk_path(metadata,owner,code("tkhd"),tkhd)||!walk_path(metadata,owner,code("mdia"),mdia)||
     !walk_path(metadata,mdia,code("mdhd"),mdhd)||tkhd.end-tkhd.body<4||mdhd.end-mdhd.body<4||
     metadata[tkhd.body]>1||metadata[mdhd.body]>1||
     tkhd.end-tkhd.body!=(metadata[tkhd.body]?96u:84u)||mdhd.end-mdhd.body!=(metadata[mdhd.body]?36u:24u)||
     metadata[mdhd.body+1]||metadata[mdhd.body+2]||metadata[mdhd.body+3]||
     !u32(metadata.data()+mdhd.body+(metadata[mdhd.body]?20:12)))return false;
  std::vector<node> track_members;
  if(!children(metadata,owner.body,owner.end,track_members))return false;
  for(const auto& member:track_members)if(member.type==code("edts")) {
   std::vector<node> edits;if(!children(metadata,member.body,member.end,edits)||edits.size()!=1||edits[0].type!=code("elst"))return false;
   const auto& list=edits[0];
   if(list.end-list.body<8||metadata[list.body]>1||metadata[list.body+1]||metadata[list.body+2]||metadata[list.body+3])return false;
   const uint32_t count=u32(metadata.data()+list.body+4);const size_t width=metadata[list.body]?20:12;
   if(!count||(list.end-list.body-8)/width!=count||(list.end-list.body-8)%width)return false;
   for(size_t at=list.body+8;at<list.end;at+=width) {
    const size_t time=at+(width==20?8:4),rate=at+width-4;
    const uint32_t high=u32(metadata.data()+time);
    if(high&0x80000000U)if(high!=UINT32_MAX||(width==20&&u32(metadata.data()+time+4)!=UINT32_MAX))return false;
    const uint32_t value=u32(metadata.data()+rate);if(value!=0&&value!=0x00010000U)return false;
   }
  }
  std::vector<node> info;const node* dinf{};
  if(!children(metadata,track.media_info_begin,track.media_info_end,info)||!unique(info,code("dinf"),dinf))return false;
  for(const auto& member:info) {
   if(member.type==code("nmhd")&&(member.end-member.body!=4||u32(metadata.data()+member.body)))return false;
   if(member.type==code("gmhd")) {
    std::vector<node> generic;if(!children(metadata,member.body,member.end,generic)||generic.size()!=1||generic[0].end-generic[0].body!=16||u32(metadata.data()+generic[0].body))return false;
   }
  }
  std::vector<node> refs;const node* dref{};
  if(!children(metadata,dinf->body,dinf->end,refs)||refs.size()!=1||!unique(refs,code("dref"),dref)||dref->end-dref->body<8||
     u32(metadata.data()+dref->body)||u32(metadata.data()+dref->body+4)!=1)return false;
  std::vector<node> entries;
  if(!children(metadata,dref->body+8,dref->end,entries)||entries.size()!=1)return false;
  const auto& ref=entries.front();
  if((ref.type!=code("url ")&&ref.type!=code("alis"))||ref.end-ref.body!=4||u32(metadata.data()+ref.body)!=1)return false;
  std::vector<isobmff_media_data_range> mdats;
  for(uint64_t at=0;at<source.length;) {
   uint8_t header[16]{};if(source.length-at<8||!read(at,std::span<uint8_t>(header,8)))return false;
   uint64_t size=u32(header);uint64_t width=8;
   if(size==1) {
    if(source.length-at<16||!read(at+8,std::span<uint8_t>(header+8,8)))return false;
    size=(uint64_t(u32(header+8))<<32)|u32(header+12);width=16;
   }else if(!size)size=source.length-at;
   if(size<width||size>source.length-at)return false;
   if(std::memcmp(header+4,"mdat",4)==0)mdats.push_back({at+width,at+size});at+=size;
  }
  isobmff_sample_table_layout table;uint64_t bad{};
  if(!inspect_isobmff_strict_sample_table(metadata,track.sample_table_offset,track.sample_table_end,mdats,0,table,bad)||table.sample_description_count!=1)return false;
  std::vector<node> rows;const node* stsz{};
  if(!children(metadata,track.sample_table_begin,track.sample_table_end,rows)||!unique(rows,code("stsz"),stsz)||stsz->end-stsz->body<12)return false;
  const uint32_t fixed=u32(metadata.data()+stsz->body+4),count=u32(metadata.data()+stsz->body+8);
  if(count!=table.sample_count||(!fixed&&(stsz->end-stsz->body-12)/4!=count))return false;
  uint64_t sample_index=0;
  for(const auto& chunk:table.chunks) {
   uint64_t at=chunk.offset;
   if(chunk.first_sample!=sample_index||at>source.length||chunk.size>source.length-at)return false;
   for(uint32_t sample=0;sample<chunk.sample_count;++sample,++sample_index) {
    if(sample_index>=count)return false;
    const uint32_t length=fixed?fixed:u32(metadata.data()+stsz->body+12+static_cast<size_t>(sample_index)*4);
    if(at>source.length||length>source.length-at)return false;
    if(scope==metadata_sample_scope::keyed_atoms) {
     std::set<uint32_t> seen;
     for(uint64_t cursor=at;cursor<at+length;) {
      const uint64_t remaining=at+length-cursor;uint8_t header[16]{};
      if(remaining<8||!read(cursor,std::span<uint8_t>(header,8)))return false;
      uint64_t size=u32(header);size_t width=8;const uint32_t id=u32(header+4);
      if(size==1) {
       if(remaining<16||!read(cursor+8,std::span<uint8_t>(header+8,8)))return false;
       size=(uint64_t(u32(header+8))<<32)|u32(header+12);width=16;
      }else if(!size)size=remaining;
      if(size<width||size>remaining||!seen.insert(id).second||
       std::none_of(track.keys.begin(),track.keys.end(),[&](const auto& key){return key.key_id==id&&key.description_index==1;}))return false;
      cursor+=size;
     }
    }
    at+=length;
   }
   if(at!=chunk.offset+chunk.size)return false;
  }
  return sample_index==count;
 }catch(...){return false;}
}
}
