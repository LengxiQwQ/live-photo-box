#include "media/target_primary_observation.h"
#include "media/media_inspector.h"
#include "media/jpeg_mpf_vendor.h"
#include "protocols/google_v2_writer.h"
#include "metadata/target_exif.h"
#include "protocols/clean/xmp_cleaner.h"
#include "foundation/residue_fingerprint.h"
#include <algorithm>
#include <cstddef>
#include <iostream>
#include <stdexcept>
#include <vector>
#define REQUIRE(x) do { if(!(x)) throw std::runtime_error(std::string("line ")+std::to_string(__LINE__)+": "+#x); } while(false)
namespace {
using bytes=std::vector<uint8_t>;
void be(bytes& b,uint64_t n,size_t count) { while(count) { --count; b.push_back(static_cast<uint8_t>(n>>(count*8))); } }
void add(bytes& a,const bytes& b) { a.insert(a.end(),b.begin(),b.end()); }
bytes box(const char* type,bytes p) { bytes b; be(b,p.size()+8,4); b.insert(b.end(),type,type+4); add(b,p); return b; }
void marker(bytes& b,uint8_t type,bytes p) { b.push_back(0xff); b.push_back(type); be(b,p.size()+2,2); add(b,p); }
std::string xml(std::string items={},std::string attributes={},std::string declarations={}) {
    return "<x:xmpmeta xmlns:x='adobe:ns:meta/'><r:RDF xmlns:r='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><r:Description xmlns:C='http://ns.google.com/photos/1.0/container/' xmlns:I='http://ns.google.com/photos/1.0/container/item/' "+declarations+" "+attributes+">"+
        (items.empty() ? "" : "<C:Directory><r:Seq>"+items+"</r:Seq></C:Directory>")+"</r:Description></r:RDF></x:xmpmeta>";
}
std::string item(const std::string& semantic,const std::string& mime,const std::string& extra={}) {
    return "<r:li r:parseType='Resource'><C:Item I:Semantic='"+semantic+"' I:Mime='"+mime+"' "+extra+"/></r:li>";
}
bytes jpeg(std::string packet={}) {
    bytes b{0xff,0xd8};
    if(!packet.empty()) { constexpr char signature[]="http://ns.adobe.com/xap/1.0/"; bytes p(signature,signature+sizeof(signature)); p.insert(p.end(),packet.begin(),packet.end()); marker(b,0xe1,p); }
    bytes quant(65,1); quant[0]=0; marker(b,0xdb,quant);
    marker(b,0xc0,{8,0,1,0,1,1,1,0x11,0});
    bytes tables;
    for(const uint8_t type : {uint8_t{0},uint8_t{0x10}}) { tables.push_back(type); tables.push_back(1); tables.insert(tables.end(),15,0); tables.push_back(0); }
    marker(b,0xc4,tables); marker(b,0xda,{1,1,0,0,63,0}); b.insert(b.end(),{0x3f,0xff,0xd9}); return b;
}
void le(bytes& b,size_t at,uint32_t value,size_t count=4) { for(size_t i=0;i<count;++i)b[at+i]=static_cast<uint8_t>(value>>(i*8)); }
bytes attribute_image(std::string packet={}) {
    bytes payload(42,0);std::copy_n("MPF\0II",6,payload.begin());le(payload,6,42,2);le(payload,8,8);le(payload,12,2,2);
    le(payload,14,0xb000,2);le(payload,16,7,2);le(payload,18,4);std::copy_n("0100",4,payload.begin()+22);
    le(payload,26,0xb001,2);le(payload,28,4,2);le(payload,30,1);le(payload,34,2);
    auto plain=jpeg(packet);bytes out{0xff,0xd8};marker(out,0xe2,payload);out.insert(out.end(),plain.begin()+2,plain.end());return out;
}
bytes mpf_image(const bytes& secondary,bool suffix=false) {
    bytes payload(86,0);std::copy_n("MPF\0II",6,payload.begin());le(payload,6,42,2);le(payload,8,8);le(payload,12,3,2);
    le(payload,14,0xb000,2);le(payload,16,7,2);le(payload,18,4);std::copy_n("0100",4,payload.begin()+22);
    le(payload,26,0xb001,2);le(payload,28,4,2);le(payload,30,1);le(payload,34,2);
    le(payload,38,0xb002,2);le(payload,40,7,2);le(payload,42,32);le(payload,46,50);
    const auto plain=jpeg();const auto eoi=static_cast<uint32_t>(plain.size()+payload.size()+4);
    le(payload,54,0x20030000);le(payload,58,eoi);le(payload,70,0x00030000);
    le(payload,74,static_cast<uint32_t>(secondary.size()));le(payload,78,eoi-10);
    bytes out{0xff,0xd8};marker(out,0xe2,payload);out.insert(out.end(),plain.begin()+2,plain.end());add(out,secondary);
    if(suffix)out.insert(out.end(),{'V','L','D','M','A','G','I','C',1,2,3,4});return out;
}
bytes heif(uint8_t rotation=0,bool mirror=false,bool owned_size=true,bool duplicate_size=false,bytes clap={}) {
    bytes infe{2,0,0,0,0,1,0,0,'h','v','c','1',0};
    bytes iinf{0,0,0,0,0,1}; add(iinf,box("infe",infe));
    bytes ispe{0,0,0,0}; be(ispe,7,4); be(ispe,5,4);
    bytes wrong_ispe{0,0,0,0}; be(wrong_ispe,999,4); be(wrong_ispe,888,4);
    bytes properties; add(properties,box("ispe",ispe)); add(properties,box("ispe",wrong_ispe));
    add(properties,box("irot",{rotation})); add(properties,box("imir",{1}));
    if(!clap.empty()) add(properties,box("clap",clap));
    bytes ipma{0,0,0,0,0,0,0,1,0,1};
    bytes associations;
    if(owned_size) associations.push_back(1);
    if(duplicate_size) associations.push_back(2);
    associations.push_back(3); if(mirror) associations.push_back(4);
    if(!clap.empty()) associations.push_back(5);
    ipma.push_back(static_cast<uint8_t>(associations.size())); add(ipma,associations);
    bytes iprp; add(iprp,box("ipco",properties)); add(iprp,box("ipma",ipma));
    bytes iloc{1,0,0,0,0x44,0,0,1,0,1,0,1,0,0,0,1}; // method1/ref0/one extent
    be(iloc,0,4); be(iloc,4,4);
    bytes meta(4,0); add(meta,box("pitm",{0,0,0,0,0,1})); add(meta,box("iinf",iinf));
    add(meta,box("iloc",iloc)); add(meta,box("iprp",iprp)); add(meta,box("idat",{9,8,7,6}));
    bytes b=box("ftyp",{'h','e','i','c',0,0,0,0,'m','i','f','1'}); add(b,box("meta",meta)); return b;
}
void success(const bytes& b,lpb::media::target_primary_observation& facts,bool output=false) {
    std::string error; const bool ok=lpb::media::observe_target_primary(b,facts,error,output);
    if(!ok) throw std::runtime_error(error);
}
void rejection(const bytes& b,const char* expected,bool output=false) {
    lpb::media::target_primary_observation facts; std::string error;
    REQUIRE(!lpb::media::observe_target_primary(b,facts,error,output));
    REQUIRE(facts.width==0 && facts.auxiliaries.empty()); REQUIRE(error.find(expected)!=std::string::npos);
}
void mapped_comment_cases() {
 using namespace lpb::media;
 for(const uint32_t target:{3u,4u})for(const uint16_t type:{uint16_t{2},uint16_t{7}}) {
  for(const bytes raw:{bytes{0xff,0,0x81},bytes{'A','S','C','I','I',0,0,0,'n',0,'x',0},bytes{'o','r','d','i','n','a','r','y',0}}) {
   bytes input,output;jpeg_observation_failure failure{};
   REQUIRE(lpb::metadata::add_target_user_comment(jpeg(xml({},"o:Keep='yes'","xmlns:o='urn:ordinary'")),raw,input,failure));
   jpeg_observation observed;lpb::memory_random_access source{input};uint64_t bad{};
   REQUIRE(observe_jpeg(source.view(),observed,failure,bad,{true,true,true,true}));
   // Only fixture construction changes the actual formal TIFF type.
   const size_t type_at=static_cast<size_t>(observed.exif_marker_start)+4+6+observed.exif.user_comment_entry+2;
   input[type_at]=static_cast<uint8_t>(type);input[type_at+1]=0;
   const auto original=input;lpb::target::google_xmp_request request;request.target=target;request.video_length=4;
   auto expected=comment_tuple(type,static_cast<uint32_t>(raw.size()),raw);
   REQUIRE(!lpb::metadata::map_target_user_comment(input,raw,expected,output,failure)&&output.empty());
   request.cover_present=true;request.primary_cover_present=true;
   user_comment_mapping receipt;
   REQUIRE(lpb::target::build_google_primary(nullptr,input,request,output,&receipt)==LPB_RESULT_OK);
   REQUIRE(input==original&&receipt.mapped&&receipt.input_compared&&receipt.output_compared);
   REQUIRE(receipt.original==comment_tuple(type,static_cast<uint32_t>(raw.size()),raw));
   lpb::memory_random_access candidate{output};
   REQUIRE(observe_jpeg(candidate.view(),observed,failure,bad,{true,true,true,true}));
   target_xmp_observation xml;xmp_observation_failure why{};
   REQUIRE(observe_target_xmp(observed.xmp,xml,why)&&xml.preserved_comment==receipt.original);
   REQUIRE(observed.xmp.find("o:Keep='yes'")!=std::string::npos);
   for(const auto& change:std::vector<std::pair<std::string,std::string>>{{"<p:schemaVersion>1","<p:schemaVersion>2"},{"<p:tiffType>"+std::to_string(type),"<p:tiffType>4"},{"<p:rawSha256>","<p:rawSha256>0"},{"<p:count>","<p:count>0"},{"<p:rawHex>","<p:rawHex>ff"},{"urn:livephotobox:preserved-exif:1","urn:livephotobox:preserved-exif:2"},{"<p:owner>ExifIFD","<p:owner>IFD0"},{"<p:tag>37510","<p:tag>1"},{"<p:byteLength>","<p:byteLength>9"},{"<p:rawHex>","X<p:rawHex>"}}) {
    auto broken=observed.xmp;const size_t at=broken.find(change.first);REQUIRE(at!=std::string::npos);broken.replace(at,change.first.size(),change.second);
    REQUIRE(!observe_target_xmp(broken,xml,why));
   }
   const auto record_start=observed.xmp.find("<r:Description "),record_end=observed.xmp.find("</r:Description>",record_start);
   REQUIRE(record_start!=std::string::npos&&record_end!=std::string::npos);
   const auto record=observed.xmp.substr(record_start,record_end+16-record_start);
   auto duplicate=observed.xmp;duplicate.insert(record_start,record);REQUIRE(!observe_target_xmp(duplicate,xml,why));
   for(const auto& attributes:std::vector<std::string>{"r:about=\"x\"","r:nodeID=\"x\"","r:about=\"\" r:nodeID=\"x\"","r:about=\"\" r:resource=\"x\"","r:about=\"\" r:ID=\"x\""}) {
    auto wrong_owner=observed.xmp;const auto at=wrong_owner.find("r:about=\"\"",record_start);REQUIRE(at!=std::string::npos);
    wrong_owner.replace(at,10,attributes);REQUIRE(!observe_target_xmp(wrong_owner,xml,why));
   }
   const std::string guid(32,'A');auto standard=observed.xmp;
   const auto owner=standard.find(" lpbrdf:about=\"\"");REQUIRE(owner!=std::string::npos);
   standard.insert(owner," xmlns:n=\"http://ns.adobe.com/xmp/note/\" n:HasExtendedXMP=\""+guid+"\"");
   const std::string extension="<r:RDF xmlns:r=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"+record+"</r:RDF>";
   REQUIRE(!observe_target_xmp_packets(standard,extension,guid,xml,why));
   auto forged=receipt.original;forged.raw[0]^=1;
   REQUIRE(!lpb::metadata::map_target_user_comment(input,raw,forged,output,failure)&&output.empty());
  }
 }
}
void scalar_cleanup_cases() {
 const std::string uri="http://ns.google.com/photos/1.0/camera/";
 lpb::target::google_xmp_request request;request.target=1;request.video_length=4;request.cover_present=true;
 std::string packet;REQUIRE(lpb::target::build_google_xmp({},request,packet)==LPB_RESULT_OK);
 const std::string property="<lpbc:MicroVideoVersion xmlns:lpbc=\""+uri+"\">1</lpbc:MicroVideoVersion>";
 REQUIRE(packet.find(property)!=std::string::npos);
 lpb_cleanup_action action{};action.struct_size=sizeof(action);action.owner_protocol=LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1;
 action.artifact_role=LPB_ARTIFACT_PRIMARY_IMAGE;action.structure_kind=LPB_RESIDUE_XMP_PROPERTY;
 action.coordinate_space=LPB_COORD_STRUCTURED_SELECTOR;action.removal_mode=LPB_REMOVAL_DELETE;action.is_mandatory=1;
 strcpy_s(action.residue_id,"google-v1-xmp-version");strcpy_s(action.selector,"GCamera:MicroVideoVersion");strcpy_s(action.expected_semantic,"MicroVideoVersion");
 const auto fp=lpb::crypto::compute_xmp_property_fingerprint(uri,"MicroVideoVersion","1");strcpy_s(action.expected_fingerprint,fp.c_str());
 std::string output;std::vector<lpb_removed_protocol_fact> facts;
 REQUIRE(lpb::protocols::clean::clean_xmp_metadata_with_plan(packet,action.owner_protocol,&action,1,output,facts));
 auto expected=packet;expected.erase(expected.find(property),property.size());REQUIRE(output==expected&&facts.size()==1&&facts[0].before_fingerprint==fp);
 for(unsigned probe=0;probe<6;++probe) {
  auto input=packet;auto bad=action;
  if(probe==0)bad.expected_fingerprint[0]=bad.expected_fingerprint[0]=='a'?'b':'a';
  if(probe==1)input.insert(input.find(property),property);
  if(probe==2)input.insert(input.find(property)+property.find('>')+1,"<o:Data xmlns:o='urn:ordinary'>keep</o:Data>");
  if(probe==3)input.insert(input.find(property)+property.find('>')," o:Keep='yes' xmlns:o='urn:ordinary'");
  if(probe==4) {const auto at=input.find(property);input.replace(at,property.size(),"<o:wrapper xmlns:o='urn:ordinary'>"+property+"</o:wrapper>");}
  if(probe==5)bad.owner_protocol=LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO;
  output.clear();facts.clear();
  REQUIRE(!lpb::protocols::clean::clean_xmp_metadata_with_plan(input,action.owner_protocol,&bad,1,output,facts)&&output.empty()&&facts.empty());
 }
}
}
#ifdef LPB_NATIVE_TEST_HARNESS
extern "C" LPB_API int LPB_CALL lpb_test_p9_primary_observation() {
#else
int main() {
#endif
    try {
        mapped_comment_cases();
        scalar_cleanup_cases();
        lpb::media::target_primary_observation facts;
        {
            std::string error;lpb::media::mpf_vendor_primary vendor;
            for(const auto& secondary:{jpeg(),attribute_image()})for(const bool suffix:{false,true}) {
                const auto input=mpf_image(secondary,suffix);
                if(!lpb::media::observe_cross_target_mpf_primary(input,vendor,error))
                    throw std::runtime_error("MPF input (secondary="+std::to_string(secondary.size())+", suffix="+std::to_string(suffix)+"): "+error);
                success(input,facts);const auto padding=input.size()-facts.encoding_boundary;
                for(const uint32_t target:{1u,2u,3u}) {
                    lpb::target::google_xmp_request request;request.target=target;request.video_length=12345;request.cover_present=true;request.cover_us=1000;
                    request.primary_cover_present=target==3;request.primary_cover_us=1000;request.primary_padding=padding;
                    bytes output;REQUIRE(lpb::target::build_google_primary(nullptr,input,request,output)==LPB_RESULT_OK);
                    REQUIRE(lpb::media::observe_cross_target_mpf_primary(output,vendor,error));
                    REQUIRE(output.size()-vendor.image_end==padding);
                    REQUIRE(std::equal(input.end()-static_cast<ptrdiff_t>(padding),input.end(),output.end()-static_cast<ptrdiff_t>(padding)));
                    if(target==1)success(output,facts);
                    else REQUIRE(lpb::media::observe_target_jpeg_prefix(output,output.size()+12345,facts,error));
                    REQUIRE(facts.auxiliaries.size()==1&&facts.auxiliaries[0].semantic=="MpfAuxiliaryImage");
                    if(target!=1) {
                        auto wrong_padding=output;
                        const std::string from="lpbi:Padding=\""+std::to_string(padding)+"\"";
                        const std::string to="lpbi:Padding=\""+std::to_string(padding+1)+"\"";
                        REQUIRE(from.size()==to.size());auto at=std::search(wrong_padding.begin(),wrong_padding.end(),from.begin(),from.end());
                        REQUIRE(at!=wrong_padding.end());std::copy(to.begin(),to.end(),at);
                        REQUIRE(!lpb::media::observe_target_jpeg_prefix(wrong_padding,wrong_padding.size()+12345,facts,error));
                    }
                }
            }
            auto live=jpeg(xml({},"G:MotionPhoto='1' G:MotionPhotoVersion='1'", "xmlns:G='http://ns.google.com/photos/1.0/camera/'"));
            REQUIRE(!lpb::media::observe_cross_target_mpf_primary(mpf_image(live),vendor,error));
            const auto attribute_only=attribute_image();
            lpb::memory_random_access standalone{attribute_only};lpb::media::jpeg_observation observed;
            lpb::media::jpeg_observation_failure failure{};uint64_t bad{};
            REQUIRE(!lpb::media::observe_jpeg(standalone.view(),observed,failure,bad,{true,true,true,true}));
            REQUIRE(!lpb::media::observe_target_primary(attribute_only,facts,error));
            REQUIRE(!lpb::media::observe_cross_target_mpf_primary(mpf_image(attribute_image(xml({},"G:MotionPhoto='1' G:MotionPhotoVersion='1'", "xmlns:G='http://ns.google.com/photos/1.0/camera/'"))),vendor,error));
            auto malformed_attribute=attribute_image();le(malformed_attribute,40,3);
            REQUIRE(!lpb::media::observe_cross_target_mpf_primary(mpf_image(malformed_attribute),vendor,error));
            auto duplicate_attribute=attribute_image();duplicate_attribute.insert(duplicate_attribute.begin()+2,attribute_only.begin()+2,attribute_only.begin()+48);
            REQUIRE(!lpb::media::observe_cross_target_mpf_primary(mpf_image(duplicate_attribute),vendor,error));
            REQUIRE(!lpb::media::observe_cross_target_mpf_primary(mpf_image(jpeg(xml(item("Primary","image/jpeg")))),vendor,error));
            REQUIRE(!lpb::media::observe_cross_target_mpf_primary(mpf_image(mpf_image(jpeg())),vendor,error));
            const std::string camera_ns="xmlns:M='http://ns.xiaomi.com/photos/1.0/camera/'";
            const std::string ordinary_camera="M:XMPMeta='&lt;video_madrid_image type=&quot;701&quot; offset=&quot;0&quot; length=&quot;0&quot; width=&quot;3072&quot; height=&quot;4096&quot; /&gt;'";
            const auto ordinary_packet=xml({},ordinary_camera,camera_ns);
            REQUIRE(lpb::media::observe_cross_target_mpf_primary(mpf_image(attribute_image(ordinary_packet)),vendor,error));
            std::string unchanged;REQUIRE(lpb::media::rebase_micamera_offsets(ordinary_packet,12345,unchanged));
            REQUIRE(unchanged==ordinary_packet);
            for(const auto& fragment:{
                "&lt;video_madrid_image offset=&quot;1&quot; length=&quot;0&quot; /&gt;",
                "&lt;video_madrid_image offset=&quot;0&quot; /&gt;",
                "&lt;video_madrid_image offset=&quot;0&quot; offset=&quot;0&quot; length=&quot;0&quot; /&gt;",
                "&lt;video_madrid_image offset=&quot;0&quot; length=&quot;0&quot; /&gt;&lt;subimage /&gt;",
                "&lt;video_madrid_image offset=&quot;0&quot; length=&quot;0&quot;&gt;&lt;subimage /&gt;&lt;/video_madrid_image&gt;",
                "&lt;video_madrid_image offset=&quot;0&quot; length=&quot;0&quot; note=&quot;&amp;invalid;&quot; /&gt;",
                "&lt;video_madrid_image offset=&quot;0&quot;length=&quot;0&quot; /&gt;"}) {
                const auto packet=xml({},"M:XMPMeta='"+std::string(fragment)+"'",camera_ns);
                REQUIRE(!lpb::media::observe_cross_target_mpf_primary(mpf_image(jpeg(packet)),vendor,error));
                REQUIRE(!lpb::media::rebase_micamera_offsets(packet,12345,unchanged));
            }
            const auto malformed_camera=xml({},"M:XMPMeta='&lt;subimage offset=&quot;20&quot; length=&quot;10&quot; /&gt;'",camera_ns);
            REQUIRE(!lpb::media::observe_cross_target_mpf_primary(mpf_image(attribute_image(malformed_camera)),vendor,error));
            REQUIRE(!lpb::media::observe_cross_target_mpf_primary(mpf_image(jpeg(malformed_camera)),vendor,error));
            auto gap=mpf_image(jpeg());REQUIRE(lpb::media::observe_cross_target_mpf_primary(gap,vendor,error));
            const auto original_relative=static_cast<uint32_t>(vendor.auxiliary_offset-10);
            le(gap,84,original_relative+1);REQUIRE(!lpb::media::observe_cross_target_mpf_primary(gap,vendor,error));
            auto overlap=mpf_image(jpeg());le(overlap,84,original_relative-1);REQUIRE(!lpb::media::observe_cross_target_mpf_primary(overlap,vendor,error));
            auto dependency=mpf_image(jpeg());le(dependency,72,1,2);REQUIRE(!lpb::media::observe_cross_target_mpf_primary(dependency,vendor,error));
        }
        success(jpeg(),facts); REQUIRE(facts.width==1 && facts.height==1 && facts.auxiliaries.empty());
        REQUIRE(!lpb::media::target_primary_has_live_binding(facts));
        std::string asset_error;
        auto old_original=jpeg(xml({},"G:MotionPhoto='1' G:MotionPhotoVersion='1' G:MotionPhotoPresentationTimestampUs='0'", "xmlns:G='http://ns.google.com/photos/1.0/camera/'"));
        REQUIRE(lpb::media::observe_original_jpeg_asset(old_original,asset_error));
        REQUIRE(!lpb::media::observe_gainmap_jpeg_asset(old_original,asset_error));
        auto padded_gain=jpeg();padded_gain.insert(padded_gain.end(),16,0);
        REQUIRE(lpb::media::observe_gainmap_jpeg_asset(padded_gain,asset_error));
        padded_gain.back()=1;REQUIRE(!lpb::media::observe_gainmap_jpeg_asset(padded_gain,asset_error));
        // MiCamera owns a JPEG subimage followed by a PNG watermark through
        // its escaped XMPMeta attribute, independently of Google Directory.
        const bytes mi_jpeg=jpeg();
        const bytes mi_png{0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a,
            0,0,0,13,'I','H','D','R',0,0,0,1,0,0,0,1,8,6,0,0,0,0x1f,0x15,0xc4,0x89,
            0,0,0,11,'I','D','A','T',0x78,0x9c,0x63,0x60,0,2,0,0,5,0,1,0xa5,0xf6,0x45,0x40,
            0,0,0,0,'I','E','N','D',0xae,0x42,0x60,0x82};
        const size_t mi_length=mi_jpeg.size()+mi_png.size();
        const auto mi_attribute="M:XMPMeta=\"&lt;subimage offset=&quot;"+std::to_string(mi_length)+
            "&quot; length=&quot;"+std::to_string(mi_jpeg.size())+"&quot; /&gt;&lt;lenswatermark offset=&quot;"+
            std::to_string(mi_png.size())+"&quot; length=&quot;"+std::to_string(mi_png.size())+"&quot; /&gt;\"";
        const std::string mi_namespace="xmlns:M='http://ns.xiaomi.com/photos/1.0/camera/'";
        const auto mi_packet=xml({},mi_attribute,mi_namespace);
        auto mi=jpeg(mi_packet);add(mi,mi_jpeg);add(mi,mi_png);success(mi,facts);
        { lpb::media::mpf_vendor_primary vendor;std::string error;
          REQUIRE(!lpb::media::observe_cross_target_mpf_primary(mpf_image(mi),vendor,error)); }
        REQUIRE(facts.auxiliaries.size()==2&&facts.auxiliaries[0].selector_kind==3&&facts.auxiliaries[1].mime=="image/png");
        std::string mi_rebased;REQUIRE(lpb::media::rebase_micamera_offsets(mi_packet,4,mi_rebased));
        lpb_media_range mi_sub{},mi_water{};
        REQUIRE(lpb::media::observe_micamera_assets(mi,mi_rebased,mi.size()+4,mi_sub,mi_water)==1);
        REQUIRE(mi_sub.length==mi_jpeg.size()&&mi_water.offset+mi_water.length==mi.size());
        REQUIRE(lpb::media::observe_micamera_assets(mi,mi_packet,mi.size()+4,mi_sub,mi_water)==-1);
        auto truncated_mi=mi;truncated_mi.pop_back();rejection(truncated_mi,"MiCamera asset graph");
        REQUIRE(!lpb::media::rebase_micamera_offsets(xml({},mi_attribute+" "+mi_attribute,mi_namespace),4,mi_rebased));
        success(jpeg(xml({}, "H:Version='1.0'", "xmlns:H='http://ns.adobe.com/hdr-gain-map/1.0/'")),facts);
        REQUIRE(!lpb::media::target_primary_has_live_binding(facts));
        success(jpeg(xml({}, "O:MotionPhotoOwner='oplus'", "xmlns:O='http://ns.oplus.com/photos/1.0/camera/'")),facts);
        REQUIRE(lpb::media::target_primary_has_live_binding(facts));
        success(jpeg(xml({}, {}, "xmlns:G='http://ns.google.com/photos/1.0/camera/' xmlns:H='http://ns.adobe.com/hdr-gain-map/1.0/'")),facts);
        REQUIRE(!lpb::media::target_primary_has_live_binding(facts));
        success(jpeg(xml(item("Primary","image/jpeg"))),facts); REQUIRE(facts.auxiliaries.empty());
        const auto aux=jpeg();
        auto multiple=jpeg(xml(item("Primary","image/jpeg")+item("GainMap","image/jpeg","I:Length='"+std::to_string(aux.size())+"'")));
        const auto expected_aux_offset=multiple.size(); add(multiple,aux); success(multiple,facts);
        REQUIRE(facts.auxiliaries.size()==1 && facts.auxiliaries[0].semantic=="GainMap");
        REQUIRE(facts.auxiliaries[0].selector_kind==1 && facts.auxiliaries[0].selector_id==1);
        REQUIRE(facts.auxiliaries[0].relationship=="gain-map" && facts.auxiliaries[0].range.offset==expected_aux_offset);
        REQUIRE(facts.auxiliaries[0].range.length==aux.size());
        multiple=jpeg(); multiple.push_back(7); rejection(multiple,"unowned");
        multiple=jpeg(xml(item("Primary","image/jpeg")+item("GainMap","image/jpeg","I:Length='99999'"))); rejection(multiple,"exceeds");
        auto video=jpeg(xml(item("Primary","image/jpeg")+item("MotionPhoto","video/mp4","I:Length='4'")));
        const auto video_prefix=video;
        add(video,{1,2,3,4}); success(video,facts); REQUIRE(lpb::media::target_primary_has_live_binding(facts));
        std::string prefix_error;
        REQUIRE(lpb::media::observe_target_jpeg_prefix(video_prefix,video.size(),facts,prefix_error));
        REQUIRE(facts.video_offset==video_prefix.size() && facts.video_length==4 && facts.primary_size==video_prefix.size());
        REQUIRE(!lpb::media::observe_target_jpeg_prefix(video_prefix,video.size()+1,facts,prefix_error));
        REQUIRE(facts.width==0 && prefix_error.find("full file length")!=std::string::npos);
        const auto aux_video_xml=xml(item("Primary","image/jpeg")+
            item("GainMap","image/jpeg","I:Length='"+std::to_string(aux.size())+"'")+
            item("MotionPhoto","video/mp4","I:Length='4'"));
        auto aux_prefix=jpeg(aux_video_xml); const auto missing_aux_prefix=aux_prefix;
        add(aux_prefix,aux);
        REQUIRE(lpb::media::observe_target_jpeg_prefix(aux_prefix,aux_prefix.size()+4,facts,prefix_error));
        REQUIRE(facts.auxiliaries.size()==1 && facts.auxiliaries[0].range.length==aux.size());
        REQUIRE(!lpb::media::observe_target_jpeg_prefix(missing_aux_prefix,aux_prefix.size()+4,facts,prefix_error));
        REQUIRE(facts.width==0 && prefix_error.find("exceeds")!=std::string::npos);
        auto extra_prefix=video_prefix; extra_prefix.push_back(0);
        REQUIRE(!lpb::media::observe_target_jpeg_prefix(extra_prefix,extra_prefix.size()+4,facts,prefix_error));
        REQUIRE(facts.width==0 && prefix_error.find("boundary")!=std::string::npos);
        const auto no_video_prefix=jpeg(xml(item("Primary","image/jpeg")));
        REQUIRE(!lpb::media::observe_target_jpeg_prefix(no_video_prefix,no_video_prefix.size()+4,facts,prefix_error));
        REQUIRE(facts.width==0 && prefix_error.find("requires an actual final")!=std::string::npos);
        auto h=heif(); success(h,facts); REQUIRE(facts.width==7 && facts.height==5 && facts.orientation==1 && facts.primary_item_id==1);
        auto wrong_brand=h; std::copy_n("avif",4,wrong_brand.begin()+8); rejection(wrong_brand,"file type brand");
        auto wrong_codec=h;
        const auto codec=std::search(wrong_codec.begin(),wrong_codec.end(),"hvc1","hvc1"+4);
        REQUIRE(codec!=wrong_codec.end()); std::copy_n("av01",4,codec); rejection(wrong_codec,"must be HEVC");
        std::copy_n("grid",4,codec); rejection(wrong_codec,"HEVC-backed grid");
        REQUIRE(facts.primary_extents.size()==1 && facts.primary_extents[0].length==4);
        success(heif(1),facts); REQUIRE(facts.orientation==8);
        success(heif(0,true),facts); REQUIRE(facts.orientation==2);
        success(heif(1,true),facts); REQUIRE(facts.orientation==7); // rotate CCW then horizontal mirror
        bytes aperture;
        for(const uint32_t value:{5u,1u,3u,1u,0u,1u,0u,1u}) be(aperture,value,4);
        success(heif(0,false,true,false,aperture),facts); REQUIRE(facts.width==5 && facts.height==3);
        auto bad_aperture=aperture; bad_aperture[3]=8;
        rejection(heif(0,false,true,false,bad_aperture),"clean aperture");
        bad_aperture=aperture; bad_aperture[7]=2;
        rejection(heif(0,false,true,false,bad_aperture),"clean aperture");
        bad_aperture=aperture; bad_aperture[23]=0;
        rejection(heif(0,false,true,false,bad_aperture),"clean aperture");
        bad_aperture=aperture; bad_aperture[19]=2;
        rejection(heif(0,false,true,false,bad_aperture),"clean aperture");
        rejection(heif(4),"rotation"); rejection(heif(0,false,false),"no owned ispe"); rejection(heif(0,false,true,true),"duplicate ispe");
        {
            const auto le=[](bytes& b,uint32_t n){for(unsigned i=0;i<4;++i)b.push_back(static_cast<uint8_t>(n>>(i*8)));};
            bytes sef{0,0,0x23,1,3,0,0,0,'F','o','o',99};const auto table=static_cast<uint32_t>(sef.size());
            sef.insert(sef.end(),{'S','E','F','H'});le(sef,107);le(sef,1);
            sef.insert(sef.end(),{0,0,0x23,1});le(sef,table);le(sef,table);le(sef,24);sef.insert(sef.end(),{'S','E','F','T'});
            auto with_sef=heif();const auto at=with_sef.size();add(with_sef,box("sefd",sef));const auto boundary=with_sef.size();
            success(with_sef,facts);REQUIRE(facts.samsung_metadata.offset==at);
            add(with_sef,box("mpvd",{1,2,3,4}));success(with_sef,facts,true);
            REQUIRE(facts.encoding_boundary==boundary&&facts.samsung_metadata.offset==at&&facts.video_length==4);
            auto interleaved=heif();add(interleaved,box("sefd",sef));add(interleaved,box("free",{}));add(interleaved,box("mpvd",{1}));
            std::string error;REQUIRE(!lpb::media::observe_target_primary(interleaved,facts,error,true));
        }
        const auto image_size=h.size(); add(h,box("mpvd",{1,2,3,4})); success(h,facts,true);
        REQUIRE(facts.primary_size==image_size && facts.video_header_size==8 && facts.video_offset==image_size+8 && facts.video_length==4);
        rejection(h,"explicitly enabled");
        auto large=heif(); bytes mpvd; be(mpvd,1,4); mpvd.insert(mpvd.end(),{'m','p','v','d'}); be(mpvd,20,8); add(mpvd,{1,2,3,4}); add(large,mpvd);
        success(large,facts,true); REQUIRE(facts.video_header_size==16 && facts.video_length==4);
        add(large,box("free",{})); rejection(large,"one nonempty final mpvd",true);
        std::cout<<"Target primary factual JPEG/HEIF observations passed\n"; return 0;
    } catch(const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
    catch(...) { std::cerr<<"Primary test entry failed at its C ABI exception boundary\n";return 1; }
}
