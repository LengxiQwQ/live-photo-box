#include "containers/isobmff_mebx_transplant.h"
#include <cstring>
#include "binary/portable_io.h"
#include <iostream>
#include <stdexcept>
namespace {
using bytes=std::vector<uint8_t>;
void need(bool value,const char* message){if(!value)throw std::runtime_error(message);}
void put(bytes& b,size_t p,uint32_t v){for(size_t i=0;i<4;++i)b[p+i]=uint8_t(v>>(24-i*8));}
uint32_t get(const bytes& b,size_t p){return uint32_t(b[p])<<24|uint32_t(b[p+1])<<16|uint32_t(b[p+2])<<8|b[p+3];}
bytes words(std::initializer_list<uint32_t> values){bytes b(values.size()*4);size_t p=0;for(auto v:values){put(b,p,v);p+=4;}return b;}
bytes box(const char* type,bytes body){bytes b(8);put(b,0,uint32_t(body.size()+8));std::memcpy(b.data()+4,type,4);b.insert(b.end(),body.begin(),body.end());return b;}
bytes join(std::initializer_list<bytes> values){bytes b;for(const auto& v:values)b.insert(b.end(),v.begin(),v.end());return b;}
size_t child(const bytes& b,size_t first,size_t last,const char* kind){for(size_t p=first;p<last;){isobmff_box_header h{};need(try_read_box_header(b.data(),p,last,h),"fixture box");if(!std::memcmp(b.data()+p+4,kind,4))return p;p+=h.size;}throw std::runtime_error("fixture child");}
bytes movie(uint32_t movie_scale,uint32_t media_scale,uint32_t offset,bool edit,bool leading=false,bool vfr=false){
 bytes mv(100),tk(84),md(24),hd(24);put(mv,12,movie_scale);put(mv,16,movie_scale*(leading?3U:2U));put(mv,96,2);
 put(tk,0,3);put(tk,12,1);put(tk,20,movie_scale*(leading?3U:2U));put(tk,40,65536);put(tk,56,65536);put(tk,72,0x40000000);put(tk,76,65536);put(tk,80,65536);
 put(md,12,media_scale);put(md,16,media_scale*2);std::memcpy(hd.data()+8,"vide",4);
 auto stbl=box("stbl",join({box("stsd",join({words({0,1}),box("hvc1",{})})),box("stts",vfr?words({0,2,1,media_scale/2,1,media_scale+media_scale/2}):words({0,1,2,media_scale})),box("stsz",words({0,1,2})),box("stsc",words({0,1,1,2,1})),box("stco",words({0,1,0})),box("ctts",words({0,1,2,offset}))}));
 auto trak=box("trak",join({box("tkhd",tk),edit?box("edts",box("elst",leading?words({0,2,movie_scale,UINT32_MAX,65536,movie_scale*2,0,65536}):words({0,1,movie_scale*2,0,65536}))):bytes{},box("mdia",join({box("mdhd",md),box("hdlr",hd),box("minf",stbl)}))}));
 auto b=join({box("ftyp",words({0x69736f6d,0,0x69736f6d})),box("moov",join({box("mvhd",mv),trak})),box("mdat",{0x31,0x32})});
 auto m=child(b,0,b.size(),"moov"),t=child(b,m+8,m+get(b,m),"trak"),d=child(b,t+8,t+get(b,t),"mdia"),n=child(b,d+8,d+get(b,d),"minf"),s=child(b,n+8,n+get(b,n),"stbl"),c=child(b,s+8,s+get(b,s),"stco");
 put(b,c+16,uint32_t(b.size()-2));return b;
}
void run(){
 {
  auto clipped=movie(1000,90000,0,true);
  auto m=child(clipped,0,clipped.size(),"moov"),t=child(clipped,m+8,m+get(clipped,m),"trak"),e=child(clipped,t+8,t+get(clipped,t),"edts"),l=child(clipped,e+8,e+get(clipped,e),"elst");
  put(clipped,l+16,1500);
  isobmff_video_presentation_timeline timeline{};
  need(inspect_isobmff_video_presentation_timeline(clipped,timeline),"legal final-frame edit clipping must be modeled");
  need(timeline.visible_sample_count==2&&timeline.presentation_order.back().duration_100ns==5000000,
      "clipped final frame must retain its exact half-second visible interval");
 }
 auto source=movie(1000,90000,0,true),target=movie(1000,90000,0,false);bytes output;bool changed=false;const char* failure=nullptr;
 need(lpb::media::preserve_isobmff_copy_timing(source,target,output,changed,&failure),"different movie/media clocks must map");
 auto m=child(output,0,output.size(),"moov"),t=child(output,m+8,m+get(output,m),"trak"),e=child(output,t+8,t+get(output,t),"edts"),l=child(output,e+8,e+get(output,e),"elst");
 need(get(output,l+16)==2000,"two seconds must not become 23 ms");
 target=movie(1000,90000,0x80000000U,false);
 need(lpb::media::preserve_isobmff_copy_timing(source,target,output,changed,&failure),"large unsigned composition shift must map");
 m=child(output,0,output.size(),"moov");t=child(output,m+8,m+get(output,m),"trak");e=child(output,t+8,t+get(output,t),"edts");l=child(output,e+8,e+get(output,e),"elst");
 need(output[l+8]==1,"large media_time requires v1");need(get(output,l+24)==0 && get(output,l+28)==0x80000000U,"v1 signed media_time remains positive");
 auto bad=source;m=child(bad,0,bad.size(),"moov");t=child(bad,m+8,m+get(bad,m),"trak");e=child(bad,t+8,t+get(bad,t),"edts");l=child(bad,e+8,e+get(bad,e),"elst");
 for(const uint32_t count:{0U,3U,UINT32_MAX}){auto x=bad;put(x,l+12,count);need(!lpb::media::preserve_isobmff_copy_timing(x,target,output,changed,&failure)&&output.empty(),"unsupported/truncated edits fail closed");}
 auto x=bad;x[l+8]=2;need(!lpb::media::preserve_isobmff_copy_timing(x,target,output,changed,&failure),"unknown elst version rejected");
 x=bad;put(x,l+24,0x00020000U);need(!lpb::media::preserve_isobmff_copy_timing(x,target,output,changed,&failure),"unsupported rate rejected");
 x=bad;put(x,l+20,UINT32_MAX-1);need(!lpb::media::preserve_isobmff_copy_timing(x,target,output,changed,&failure),"invalid negative media time rejected");
 auto leading=movie(1000,90000,0,true,true);
 need(lpb::media::preserve_isobmff_copy_timing(leading,movie(1000,90000,0,false),output,changed,&failure),"leading empty edit must map");
 m=child(output,0,output.size(),"moov");t=child(output,m+8,m+get(output,m),"trak");e=child(output,t+8,t+get(output,t),"edts");l=child(output,e+8,e+get(output,e),"elst");
 need(get(output,l+12)==2&&get(output,l+16)==1000&&get(output,l+20)==UINT32_MAX,"leading empty edit remains empty and one second");
 auto vfr=movie(1000,90000,0,true,false,true);
 need(lpb::media::preserve_isobmff_copy_timing(vfr,movie(1000,90000,0,false,false,true),output,changed,&failure),"VFR sample intervals must map");
 auto language_source=source;
 m=child(language_source,0,language_source.size(),"moov");t=child(language_source,m+8,m+get(language_source,m),"trak");
 auto mdia=child(language_source,t+8,t+get(language_source,t),"mdia");
 bytes language_body{0,0,0,0,'z','h','-','H','a','n','s',0};auto elng=box("elng",language_body);
 const size_t insert_at=mdia+get(language_source,mdia);
 language_source.insert(language_source.begin()+insert_at,elng.begin(),elng.end());
 for(auto owner:{m,t,mdia})put(language_source,owner,get(language_source,owner)+uint32_t(elng.size()));
 auto minf=child(language_source,mdia+8,mdia+get(language_source,mdia),"minf"),stbl=child(language_source,minf+8,minf+get(language_source,minf),"stbl"),co=child(language_source,stbl+8,stbl+get(language_source,stbl),"stco");
 put(language_source,co+16,get(language_source,co+16)+uint32_t(elng.size()));
 need(lpb::media::preserve_isobmff_additional_tracks(language_source,movie(1000,90000,0,false),output),"source-only extended language must be retained");
 m=child(output,0,output.size(),"moov");t=child(output,m+8,m+get(output,m),"trak");mdia=child(output,t+8,t+get(output,t),"mdia");
 auto out_language=child(output,mdia+8,mdia+get(output,mdia),"elng");
 need(get(output,out_language)==elng.size()&&std::equal(elng.begin(),elng.end(),output.begin()+out_language),"extended language bytes must match");
 auto qt_target=movie(1000,90000,0,false);std::memcpy(qt_target.data()+8,"qt  ",4);
 m=child(qt_target,0,qt_target.size(),"moov");t=child(qt_target,m+8,m+get(qt_target,m),"trak");mdia=child(qt_target,t+8,t+get(qt_target,t),"mdia");minf=child(qt_target,mdia+8,mdia+get(qt_target,mdia),"minf");
 bytes data_handler(24);std::memcpy(data_handler.data()+8,"url ",4);data_handler.insert(data_handler.end(),{4,'D','a','t','a'});auto generated_handler=box("hdlr",data_handler);
 qt_target.insert(qt_target.begin()+minf+8,generated_handler.begin(),generated_handler.end());
 for(auto owner:{m,t,mdia,minf})put(qt_target,owner,get(qt_target,owner)+uint32_t(generated_handler.size()));
 stbl=child(qt_target,minf+8,minf+get(qt_target,minf),"stbl");co=child(qt_target,stbl+8,stbl+get(qt_target,stbl),"stco");put(qt_target,co+16,get(qt_target,co+16)+uint32_t(generated_handler.size()));
 need(lpb::media::preserve_isobmff_additional_tracks(source,qt_target,output),"target-only QuickTime data-handler must retain target string representation");
 auto nested_source=source;bytes nested=box("free",{});
 for(unsigned depth=0;depth<16U;++depth)nested=box("mdia",nested);
 m=child(nested_source,0,nested_source.size(),"moov");t=child(nested_source,m+8,m+get(nested_source,m),"trak");mdia=child(nested_source,t+8,t+get(nested_source,t),"mdia");
 const size_t nested_at=mdia+get(nested_source,mdia);nested_source.insert(nested_source.begin()+nested_at,nested.begin(),nested.end());
 for(auto owner:{m,t,mdia})put(nested_source,owner,get(nested_source,owner)+uint32_t(nested.size()));
 minf=child(nested_source,mdia+8,mdia+get(nested_source,mdia),"minf");stbl=child(nested_source,minf+8,minf+get(nested_source,minf),"stbl");co=child(nested_source,stbl+8,stbl+get(nested_source,stbl),"stco");put(nested_source,co+16,get(nested_source,co+16)+uint32_t(nested.size()));
 lpb::memory_random_access nested_reader{nested_source};std::string nested_json;
 need(!lpb::media::inspect_isobmff_asset_inventory(nested_reader.view(),nested_json),"hostile deep media containers must fail without unbounded recursion");
 auto inventory_source=source;
 auto u=box("uuid",bytes(16,0x11)),u2=box("uuid",bytes(16,0x22));
 inventory_source.insert(inventory_source.end(),u.begin(),u.end());inventory_source.insert(inventory_source.end(),u2.begin(),u2.end());
 lpb::memory_random_access reader{inventory_source};std::string json;
 need(lpb::media::inspect_isobmff_asset_inventory(reader.view(),json),"distinct UUID types must inventory");
 need(json.find("11111111111111111111111111111111")!=std::string::npos&&json.find("22222222222222222222222222222222")!=std::string::npos,"UUID user types must qualify identities");
 auto short_uuid=box("uuid",bytes(15));inventory_source.insert(inventory_source.end(),short_uuid.begin(),short_uuid.end());
 reader.bytes=inventory_source;need(!lpb::media::inspect_isobmff_asset_inventory(reader.view(),json),"short UUID must fail without out-of-bounds read");
 std::cout<<"PASS: clock-domain duration, v1 positive media_time, malformed/multi-edit/version/rate/negative-time rejection\n";
}
}
int main(){try{run();return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
