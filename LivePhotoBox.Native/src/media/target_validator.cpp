#include "media/target_validator.h"
#include "media/jpeg_observation.h"
#include "media/xmp_observation.h"
#include "containers/isobmff.h"
#include "containers/isobmff_video_profile.h"
#include "foundation/internal.h"
#include "foundation/sha256_core.h"
#include "protocols/target_structure.h"
#include <array>
#include <algorithm>
#include <cstring>
static_assert(sizeof(lpb_target_reader_v1)==48);
static_assert(sizeof(lpb_target_validation_request_v1)==64);
static_assert(sizeof(lpb_target_validation_result_v1)==176);
static_assert(offsetof(lpb_target_validation_result_v1,observed_sha256)==144);
namespace {
lpb_result cancelled(lpb_context* context) {
 const auto result=lpb_context_check_cancelled(context);if(result!=LPB_RESULT_OK)return result;
 std::scoped_lock lock(context->lifetime_mutex);return context->destroy_requested?LPB_RESULT_CANCELLED:LPB_RESULT_OK;
}
struct reader_adapter {
 lpb_context* context;const lpb_target_reader_v1& source;lpb_result error{LPB_RESULT_OK};
 static bool read(void* user,uint64_t offset,std::span<uint8_t> bytes) noexcept {
  auto& self=*static_cast<reader_adapter*>(user);
  if(self.error!=LPB_RESULT_OK)return false;
  if(offset>self.source.byte_length||bytes.size()>self.source.byte_length-offset){self.error=LPB_RESULT_INVALID_ARGUMENT;return false;}
  try {
   while(!bytes.empty()) {
    self.error=cancelled(self.context);if(self.error!=LPB_RESULT_OK)return false;
    const uint32_t count=static_cast<uint32_t>(std::min<size_t>(bytes.size(),1024*1024));uint32_t got=0;
    {lpb_writer_callback_frame callback(self.context);self.error=self.source.read_at(self.source.user,offset,bytes.data(),count,&got);}
    if(self.error!=LPB_RESULT_OK)return false;
    if(got!=count){self.error=LPB_RESULT_INTERNAL_ERROR;return false;}
    self.error=cancelled(self.context);if(self.error!=LPB_RESULT_OK)return false;
    bytes=bytes.subspan(count);offset+=count;
   }
   return true;
  }catch(...){self.error=LPB_RESULT_INTERNAL_ERROR;return false;}
 }
 lpb::random_access_reader view(){return {source.byte_length,this,read};}
};
struct slice_reader {
 lpb::random_access_reader parent;uint64_t start,length;
 static bool read(void* user,uint64_t at,std::span<uint8_t> out) noexcept {
  auto& s=*static_cast<slice_reader*>(user);
  return at<=s.length&&out.size()<=s.length-at&&s.parent.read_exact(s.start+at,out);
 }
 lpb::random_access_reader view(){return {length,this,read};}
};
}
lpb_result LPB_CALL lpb_target_validate_v1(lpb_context* context,const lpb_target_validation_request_v1* request,
 const lpb_target_reader_v1* reader,lpb_target_validation_result_v1* output) {
 if(!context||lpb_writer_callback_active(context))return LPB_RESULT_AUTHORITY_VIOLATION;
 lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
 lpb_writer_frame frame(context);
 if(!output||output->struct_size!=sizeof(*output)||output->version!=1)return LPB_RESULT_INVALID_ARGUMENT;
 *output={};output->struct_size=sizeof(*output);output->version=1;output->failure_byte_offset=UINT64_MAX;
 const auto reject=[&](uint32_t category,uint32_t stage,uint32_t rule,bool unsupported=false){
  output->structural_status=unsupported?3U:2U;output->failure_category=category;output->failure_stage=stage;output->rule_id=rule;
  return LPB_RESULT_OK;
 };
 try {
  if(!request||!reader||request->struct_size!=sizeof(*request)||request->version!=1||
   reader->struct_size!=sizeof(*reader)||reader->version!=1||!reader->read_at||reader->reserved0||reader->reserved1||
   request->reserved||request->flags||request->cover_timestamp_present>1||
   (!request->cover_timestamp_present&&request->cover_timestamp_us)||request->member_count==0||
   request->member_index>=request->member_count||request->artifact_role>1||request->target_profile<1||request->target_profile>9||
   request->media_format>4||request->expected_video_codec<2||request->expected_video_codec>3) {
    reject(1,1,1);return LPB_RESULT_INVALID_ARGUMENT;
  }
  if(request->target_profile!=2||request->target_version!=1||request->media_format>1||
   request->member_count!=1||request->member_index||request->artifact_role||request->expected_auxiliary_count)
    return reject(4,1,13,true);
  reader_adapter adapter{context,*reader};const auto full=adapter.view();
  const auto read_failure=[&](){
   if(adapter.error==LPB_RESULT_OK)return false;
   output->structural_status=0;output->generic_media_status=0;output->failure_category=adapter.error==LPB_RESULT_CANCELLED?6U:5U;
   output->failure_stage=2;output->rule_id=2;return true;
  };
  lpb::crypto::sha256_ctx hash;std::array<uint8_t,65536> bytes{};
  for(uint64_t at=0;at<full.length;){const size_t n=static_cast<size_t>(std::min<uint64_t>(bytes.size(),full.length-at));
   if(!full.read_exact(at,std::span<uint8_t>(bytes.data(),n))){read_failure();return adapter.error;}
   hash.update(bytes.data(),n);at+=n;
  }
  hash.finalize(output->observed_sha256);output->observed_byte_length=full.length;
  lpb::media::jpeg_observation jpeg;lpb::media::jpeg_observation_failure jpeg_failure{};uint64_t bad=0;
  if(!lpb::media::observe_jpeg(full,jpeg,jpeg_failure,bad)) {
   if(read_failure())return adapter.error;output->failure_byte_offset=bad;
   output->generic_media_status=jpeg_failure==lpb::media::jpeg_observation_failure::unsupported?3U:2U;
   return reject(jpeg_failure==lpb::media::jpeg_observation_failure::unsupported?4U:2U,3,3,jpeg_failure==lpb::media::jpeg_observation_failure::unsupported);
  }
  output->primary_length=jpeg.eoi_end;output->image_width=jpeg.width;output->image_height=jpeg.height;output->image_orientation=jpeg.orientation;
  if(jpeg.standard_packets!=1)return reject(2,4,4);
  lpb::media::target_xmp_observation xmp;lpb::media::xmp_observation_failure xmp_failure{};
  if(!lpb::media::observe_target_xmp(jpeg.xmp,xmp,xmp_failure))return reject(
   xmp_failure==lpb::media::xmp_observation_failure::unsupported?4U: xmp_failure==lpb::media::xmp_observation_failure::ambiguous?3U:2U,4,5,
   xmp_failure==lpb::media::xmp_observation_failure::unsupported);
  if(!xmp.live_claim||xmp.foreign_claim||xmp.motion_photo!=1||xmp.motion_version!=1)return reject(2,4,6);
  size_t primary_count=0,video_count=0;
  for(const auto& item:xmp.items){if(item.semantic=="Primary")++primary_count;if(item.semantic=="MotionPhoto")++video_count;}
  if(primary_count!=1||video_count!=1)return reject(2,4,7);
  if(xmp.items.size()!=2)return reject(4,4,7,true);
  const auto& primary=xmp.items[0];const auto& video=xmp.items[1];
  if(primary.semantic!="Primary"||primary.mime!="image/jpeg"||(primary.has_length&&primary.length)||
   video.semantic!="MotionPhoto"||video.mime!=(request->media_format==0?"video/mp4":"video/quicktime")||
   !video.has_length||!video.length||(video.has_padding&&video.padding))return reject(2,4,7);
  if(video.length>full.length||jpeg.eoi_end>full.length-video.length||
   primary.padding!=full.length-video.length-jpeg.eoi_end)return reject(2,4,8);
  output->video_offset=full.length-video.length;output->video_length=video.length;
  slice_reader video_slice{full,output->video_offset,video.length};const auto video_reader=video_slice.view();
  isobmff_reader_observation media;isobmff_reader_diagnostic diagnostic;
  if(!inspect_isobmff_reader(video_reader,request->media_format==0,media,diagnostic)){
   if(read_failure())return adapter.error;output->generic_media_status=diagnostic.failure==isobmff_reader_failure::unsupported?3U:2U;
   output->failure_byte_offset=diagnostic.offset==UINT64_MAX?UINT64_MAX:output->video_offset+diagnostic.offset;
   return reject(diagnostic.failure==isobmff_reader_failure::unsupported?4U:2U,5,10,diagnostic.failure==isobmff_reader_failure::unsupported);
  }
  if(media.quicktime!=(request->media_format==1))return reject(2,5,9);
  if(media.opaque_metadata)return reject(4,5,13,true);
  lpb::media::isobmff_video_profile profile;lpb::media::video_profile_failure why{};
  if(!lpb::media::inspect_isobmff_video_profile(video_reader,profile,why)){
   if(read_failure())return adapter.error;return reject(why==lpb::media::video_profile_failure::unsupported?4U:2U,5,11,why==lpb::media::video_profile_failure::unsupported);
  }
  if(profile.classification!=lpb::media::video_profile_class::ordinary_sdr)return reject(4,5,11,true);
  output->observed_video_codec=media.video_codec;output->video_width=media.video_width;output->video_height=media.video_height;output->video_orientation=media.video_orientation;
  if(media.video_codec!=request->expected_video_codec)return reject(2,5,11);
  if(!xmp.cover_present)return reject(4,6,12,true);
  output->observed_cover_timestamp_us=xmp.cover_us;output->cover_encoding=xmp.cover_us==-1?2U:1U;
  if(request->cover_timestamp_present) {
   if(xmp.cover_us!=request->cover_timestamp_us||xmp.cover_us<0||xmp.cover_us>INT64_MAX/10)return reject(2,6,12);
   bool found=false;const int64_t stamp=xmp.cover_us*10;
   for(const auto& sample:media.timeline.presentation_order)if(sample.visible&&sample.normalized_time_100ns>=0&&
    stamp>=sample.normalized_time_100ns&&stamp-sample.normalized_time_100ns<sample.duration_100ns)found=true;
   if(!found)return reject(2,6,12);
  }else if(xmp.cover_us!=-1)return reject(2,6,12);
  const auto cancellation=cancelled(context);if(cancellation!=LPB_RESULT_OK){output->failure_category=6;output->failure_stage=2;return cancellation;}
  output->generic_media_status=1;output->structural_status=1;output->observed_target_profile=2;output->observed_target_version=1;output->observed_media_format=request->media_format;
  return LPB_RESULT_OK;
 }catch(...){reject(9,2,2);return LPB_RESULT_INTERNAL_ERROR;}
}
