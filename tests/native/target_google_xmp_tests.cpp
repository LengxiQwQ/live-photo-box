#include "protocols/google_v2_writer.h"
#include "media/xmp_observation.h"
#include "media/target_primary_observation.h"
#include "media/hdr_image_metadata.h"
#include "media/image_exif_mapping.h"
#include "media/hdr_image_exif_mapping.h"
#include "media/heic_backend.h"
#include "containers/heif_internal.h"
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <memory>
#include <iostream>
#include <stdexcept>
#define REQUIRE(x) do{if(!(x))throw std::runtime_error(std::string("line ")+std::to_string(__LINE__)+": "+#x);}while(false)
std::string packet(std::string item){return "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><r:RDF xmlns:r=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><r:Description xmlns:C=\"http://ns.google.com/photos/1.0/container/\" xmlns:I=\"http://ns.google.com/photos/1.0/container/item/\" xmlns:custom=\"urn:kept\" custom:Description=\"hello\"><C:Directory><r:Seq><r:li r:parseType=\"Resource\">"+item+"</r:li></r:Seq></C:Directory></r:Description></r:RDF></x:xmpmeta>";}
#ifdef LPB_NATIVE_TEST_HARNESS
extern "C" LPB_API int LPB_CALL lpb_test_p9_google_xmp(){
#else
int main(){
#endif




 try {
 using namespace lpb::target;using namespace lpb::media;
 google_xmp_request req;req.video_length=12345;req.cover_present=true;req.cover_us=2000;
 std::string out;target_xmp_observation got;xmp_observation_failure why;
 const auto direction_xml=[](const std::string& subject,const std::string& contents,const std::string& attributes="") {
  return "<x:xmpmeta xmlns:x='adobe:ns:meta/'><r:RDF xmlns:r='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><r:Description "+subject+
    " xmlns:t='http://ns.adobe.com/tiff/1.0/' "+attributes+">"+contents+"</r:Description></r:RDF></x:xmpmeta>";
 };
 REQUIRE(validate_stored_pixel_xmp_direction(""));
 REQUIRE(validate_stored_pixel_xmp_direction(direction_xml("r:about=''","","t:Orientation='1'")));
 REQUIRE(validate_stored_pixel_xmp_direction(direction_xml("r:about=''","<t:Orientation>1</t:Orientation>")));
 REQUIRE(!validate_stored_pixel_xmp_direction(direction_xml("r:about=''","","t:Orientation='6'")));
 REQUIRE(!validate_stored_pixel_xmp_direction(direction_xml("r:about=''","","t:Orientation='0'")));
 REQUIRE(!validate_stored_pixel_xmp_direction(direction_xml("r:about='other'","","t:Orientation='1'")));
 REQUIRE(!validate_stored_pixel_xmp_direction(direction_xml("","","t:Orientation='1'")));
 REQUIRE(!validate_stored_pixel_xmp_direction(direction_xml("r:about=''","<t:Orientation>1</t:Orientation>","t:Orientation='1'")));
 // The raw encoding padding has its own source context; the fixed eight
 // metadata ledger entries do not change to hide an extra byte carrier.
 const std::vector<uint8_t> padded_source{0xff,0xd8,1,2,0xff,0xd9,0,0,0};
 std::vector<uint8_t> padding_record,padding_heif,padding_read;
 REQUIRE(original_jpeg_padding::encode(padded_source,6,padding_record)&&original_jpeg_padding::valid(padding_record));
 REQUIRE(original_jpeg_padding::attach(padding_heif,padding_record)&&original_jpeg_padding::observe(padding_heif,padding_read)&&padding_read==padding_record);
 auto duplicate_padding=padding_heif;REQUIRE(original_jpeg_padding::attach(duplicate_padding,padding_record));
 REQUIRE(!original_jpeg_padding::observe(duplicate_padding,padding_read)&&padding_read.empty());
 for(size_t at:std::vector<size_t>{19,20,24,64,72,80}) {
  auto bad_record=padding_record;bad_record[at]^=1;
  REQUIRE(!original_jpeg_padding::valid(bad_record));
 }
 auto bad_padding=padded_source;bad_padding.back()=1;
 REQUIRE(!original_jpeg_padding::encode(bad_padding,6,padding_read)&&padding_read.empty());
 REQUIRE(!original_jpeg_padding::encode(padded_source,5,padding_read)&&padding_read.empty());
 auto wrong_context=padding_record;wrong_context[32]^=1;std::vector<uint8_t> wrong_file;
 REQUIRE(original_jpeg_padding::attach(wrong_file,wrong_context)&&original_jpeg_padding::observe(wrong_file,padding_read));
 REQUIRE(padding_read!=padding_record); // Mandatory expected-vs-actual producer gate.
 REQUIRE(original_jpeg_padding::observe({},padding_read)&&padding_read.empty());
 REQUIRE(padding_record!=padding_read); // Missing/unexpected padding is not a match.
 // A format conversion moves only the closed known JPEG layout entry into
 // actual HEIF auxiliary ownership; it must not erase ordinary item content.
 const auto bound_hdr_packet=[](std::string extra_map="",std::string extra_li="") {
  auto xml=packet("<C:Item I:Semantic='Primary' I:Mime='image/jpeg' I:Padding='16' custom:Keep='primary'/></r:li><r:li r:parseType='Resource' "+extra_li+"><C:Item I:Semantic='GainMap' I:Mime='image/jpeg' I:Length='777' "+extra_map+"/>");
  const auto at=xml.find("<r:Description ");xml.insert(at+15,"r:about='' xmlns:h='http://ns.adobe.com/hdr-gain-map/1.0/' h:Version='1.0' ");return xml;
 };
 const auto hdr_source=bound_hdr_packet();
 const auto aux_xml=[](std::string fields,std::string subject="") {
  return "<r:RDF xmlns:r='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><r:Description r:about='"+subject+
   "' xmlns:h='http://ns.adobe.com/hdr-gain-map/1.0/' h:Version='1.0' h:GainMapMax='2' "+fields+"/></r:RDF>";
 };
 const auto geometry="xmlns:t='http://ns.adobe.com/tiff/1.0/' t:Orientation='6' t:ImageWidth='512' t:ImageLength='384'";
 REQUIRE(rewrite_hdr_gainmap_owner_xmp(aux_xml(geometry),6,512,384,out,why));
 REQUIRE(out.find("t:Orientation='1'")!=out.npos&&out.find("t:ImageWidth='384'")!=out.npos&&
  out.find("t:ImageLength='512'")!=out.npos&&out.find("h:GainMapMax='2'")!=out.npos);
 REQUIRE(!rewrite_hdr_gainmap_owner_xmp(aux_xml(geometry,"other"),6,512,384,out,why)&&out.empty());
 REQUIRE(!rewrite_hdr_gainmap_owner_xmp(aux_xml(geometry),2,512,384,out,why)&&out.empty());
 REQUIRE(!rewrite_hdr_gainmap_owner_xmp(aux_xml("xmlns:c='http://ns.adobe.com/camera-raw-settings/1.0/' c:CropTop='0.1'"),6,512,384,out,why)&&out.empty());
 REQUIRE(rewrite_jpeg_gainmap_primary_xmp_to_heif(hdr_source,777,16,1,3072,4096,out,why));
 REQUIRE(observe_target_xmp(out,got,why)&&got.hdr_claim&&got.items.size()==1&&got.items[0].semantic=="Primary"&&
  got.items[0].mime=="image/heic"&&got.items[0].padding==0);
 REQUIRE(out.find("custom:Keep='primary'")!=out.npos&&out.find("h:Version='1.0'")!=out.npos);
 REQUIRE(!rewrite_jpeg_gainmap_primary_xmp_to_heif(bound_hdr_packet("custom:Keep='must-remain'"),777,16,1,3072,4096,out,why)&&out.empty());
 REQUIRE(!rewrite_jpeg_gainmap_primary_xmp_to_heif(bound_hdr_packet("","custom:Keep='must-remain'"),777,16,1,3072,4096,out,why)&&out.empty());
 REQUIRE(!rewrite_jpeg_gainmap_primary_xmp_to_heif(hdr_source,778,16,1,3072,4096,out,why)&&out.empty());
 REQUIRE(!rewrite_jpeg_gainmap_primary_xmp_to_heif(hdr_source,777,0,1,3072,4096,out,why)&&out.empty());
 auto wrong_subject=hdr_source;wrong_subject.replace(wrong_subject.find("r:about=''"),10,"r:about='other'");
 REQUIRE(!rewrite_jpeg_gainmap_primary_xmp_to_heif(wrong_subject,777,16,1,3072,4096,out,why)&&out.empty());
 auto anonymous=hdr_source;anonymous.erase(anonymous.find("r:about=''"),10);
 REQUIRE(!rewrite_jpeg_gainmap_primary_xmp_to_heif(anonymous,777,16,1,3072,4096,out,why)&&out.empty());
 auto unknown_child=hdr_source;auto map_end=unknown_child.find("I:Length='777'");auto li_end=unknown_child.find("</r:li>",map_end);
 unknown_child.insert(li_end,"<custom:Keep>do not erase</custom:Keep>");
 REQUIRE(!rewrite_jpeg_gainmap_primary_xmp_to_heif(unknown_child,777,16,1,3072,4096,out,why)&&out.empty());
 auto comment=hdr_source;comment.insert(comment.find("</r:li>",comment.find("I:Length='777'")),"<!-- keep -->");
 REQUIRE(!rewrite_jpeg_gainmap_primary_xmp_to_heif(comment,777,16,1,3072,4096,out,why)&&out.empty());
 REQUIRE(!rewrite_neutral_image_xmp(hdr_source,"image/jpeg","image/heic",1,3072,4096,out,why)&&out.empty());
 const std::string mi="<x:xmpmeta xmlns:x='adobe:ns:meta/'><r:RDF xmlns:r='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><r:Description xmlns:M='http://ns.xiaomi.com/photos/1.0/camera/' M:XMPMeta='opaque ordinary value'/></r:RDF></x:xmpmeta>";
 for(uint32_t target:{1u,2u}){
  auto request=req;request.target=target;request.primary_padding=target==2?100:0;
  REQUIRE(build_google_xmp(mi,request,out)==LPB_RESULT_OK);
  REQUIRE(observe_target_xmp(out,got,why));
  REQUIRE(got.owner_close_offset==got.micamera_owner_close_offset);
  REQUIRE(out.find("M:XMPMeta='opaque ordinary value'")!=std::string::npos);
 }
 REQUIRE(build_google_xmp("",req,out)==LPB_RESULT_OK);REQUIRE(observe_target_xmp(out,got,why)&&got.items.size()==2&&got.cover_us==2000);
 req.target=3;REQUIRE(build_google_xmp("",req,out)==LPB_RESULT_UNSUPPORTED&&out.empty());
 req.primary_cover_present=true;req.primary_cover_us=1500;
 REQUIRE(build_google_xmp("",req,out)==LPB_RESULT_OK);REQUIRE(observe_target_xmp(out,got,why)&&got.oppo_claim&&got.oppo_owner=="oplus"&&got.primary_cover_us==1500&&!got.edit_flag_present);
 req.target=2;req.format=3;req.primary_mime="image/heic";req.primary_padding=16;
 const auto ordinary=packet("<C:Item I:Semantic=\"Primary\" I:Mime=\"image/heic\" I:Padding=\"0\" custom:Keep=\"yes\"/>");
 REQUIRE(build_google_xmp(ordinary,req,out)==LPB_RESULT_OK);REQUIRE(observe_target_xmp(out,got,why)&&got.items[0].padding==16&&got.items[1].mime=="video/quicktime");
 REQUIRE(out.find("custom:Keep=\"yes\"")!=std::string::npos&&out.find("custom:Description=\"hello\"")!=std::string::npos);
 const auto child=packet("<C:Item I:Semantic=\"Primary\" I:Mime=\"image/heic\"><I:Padding>0</I:Padding><custom:Keep>hi</custom:Keep></C:Item>");
 REQUIRE(build_google_xmp(child,req,out)==LPB_RESULT_OK);REQUIRE(observe_target_xmp(out,got,why)&&got.items[0].padding==16);REQUIRE(out.find("<custom:Keep>hi</custom:Keep>")!=std::string::npos);
 const auto absent=packet("<C:Item I:Semantic=\"Primary\" I:Mime=\"image/heic\"/>");
 REQUIRE(build_google_xmp(absent,req,out)==LPB_RESULT_OK);REQUIRE(observe_target_xmp(out,got,why)&&got.items[0].padding==16);
 req.format=0;req.primary_mime="image/jpeg";req.primary_padding=0;
 req.auxiliary_items.push_back({"GainMap","image/jpeg",400,0,true,false});req.auxiliary_items.push_back({"Original","image/jpeg",800,0,true,false});
 const auto jpeg=packet("<C:Item I:Semantic=\"Primary\" I:Mime=\"image/jpeg\" custom:Keep=\"yes\"/>");
 REQUIRE(build_google_xmp(jpeg,req,out)==LPB_RESULT_OK);REQUIRE(observe_target_xmp(out,got,why)&&got.items.size()==4&&got.items[1].semantic=="GainMap"&&got.items[2].semantic=="Original");
 const auto live=out;REQUIRE(build_google_xmp(live,req,out)==LPB_RESULT_UNSUPPORTED&&out.empty());
 req.target=1;req.primary_padding=12;
 REQUIRE(build_google_xmp("",req,out)==LPB_RESULT_OK);REQUIRE(observe_target_xmp(out,got,why)&&got.micro_claim&&!got.live_claim&&got.micro_offset==12345&&got.items.size()==3&&got.items[0].padding==12&&got.items[2].semantic=="Original");
 req.primary_padding=0;REQUIRE(build_google_xmp(jpeg,req,out)==LPB_RESULT_OK);REQUIRE(observe_target_xmp(out,got,why)&&got.items.size()==3&&got.micro_claim&&!got.live_claim&&got.items[1].semantic=="GainMap");
 // Compatible kind3 mapping: only values of an existing owned HEIF directory
 // may change. The entire remaining XML is preserved, including ordinary bytes.
 const auto hdr_packet=[](const std::string& body,const std::string& attributes="H:Version='1.0'") {
  return "<x:xmpmeta xmlns:x='adobe:ns:meta/'><r:RDF xmlns:r='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><r:Description xmlns:C='http://ns.google.com/photos/1.0/container/' xmlns:I='http://ns.google.com/photos/1.0/container/item/' xmlns:H='http://ns.adobe.com/hdr-gain-map/1.0/' xmlns:O='urn:ordinary' O:Keep='unchanged &amp; exact' "+attributes+">"+body+"</r:Description></r:RDF></x:xmpmeta>";
 };
 const auto directory=[](const std::string& contents){return "<C:Directory><r:Seq>"+contents+"</r:Seq></C:Directory>";};
 const std::string first="<r:li r:parseType='Resource'><C:Item I:Semantic='Primary' I:Mime='image/heic' I:Padding='8'/></r:li>";
 const std::string second="<r:li r:parseType='Resource'><C:Item I:Semantic='GainMap' I:Mime='image/heic' I:Length='0'/></r:li>";
 const auto replace_one=[](std::string value,const std::string& before,const std::string& after){auto at=value.find(before);REQUIRE(at!=std::string::npos);value.replace(at,before.size(),after);return value;};
 const auto original=hdr_packet(directory(first+second));
 const auto embedded_layout=hdr_packet(directory(first+second),"H:Version='1.0' r:about=''");
 REQUIRE(reduce_embedded_heif_gainmap_layout(embedded_layout,out,why)&&out==hdr_packet(directory(first),"H:Version='1.0' r:about=''") );
 REQUIRE(!reduce_embedded_heif_gainmap_layout(original,out,why)&&out.empty());
 REQUIRE(!reduce_embedded_heif_gainmap_layout(hdr_packet(directory(first+second),"H:Version='1.0' r:about='other'"),out,why)&&out.empty());
 auto embedded_unknown=embedded_layout;embedded_unknown.insert(embedded_unknown.find("I:Length='0'"),"O:Keep='cannot erase' ");
 REQUIRE(!reduce_embedded_heif_gainmap_layout(embedded_unknown,out,why)&&out.empty());
 embedded_unknown=embedded_layout;embedded_unknown.insert(embedded_unknown.find("</r:li>",embedded_unknown.find("I:Length='0'")),"<!-- cannot erase -->");
 REQUIRE(!reduce_embedded_heif_gainmap_layout(embedded_unknown,out,why)&&out.empty());
 embedded_unknown=replace_one(embedded_layout,"I:Length='0'","I:Length='10'");
 REQUIRE(!reduce_embedded_heif_gainmap_layout(embedded_unknown,out,why)&&out.empty());
 auto expected=replace_one(original,"I:Padding='8'","I:Padding='321'");
 expected=replace_one(expected,"I:Length='0'","I:Length='12345'");
 expected=replace_one(expected,"I:Mime='image/heic'","I:Mime='image/jpeg'");
 expected=replace_one(expected,"I:Mime='image/heic'","I:Mime='image/jpeg'");
 REQUIRE(rewrite_heif_gainmap_directory_to_jpeg(original,12345,321,out,why)&&out==expected);
 const std::string first_child="<r:li r:parseType='Resource'><C:Item><I:Semantic>Primary</I:Semantic><I:Mime>image/heic</I:Mime><I:Padding>8</I:Padding><O:Keep> exact </O:Keep></C:Item></r:li>";
 const std::string second_child="<r:li r:parseType='Resource'><C:Item><I:Semantic>GainMap</I:Semantic><I:Mime>image/heic</I:Mime><I:Length>0</I:Length></C:Item></r:li>";
 const auto child_input=hdr_packet(directory(first_child+second_child));
 expected=replace_one(child_input,">8</I:Padding>",">321</I:Padding>");
 expected=replace_one(expected,">0</I:Length>",">12345</I:Length>");
 expected=replace_one(expected,">image/heic</I:Mime>",">image/jpeg</I:Mime>");
 expected=replace_one(expected,">image/heic</I:Mime>",">image/jpeg</I:Mime>");
 REQUIRE(rewrite_heif_gainmap_directory_to_jpeg(child_input,12345,321,out,why)&&out==expected);
 const std::vector<std::string> bad_mapping{
  hdr_packet("<H:Version>1.0</H:Version>"+directory(first+second)),
  hdr_packet(directory(first+second),"H:Version='2.0'"),
  hdr_packet(directory(first+second),"H:Version='1.0' H:Gamma='1'"),
  hdr_packet(directory(first+second),"H:Version='1.0' xmlns:G='http://ns.google.com/photos/1.0/camera/' G:MotionPhoto='1'"),
  hdr_packet(directory(first+replace_one(second,"I:Length='0'","I:Length='1'"))),
  hdr_packet(directory(first+replace_one(second,"image/heic","image/jpeg"))),
  hdr_packet(directory(first+replace_one(second,"I:Length='0'","I:Length='0' I:Padding='1'"))),
  hdr_packet(directory(first+second+second)), hdr_packet(directory(first+second)+directory(first+second)),
  hdr_packet(directory(replace_one(first,"I:Padding='8'","I:Padding='8' I:Padding='8'")+second)),
  hdr_packet(directory(replace_one(first,"I:Padding='8'/>","I:Padding='8'><I:Padding>8</I:Padding></C:Item>")+second)),
  hdr_packet(directory(replace_one(first,"<r:li r:parseType='Resource'>","<r:li r:parseType='Resource' I:Mime='image/heic'>")+second)),
  hdr_packet(directory(replace_one(first," I:Padding='8'","")+second))
 };
 for(const auto& invalid:bad_mapping) {
  out="must clear";REQUIRE(!rewrite_heif_gainmap_directory_to_jpeg(invalid,12345,321,out,why)&&out.empty());
 }
 std::cout<<"P9R3_GoogleXmp: OPPO independent timing, ordered assets, structural HEIF Padding, exact kind3 mapping positive/negative PASS\n";
 return 0;
 }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
 catch(...){std::cerr<<"XMP test entry failed at its C ABI exception boundary\n";return 1;}
}

#ifdef LPB_NATIVE_TEST_HARNESS
extern "C" LPB_API int LPB_CALL lpb_test_p9_hdr_real_xmp() {
 try {
  using namespace lpb::media;
  char* raw=nullptr;size_t count=0;REQUIRE(_dupenv_s(&raw,&count,"LIVEPHOTOBOX_P9_HDR_XMP_INPUT_ROOT")==0);
  std::unique_ptr<char,decltype(&std::free)> path(raw,&std::free);REQUIRE(path&&count>1);
  const std::filesystem::path root(path.get());
  lpb_context* raw_context=nullptr;REQUIRE(lpb_create_context(nullptr,&raw_context)==LPB_RESULT_OK&&raw_context);
  std::unique_ptr<lpb_context,decltype(&lpb_destroy_context)> context(raw_context,&lpb_destroy_context);
  const std::filesystem::path names[]={std::filesystem::path(u8"vivo.jpg"),std::filesystem::path(u8"三星.jpg"),
   std::filesystem::path(u8"一加.jpg"),std::filesystem::path(u8"一加-改了封面照片.jpg")};
  for(const auto& name:names) {
   std::ifstream file(root/name/"asset-0.jpg",std::ios::binary);REQUIRE(file);
   std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(file)),{});target_primary_observation source;std::string error;
   REQUIRE(observe_target_primary(bytes,source,error));REQUIRE(source.container==LPB_IMAGE_CONTAINER_JPEG&&source.auxiliaries.size()==1);
   REQUIRE(source.auxiliaries[0].semantic=="GainMap"&&source.xmp.items.size()==2);
   const auto range=source.auxiliaries[0].range;jpeg_rgb_image primary_pixels,gainmap_pixels;
   REQUIRE(decode_jpeg_rgb_bytes(context.get(),bytes,primary_pixels)==LPB_RESULT_OK);
   REQUIRE(decode_jpeg_rgb_bytes(context.get(),std::span<const uint8_t>(bytes).subspan(static_cast<size_t>(range.offset),
    static_cast<size_t>(range.length)),gainmap_pixels)==LPB_RESULT_OK);
   hdr_image_metadata_inventory inventory;jpeg_observation gainmap;
   REQUIRE(capture_hdr_jpeg_image_metadata(bytes,range,primary_pixels,gainmap_pixels,source,gainmap,inventory,error));
   REQUIRE(!inventory.primary_exif.empty()&&!inventory.primary_xmp.empty()&&!inventory.gainmap_xmp.empty()&&
    !inventory.original_markers.empty()&&original_jpeg_metadata::valid(inventory.original_markers));
   REQUIRE(inventory.primary_icc==primary_pixels.icc_profile&&inventory.gainmap_icc==gainmap_pixels.icc_profile);
   uint8_t digest[32]{};hdr_image_metadata_hash(inventory,digest);
   REQUIRE(std::any_of(std::begin(digest),std::end(digest),[](uint8_t b){return b!=0;}));
   std::string mapped; xmp_observation_failure why{};
   REQUIRE(rewrite_jpeg_gainmap_primary_xmp_to_heif(source.standard_xml,source.auxiliaries[0].range.length,
    source.xmp.items[0].padding,source.orientation,source.width,source.height,mapped,why));
   target_xmp_observation actual;REQUIRE(observe_target_xmp(mapped,actual,why));
   REQUIRE(actual.items.size()==1&&actual.items[0].semantic=="Primary"&&actual.items[0].mime=="image/heic"&&
    actual.items[0].padding==0&&actual.hdr_claim==source.xmp.hdr_claim);
   std::cout<<"actual bound JPEG HDR primary XMP mapped: "<<name.u8string().size()<<" filename bytes\n";
  }
  return 0;
 }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
 catch(...){std::cerr<<"Real HDR XMP model test failed\n";return 1;}
}
#endif

