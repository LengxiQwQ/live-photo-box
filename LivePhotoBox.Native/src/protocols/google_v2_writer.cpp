#include "protocols/google_v2_writer.h"
#include "media/jpeg_observation.h"
#include "media/xmp_observation.h"
#include "media/media_inspector.h"
#include "media/target_primary_observation.h"
#include "media/jpeg_gainmap_mpf.h"
#include "metadata/jpeg.h"
#include "metadata/target_exif.h"
#include "foundation/portable_internal.h"

namespace lpb::target {
namespace {
constexpr std::string_view camera="http://ns.google.com/photos/1.0/camera/";
std::string property(std::string_view name,std::string_view value,std::string_view ns=camera) {
 return "<lpbc:"+std::string(name)+" xmlns:lpbc=\""+std::string(ns)+"\">"+
  std::string(value)+"</lpbc:"+std::string(name)+">";
}
std::string resource(std::string_view semantic,std::string_view mime,uint64_t length,uint64_t padding=0) {
 return "<lpbrdf:li xmlns:lpbrdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" lpbrdf:parseType=\"Resource\">"
  "<lpbcontainer:Item xmlns:lpbcontainer=\"http://ns.google.com/photos/1.0/container/\" xmlns:lpbi=\"http://ns.google.com/photos/1.0/container/item/\" lpbi:Mime=\""+
  std::string(mime)+"\" lpbi:Semantic=\""+std::string(semantic)+"\" lpbi:Length=\""+std::to_string(length)+"\""+
  (semantic=="Primary"?" lpbi:Padding=\""+std::to_string(padding)+"\"":"")+"/></lpbrdf:li>";
}
}

lpb_result build_google_xmp(std::string_view input,const google_xmp_request& request,std::string& output) noexcept {
 output.clear();
 try {
  const bool samsung=request.target==5;
  if((request.target<1||request.target>3)&&!samsung)return LPB_RESULT_UNSUPPORTED;
  if((request.format!=0&&request.format!=1&&request.format!=3&&!(samsung&&request.format==2))||!request.video_length)return LPB_RESULT_UNSUPPORTED;
  const bool heif=request.format==3||(samsung&&request.format==2);
  if(request.primary_mime!=(heif?"image/heic":"image/jpeg")||
     (heif&&(request.primary_padding!=8&&request.primary_padding!=16)))return LPB_RESULT_UNSUPPORTED;
  if(request.target==1&&heif)return LPB_RESULT_UNSUPPORTED;
  if(request.target==3&&!request.primary_cover_present)return LPB_RESULT_UNSUPPORTED;
  if(request.primary_cover_present&&request.primary_cover_us<0)return LPB_RESULT_INVALID_ARGUMENT;
  bool gainmap=false,original=false;
  for(const auto& item:request.auxiliary_items){
   const bool embedded_samsung_gainmap=samsung&&heif&&item.semantic=="GainMap"&&item.mime=="image/heic"&&item.has_length&&!item.length&&!item.padding;
   if(!embedded_samsung_gainmap&&(!item.has_length||!item.length||item.padding||item.mime!="image/jpeg"))return LPB_RESULT_UNSUPPORTED;
   if(item.semantic=="GainMap"){if(gainmap||original)return LPB_RESULT_UNSUPPORTED;gainmap=true;}
   else if(item.semantic=="Original"){if(original)return LPB_RESULT_UNSUPPORTED;original=true;}
   else return LPB_RESULT_UNSUPPORTED;
  }
  // V1 facts establish known nonnegative cover only; do not import V2's -1 rule.
  if(request.target==1&&!request.cover_present)return LPB_RESULT_UNSUPPORTED;
  if(request.cover_present&&request.cover_us<0)return LPB_RESULT_INVALID_ARGUMENT;
  const bool v1=request.target==1;
  std::string properties;
  if(v1) properties=property("MicroVideo","1")+property("MicroVideoVersion","1")+
   property("MicroVideoOffset",std::to_string(request.video_length))+
   property("MicroVideoPresentationTimestampUs",std::to_string(request.cover_us));
  else properties=property("MotionPhoto","1")+property("MotionPhotoVersion","1")+
   property("MotionPhotoPresentationTimestampUs",std::to_string(request.cover_present?request.cover_us:-1));
  if(request.target==3){
   constexpr std::string_view op="http://ns.oplus.com/photos/1.0/camera/";
   properties+=property("MotionPhotoOwner","oplus",op)+property("OLivePhotoVersion","2",op)+
    property("VideoLength",std::to_string(request.video_length),op)+
    property("MotionPhotoPrimaryPresentationTimestampUs",std::to_string(request.primary_cover_us),op);
  }
  const std::string video=resource("MotionPhoto",request.format==0||samsung?"video/mp4":"video/quicktime",request.video_length);
  std::string assets;for(const auto& item:request.auxiliary_items)assets+=resource(item.semantic,item.mime,item.length);
  const std::string directory=v1&&request.auxiliary_items.empty()?"":"<lpbcontainer:Directory xmlns:lpbcontainer=\"http://ns.google.com/photos/1.0/container/\">"
   "<lpbrdf:Seq xmlns:lpbrdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"+
   resource("Primary",request.primary_mime,0,request.primary_padding)+assets+(v1?"":video)+"</lpbrdf:Seq></lpbcontainer:Directory>";
  const std::string description="<lpbrdf:Description xmlns:lpbrdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" lpbrdf:about=\"\">"+
   properties+directory+"</lpbrdf:Description>";
  if(input.empty()) {
   output="<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"+
    description+"</rdf:RDF></x:xmpmeta>";
   return LPB_RESULT_OK;
  }
  lpb::media::target_xmp_observation observed;lpb::media::xmp_observation_failure why{};
  if(!lpb::media::observe_target_xmp(input,observed,why))
   return why==lpb::media::xmp_observation_failure::unsupported?LPB_RESULT_UNSUPPORTED:LPB_RESULT_INVALID_ARGUMENT;
  if(observed.live_claim||observed.micro_claim||observed.foreign_claim||observed.oppo_claim||observed.vivo_claim||
     (observed.hdr_claim&&(!(gainmap||heif)||!request.hdr_relationship_validated)))return LPB_RESULT_UNSUPPORTED;
  std::string adjusted;
  if(heif&&observed.directory_present&&observed.items[0].padding!=request.primary_padding){
   if(!lpb::media::rewrite_target_primary_padding(input,request.primary_padding,adjusted,why)||
      !lpb::media::observe_target_xmp(adjusted,observed,why))return LPB_RESULT_INVALID_ARGUMENT;
   input=adjusted;
  }
  if(observed.directory_present) {
   if(observed.items.size()>1+request.auxiliary_items.size()||observed.items[0].semantic!="Primary"||observed.items[0].mime!=request.primary_mime||
      observed.items[0].length||observed.items[0].padding!=request.primary_padding||observed.owner_close_offset==std::string::npos||
      observed.sequence_close_offset==std::string::npos)return LPB_RESULT_UNSUPPORTED;
   for(size_t i=0;i+1<observed.items.size();++i){const auto& have=observed.items[i+1];const auto& want=request.auxiliary_items[i];
    if(have.semantic!=want.semantic||have.mime!=want.mime||!have.has_length||have.length!=want.length||have.padding)return LPB_RESULT_UNSUPPORTED;}
   if(observed.sequence_close_offset>=observed.owner_close_offset)return LPB_RESULT_INVALID_ARGUMENT;
   output=input;
   output.insert(observed.owner_close_offset,properties);
   std::string missing;for(size_t i=observed.items.size()-1;i<request.auxiliary_items.size();++i){const auto& item=request.auxiliary_items[i];missing+=resource(item.semantic,item.mime,item.length);}
   output.insert(observed.sequence_close_offset,missing+(v1?"":video));
  }else if(!observed.micamera_owner_name.empty()) {
   output=input;
   if(observed.micamera_owner_close_offset!=std::string::npos)
    output.insert(observed.micamera_owner_close_offset,properties+directory);
   else {
    const auto end=observed.micamera_owner_content_offset;
    if(end<2||end>input.size()||input.substr(end-2,2)!="/>")return LPB_RESULT_INVALID_ARGUMENT;
    output.replace(end-2,2,">"+properties+directory+"</"+observed.micamera_owner_name+">");
   }
  }else if(observed.rdf_close_offset!=std::string::npos){output=input;output.insert(observed.rdf_close_offset,description);}
  else {
   if(observed.rdf_content_offset<2||observed.rdf_content_offset>input.size()||observed.rdf_name.empty()||
      input.substr(observed.rdf_content_offset-2,2)!="/>")return LPB_RESULT_INVALID_ARGUMENT;
   output=input;
   output.replace(observed.rdf_content_offset-2,2,">"+description+"</"+observed.rdf_name+">");
  }
  return LPB_RESULT_OK;
 }catch(...){output.clear();return LPB_RESULT_INTERNAL_ERROR;}
}

lpb_result build_google_primary(lpb_context* context,std::span<const uint8_t> input,
 const google_xmp_request& request,std::vector<uint8_t>& output) noexcept {
 output.clear();
 try {
  lpb::memory_random_access memory{input};lpb::media::jpeg_observation jpeg;
  lpb::media::jpeg_observation_failure failure{};uint64_t bad=0;
  if(!lpb::media::observe_jpeg(memory.view(),jpeg,failure,bad,{true,true,true,true}))
   return failure==lpb::media::jpeg_observation_failure::unsupported?LPB_RESULT_UNSUPPORTED:LPB_RESULT_INVALID_ARGUMENT;
  if(request.format==3||jpeg.metadata_after_scan)return LPB_RESULT_UNSUPPORTED;
  uint64_t expected=jpeg.eoi_end;
  if(request.primary_padding>input.size()-expected)return LPB_RESULT_INVALID_ARGUMENT;
  expected+=request.primary_padding;
  for(const auto& item:request.auxiliary_items){
   if(item.mime!="image/jpeg"||!item.length||item.length>input.size()-expected)return LPB_RESULT_INVALID_ARGUMENT;
   if(item.semantic=="Original") {
    std::string original_error;
    if(!lpb::media::observe_original_jpeg_asset(input.subspan(static_cast<size_t>(expected),static_cast<size_t>(item.length)),original_error)) {
     set_error(context,original_error.c_str());return LPB_RESULT_INVALID_ARGUMENT;
    }
    expected+=item.length;continue;
   }
   lpb::memory_random_access asset{input.subspan(static_cast<size_t>(expected),static_cast<size_t>(item.length))};
   lpb::media::jpeg_observation aux;uint64_t aux_bad{};
   if(!lpb::media::observe_jpeg(asset.view(),aux,failure,aux_bad,{true,true,true,true})||aux.metadata_after_scan||
      !lpb::media::jpeg_gainmap_mpf::valid_jpeg_media_range(input,static_cast<size_t>(expected),static_cast<size_t>(item.length)))return LPB_RESULT_INVALID_ARGUMENT;
   lpb::media::target_xmp_observation aux_xml;lpb::media::xmp_observation_failure aux_why{};
   if(!aux.xmp.empty()&&(!lpb::media::observe_target_xmp_packets(aux.xmp,aux.extended_xmp,aux.extended_guid,aux_xml,aux_why)||
       aux_xml.live_claim||aux_xml.micro_claim||aux_xml.foreign_claim||aux_xml.directory_present))return LPB_RESULT_UNSUPPORTED;
   expected+=item.length;
  }
  if(expected!=input.size())return LPB_RESULT_INVALID_ARGUMENT;
  if(!jpeg.xmp.empty()) {
   lpb::media::target_xmp_observation logical;lpb::media::xmp_observation_failure why{};
   if(!lpb::media::observe_target_xmp_packets(jpeg.xmp,jpeg.extended_xmp,jpeg.extended_guid,logical,why))
    return why==lpb::media::xmp_observation_failure::unsupported?LPB_RESULT_UNSUPPORTED:LPB_RESULT_INVALID_ARGUMENT;
  }
  lpb::media::jpeg_gainmap_mpf::jpeg_info mpf;
  if(jpeg.mpf_index){
   if(!lpb::media::jpeg_gainmap_mpf::parse_jpeg(input,mpf)||mpf.mpf.image_count!=request.auxiliary_items.size()+1)return LPB_RESULT_UNSUPPORTED;
   // The approved singleton self-entry has no external image relationship.
   // Its redundant historical size may be stale (RELEASE-004); the complete
   // bounded JPEG, exact EOF and strict singleton identity authorize reflow.
   // Multi-image indexes retain strict original range/size validation.
   const bool singleton=lpb::media::jpeg_gainmap_mpf::singleton_primary(mpf)&&
    request.auxiliary_items.empty()&&!request.primary_padding&&jpeg.eoi_end==input.size();
   if(!singleton&&mpf.mpf.entries[0].size!=jpeg.eoi_end)return LPB_RESULT_UNSUPPORTED;
   uint64_t offset=jpeg.eoi_end+request.primary_padding;
   for(size_t i=1;i<mpf.mpf.image_count;++i){const auto& entry=mpf.mpf.entries[i];
    if(entry.absolute_offset!=offset||entry.size!=request.auxiliary_items[i-1].length||
       entry.dependent_image_1>mpf.mpf.image_count||entry.dependent_image_2>mpf.mpf.image_count)return LPB_RESULT_INVALID_ARGUMENT;
    offset+=entry.size;
   }
  }
  std::string xml;
  std::string rebased;
  std::string_view packet=jpeg.xmp;
  if(packet.find("XMPMeta")!=std::string_view::npos){
   if(!lpb::media::rebase_micamera_offsets(packet,request.video_length+(request.target==5?24:0),rebased))return LPB_RESULT_UNSUPPORTED;
   packet=rebased;
  }
  const auto merged=build_google_xmp(packet,request,xml);
  if(merged!=LPB_RESULT_OK)return merged;
  if(xml.size()>65535-2-sizeof("http://ns.adobe.com/xap/1.0/"))return LPB_RESULT_UNSUPPORTED;
  std::vector<uint8_t> commented;
  if(request.target==3){
   constexpr char raw[]="ASCII\0\0\0oplus_10485792";
   const std::span<const uint8_t> marker(reinterpret_cast<const uint8_t*>(raw),sizeof(raw)-1);
   if(jpeg.exif.has_user_comment){
    // Existing compatible marker bytes are retained, never overwritten to
    // force one camera's capacity literal. These exact variants were observed
    // in the retained OPlus corpus; no integer/capacity rule is inferred.
    constexpr char observed_oppo[]="ASCII\0\0\0oplus_8388640";
    constexpr char observed_edited[]="oplus_27263008\0";
    const auto equal=[&](const char* raw,size_t count){return jpeg.exif.user_comment.size()==count&&
        std::memcmp(jpeg.exif.user_comment.data(),raw,count)==0;};
    if(!equal(raw,sizeof(raw)-1)&&!equal(observed_oppo,sizeof(observed_oppo)-1)&&
       !equal(observed_edited,sizeof(observed_edited)-1))return LPB_RESULT_UNSUPPORTED;
   }else{
    if(!lpb::metadata::add_target_user_comment(input,marker,commented,failure))return failure==lpb::media::jpeg_observation_failure::unsupported?LPB_RESULT_UNSUPPORTED:LPB_RESULT_INVALID_ARGUMENT;
    input=commented;
   }
  }
  std::string error;
  const auto result=lpb::metadata::replace_standard_jpeg_xmp_unchecked(input,
   std::span<const uint8_t>(reinterpret_cast<const uint8_t*>(xml.data()),xml.size()),output,error);
  if(result!=LPB_RESULT_OK)set_error(context,error.c_str());
  if(result==LPB_RESULT_OK&&jpeg.mpf_index) {
   lpb::media::jpeg_gainmap_mpf::jpeg_info changed;
   if(!lpb::media::jpeg_gainmap_mpf::parse_jpeg(output,changed)||changed.mpf.image_count!=mpf.mpf.image_count||changed.eoi_end>UINT32_MAX){
    output.clear();return LPB_RESULT_INVALID_ARGUMENT;
   }
   auto& after=changed.mpf;const auto& before=mpf.mpf;
   lpb::media::jpeg_gainmap_mpf::write_u32(output,after.entries[0].size_field_offset,static_cast<uint32_t>(changed.eoi_end),after.little_endian);
   for(size_t i=0;i<before.image_count;++i){const auto& old=before.entries[i];const auto& current=after.entries[i];
    if(old.attributes!=current.attributes||old.dependent_image_1!=current.dependent_image_1||old.dependent_image_2!=current.dependent_image_2){output.clear();return LPB_RESULT_INVALID_ARGUMENT;}
    if(!i)continue;
    const uint64_t relative_to_eoi=old.absolute_offset-jpeg.eoi_end;
    const uint64_t location=changed.eoi_end+relative_to_eoi;
    if(location<after.tiff_header_offset||location-after.tiff_header_offset>UINT32_MAX||location>output.size()||old.size>output.size()-location){output.clear();return LPB_RESULT_UNSUPPORTED;}
    lpb::media::jpeg_gainmap_mpf::write_u32(output,current.offset_field_offset,static_cast<uint32_t>(location-after.tiff_header_offset),after.little_endian);
   }
  }
  return result;
 }catch(...){output.clear();set_error(context,"Google primary serialization failed.");return LPB_RESULT_INTERNAL_ERROR;}
}
lpb_result build_google_v2_primary(lpb_context* context,std::span<const uint8_t> input,uint64_t video_length,
 uint32_t format,bool cover_present,int64_t cover,std::vector<uint8_t>& output,uint32_t target) noexcept {
 google_xmp_request request;request.target=target;request.format=format;request.video_length=video_length;
 request.cover_present=cover_present;request.cover_us=cover;
 return build_google_primary(context,input,request,output);
}
}
