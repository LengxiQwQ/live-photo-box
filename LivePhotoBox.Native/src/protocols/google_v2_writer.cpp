#include "protocols/google_v2_writer.h"
#include "media/jpeg_observation.h"
#include "media/xmp_observation.h"
#include "metadata/jpeg.h"
#include "foundation/portable_internal.h"
namespace lpb::target {
lpb_result build_google_v2_primary(lpb_context* context,std::span<const uint8_t> input,uint64_t video_length,
 uint32_t format,bool cover_present,int64_t cover,std::vector<uint8_t>& output) noexcept {
 output.clear();
 try {
  lpb::memory_random_access memory{input};lpb::media::jpeg_observation jpeg;
  lpb::media::jpeg_observation_failure failure{};uint64_t bad=0;
  if(!lpb::media::observe_jpeg(memory.view(),jpeg,failure,bad))return failure==lpb::media::jpeg_observation_failure::unsupported?LPB_RESULT_UNSUPPORTED:LPB_RESULT_INVALID_ARGUMENT;
  if(jpeg.eoi_end!=input.size()||jpeg.metadata_after_scan)return LPB_RESULT_UNSUPPORTED;
  const std::string description="<lpbrdf:Description xmlns:lpbrdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" lpbrdf:about=\"\" xmlns:lpbc=\"http://ns.google.com/photos/1.0/camera/\" xmlns:lpbcontainer=\"http://ns.google.com/photos/1.0/container/\" xmlns:lpbi=\"http://ns.google.com/photos/1.0/container/item/\" lpbc:MotionPhoto=\"1\" lpbc:MotionPhotoVersion=\"1\" lpbc:MotionPhotoPresentationTimestampUs=\""+
   std::to_string(cover_present?cover:-1)+"\"><lpbcontainer:Directory><lpbrdf:Seq><lpbrdf:li lpbrdf:parseType=\"Resource\"><lpbcontainer:Item lpbi:Mime=\"image/jpeg\" lpbi:Semantic=\"Primary\" lpbi:Length=\"0\" lpbi:Padding=\"0\"/></lpbrdf:li><lpbrdf:li lpbrdf:parseType=\"Resource\"><lpbcontainer:Item lpbi:Mime=\""+
   std::string(format==0?"video/mp4":"video/quicktime")+"\" lpbi:Semantic=\"MotionPhoto\" lpbi:Length=\""+std::to_string(video_length)+"\"/></lpbrdf:li></lpbrdf:Seq></lpbcontainer:Directory></lpbrdf:Description>";
  std::string xml=jpeg.xmp;
  if(xml.empty())xml="<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"+description+"</rdf:RDF></x:xmpmeta>";
  else {
   lpb::media::target_xmp_observation observed;lpb::media::xmp_observation_failure why{};
   if(!lpb::media::observe_target_xmp(xml,observed,why))return why==lpb::media::xmp_observation_failure::unsupported?LPB_RESULT_UNSUPPORTED:LPB_RESULT_INVALID_ARGUMENT;
   if(observed.live_claim||observed.foreign_claim||observed.rdf_close_offset==std::string::npos)return LPB_RESULT_UNSUPPORTED;
   xml.insert(observed.rdf_close_offset,description);
  }
  if(xml.size()>65535-2-sizeof("http://ns.adobe.com/xap/1.0/"))return LPB_RESULT_UNSUPPORTED;
  std::string error;
  const auto result=lpb::metadata::replace_standard_jpeg_xmp_unchecked(input,
   std::span<const uint8_t>(reinterpret_cast<const uint8_t*>(xml.data()),xml.size()),output,error);
  if(result!=LPB_RESULT_OK)set_error(context,error.c_str());
  return result;
 }catch(...){set_error(context,"Google V2 primary serialization failed.");return LPB_RESULT_INTERNAL_ERROR;}
}
}
