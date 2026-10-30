#include "containers/isobmff_track_graph.h"
#include "containers/isobmff.h"
#include <algorithm>
#include <cstring>
#include <map>
#include <set>
#include <string_view>
#include <utility>
namespace lpb::media {
namespace {
uint32_t u32(const uint8_t* p){return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3];}
struct node {size_t at,body,end;std::string kind;};
bool children(std::span<const uint8_t> data,size_t first,size_t last,std::vector<node>& out) {
 if(first>last||last>data.size())return false;
 for(size_t at=first;at<last;) {
  isobmff_box_header box{};if(!try_read_box_header(data.data(),at,last,box)||box.extends_to_end)return false;
  out.push_back({at,at+box.header_size,at+box.size,std::string(reinterpret_cast<const char*>(data.data()+at+4),4)});at+=box.size;
 }return true;
}
bool unique(const std::vector<node>& rows,const char* kind,const node*& found,bool required=true) {
 found=nullptr;for(const auto& row:rows)if(row.kind==kind){if(found)return false;found=&row;}return found||!required;
}
bool presentation(std::string_view role){return role=="vide"||role=="auxv"||role=="soun";}
}
bool inspect_isobmff_track_graph(std::span<const uint8_t> data,isobmff_track_graph& out) noexcept {
 out={};try {
  std::vector<node> top,movie;const node* moov{};
  if(!children(data,0,data.size(),top)||top.size()!=1||!unique(top,"moov",moov)||!children(data,moov->body,moov->end,movie))return false;
  isobmff_track_graph result;std::map<uint32_t,size_t> ids;
  for(const auto& track:movie)if(track.kind=="trak") {
   std::vector<node> members,media;const node *header{},*mdia{},*handler{},*tref{};
   if(!children(data,track.body,track.end,members)||!unique(members,"tkhd",header)||!unique(members,"mdia",mdia)||
      !unique(members,"tref",tref,false)||!children(data,mdia->body,mdia->end,media)||!unique(media,"hdlr",handler))return false;
   if(header->end-header->body<4||data[header->body]>1||header->end-header->body!=(data[header->body]?96u:84u)||handler->end-handler->body<24)return false;
   if(u32(data.data()+handler->body))return false;
   isobmff_track_node value;value.id=u32(data.data()+header->body+(data[header->body]?20:12));
   value.handler.assign(reinterpret_cast<const char*>(data.data()+handler->body+8),4);value.offset=track.at;value.length=track.end-track.at;
   if(!value.id||!ids.emplace(value.id,result.tracks.size()).second)return false;
   if(tref) {
    std::vector<node> references;if(!children(data,tref->body,tref->end,references)||references.empty())return false;
    std::set<std::string> kinds;
    for(const auto& ref:references) {
     if(!kinds.insert(ref.kind).second||ref.end==ref.body||(ref.end-ref.body)%4)return false;
     isobmff_track_reference edge;edge.kind=ref.kind;std::set<uint32_t> targets;
     for(size_t at=ref.body;at<ref.end;at+=4) {
      const auto target=u32(data.data()+at);if(!target||!targets.insert(target).second)return false;edge.targets.push_back(target);
     }
     value.references.push_back(std::move(edge));
    }
   }
   result.tracks.push_back(std::move(value));
  }
  std::vector<std::vector<size_t>> dependencies(result.tracks.size());
  for(size_t index=0;index<result.tracks.size();++index) {
   const auto& owner=result.tracks[index];
   for(const auto& edge:owner.references)for(const auto id:edge.targets) {
    const auto target=ids.find(id);if(target==ids.end())return false;
    if(edge.kind=="cdsc") {
     if(owner.handler!="meta"||!presentation(result.tracks[target->second].handler)||id==owner.id)return false;
    }else if(edge.kind=="cdep") {
     if(id==owner.id)return false;dependencies[index].push_back(target->second);
    }else if(edge.kind!="chap"&&edge.kind!="hint")result.opaque_relationships=true;
   }
  }
  // A dependency cycle has no decodable starting owner. Other opaque relation
  // kinds are preserved without inventing their ordering or interpretation.
  std::vector<size_t> pending(result.tracks.size()),ready;
  std::vector<std::vector<size_t>> dependents(result.tracks.size());
  for(size_t index=0;index<dependencies.size();++index) {
   pending[index]=dependencies[index].size();if(!pending[index])ready.push_back(index);
   for(const auto target:dependencies[index])dependents[target].push_back(index);
  }
  size_t consumed=0;
  while(!ready.empty()) {
   const auto index=ready.back();ready.pop_back();++consumed;
   for(const auto dependent:dependents[index])if(!--pending[dependent])ready.push_back(dependent);
  }
  if(consumed!=result.tracks.size())return false;
  out=std::move(result);return true;
 }catch(...){out={};return false;}
}
}
