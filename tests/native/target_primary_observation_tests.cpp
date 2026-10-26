#include "media/target_primary_observation.h"
#include "media/media_inspector.h"
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
}
#ifdef LPB_NATIVE_TEST_HARNESS
extern "C" LPB_API int LPB_CALL lpb_test_p9_primary_observation() {
#else
int main() {
#endif
    try {
        lpb::media::target_primary_observation facts;
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