#ifdef LPB_NATIVE_TEST_HARNESS
extern "C" LPB_API int LPB_CALL lpb_test_p9_hdr_normal_metadata_codec() {
 try {
  using namespace lpb::media;
  char* raw=nullptr;size_t count=0;REQUIRE(_dupenv_s(&raw,&count,"LIVEPHOTOBOX_P9_HDR_XMP_INPUT_ROOT")==0);
  std::unique_ptr<char,decltype(&std::free)> path(raw,&std::free);REQUIRE(path&&count>1);const std::filesystem::path root(path.get());
  lpb_context* created=nullptr;REQUIRE(lpb_create_context(nullptr,&created)==LPB_RESULT_OK&&created);
  std::unique_ptr<lpb_context,decltype(&lpb_destroy_context)> context(created,&lpb_destroy_context);
  const std::filesystem::path names[]={std::filesystem::path(u8"vivo.jpg"),std::filesystem::path(u8"一加.jpg"),std::filesystem::path(u8"一加-改了封面照片.jpg")};
  for(const auto& name:names) {
   std::ifstream file(root/name/"asset-0.jpg",std::ios::binary);REQUIRE(file);std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(file)),{});
   target_primary_observation primary;std::string error;REQUIRE(observe_target_primary(bytes,primary,error)&&primary.auxiliaries.size()==1);
   const auto range=primary.auxiliaries[0].range;jpeg_rgb_image image,map;
   REQUIRE(decode_jpeg_rgb_bytes(context.get(),bytes,image)==LPB_RESULT_OK);
   REQUIRE(decode_jpeg_rgb_bytes(context.get(),std::span<const uint8_t>(bytes).subspan(static_cast<size_t>(range.offset),static_cast<size_t>(range.length)),map)==LPB_RESULT_OK);
   hdr_image_metadata_inventory expected;jpeg_observation gainmap;
   REQUIRE(capture_hdr_jpeg_image_metadata(bytes,range,image,map,primary,gainmap,expected,error));
   REQUIRE(primary.orientation==1&&(gainmap.exif_payload.empty()||gainmap.orientation==1)&&expected.ordinary_sef.empty());
   REQUIRE(map_image_exif_orientation(expected.primary_exif,primary));
   target_primary_observation aux;aux.width=gainmap.width;aux.height=gainmap.height;aux.orientation=1;
   REQUIRE(map_image_exif_orientation(expected.gainmap_exif,aux));
   std::string xml;xmp_observation_failure why{};REQUIRE(rewrite_jpeg_gainmap_primary_xmp_to_heif(expected.primary_xmp,range.length,primary.xmp.items[0].padding,
    1,primary.width,primary.height,xml,why));
   expected.primary_xmp=std::move(xml);
   const auto pixels=[](jpeg_rgb_image& input) {pixel_surface p;p.width=input.width;p.height=input.height;p.channels=3;p.signal_bit_depth=8;p.storage_bit_depth=8;
    p.stride=uint64_t(p.width)*3;p.color.has_icc=!input.icc_profile.empty();p.color.icc_profile=std::move(input.icc_profile);p.pixels=std::move(input.pixels);return p;};
   auto base=pixels(image),map_pixels=pixels(map);std::vector<uint8_t> coded,output;heic_encoded_image_facts facts;
   const auto tiff=[](const auto& exif){return exif.empty()?std::span<const uint8_t>{}:std::span<const uint8_t>(exif).subspan(6);};
   const auto text=[](const std::string& value){return std::span<const uint8_t>(reinterpret_cast<const uint8_t*>(value.data()),value.size());};
   REQUIRE(encode_heic_primary_and_gainmap_metadata_bytes(context.get(),base,map_pixels,95,tiff(expected.primary_exif),text(expected.primary_xmp),
    tiff(expected.gainmap_exif),text(expected.gainmap_xmp),coded,facts)==LPB_RESULT_OK);
   if(!lpb::containers::attach_gainmap_auxiliary_graph(coded,facts.primary_item_id,facts.secondary_item_id,output,error))
    throw std::runtime_error("GainMap graph assembly: "+error);
   REQUIRE(original_jpeg_metadata::attach(output,expected.original_markers));
   REQUIRE(original_jpeg_padding::attach(output,expected.original_padding_record));
   hdr_image_metadata_inventory observed;
   REQUIRE(lpb::containers::read_image_item_metadata(output,facts.primary_item_id,observed.primary_exif,observed.primary_xmp,error));
   REQUIRE(lpb::containers::read_image_item_metadata(output,facts.secondary_item_id,observed.gainmap_exif,observed.gainmap_xmp,error));
   REQUIRE(observed.primary_exif==expected.primary_exif&&observed.primary_xmp==expected.primary_xmp&&
    observed.gainmap_exif==expected.gainmap_exif&&observed.gainmap_xmp==expected.gainmap_xmp);
   heic_image_facts encoded;REQUIRE(inspect_heic_bytes(context.get(),output,encoded)==LPB_RESULT_OK&&encoded.auxiliaries.size()==1);
   REQUIRE(encoded.color.icc_profile==expected.primary_icc&&encoded.auxiliaries[0].color.icc_profile==expected.gainmap_icc);
   REQUIRE(original_jpeg_metadata::observe(output,observed.original_markers)&&observed.original_markers==expected.original_markers);
   REQUIRE(original_jpeg_padding::observe(output,observed.original_padding_record)&&observed.original_padding_record==expected.original_padding_record);
   std::cout<<"normal-coordinate HDR codec owner metadata exact\n";
  }
  return 0;
 }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
 catch(...){std::cerr<<"Normal HDR metadata codec test failed\n";return 1;}
}
#endif

