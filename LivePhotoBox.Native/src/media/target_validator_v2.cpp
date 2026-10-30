#include "media/target_validator_v2.h"
#include "media/target_primary_observation.h"
#include "media/jpeg_mpf_vendor.h"
#include "media/target_video_observation.h"
#include "media/huawei_conformance.h"
#include "media/target_writer.h"
#include "media/vivo_comment.h"
#include "media/heic_backend.h"
#include "media/ordinary_metadata_container.h"
#include "foundation/internal.h"
#include "foundation/sha256_core.h"
#include "protocols/samsung_inventory.h"
#include <algorithm>
#include <array>
#include <cstring>
#include <set>

namespace {
lpb_result cancellation(lpb_context* c) {
 const auto r=lpb_context_check_cancelled(c);if(r!=LPB_RESULT_OK)return r;
 std::scoped_lock lock(c->lifetime_mutex);return c->destroy_requested?LPB_RESULT_CANCELLED:LPB_RESULT_OK;
}
struct adapter {
 lpb_context* context;const lpb_target_reader_v1& input;lpb_result error{LPB_RESULT_OK};
 static bool read(void* user,uint64_t offset,std::span<uint8_t> bytes) noexcept {
  auto& s=*static_cast<adapter*>(user);
  if(s.error!=LPB_RESULT_OK)return false;
  if(offset>s.input.byte_length||bytes.size()>s.input.byte_length-offset){s.error=LPB_RESULT_INVALID_ARGUMENT;return false;}
  try {
   while(!bytes.empty()) {
    s.error=cancellation(s.context);if(s.error!=LPB_RESULT_OK)return false;
    const uint32_t n=static_cast<uint32_t>(std::min<size_t>(bytes.size(),1024*1024));uint32_t got=0;
    {lpb_writer_callback_frame frame(s.context);s.error=s.input.read_at(s.input.user,offset,bytes.data(),n,&got);}
    if(s.error!=LPB_RESULT_OK)return false;
    if(got!=n){s.error=LPB_RESULT_INTERNAL_ERROR;return false;}
    s.error=cancellation(s.context);if(s.error!=LPB_RESULT_OK)return false;
    offset+=n;bytes=bytes.subspan(n);
   }
   return true;
  }catch(...){s.error=LPB_RESULT_INTERNAL_ERROR;return false;}
 }
 lpb::random_access_reader view(){return {input.byte_length,this,read};}
};
struct slice {
 lpb::random_access_reader parent;uint64_t at,length;
 static bool read(void* user,uint64_t offset,std::span<uint8_t> bytes) noexcept {
  const auto& s=*static_cast<slice*>(user);return offset<=s.length&&bytes.size()<=s.length-offset&&s.parent.read_exact(s.at+offset,bytes);
 }
 lpb::random_access_reader view(){return {length,this,read};}
};
bool hash_range(const lpb::random_access_reader& r,uint64_t at,uint64_t count,uint8_t out[32]) {
 if(at>r.length||count>r.length-at)return false;
 lpb::crypto::sha256_ctx hash;std::array<uint8_t,65536> bytes{};
 while(count){const size_t n=static_cast<size_t>(std::min<uint64_t>(bytes.size(),count));
  if(!r.read_exact(at,std::span<uint8_t>(bytes.data(),n)))return false;
  hash.update(bytes.data(),n);at+=n;count-=n;
 }hash.finalize(out);return true;
}
bool selects(const isobmff_video_presentation_timeline& timeline,int64_t us) {
 if(us<0||us>INT64_MAX/10)return false;const int64_t stamp=us*10;
 for(const auto& s:timeline.presentation_order)if(s.visible&&s.normalized_time_100ns>=0&&s.duration_100ns>0&&stamp>=s.normalized_time_100ns&&stamp-s.normalized_time_100ns<s.duration_100ns)return true;
 return false;
}
}
static lpb_result validate_target_extended(lpb_context* context,const lpb_target_validation_request_v2* request,
 const lpb_target_reader_v1* reader,lpb_target_validation_result_v2* output,const lpb_compound_primary_observation_v1* compound,
 const lpb_target_ordinary_metadata_expectation_v1* metadata=nullptr,lpb_target_ordinary_metadata_observed_v1* metadata_observed=nullptr) {
 if(!context||lpb_writer_callback_active(context))return LPB_RESULT_AUTHORITY_VIOLATION;
 lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
 lpb_writer_frame frame(context);
 set_error(context,"");
 if(!output||output->struct_size!=sizeof(*output)||output->version!=2)return LPB_RESULT_INVALID_ARGUMENT;
 *output={};output->struct_size=sizeof(*output);output->version=2;
 auto& result=output->base;result.struct_size=sizeof(result);result.version=1;result.failure_byte_offset=UINT64_MAX;
 const auto reject=[&](uint32_t category,uint32_t stage,uint32_t rule,const char* message,bool unsupported=false){
  result.structural_status=unsupported?3U:2U;result.failure_category=category;result.failure_stage=stage;result.rule_id=rule;
  set_error(context,message);return LPB_RESULT_OK;
 };
 try {
  if(!request||!reader||request->struct_size!=sizeof(*request)||request->version!=2||request->reserved||
   reader->struct_size!=sizeof(*reader)||reader->version!=1||!reader->read_at||reader->reserved0||reader->reserved1||
   request->primary_timestamp_present>1||(!request->primary_timestamp_present&&request->primary_timestamp_us)||
   request->auxiliary_count>2||(request->auxiliary_count&&!request->auxiliaries)) {
   reject(1,1,101,"Malformed target v2 request or reader");return LPB_RESULT_INVALID_ARGUMENT;
  }
  const auto expected=request->base;
  if(expected.struct_size!=sizeof(expected)||expected.version!=1||expected.flags||expected.reserved||
   expected.cover_timestamp_present>1||(!expected.cover_timestamp_present&&expected.cover_timestamp_us)||
   expected.expected_video_codec<2||expected.expected_video_codec>3||expected.expected_auxiliary_count!=request->auxiliary_count) {
   reject(1,1,101,"Malformed target v2 base expectations");return LPB_RESULT_INVALID_ARGUMENT;
  }
  const bool v1=expected.target_profile==1,v2=expected.target_profile==2,oppo=expected.target_profile==3,vivo=expected.target_profile==4,huawei=expected.target_profile==6,samsung=expected.target_profile==5;
  const bool heif=expected.media_format==3||((huawei||samsung)&&(expected.media_format==2||expected.media_format==4));
  if((samsung?(expected.target_version!=2&&expected.target_version!=3):(expected.target_version!=1&&!(huawei&&expected.target_version==2)))||expected.member_count!=1||expected.member_index||expected.artifact_role||
   !(v1||v2||oppo||vivo||huawei||samsung)||!((v1&&expected.media_format<=1)||(v2&&(expected.media_format<=1||heif))||((oppo||vivo)&&expected.media_format==0)||(samsung&&(expected.media_format==0||expected.media_format==2))||(huawei&&(expected.media_format==0||expected.media_format==2||expected.media_format==4))))
   return reject(4,1,102,"Target/format combination is outside v2 validator rules",true);
  if(!oppo&&request->primary_timestamp_present)return reject(2,1,103,"Original timestamp expectation belongs to OPlus target only");
  if(compound&&(!huawei||expected.media_format!=0||compound->struct_size!=sizeof(*compound)||compound->version!=1||
      !compound->known_length||!compound->opaque_length))return reject(2,1,145,"Malformed compound primary expectations");
  std::array<lpb_target_auxiliary_expectation_v2,2> aux_expect{};std::set<uint32_t> semantics;
  for(uint32_t i=0;i<request->auxiliary_count;++i) {
   aux_expect[i]=request->auxiliaries[i];const auto& a=aux_expect[i];
   if(a.struct_size!=sizeof(a)||a.version!=2||a.semantic<1||a.semantic>5||a.container<1||a.container>2||!a.byte_length||!semantics.insert(a.semantic).second) {
    reject(1,1,104,"Malformed or duplicate auxiliary expectations");return LPB_RESULT_INVALID_ARGUMENT;
   }
  }
  adapter source{context,*reader};const auto full=source.view();
  const auto io=[&](){
   if(source.error==LPB_RESULT_OK)return false;
   result.structural_status=0;result.generic_media_status=0;result.failure_category=source.error==LPB_RESULT_CANCELLED?6U:5U;
   result.failure_stage=2;result.rule_id=105;return true;
  };
  if(!hash_range(full,0,full.length,result.observed_sha256)){io();return source.error;}
  result.observed_byte_length=full.length;
  uint64_t prefix_length=0,video_offset=0,video_length=0,video_header=0;
  uint64_t samsung_padding=0,samsung_resource=0;
  lpb::media::jpeg_observation jpeg;
  lpb::media::huawei_footer_observation footer;
  if(samsung) {
   lpb::protocols::sef_inventory inventory;std::string error;uint32_t version=0;
   uint64_t lower=0;
   if(heif) {
    const auto top=scan_top_level_boxes(full);const top_level_box* mpvd=nullptr;const top_level_box* sefd=nullptr;
    for(const auto& box:top) {
     if(!std::memcmp(box.type,"mpvd",4)){if(mpvd)return reject(3,3,150,"Samsung HEIC duplicate mpvd owner");mpvd=&box;}
     if(!std::memcmp(box.type,"sefd",4)){if(sefd)return reject(3,3,150,"Samsung HEIC duplicate sefd owner");sefd=&box;}
    }
    if(!mpvd||!sefd||mpvd->header_size!=8||sefd->header_size!=8||mpvd->offset+mpvd->size!=sefd->offset||
       sefd->offset+sefd->size!=full.length||mpvd->size<=8||sefd->size<=8)return reject(2,3,150,"Samsung HEIC mpvd/sefd must be explicit adjacent final siblings");
    std::array<uint8_t,4> box_size{};
    if(!full.read_exact(mpvd->offset,box_size)){if(io())return source.error;return reject(2,3,150,"Samsung mpvd header missing");}
    if(std::all_of(box_size.begin(),box_size.end(),[](auto b){return b==0;}))return reject(2,3,150,"Samsung mpvd implicit size rejected");
    if(!full.read_exact(sefd->offset,box_size)){if(io())return source.error;return reject(2,3,150,"Samsung sefd header missing");}
    if(std::all_of(box_size.begin(),box_size.end(),[](auto b){return b==0;}))return reject(2,3,150,"Samsung sefd implicit size rejected");
    prefix_length=mpvd->offset;video_offset=mpvd->offset+8;video_length=mpvd->size-8;video_header=8;lower=sefd->offset+8;
   }
   if(!lpb::protocols::observe_sef(full,lower,full.length,inventory,error)) {
    if(io())return source.error;return reject(2,4,151,error.c_str());
   }
   if(heif&&inventory.begin!=lower)return reject(2,4,151,"SEFD contains bytes outside indexed tag ownership");
   if(!lpb::protocols::read_sef_version(full,inventory,version)||version!=expected.target_version)return reject(2,4,152,"Samsung MotionPhoto_Version missing or expectation mismatch");
   const auto* motion=inventory.find(0x0a30);
   if(!motion)return reject(2,4,153,"Samsung MotionPhoto_Data owner missing");
   if(heif) {
    std::array<uint8_t,12> pointer{};
    if(motion->data_size!=12||!full.read_exact(motion->data_offset,pointer)){if(io())return source.error;return reject(2,4,153,"Samsung HEIC pointer extent invalid");}
    const auto be=[](const uint8_t* p){return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|uint32_t(p[3]);};
    if(std::memcmp(pointer.data(),"mpv2",4)||be(pointer.data()+4)!=video_offset||be(pointer.data()+8)!=video_length)
     return reject(2,4,153,"Samsung HEIC absolute video pointer/signature/size mismatch");
   }else {
    prefix_length=inventory.begin;video_offset=motion->data_offset;video_length=motion->data_size;
    if(!video_length||video_offset<prefix_length||video_offset>=full.length)return reject(2,4,153,"Samsung JPEG video extent invalid");
    samsung_padding=video_offset-prefix_length;
    for(const auto& tag:inventory.tags) {
     if(tag.marker==0x0a30)continue;
     if(tag.marker==0x0a31) {if(tag.offset!=video_offset+video_length)return reject(2,4,154,"Samsung output version tag must follow the complete movie");}
     else if(tag.offset+tag.size>motion->offset)return reject(2,4,154,"Samsung ordinary tag overlaps or follows movie ownership");
    }
   }
   samsung_resource=full.length-video_offset;
  }else if(huawei) {
   std::array<uint8_t,60> tail{};
   if(full.length<=60||!full.read_exact(full.length-60,tail)){if(io())return source.error;return reject(2,4,140,"Huawei fixed footer is missing");}
   if(!lpb::media::observe_huawei_footer(tail,footer)||footer.variant!=expected.target_version||footer.movie_length>=full.length-60)
    return reject(2,4,140,"Huawei footer variant, numeric fields or LIVE length is invalid");
   video_length=footer.movie_length;prefix_length=video_offset=full.length-60-video_length;
  }else if(!heif) {
   lpb::media::jpeg_observation_failure why;uint64_t bad=0;
   if(!lpb::media::observe_jpeg(full,jpeg,why,bad,{true,true,true,true})) {
    if(io())return source.error;result.failure_byte_offset=bad;
    return reject(why==lpb::media::jpeg_observation_failure::unsupported?4:2,3,106,"Invalid JPEG marker/Exif/XMP transport",why==lpb::media::jpeg_observation_failure::unsupported);
   }
   lpb::media::target_xmp_observation facts;lpb::media::xmp_observation_failure why_xml;
   if(!lpb::media::observe_target_xmp_packets(jpeg.xmp,jpeg.extended_xmp,jpeg.extended_guid,facts,why_xml))
    return reject(why_xml==lpb::media::xmp_observation_failure::ambiguous?3:2,4,107,"Invalid or shadowed logical XMP ownership");
   if(v1)video_length=facts.micro_offset;
   else {size_t count=0;for(const auto& item:facts.items)if(item.semantic=="MotionPhoto"){++count;video_length=item.length;if(!item.has_length)return reject(2,4,108,"MotionPhoto length is missing");}
    if(count!=1)return reject(2,4,108,"MotionPhoto inventory must contain one video");}
   if(!video_length||video_length>=full.length)return reject(2,4,108,"Video length is outside the pinned file");
   prefix_length=video_offset=full.length-video_length;
  }else {
   const auto top=scan_top_level_boxes(full);size_t count=0;
   for(const auto& b:top)if(!std::memcmp(b.type,"mpvd",4)) {
    if(++count!=1||b.offset+b.size!=full.length||b.size<=b.header_size)return reject(2,3,109,"HEIF requires one nonempty final mpvd");
    prefix_length=b.offset;video_header=b.header_size;video_offset=b.offset+b.header_size;video_length=b.size-b.header_size;
    std::array<uint8_t,4> size_bytes{};
    if(!full.read_exact(b.offset,size_bytes)){io();return source.error;}
    if(std::all_of(size_bytes.begin(),size_bytes.end(),[](uint8_t value){return value==0;}))return reject(2,3,109,"Implicit-size mpvd is not an explicit target extent");
   }
   if(count!=1||!prefix_length)return reject(2,3,109,"HEIF mpvd is missing or unowned");
  }
  constexpr uint64_t primary_resource_limit=64ULL*1024*1024;
  if(prefix_length>primary_resource_limit)return reject(4,3,110,"Primary/auxiliary structural snapshot exceeds 64MiB resource bound",true);
  std::vector<uint8_t> prefix(static_cast<size_t>(prefix_length));
  if(!full.read_exact(0,prefix)){io();return source.error;}
  if(metadata) {
   if(!metadata_observed||metadata->struct_size!=sizeof(*metadata)||metadata->version!=1||metadata->kind!=1||
      metadata->owner_index||metadata->flags||metadata->reserved||!metadata->byte_length||metadata->byte_length>32ULL*1024*1024||
      std::all_of(std::begin(metadata->sha256),std::end(metadata->sha256),[](uint8_t b){return b==0;}))
    return reject(1,1,154,"Malformed ordinary metadata expectations");
   lpb::media::ordinary_metadata::observed_capsule actual;
   const auto presence=heif?lpb::media::ordinary_metadata::observe_heif_capsule(prefix,actual)
    :lpb::media::ordinary_metadata::observe_jpeg_capsule(prefix,actual);
   if(presence!=lpb::media::ordinary_metadata::observation::present||actual.raw.size()!=metadata->byte_length)
    return reject(3,4,154,"Ordinary metadata owner is missing, duplicate, malformed or has the wrong length");
   const auto hash=lpb::media::ordinary_metadata::digest(actual.raw);
   if(std::memcmp(hash.data(),metadata->sha256,32))return reject(3,4,154,"Ordinary metadata raw bytes differ from the expected artifact");
   metadata_observed->kind=1;metadata_observed->owner_index=0;metadata_observed->byte_length=actual.raw.size();
   std::copy(hash.begin(),hash.end(),metadata_observed->sha256);metadata_observed->chunk_count=actual.chunks;
   metadata_observed->container=heif?2U:1U;
  }
  lpb::media::target_primary_observation primary;std::string error;
  if(compound) {
   if(compound->known_length>=prefix_length||compound->opaque_length!=prefix_length-compound->known_length)
    return reject(2,3,146,"Compound primary ranges do not exactly partition the observed prefix");
   uint8_t digest[32]{};
   if(!hash_range(full,compound->known_length,compound->opaque_length,digest)){if(io())return source.error;return reject(2,3,146,"Opaque range read failed");}
   if(std::memcmp(digest,compound->opaque_sha256,32))return reject(2,3,146,"Opaque source-prefix bytes were changed or lost");
  }
  const auto known=std::span<const uint8_t>(prefix).first(static_cast<size_t>(compound?compound->known_length:prefix_length));
  const bool observed=samsung&&!heif?lpb::media::observe_samsung_jpeg_prefix(prefix,full.length,samsung_padding,primary,error):
   !heif&&!v1&&!huawei?lpb::media::observe_target_jpeg_prefix(prefix,full.length,primary,error):lpb::media::observe_target_primary(known,primary,error,false,huawei?known.size():full.length);
  if(!observed)return reject(2,3,111,error.c_str());
  if(expected.target_profile>=LPB_TARGET_GOOGLE_V1&&expected.target_profile<=LPB_TARGET_OPPO&&
     std::any_of(primary.auxiliaries.begin(),primary.auxiliaries.end(),[](const auto& a){return a.semantic=="MpfAuxiliaryImage";})) {
   lpb::media::mpf_vendor_primary vendor;
   if(primary.auxiliaries.size()!=1||!lpb::media::observe_cross_target_mpf_primary(prefix,vendor,error))
    return reject(2,3,111,("Cross-target MPF secondary ownership: "+error).c_str());
  }
  if(primary.container!=(heif?LPB_IMAGE_CONTAINER_HEIC:LPB_IMAGE_CONTAINER_JPEG)||!primary.width||!primary.height)
   return reject(2,3,112,"Primary representation does not match target MIME");
  if(primary.singleton_mpf&&!primary.singleton_size_matches)return reject(2,3,113,"MPF singleton size does not match actual primary encoding");
  const auto exif=primary.exif_payload.empty()?LPB_RESULT_OK:lpb::media::observe_target_neutral_exif(primary.exif_payload);
  if(exif!=LPB_RESULT_OK)return reject(exif==LPB_RESULT_UNSUPPORTED?4:2,3,114,"Primary retains foreign live Exif ownership",exif==LPB_RESULT_UNSUPPORTED);
  const auto& x=primary.xmp;
  if(huawei) {
   if(lpb::media::target_primary_has_live_binding(primary)||x.foreign_claim||x.oppo_claim||x.vivo_claim)
    return reject(2,4,141,"Huawei primary has a second or foreign live owner");
   if(!expected.cover_timestamp_present||(expected.media_format==4&&expected.expected_video_codec!=3))
    return reject(4,6,142,"Huawei requires known cover and matching H264/HEVC media selection",true);
  }else if(v1) {
   if(!x.micro_claim||x.micro_video!=1||x.micro_version!=1||x.micro_offset!=video_length||x.live_claim||x.oppo_claim||x.vivo_claim)
    return reject(2,4,115,"V1 binding/version/offset or foreign ownership mismatch");
   if(!expected.cover_timestamp_present)return reject(4,6,116,"Unknown V1 cover has no established writable semantic",true);
   if(!x.micro_cover_present||x.micro_cover_us!=expected.cover_timestamp_us)return reject(2,6,116,"V1 cover expectation mismatch");
   result.observed_cover_timestamp_us=x.micro_cover_us;result.cover_encoding=1;
  }else {
   // Legacy MicroVideo fields are deliberately ignored as V2 location authority.
   if(!x.live_claim||x.motion_photo!=1||x.motion_version!=1||(!vivo&&x.vivo_claim)||(!oppo&&x.oppo_claim))
    return reject(2,4,117,"V2 identity/version or foreign ownership mismatch");
   size_t primaries=0,videos=0;
   for(const auto& item:x.items){if(item.semantic=="Primary")++primaries;if(item.semantic=="MotionPhoto")++videos;}
   if(primaries!=1||videos!=1||x.items.empty()||x.items.front().semantic!="Primary"||x.items.back().semantic!="MotionPhoto")
    return reject(2,4,118,"V2 primary/video cardinality or order mismatch");
   const auto& p=x.items.front();const auto& v=x.items.back();
   if(p.mime!=(heif?"image/heic":"image/jpeg")||(p.has_length&&p.length)||v.mime!=(expected.media_format==0||samsung?"video/mp4":"video/quicktime")||
      !v.has_length||v.length!=(samsung?samsung_resource:video_length)||v.padding||(heif&&p.padding!=video_header))return reject(2,4,119,"Target XMP MIME/extent/padding does not match actual placement");
   if(heif&&x.items.size()!=2) {
    const bool embedded_samsung=samsung&&x.items.size()==3&&x.items[1].semantic=="GainMap"&&x.items[1].mime=="image/heic"&&
      x.items[1].has_length&&!x.items[1].length&&!x.items[1].padding&&
      std::count_if(primary.auxiliaries.begin(),primary.auxiliaries.end(),[](const auto& a){return a.semantic=="GainMap";})==1;
    if(!embedded_samsung)return reject(2,4,119,"HEIF embedded auxiliaries belong to its item graph; external directory items would change placement");
   }
   if(expected.cover_timestamp_present) {
    if(!x.cover_present||x.cover_us!=expected.cover_timestamp_us)return reject(2,6,120,"Current cover expectation mismatch");
   }else if(x.cover_present&&x.cover_us!=-1)return reject(2,6,120,"Unknown cover must remain unset or absent");
   result.observed_cover_timestamp_us=x.cover_us;result.cover_encoding=!x.cover_present?3:x.cover_us==-1?2:1;
  }
  if(oppo) {
   if(!x.oppo_claim||x.oppo_owner!="oplus"||!x.oppo_version_present||x.oppo_version!=2||!x.oppo_length_present||x.oppo_video_length!=video_length||x.edit_flag_present)
    return reject(2,4,121,"OPlus synthetic binding/version/video length or fabricated edit history");
   if(!request->primary_timestamp_present)return reject(4,6,122,"OPlus original timestamp is unknown and cannot be inferred",true);
   if(!x.primary_cover_present||x.primary_cover_us!=request->primary_timestamp_us)return reject(2,6,122,"OPlus original timestamp expectation mismatch");
   constexpr char comment[]="ASCII\0\0\0oplus_10485792";
   const auto& raw=primary.jpeg.exif.user_comment;
   constexpr char observed_oppo[]="ASCII\0\0\0oplus_8388640";
   constexpr char observed_edited[]="oplus_27263008\0";
   const auto equal=[&](const char* bytes,size_t length){return raw.size()==length&&std::memcmp(raw.data(),bytes,length)==0;};
   if(!equal(comment,sizeof(comment)-1)&&!equal(observed_oppo,sizeof(observed_oppo)-1)&&!equal(observed_edited,sizeof(observed_edited)-1))
    return reject(2,4,123,"OPlus UserComment raw signature mismatch");
   output->observed_primary_timestamp_us=x.primary_cover_us;output->primary_timestamp_encoding=1;
  }
  if(vivo) {
   if(!x.vivo_claim||!x.vivo_version_present||x.vivo_version!=1||x.vivo_source_present||x.vivo_mediakit_present||x.vivo_flags_present||x.oppo_claim||x.micro_claim)
    return reject(2,4,160,"Vivo experimental identity/version or fabricated camera history mismatch");
   if(x.items.front().has_length||x.items.front().has_padding||!x.items.back().has_padding||x.items.back().padding||std::any_of(primary.auxiliaries.begin(),primary.auxiliaries.end(),[](const auto& a){return a.semantic!="GainMap";}))
    return reject(2,4,161,"Vivo unsupported padding or non-GainMap auxiliary inventory");
   std::map<std::string,std::string> observed_comment;
   if(!primary.jpeg.exif.has_user_comment||!lpb::media::observe_raw_camera_comment(primary.jpeg.exif.user_comment,observed_comment))
    return reject(2,4,162,"Vivo raw UTF8 NUL-terminated UserComment syntax mismatch");
   const auto have=[&](const char* k,const char* v){const auto f=observed_comment.find(k);return f!=observed_comment.end()&&f->second==v;};
   if(!have("multi-frame","1")||!have("ispap","1")||!have("module","photo"))
    return reject(2,4,163,"Vivo experimental comment fields mismatch");
   // SDR remains experimental: a GainMap is never manufactured to satisfy a
   // camera-template assumption. Existing HDR ownership is checked below.
  }
  if(primary.auxiliaries.size()!=request->auxiliary_count)return reject(2,4,124,"Auxiliary cardinality differs from semantic expectations");
  std::set<uint32_t> actual_semantics;
  for(size_t i=0;i<primary.auxiliaries.size();++i) {
   const auto& a=primary.auxiliaries[i];const uint32_t semantic=a.semantic=="GainMap"?1:a.semantic=="Original"?2:a.semantic=="MiCameraSubimage"?3:a.semantic=="MiCameraLensWatermark"?4:a.semantic=="MpfAuxiliaryImage"?5:0;
   const uint32_t container=a.mime=="image/jpeg"||a.mime=="image/png"?1:a.mime=="image/heic"?2:0;
   if(!semantic||!container||!actual_semantics.insert(semantic).second||a.owner_selector!=primary.primary_selector||!a.range.length)
    return reject(2,4,125,"Auxiliary selector/semantic/owner graph is invalid");
   if(a.range.offset>prefix_length||a.range.length>prefix_length-a.range.offset)
    return reject(2,4,125,"Auxiliary extent is outside actual primary container");
   if(a.dependency_ids.size()!=a.dependency_ranges.size())return reject(2,4,125,"Auxiliary dependency graph is incomplete");
   for(const auto& range:a.dependency_ranges)if(!range.length||range.offset>prefix_length||range.length>prefix_length-range.offset)
    return reject(2,4,125,"Auxiliary dependency extends outside actual primary container");
   const lpb_target_auxiliary_expectation_v2* wanted=nullptr;
   for(uint32_t j=0;j<request->auxiliary_count;++j)if(aux_expect[j].semantic==semantic)wanted=&aux_expect[j];
   if(!wanted||wanted->container!=container||wanted->byte_length!=a.range.length)return reject(2,4,125,"Auxiliary semantic representation mismatch");
   auto& actual=output->auxiliaries[i];actual.semantic=semantic;actual.container=container;actual.selector_kind=a.selector_kind;actual.selector_id=a.selector_id;
   actual.byte_offset=a.range.offset;actual.byte_length=a.range.length;
   if(!hash_range(full,a.range.offset,a.range.length,actual.sha256)){if(io())return source.error;return reject(2,4,125,"Auxiliary extent exceeds actual file");}
   if(std::memcmp(actual.sha256,wanted->sha256,32))return reject(2,4,125,"Auxiliary bytes differ from expected semantic asset");
  }
  output->auxiliary_count=static_cast<uint32_t>(primary.auxiliaries.size());result.observed_auxiliary_count=output->auxiliary_count;
  slice video{full,video_offset,video_length};lpb::media::target_video_observation media;lpb::media::target_video_diagnostic diagnostic;
  lpb::media::target_covertime_observation cover_properties;
  if(!lpb::media::observe_target_video(video.view(),expected.media_format==0||huawei||samsung,media,diagnostic,huawei?&cover_properties:nullptr)) {
   if(io())return source.error;const bool unsupported=diagnostic.failure==lpb::media::target_video_failure::unsupported;
   result.generic_media_status=unsupported?3:2;result.failure_byte_offset=diagnostic.offset==UINT64_MAX?UINT64_MAX:video_offset+diagnostic.offset;
   return reject(unsupported?4:2,5,126,diagnostic.reason.c_str(),unsupported);
  }
  if(media.media.quicktime!=(!huawei&&!samsung&&expected.media_format!=0)||media.media.video_codec!=expected.expected_video_codec)
   return reject(2,5,127,"Observed video container/codec differs from target expectations");
  if(expected.cover_timestamp_present&&!selects(media.media.timeline,expected.cover_timestamp_us))return reject(2,6,128,"Current cover does not select an observed visible sample");
  if(oppo&&!selects(media.media.timeline,request->primary_timestamp_us))return reject(2,6,129,"Original timestamp does not select an observed visible sample");
  if(huawei) {
   int64_t observed_time=0;uint32_t frame_index=0,frame_count=0;
   if(media.media.sample_tables.size()!=2||!lpb::media::observe_huawei_covertime(cover_properties,observed_time)||
      observed_time!=expected.cover_timestamp_us||!lpb::media::select_huawei_frame(media.media.timeline,observed_time,frame_index,frame_count)||
      footer.frame!=frame_index||footer.history!=std::to_string(frame_index)+":"+std::to_string(frame_count))
    return reject(2,6,143,"Huawei typed millisecond cover, legacy frame or new-file frame/count history mismatch");
   const auto& boxes=media.media.top_level_boxes;
   std::array<uint8_t,4> brand{};
   if(boxes.empty()||std::memcmp(boxes.front().type,"ftyp",4)||!video.view().read_exact(boxes.front().header_size,brand)||std::memcmp(brand.data(),"mp42",4))
    return reject(2,5,144,"Huawei movie brand differs from mp42");
   result.observed_cover_timestamp_us=observed_time;result.cover_encoding=1;
  }
  result.primary_length=prefix_length;result.video_offset=video_offset;result.video_length=video_length;
  result.image_width=primary.width;result.image_height=primary.height;result.image_orientation=primary.orientation;
  result.video_width=media.media.video_width;result.video_height=media.media.video_height;result.video_orientation=media.media.video_orientation;result.observed_video_codec=media.media.video_codec;
  result.observed_target_profile=expected.target_profile;result.observed_target_version=expected.target_version;result.observed_media_format=expected.media_format;
  if(heif) {
   const auto decoded=lpb::media::validate_heic_bytes(context,prefix);
   if(decoded==LPB_RESULT_CANCELLED){result.failure_category=6;result.failure_stage=5;return decoded;}
   if(decoded!=LPB_RESULT_OK){const bool unsupported=decoded==LPB_RESULT_UNSUPPORTED;result.generic_media_status=unsupported?3:2;
    return reject(unsupported?4:2,5,130,"Independent HEIF full decode rejected actual primary/auxiliary media",unsupported);}
  }
  const auto cancelled=cancellation(context);if(cancelled!=LPB_RESULT_OK){result.failure_category=6;result.failure_stage=2;return cancelled;}
  result.structural_status=1;result.generic_media_status=1;return LPB_RESULT_OK;
 }catch(...){reject(9,2,131,"Target v2 observation failed at exception boundary");return LPB_RESULT_INTERNAL_ERROR;}
}
lpb_result LPB_CALL lpb_target_validate_v2(lpb_context* context,const lpb_target_validation_request_v2* request,
 const lpb_target_reader_v1* reader,lpb_target_validation_result_v2* output) {
 return validate_target_extended(context,request,reader,output,nullptr);
}
lpb_result LPB_CALL lpb_target_validate_v3(lpb_context* context,const lpb_target_validation_request_v3* request,
 const lpb_target_reader_v1* reader,lpb_target_validation_result_v2* output) {
 if(!request||request->struct_size!=sizeof(*request)||request->version!=3||request->reserved)return LPB_RESULT_INVALID_ARGUMENT;
 return validate_target_extended(context,&request->base,reader,output,&request->compound);
}
lpb_result LPB_CALL lpb_target_validate_v4(lpb_context* context,const lpb_target_validation_request_v4* request,
 const lpb_target_reader_v1* reader,lpb_target_validation_result_v3* output) {
 if(!output||output->struct_size!=sizeof(*output)||output->version!=3)return LPB_RESULT_INVALID_ARGUMENT;
 *output={};output->struct_size=sizeof(*output);output->version=3;
 output->base.struct_size=sizeof(output->base);output->base.version=2;
 if(!request||request->struct_size!=sizeof(*request)||request->version!=4||request->reserved||request->reserved_tail||
    request->metadata_count!=1||!request->metadata||request->base.struct_size!=sizeof(request->base)||request->base.version!=3||
    request->base.reserved||request->base.compound.struct_size||request->base.compound.version||
    request->base.compound.known_length||request->base.compound.opaque_length||
    std::any_of(std::begin(request->base.compound.opaque_sha256),std::end(request->base.compound.opaque_sha256),[](uint8_t b){return b!=0;}))
  return LPB_RESULT_INVALID_ARGUMENT;
 const auto target=request->base.base.base.target_profile;
 if(target<LPB_TARGET_GOOGLE_V1||target>LPB_TARGET_OPPO)return LPB_RESULT_UNSUPPORTED;
 lpb_target_ordinary_metadata_observed_v1 observed{};
 const auto result=validate_target_extended(context,&request->base.base,reader,&output->base,nullptr,request->metadata,&observed);
 // Rule rejection deliberately returns OK with a failing base verdict. A
 // successful call is not a complete preservation proof.
 if(result==LPB_RESULT_OK&&output->base.base.structural_status==1&&output->base.base.generic_media_status==1&&
    observed.kind==1&&observed.owner_index==0&&observed.byte_length==request->metadata->byte_length&&observed.chunk_count&&
    !std::memcmp(observed.sha256,request->metadata->sha256,32)) {
  output->metadata=observed;output->metadata_count=1;output->metadata_preservation_status=1;
 }
 return result;
}
