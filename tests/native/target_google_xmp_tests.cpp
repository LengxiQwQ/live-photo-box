#include "protocols/google_v2_writer.h"
#include "media/xmp_observation.h"
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
 std::cout<<"P9R3_GoogleXmp: OPPO independent timing, ordered assets and structural HEIF Padding preservation PASS\n";
 return 0;
 }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
 catch(...){std::cerr<<"XMP test entry failed at its C ABI exception boundary\n";return 1;}
}
