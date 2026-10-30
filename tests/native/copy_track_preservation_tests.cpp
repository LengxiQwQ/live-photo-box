#include "containers/isobmff_mebx_transplant.h"
#include "containers/isobmff_track_graph.h"
#include "containers/remux_byte_proof.h"
#include "containers/inline_transform_metadata.h"
#include "containers/inline_scene_metadata.h"
#include <algorithm>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace {
using bytes=std::vector<uint8_t>;
void require(bool v,const char* m){if(!v)throw std::runtime_error(m);}
uint32_t u32(const uint8_t* p){return uint32_t(p[0])<<24|uint32_t(p[1])<<16|uint32_t(p[2])<<8|p[3];}
void put(bytes& b,size_t p,uint32_t n){for(size_t i=0;i<4;++i)b[p+i]=static_cast<uint8_t>(n>>(24-i*8));}
struct box {size_t at,body,end;std::string kind;};
std::vector<box> children(const bytes& b,size_t p,size_t end){
 std::vector<box> rows;
 while(p<end){isobmff_box_header h{};require(try_read_box_header(b.data(),p,end,h),"test fixture box grammar");rows.push_back({p,p+h.header_size,p+h.size,std::string(reinterpret_cast<const char*>(b.data()+p+4),4)});p+=h.size;}
 return rows;
}
box one(const std::vector<box>& r,const char* kind){box v{};size_t count=0;for(const auto& x:r)if(x.kind==kind){v=x;++count;}require(count==1,"test fixture unique owner");return v;}
std::vector<isobmff_media_data_range> mdats(const bytes& b){std::vector<isobmff_media_data_range> rows;for(const auto& x:children(b,0,b.size()))if(x.kind=="mdat")rows.push_back({x.body,x.end});return rows;}
bool evidence(const bytes& a,const bytes& b,bool copy=true,const char** reason=nullptr){lpb::media::isobmff_static_metadata_evidence e{};return lpb::media::capture_isobmff_static_metadata_evidence(a,b,e,{}, {},reason,copy);}
bool stream(const bytes& a,const bytes& b){lpb::memory_random_access s{a},t{b};const auto sm=mdats(a),tm=mdats(b);return lpb::media::validate_isobmff_mebx_stream_remux(a,sm,s.view(),b,tm,t.view(),nullptr,nullptr);}
void run_scene(const std::filesystem::path& path) {
 std::ifstream input(path,std::ios::binary);require(bool(input),"real scene metadata input required");
 bytes source((std::istreambuf_iterator<char>(input)),{});
 const auto movie=one(children(source,0,source.size()),"moov"),scene=one(children(source,movie.body,movie.end),"gnre");
 for(const auto& mdat:mdats(source))require(mdat.end<=movie.at,"derived scene mutations must not relocate referenced media");
 bytes payload(source.begin()+scene.body,source.begin()+scene.end);
 require(lpb::media::copy_inline_scene_metadata(payload),"real scene inline grammar");
 require(evidence(source,source)&&stream(source,source),"real whole Copy media and metadata self proof");
 require(!evidence(source,source,false),"scene mapping does not extend transcode scope");
 const auto wrap=[&](const bytes& body){bytes box(8);put(box,0,static_cast<uint32_t>(8U+body.size()));std::memcpy(box.data()+4,"gnre",4);box.insert(box.end(),body.begin(),body.end());return box;};
 const auto replace=[&](const bytes& box){auto result=source;result.erase(result.begin()+scene.at,result.begin()+scene.end);result.insert(result.begin()+scene.at,box.begin(),box.end());put(result,movie.at,static_cast<uint32_t>(movie.end-movie.at+box.size()-(scene.end-scene.at)));return result;};
 const auto change=[&](std::string_view from,std::string_view to){auto b=payload;std::string s(b.begin(),b.end());const auto at=s.find(from);require(at!=std::string::npos,"scene malformed fixture field");s.replace(at,from.size(),to);return bytes(s.begin(),s.end());};
 auto changed=change("\"score\":22.755201","\"score\":22.755202");
 require(lpb::media::copy_inline_scene_metadata(changed)&&!evidence(source,replace(wrap(changed))),"valid but changed ordinary payload cannot claim preserved");
 require(!evidence(source,replace({})),"missing scene record cannot claim preserved");
 auto doubled=wrap(payload);const auto second=doubled;doubled.insert(doubled.end(),second.begin(),second.end());
 require(!evidence(replace(doubled),replace(doubled)),"duplicate movie scene owner rejects");
 std::vector<bytes> invalid{
  change("\"duration\":3030","\"duration\":3030,\"duration\":3030"),
  change("\"effect\":1,",""),change("\"effect\":1","\"offset\":1"),
  change("\"duration\":3030","\"duration\":-1"),change("\"duration\":3030","\"duration\":03030"),
  change("\"duration\":3030","\"duration\":9223372036854775808"),
  change("\"score\":22.755201","\"score\":1e999"),change("\"score\":22.755201","\"score\":nan"),
  change("\"score\":22.755201","\"score\":0x01"),change("\"isSimilar\":true","\"isSimilar\":1"),
  change("\"isSimilar\":true","\"isSimilar\":truefalse"),change("\"type\":\"livePhoto\"","\"type\":\"other\""),
  change("\"sceneSlices\":[]","\"sceneSlices\":[0]"),change("\"sceneSlices\":[]","\"sceneSlices\":[],\"sceneSlices\":[]"),
  change("\"sceneSlices\":[]","\"offset\":0"),change("\"lens\":\"back\"","\"lens\":\"back\",\"lens\":\"back\""),
  change("\"endTime\":1784956304054","\"endTime\":0"),change("\"id\":\"LpTask_IMG_20260725_131142\"","\"id\":\"\""),
  change("\"scene\":\"122\"","\"scene\":\"\\u0031\""),change("\"lensInformations\":[{","\"lensInformations\":[] ,\"unknown\":{ "),
  change("\"type\":\"livePhoto\"}","\"type\":\"livePhoto\"}trailing"),
  change("\"type\":\"livePhoto\"","\"type\":{\"livePhoto\":true}")
 };
 auto bad_prefix=payload;bad_prefix[0]=1;invalid.push_back(bad_prefix);
 auto bad_suffix=payload;bad_suffix.back()=1;invalid.push_back(bad_suffix);
 auto bad_padding=payload;bad_padding.pop_back();invalid.push_back(bad_padding);
 auto too_big=payload;too_big.insert(too_big.begin()+6,16384,' ');invalid.push_back(too_big);
 for(const auto& bad:invalid) {
  require(!lpb::media::copy_inline_scene_metadata(bad),"unknown/ambiguous/malformed scene grammar rejects");
  const auto derived=replace(wrap(bad));require(!evidence(derived,derived),"actual Copy classifier cannot bypass malformed scene grammar");
 }
 std::cout<<"Copy inline scene: actual full self proof, unchanged-only/nonCopy/duplicate/loss checks and "<<invalid.size()<<" malformed grammar negatives passed\n";
}
void check_transform_json() {
 const std::string value=R"(LivePhotoExtension{"coverFramePts":1466388,"desc":"OppoMotionVideoExt","matrixCount":0,"photoCropFactor":0.0,"photoCropRect":[0,0,0,0],"photoEisCropFactor":[1.11,1.11],"photoEisMatrix":[1.0,0.0,-3.0E-6,-0.0,1.0,-1.9E-5,0.0,0.0,1.0],"subVideoScaleFactor":1.0,"version":1,"videoOrientation":90,"videoSize":[1728,1296]})";
 const auto change=[&](const std::string& from,const std::string& to){auto out=value;const auto at=out.find(from);require(at!=std::string::npos,"JSON negative fixture field");out.replace(at,from.size(),to);return out;};
 require(lpb::media::copy_inline_transform_metadata(value),"actual complete inline transform JSON grammar");
 require(lpb::media::copy_inline_transform_metadata(change("\"version\":1,","\"version\" : 1 ,\n")),"JSON insignificant whitespace remains legal");
 require(!lpb::media::copy_inline_transform_metadata(value+std::string(16384,' ')),"otherwise complete JSON exceeds explicit resource bound");
 const std::vector<std::string> invalid{
  change("\"version\":1","\"version\":2"),change("\"matrixCount\":0","\"matrixCount\":1"),
  change("\"videoOrientation\":90","\"videoOrientation\":91"),change("\"videoSize\":[1728,1296]","\"videoSize\":[0,1296]"),
  change("\"videoSize\":[1728,1296]","\"videoSize\":[65536,1296]"),change("\"videoSize\":[1728,1296]","\"videoSize\":[1728,1296,1]"),
  change("\"version\":1","\"version\":1,\"version\":1"),change("\"version\":1","\"version\":1,\"unknown\":0"),
  change("\"version\":1,",""),change("OppoMotionVideoExt","OtherExtension"),
  change("1466388","-1"),change("1466388","9223372036854775808"),change("1466388","01466388"),change("1466388","1.0"),
  change("\"photoCropFactor\":0.0","\"photoCropFactor\":-1"),change("\"photoCropFactor\":0.0","\"photoCropFactor\":nan"),
  change("\"photoCropFactor\":0.0","\"photoCropFactor\":1e999"),change("\"photoCropFactor\":0.0","\"photoCropFactor\":01"),
  change("[0,0,0,0]","[0,0,0]"),change("[1.11,1.11]","[0,1.11]"),
  change("[1.0,0.0,-3.0E-6","[0.0,0.0,-3.0E-6"),
  change("\"version\":1","\"version\":{}"),change("\"desc\":\"OppoMotionVideoExt\"","\"desc\":\"Oppo\\u004dotionVideoExt\""),
  value+" trailing",change("\"version\":1,","\"version\":1,/*comment*/"),change("\"version\":1","\"version\":+1")
 };
 for(const auto& bad:invalid)require(!lpb::media::copy_inline_transform_metadata(bad),"malformed/unknown inline transform JSON rejects");
 std::cout<<"Closed inline transform JSON: actual/whitespace positives and26 malformed/schema negatives passed\n";
}
void run(const std::filesystem::path& path){
 std::ifstream input(path,std::ios::binary);require(bool(input),"real cached source required");
 bytes source((std::istreambuf_iterator<char>(input)),{});require(source.size()>16,"real source nonempty");
 require(!evidence(source,source,false),"transcode classifier does not inherit auxiliary acceptance");
 const char* reason=nullptr;
 if(!evidence(source,source,true,&reason)){if(reason)std::cerr<<reason<<'\n';require(false,"Copy self-proof covers complete real auxiliary graph");}
 require(stream(source,source),"complete real metadata and auxiliary stream proof");
 const auto movie=one(children(source,0,source.size()),"moov");
 // Additional inline user data is admitted only by the original Copy grammar.
 // This real fixture has a final moov, so filling its empty udta moves no mdat.
 require(movie.end==source.size(),"inline fixture requires final movie");
 const auto make_box=[](const char* kind,const bytes& body){bytes result(8);put(result,0,static_cast<uint32_t>(body.size()+8U));std::memcpy(result.data()+4,kind,4);result.insert(result.end(),body.begin(),body.end());return result;};
 const auto empty_udta=one(children(source,movie.body,movie.end),"udta");require(empty_udta.end-empty_udta.at==8U,"empty inline user data fixture");
 const auto append_movie=[&](const bytes& box_bytes){auto result=source;result.erase(result.begin()+empty_udta.at,result.begin()+empty_udta.end);result.insert(result.begin()+empty_udta.at,box_bytes.begin(),box_bytes.end());put(result,movie.at,static_cast<uint32_t>(movie.end-movie.at+box_bytes.size()-8U));return result;};
 const auto inline_leaf=[&](const char* type,const char* text){return make_box(type,bytes(text,text+std::strlen(text)));};
 bytes inline_body;for(const auto& leaf:{inline_leaf("SDLN","SEQ_PLAY"),inline_leaf("smrd","TRUEBLUE")})inline_body.insert(inline_body.end(),leaf.begin(),leaf.end());
 bytes model_body(4);const auto model=inline_leaf("mdln","MODEL-A1");model_body.insert(model_body.end(),model.begin(),model.end());
 const auto model_container=make_box("smta",model_body);inline_body.insert(inline_body.end(),model_container.begin(),model_container.end());
 const auto inline_data=make_box("udta",inline_body);const auto with_inline=append_movie(inline_data);
 require(evidence(with_inline,with_inline)&&stream(with_inline,with_inline),"valid inline metadata exact Copy proof");
 auto invalid_inline=with_inline;invalid_inline[empty_udta.at+16U]^=1;require(!evidence(invalid_inline,invalid_inline),"unknown inline SDLN payload rejects");
 invalid_inline=with_inline;invalid_inline[empty_udta.at+32U]^=1;require(!evidence(invalid_inline,invalid_inline),"unknown inline smrd payload rejects");
 invalid_inline=with_inline;invalid_inline[empty_udta.at+48U]=1;require(!evidence(invalid_inline,invalid_inline),"inline smta flags reject");
 invalid_inline=with_inline;invalid_inline[empty_udta.at+inline_data.size()-1U]=0;require(!evidence(invalid_inline,invalid_inline),"inline model embedded terminator rejects");
 invalid_inline=with_inline;std::memcpy(invalid_inline.data()+empty_udta.at+56U,"iloc",4);require(!evidence(invalid_inline,invalid_inline),"unknown inline model/reference type rejects");
 invalid_inline=with_inline;put(invalid_inline,empty_udta.at+40U,0);require(!evidence(invalid_inline,invalid_inline),"implicit-to-EOF inline container rejects");
 invalid_inline=with_inline;put(invalid_inline,empty_udta.at+52U,0);require(!evidence(invalid_inline,invalid_inline),"implicit-to-EOF inline model rejects");
 const auto single_play=make_box("udta",inline_leaf("SDLN","SEQ_PLAY"));invalid_inline=append_movie(single_play);put(invalid_inline,empty_udta.at+8U,0);require(!evidence(invalid_inline,invalid_inline),"implicit-to-EOF singleton inline leaf rejects");
 auto duplicated_body=inline_body;duplicated_body.insert(duplicated_body.end(),inline_body.begin(),inline_body.begin()+16);
 const auto duplicate_inline=append_movie(make_box("udta",duplicated_body));require(!evidence(duplicate_inline,duplicate_inline),"duplicate inline user-data field rejects");
 require(!evidence(source,with_inline),"new inline field absent from source cannot pass byteproof");
 lpb::media::isobmff_track_graph graph;require(lpb::media::inspect_isobmff_track_graph(std::span<const uint8_t>(source).subspan(movie.at,movie.end-movie.at),graph),"real graph");
 std::vector<box> aux;box metadata{};
 for(const auto& track:graph.tracks){if(track.handler=="auxv")aux.push_back({movie.at+track.offset,movie.at+track.offset+8,movie.at+track.offset+track.length,"trak"});if(track.handler=="meta"&&std::any_of(track.references.begin(),track.references.end(),[](const auto& r){return r.kind=="cdep";}))metadata={movie.at+track.offset,movie.at+track.offset+8,movie.at+track.offset+track.length,"trak"};}
 require(aux.size()==4&&metadata.end>metadata.at,"expected real multi-aux dependency coverage");
 const auto tc=children(source,aux[0].body,aux[0].end);const auto tref=one(tc,"tref"),vmap=one(children(source,tref.body,tref.end),"vmap");
 auto changed=source;put(changed,vmap.body,9999);require(!evidence(changed,changed),"foreign auxiliary owner rejects");require(!stream(source,changed),"edge target mismatch rejects");
 const auto mtref=one(children(source,metadata.body,metadata.end),"tref");const auto cdep=one(children(source,mtref.body,mtref.end),"cdep");
 changed=source;put(changed,cdep.body,2);require(!evidence(changed,changed),"dependency must match metadata primary owner");
 auto mdia=one(tc,"mdia");auto minf=one(children(source,mdia.body,mdia.end),"minf");auto stbl=one(children(source,minf.body,minf.end),"stbl");auto tables=children(source,stbl.body,stbl.end);auto stsd=one(tables,"stsd");auto codec=one(children(source,stsd.body+8,stsd.end),"hvc1");
 changed=source;std::memcpy(changed.data()+codec.at+4,"zzzz",4);require(!evidence(changed,changed),"unknown auxiliary codec rejects");
 changed=source;changed[codec.body+7]=2;require(!evidence(changed,changed),"external auxiliary data reference rejects");
 std::vector<box> descriptions;for(const auto& row:tables)if(row.kind=="sgpd")descriptions.push_back(row);
 require(descriptions.size()==2,"real distinct temporal and sync descriptions");
 changed=source;put(changed,descriptions[1].body+4,u32(source.data()+descriptions[0].body+4));require(!evidence(changed,changed),"duplicate grouping-type descriptions reject");
 changed=source;put(changed,descriptions[0].body+8,21);require(!evidence(changed,changed),"wrong temporal descriptor extent rejects");
 auto sbgp=one(tables,"sbgp");changed=source;put(changed,sbgp.body+16,999);require(!evidence(changed,changed),"invalid sample-group index rejects");
 changed=source;changed[codec.body+24]^=1;require(!stream(source,changed),"auxiliary geometry/config changes reject");
 changed=source;changed[codec.body+24]=changed[codec.body+25]=0;require(!evidence(changed,changed)&&!stream(changed,changed),"zero auxiliary coded width self-proof rejects");
 const auto hvcc=one(children(source,codec.body+78,codec.end-4),"hvcC");
 changed=source;changed[hvcc.body]=0;require(!evidence(changed,changed)&&!stream(changed,changed),"malformed auxiliary codec config self-proof rejects");
 auto tkhd=one(tc,"tkhd");size_t matrix=tkhd.body+(source[tkhd.body]?52U:40U);
 changed=source;put(changed,matrix,123);require(!evidence(changed,changed)&&!stream(changed,changed),"invalid auxiliary matrix self-proof rejects");
 auto mdhd=one(children(source,mdia.body,mdia.end),"mdhd");changed=source;put(changed,mdhd.body+(source[mdhd.body]?20U:12U),0);require(!evidence(changed,changed),"zero auxiliary timebase self-proof rejects");
 changed=source;changed[mdhd.body+1]=1;require(!evidence(changed,changed),"reserved auxiliary media-header flags self-proof reject");
 auto stco=one(tables,"stco");changed=source;put(changed,stco.body+8,u32(source.data()+stco.body+8)+1);require(!stream(source,changed),"different valid payload-relative address rejects");
 const auto ranges=mdats(source);isobmff_sample_table_layout layout{};require(inspect_isobmff_sample_table(source,stbl.body,stbl.end,ranges,0,layout),"actual aux table");
 changed=source;changed[static_cast<size_t>(layout.chunks[0].offset)]^=1;require(!stream(source,changed),"auxiliary media payload corruption rejects");
 auto stts=one(tables,"stts");changed=source;changed[stts.body+15]^=1;require(!stream(source,changed),"auxiliary timing change rejects");
 changed=source;changed[stts.body+1]=1;require(!evidence(changed,changed)&&!stream(changed,changed),"reserved auxiliary sample-table flags self-proof reject");
 changed=source;std::memcpy(changed.data()+cdep.at+4,"cdsc",4);require(!evidence(changed,changed),"duplicate metadata owner rejects");
 // Reorder complete legal tracks without changing their IDs or payloads.
 auto movie_children=children(source,movie.body,movie.end);std::vector<box> track_boxes;
 for(const auto& row:movie_children)if(row.kind=="trak")track_boxes.push_back(row);
 require(track_boxes.size()>2,"real ordered tracks");changed.clear();changed.insert(changed.end(),source.begin(),source.begin()+track_boxes[0].at);
 changed.insert(changed.end(),source.begin()+track_boxes[1].at,source.begin()+track_boxes[1].end);
 changed.insert(changed.end(),source.begin()+track_boxes[0].at,source.begin()+track_boxes[0].end);
 changed.insert(changed.end(),source.begin()+track_boxes[1].end,source.end());
 require(!evidence(source,changed)&&!stream(source,changed),"ordered track identity mismatch rejects");
 // Same bytes in another valid mdat must not authorize a different chunk owner.
 bytes two=source;const auto first=layout.chunks[0];require(first.size<=UINT32_MAX-8U,"bounded test chunk");size_t at=two.size();two.resize(at+8);put(two,at,static_cast<uint32_t>(first.size+8));std::memcpy(two.data()+at+4,"mdat",4);two.insert(two.end(),source.begin()+static_cast<size_t>(first.offset),source.begin()+static_cast<size_t>(first.offset+first.size));
 changed=two;require(at+8<=UINT32_MAX,"bounded test address");put(changed,stco.body+8,static_cast<uint32_t>(at+8));require(!stream(two,changed),"same payload in wrong ordered mdat rejects");
 std::vector<lpb::media::isobmff_mebx_track_facts> facts;require(lpb::media::inspect_isobmff_mebx_tracks(source,facts)&&!facts.empty(),"real dependency metadata parsed");
 require(!lpb::media::validate_isobmff_mebx_transplants(source,source,facts),"dependency-bearing transplant validator explicitly refuses");
 bytes plain;plain.insert(plain.end(),source.begin(),source.begin()+movie.body);
 for(const auto& row:movie_children) {
  if(row.kind=="trak") {
   auto children2=children(source,row.body,row.end);auto md=one(children2,"mdia");auto hd=one(children(source,md.body,md.end),"hdlr");
   if(std::memcmp(source.data()+hd.body+8,"meta",4)==0)continue;
  }
  plain.insert(plain.end(),source.begin()+row.at,source.begin()+row.end);
 }
 put(plain,movie.at,static_cast<uint32_t>(plain.size()-movie.at));
 plain.insert(plain.end(),source.begin()+movie.end,source.end());bytes transformed;
 std::vector<lpb::media::isobmff_mebx_track_facts> moved;
 require(!lpb::media::transplant_isobmff_mebx_tracks(source,plain,transformed,moved)&&transformed.empty()&&moved.empty(),"dependency-bearing transplantation rejects before constructing output");
 // Derived negative CTTS on a real aux track. Exercise a non-QT major with
 // compatible QT: NUL handler names and signed QuickTime composition offsets.
 require(ranges.back().end<=movie.at,"test insertion follows all source media data");
 bytes ctts(24);put(ctts,0,24);std::memcpy(ctts.data()+4,"ctts",4);put(ctts,12,1);put(ctts,16,layout.sample_count);put(ctts,20,0xffffffffU);
 bytes signed_aux=source;
 for(const auto& owner:std::vector<box>{movie,aux[0],mdia,minf,stbl})put(signed_aux,owner.at,static_cast<uint32_t>(owner.end-owner.at+ctts.size()));
 signed_aux.insert(signed_aux.begin()+stbl.end,ctts.begin(),ctts.end());
 require(evidence(signed_aux,signed_aux)&&stream(signed_aux,signed_aux),"derived signed-v0 auxiliary CTTS source valid");
 auto mixed=signed_aux;auto ftyp=one(children(source,0,source.size()),"ftyp");require(ftyp.end-ftyp.body>=12,"real compatible brand slot");
 std::memcpy(mixed.data()+ftyp.body,"isom",4);std::memcpy(mixed.data()+ftyp.body+8,"qt  ",4);bytes mixed_named;
 require(lpb::media::map_isobmff_handler_names(signed_aux,mixed,mixed_named),"mixed-brand ISO handler representation");
 require(stream(signed_aux,mixed_named),"compatible QT keeps signed CTTS semantics independent of handler major brand");
 auto iso=mixed_named;std::memcpy(iso.data()+ftyp.body+8,"isom",4);
 require(lpb::media::map_remux_composition_versions(iso,true,false)&&stream(signed_aux,iso),"signed-v0 auxiliary offsets become equivalent ISO-v1");
 std::cout<<"Copy-only real multi-aux identity/payload/time/owner proof and malformed negatives passed\n";
}
void run_opaque(const std::filesystem::path& path,const std::filesystem::path& mebx_path={}) {
 std::ifstream input(path,std::ios::binary);require(bool(input),"real Neutral opaque timed input required");
 bytes source((std::istreambuf_iterator<char>(input)),{});const auto movie=one(children(source,0,source.size()),"moov");
 box metadata{};size_t count=0;
 for(const auto& track:children(source,movie.body,movie.end))if(track.kind=="trak") {
  const auto mdia=one(children(source,track.body,track.end),"mdia");const auto handler=one(children(source,mdia.body,mdia.end),"hdlr");
  if(!std::memcmp(source.data()+handler.body+8,"meta",4)){metadata=track;++count;}
 }
 require(count==1,"one actual opaque metadata track");
 const auto mdia=one(children(source,metadata.body,metadata.end),"mdia");const auto minf=one(children(source,mdia.body,mdia.end),"minf");
 const auto stbl=one(children(source,minf.body,minf.end),"stbl");const auto tables=children(source,stbl.body,stbl.end);const auto stsd=one(tables,"stsd");
 const auto mett=one(children(source,stsd.body+8,stsd.end),"mett");
 require(evidence(source,source)&&stream(source,source),"real opaque timed Copy self proof");
 require(!evidence(source,source,false),"opaque timed data never enters transcode classifier");
 auto changed=source;changed[mett.body]^=1;require(!evidence(changed,changed)&&!stream(changed,changed),"unknown opaque MIME self proof rejects");
 changed=source;put(changed,mett.at,0);require(!evidence(changed,changed),"implicit opaque sample entry rejects");
 const auto co64=one(tables,"co64");const uint64_t offset=uint64_t(u32(source.data()+co64.body+8))<<32|u32(source.data()+co64.body+12);
 require(offset<source.size(),"actual metadata chunk address");changed=source;changed[static_cast<size_t>(offset)]^=1;
 require(!stream(source,changed),"opaque timed payload difference rejects actual streaming comparison");
 changed=source;put(changed,co64.body+12,u32(source.data()+co64.body+12)+1U);
 require(!stream(source,changed),"opaque timed address cannot be erased before exact mdat-relative proof");
 changed=source;const auto stts=one(tables,"stts");changed[stts.body+1]=1;require(!evidence(changed,changed)&&!stream(changed,changed),"opaque timed table flags reject");
 const auto tkhd=one(children(source,metadata.body,metadata.end),"tkhd");changed=source;put(changed,tkhd.body+40U,123);require(!evidence(changed,changed),"opaque metadata matrix malformed source rejects");
 std::vector<lpb::media::isobmff_mebx_track_facts> facts;bytes stage;
 require(!lpb::media::transplant_isobmff_mebx_tracks(source,source,stage,facts)&&stage.empty()&&facts.empty(),"opaque timed data cannot be silently transplanted as MEBX");
 if(!mebx_path.empty()) {
  std::ifstream other(mebx_path,std::ios::binary);require(bool(other),"real cleaned MEBX input required");bytes base((std::istreambuf_iterator<char>(other)),{});
  const auto bm=one(children(base,0,base.size()),"moov");require(bm.end==base.size(),"mixed fixture has final movie");
  auto opaque=bytes(source.begin()+metadata.at,source.begin()+metadata.end);
  put(opaque,tkhd.body+12U-metadata.at,1000U);
  const auto handler=one(children(source,mdia.body,mdia.end),"hdlr");const size_t name=handler.body+24U-metadata.at,name_end=handler.end-metadata.at;
  require(name_end>name&&opaque[name_end-1U]==0U&&name_end-name<=256U,"source ISO handler fixture");
  std::copy_backward(opaque.begin()+name,opaque.begin()+name_end-1U,opaque.begin()+name_end);opaque[name]=static_cast<uint8_t>(name_end-name-1U);
  isobmff_sample_table_layout layout{};uint64_t bad=0;
  require(inspect_isobmff_strict_sample_table(source,stbl.at,stbl.end,mdats(source),0U,layout,bad),"mixed metadata local chunks");
  bytes payload;uint64_t address=base.size()+opaque.size()+8U;
  for(size_t j=0;j<layout.chunks.size();++j) {
   const auto& chunk=layout.chunks[j];const size_t field=layout.chunk_offsets_entries_start+j*8U-metadata.at;
   put(opaque,field,static_cast<uint32_t>(address>>32));put(opaque,field+4U,static_cast<uint32_t>(address));
   payload.insert(payload.end(),source.begin()+static_cast<size_t>(chunk.offset),source.begin()+static_cast<size_t>(chunk.offset+chunk.size));address+=chunk.size;
  }
  auto mixed=base;mixed.insert(mixed.end(),opaque.begin(),opaque.end());put(mixed,bm.at,static_cast<uint32_t>(bm.end-bm.at+opaque.size()));
  const auto mvhd=one(children(base,bm.body,bm.end),"mvhd");put(mixed,mvhd.end-4U,1001U);
  bytes new_mdat(8);put(new_mdat,0,static_cast<uint32_t>(payload.size()+8U));std::memcpy(new_mdat.data()+4,"mdat",4);new_mdat.insert(new_mdat.end(),payload.begin(),payload.end());mixed.insert(mixed.end(),new_mdat.begin(),new_mdat.end());
  require(evidence(mixed,mixed)&&stream(mixed,mixed),"mixed MEBX+Mett fixture is valid in original Copy");
  std::vector<lpb::media::isobmff_mebx_track_facts> expected;require(lpb::media::inspect_isobmff_mebx_tracks(mixed,expected)&&!expected.empty(),"mixed fixture has real MEBX metadata");
  require(!lpb::media::validate_isobmff_mebx_transplants(mixed,mixed,expected),"MEBX validator cannot ignore actual extra Mett");
  auto stage_a=base;
  for(const auto& track:children(base,bm.body,bm.end))if(track.kind=="trak") {
   const auto d=one(children(base,track.body,track.end),"mdia"),h=one(children(base,d.body,d.end),"hdlr");
   if(!std::memcmp(base.data()+h.body+8U,"meta",4U))std::memcpy(stage_a.data()+track.at+4U,"free",4U);
  }
  require(!lpb::media::transplant_isobmff_mebx_tracks(mixed,stage_a,stage,facts)&&stage.empty()&&facts.empty(),"MEBX writer cannot drop actual extra Mett from mixed source");
 }
 std::cout<<"Copy opaque timed metadata: actual local grammar, payload/address/table/matrix negative and nonCopy/transplant refusal passed\n";
}
}
#ifdef _WIN32
int wmain(int argc,wchar_t** argv){try{require(argc>=2&&argc<=4,"one real cached MOV input required");check_transform_json();if(argc==3&&std::wstring_view(argv[2])==L"--scene"){run_scene(std::filesystem::path(argv[1]));return 0;}if(argc>=3){require(std::wstring_view(argv[2])==L"--opaque","known test mode");run_opaque(std::filesystem::path(argv[1]),argc==4?std::filesystem::path(argv[3]):std::filesystem::path{});}else run(std::filesystem::path(argv[1]));return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
#else
int main(int argc,char** argv){try{require(argc>=2&&argc<=4,"one real cached MOV input required");check_transform_json();if(argc==3&&std::string_view(argv[2])=="--scene"){run_scene(std::filesystem::path(argv[1]));return 0;}if(argc>=3){require(std::string_view(argv[2])=="--opaque","known test mode");run_opaque(std::filesystem::path(argv[1]),argc==4?std::filesystem::path(argv[3]):std::filesystem::path{});}else run(std::filesystem::path(argv[1]));return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
#endif