#ifdef LPB_NATIVE_TEST_HARNESS
extern "C" LPB_API int LPB_CALL lpb_test_p9_hdr_thumbnail_mapping() {
 try {
  char* raw=nullptr;size_t count=0;REQUIRE(_dupenv_s(&raw,&count,"LIVEPHOTOBOX_P9_HDR_XMP_INPUT_ROOT")==0);
  std::unique_ptr<char,decltype(&std::free)> path(raw,&std::free);REQUIRE(path&&count>1);
  std::ifstream file(std::filesystem::path(path.get())/std::filesystem::path(u8"三星.jpg")/"asset-0.jpg",std::ios::binary);REQUIRE(file);
  std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(file)),{});
  lpb::media::target_primary_observation facts;std::string error;REQUIRE(lpb::media::observe_target_primary(bytes,facts,error));
  REQUIRE(facts.orientation==6);auto exif=facts.exif_payload;const auto before=exif.size();
  lpb_context* created=nullptr;REQUIRE(lpb_create_context(nullptr,&created)==LPB_RESULT_OK&&created);
  std::unique_ptr<lpb_context,decltype(&lpb_destroy_context)> context(created,&lpb_destroy_context);
  // Derive a valid opaque Exif value which aliases the thumbnail JPEG. A
  // general TIFF observer alone does not establish JPEG payload ownership.
  auto alias=facts.exif_payload;
  uint32_t original_direction{};lpb::media::exif_observation original_exif;lpb::media::jpeg_observation_failure original_failure{};
  REQUIRE(lpb::media::observe_exif(alias,original_direction,original_exif,original_failure,true));
  const auto read=[&](size_t at,size_t n){uint32_t v{};for(size_t i=0;i<n;++i)v|=uint32_t(alias[6+at+i])<<((original_exif.big_endian?n-i-1:i)*8);return v;};
  const auto write=[&](size_t at,size_t n,uint32_t v){for(size_t i=0;i<n;++i)alias[6+at+i]=static_cast<uint8_t>(v>>((original_exif.big_endian?n-i-1:i)*8));};
  const size_t root=original_exif.ifd0_offset,thumb=read(root+2+12*read(root,2),4);
  uint32_t thumbnail_offset{};
  for(size_t i=0;i<read(thumb,2);++i) {const size_t at=thumb+2+12*i;if(read(at,2)==0x201)thumbnail_offset=read(at+8,4);}
  REQUIRE(thumbnail_offset!=0);bool changed=false;
  for(size_t i=0;i<read(original_exif.exif_ifd_offset,2);++i) {
   const size_t at=original_exif.exif_ifd_offset+2+12*i;
   if(read(at,2)==0x927c&&read(at+2,2)==7){write(at+4,4,16);write(at+8,4,thumbnail_offset);changed=true;break;}
  }
  REQUIRE(changed);auto unchanged=alias;lpb::media::exif_observation aliased;
  REQUIRE(lpb::media::observe_exif(alias,original_direction,aliased,original_failure,true));
  REQUIRE(!lpb::media::map_hdr_image_exif(context.get(),alias,facts)&&alias==unchanged);
  REQUIRE(lpb::media::map_hdr_image_exif(context.get(),exif,facts)&&exif.size()>before);
  uint32_t orientation{};lpb::media::exif_observation actual;lpb::media::jpeg_observation_failure why{};
  REQUIRE(lpb::media::observe_exif(exif,orientation,actual,why,true)&&orientation==1);
  facts.orientation=1;std::swap(facts.width,facts.height);auto again=exif;
  REQUIRE(lpb::media::map_image_exif_orientation(again,facts)&&again==exif);
  std::cout<<"actual rotated primary Exif and physically rotated owned thumbnail mapped\n";return 0;
 }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
 catch(...){std::cerr<<"HDR thumbnail mapping test failed\n";return 1;}
}
#endif
