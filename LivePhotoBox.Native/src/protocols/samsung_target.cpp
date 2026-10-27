#include "protocols/samsung_target.h"
#include "protocols/samsung_inventory.h"
#include "media/jpeg_gainmap_mpf.h"
#include "metadata/jpeg.h"
#include "containers/heif_internal.h"
#include <algorithm>
#include <cstring>

namespace lpb::target {
namespace {
void le32(std::vector<uint8_t>& b,uint32_t n){for(unsigned i=0;i<4;++i)b.push_back(static_cast<uint8_t>(n>>(i*8)));}
void be32(std::vector<uint8_t>& b,uint32_t n){for(int i=3;i>=0;--i)b.push_back(static_cast<uint8_t>(n>>(i*8)));}
void tag_header(std::vector<uint8_t>& b,uint16_t marker,std::string_view name) {
 b.insert(b.end(),{0,0,static_cast<uint8_t>(marker),static_cast<uint8_t>(marker>>8)});
 le32(b,static_cast<uint32_t>(name.size()));b.insert(b.end(),name.begin(),name.end());
}
struct owned_tag {uint16_t marker;uint32_t offset,size;};
void directory(std::vector<uint8_t>& b,const std::vector<owned_tag>& tags,uint32_t table) {
 b.insert(b.end(),{'S','E','F','H'});le32(b,107);le32(b,static_cast<uint32_t>(tags.size()));
 for(const auto& t:tags){b.insert(b.end(),{0,0,static_cast<uint8_t>(t.marker),static_cast<uint8_t>(t.marker>>8)});le32(b,table-t.offset);le32(b,t.size);}
 le32(b,static_cast<uint32_t>(12+tags.size()*12));b.insert(b.end(),{'S','E','F','T'});
}
bool replace_jpeg_xml(std::vector<uint8_t>& bytes,std::string_view xml,std::string& error) {
 media::jpeg_gainmap_mpf::jpeg_info before;
 if(!media::jpeg_gainmap_mpf::parse_jpeg(bytes,before))return false;
 const size_t trailing=bytes.size()-before.eoi_end;
 std::vector<uint8_t> changed;
 if(metadata::replace_standard_jpeg_xmp_unchecked(bytes,
  {reinterpret_cast<const uint8_t*>(xml.data()),xml.size()},changed,error)!=LPB_RESULT_OK)return false;
 media::jpeg_gainmap_mpf::jpeg_info after;
 if(!media::jpeg_gainmap_mpf::parse_jpeg(changed,after)||changed.size()<trailing)return false;
 if(after.mpf_status==media::jpeg_gainmap_mpf::status::present) {
  if(after.mpf.image_count==2&&trailing) {
   if(!media::jpeg_gainmap_mpf::patch_layout(changed,after.mpf,after.eoi_end,changed.size()-trailing,trailing))return false;
  }else if(!media::jpeg_gainmap_mpf::patch_singleton_primary(changed,after))return false;
 }
 bytes=std::move(changed);return true;
}
}
bool prepare_samsung_target(lpb_context* context,std::span<const uint8_t> input,const media::target_primary_observation& facts,
 uint64_t movie_length,uint32_t version,bool cover_present,int64_t cover,
 std::vector<uint8_t>& prefix,std::vector<uint8_t>& suffix,std::string& error) noexcept {
 prefix.clear();suffix.clear();error.clear();
 try {
  const auto fail=[&](const char* message){error=message;return false;};
  const bool heic=facts.container==LPB_IMAGE_CONTAINER_HEIC;
  if((version!=2&&version!=3)||!movie_length||movie_length>UINT32_MAX-128ULL)return fail("Samsung version or 32-bit movie extent unsupported");
  std::vector<uint8_t> ordinary;std::vector<owned_tag> tags;
  if(facts.samsung_metadata.length) {
   memory_random_access memory{input};protocols::sef_inventory inventory;
   const auto range=facts.samsung_metadata;
   if(range.offset>input.size()||range.length>input.size()-range.offset||
      !protocols::observe_sef(memory.view(),range.offset+(heic?8:0),range.offset+range.length,inventory,error)||
      inventory.begin!=range.offset+(heic?8:0)||!protocols::ordinary_sef(inventory))return false;
   auto ordered=inventory.tags;std::sort(ordered.begin(),ordered.end(),[](const auto& a,const auto& b){return a.offset<b.offset;});
   for(const auto& tag:ordered) {
    if(ordinary.size()>UINT32_MAX-tag.size)return false;
    tags.push_back({tag.marker,static_cast<uint32_t>(ordinary.size()),static_cast<uint32_t>(tag.size)});
    ordinary.insert(ordinary.end(),input.begin()+static_cast<ptrdiff_t>(tag.offset),input.begin()+static_cast<ptrdiff_t>(tag.offset+tag.size));
   }
   prefix.assign(input.begin(),input.begin()+static_cast<ptrdiff_t>(range.offset));
   prefix.insert(prefix.end(),input.begin()+static_cast<ptrdiff_t>(range.offset+range.length),input.end());
  }else prefix.assign(input.begin(),input.end());
  const uint64_t ordinary_size=ordinary.size();
  const uint64_t tail_length=31+20+(tags.size()+2)*12;
  google_xmp_request request;request.target=5;request.format=heic?2:0;
  request.video_length=movie_length+tail_length+(heic?ordinary_size+36+8:0);
  request.cover_present=cover_present;request.cover_us=cover;request.primary_mime=heic?"image/heic":"image/jpeg";
  request.hdr_relationship_validated=std::any_of(facts.auxiliaries.begin(),facts.auxiliaries.end(),[](const auto& a){return a.semantic=="GainMap";});
  if(heic) {
   for(const auto& asset:facts.auxiliaries) {
    if(asset.semantic!="GainMap"||asset.mime!="image/heic")return fail("Samsung HEIC embedded auxiliary directory semantic unsupported");
    request.auxiliary_items.push_back({"GainMap","image/heic",0,0,true,false});
   }
   request.primary_padding=8;std::string xml;std::vector<uint8_t> rewritten;
   const auto xmp_result=build_google_xmp(facts.standard_xml,request,xml);
   if(xmp_result!=LPB_RESULT_OK)return fail("Samsung HEIC shared XMP merge rejected the prepared logical packet");
   if(!containers::rewrite_primary_xmp(prefix,xml,rewritten,error))return false;
   prefix=std::move(rewritten);
   if(prefix.size()>UINT32_MAX-8ULL||ordinary_size+36+tail_length>UINT32_MAX-8ULL)return fail("Samsung HEIC absolute pointer or SEFD size overflows32bit");
   const uint32_t video_offset=static_cast<uint32_t>(prefix.size()+8);
   std::vector<uint8_t> sefd=ordinary;
   tags.push_back({0x0a30,static_cast<uint32_t>(sefd.size()),36});tag_header(sefd,0x0a30,"MotionPhoto_Data");
   sefd.insert(sefd.end(),{'m','p','v','2'});be32(sefd,video_offset);be32(sefd,static_cast<uint32_t>(movie_length));
   tags.push_back({0x0a31,static_cast<uint32_t>(sefd.size()),31});tag_header(sefd,0x0a31,"MotionPhoto_Version");
   sefd.insert(sefd.end(),{'m','p','v',static_cast<uint8_t>('0'+version)});
   directory(sefd,tags,static_cast<uint32_t>(sefd.size()));
   be32(suffix,static_cast<uint32_t>(sefd.size()+8));suffix.insert(suffix.end(),{'s','e','f','d'});suffix.insert(suffix.end(),sefd.begin(),sefd.end());
   be32(prefix,static_cast<uint32_t>(movie_length+8));prefix.insert(prefix.end(),{'m','p','v','d'});
  }else {
   const bool micamera=std::any_of(facts.auxiliaries.begin(),facts.auxiliaries.end(),[](const auto& a){return a.selector_kind==3;});
   std::string neutral=facts.standard_xml,adjusted;media::xmp_observation_failure why{};
   if(facts.xmp.directory_present&&!micamera) {
    if(!media::rewrite_target_primary_padding(neutral,0,adjusted,why)||!replace_jpeg_xml(prefix,adjusted,error))return false;
   }
   for(const auto& a:facts.auxiliaries) {
    if(a.selector_kind==3&&micamera)continue;
    if(a.semantic!="GainMap"&&a.semantic!="Original")return fail("Samsung JPEG auxiliary semantic unsupported");
    request.auxiliary_items.push_back({a.semantic,a.mime,a.range.length,0,true,false});
   }
   if(micamera)request.primary_padding=prefix.size()-facts.encoding_boundary;
   std::vector<uint8_t> rewritten;
   if(build_google_primary(context,prefix,request,rewritten)!=LPB_RESULT_OK)return fail("Samsung shared XMP/primary merge failed");
   std::string xml;
   if(!metadata::extract_standard_jpeg_xmp(rewritten,xml)||
      !media::rewrite_target_primary_padding(xml,request.primary_padding+ordinary_size+24,adjusted,why,true)||!replace_jpeg_xml(rewritten,adjusted,error)) {
       if(error.empty())error="Samsung generated XMP padding reflow failed";return false;
   }
   prefix=std::move(rewritten);
   if(ordinary_size+24+movie_length+31>UINT32_MAX)return fail("Samsung JPEG SEF backward offsets overflow32bit");
   prefix.insert(prefix.end(),ordinary.begin(),ordinary.end());
   tags.push_back({0x0a30,static_cast<uint32_t>(ordinary_size),static_cast<uint32_t>(movie_length+24)});
   tag_header(prefix,0x0a30,"MotionPhoto_Data");
   tags.push_back({0x0a31,static_cast<uint32_t>(ordinary_size+24+movie_length),31});
   tag_header(suffix,0x0a31,"MotionPhoto_Version");suffix.insert(suffix.end(),{'m','p','v',static_cast<uint8_t>('0'+version)});
   directory(suffix,tags,static_cast<uint32_t>(ordinary_size+24+movie_length+31));
  }
  return true;
 }catch(...){prefix.clear();suffix.clear();error="Samsung target preparation failed";return false;}
}
}
