#include "media/vivo_modern_inventory.h"
#include "media/empty_audio_placeholder.h"
#include "media/vivo_comment.h"
#include <iostream>
#include <stdexcept>

namespace {
using bytes=std::vector<uint8_t>;
void need(bool value,const char* message){if(!value)throw std::runtime_error(message);}
void put(bytes& b,uint32_t n){for(int i=3;i>=0;--i)b.push_back(uint8_t(n>>(8*i)));}
void append(bytes& b,const bytes& more){b.insert(b.end(),more.begin(),more.end());}
bytes atom(const char* type,bytes body={}){bytes out;put(out,uint32_t(body.size()+8));out.insert(out.end(),type,type+4);append(out,body);return out;}
bytes text(std::string_view value){return {value.begin(),value.end()};}
bytes mixed(std::string_view json,std::string_view id="pair"){
 bytes b=text("vivoMediaExtInfovivo");append(b,text(json));put(b,uint32_t(json.size()));append(b,text("cameralbum!"));put(b,uint32_t(19+id.size()));append(b,text(id));
 append(b,{255,255,255,255,0x1b,0x2a,0x39,0x48,0x57,0x66,0x75,0x84,0x93,0xa2,0xb3});return atom("uuid",b);
}
bytes track(uint32_t id,bool sound,bool empty=true,bool unknown=false){
 bytes tkhd(84);tkhd[3]=3;tkhd[12]=uint8_t(id>>24);tkhd[13]=uint8_t(id>>16);tkhd[14]=uint8_t(id>>8);tkhd[15]=uint8_t(id);
 bytes mdhd(24);mdhd[14]=0xbb;mdhd[15]=0x80;
 bytes handler(24);const auto name=sound?"soun":"vide";std::copy_n(name,4,handler.begin()+8);
 bytes smhd(8);bytes ref(4);put(ref,1);bytes url(4);url[3]=1;append(ref,atom("url ",url));
 bytes minf=atom("smhd",smhd);append(minf,atom("dinf",atom("dref",ref)));append(minf,atom("stbl",empty?bytes{}:atom("stsd",bytes(8))));
 bytes media=atom("mdhd",mdhd);append(media,atom("hdlr",handler));append(media,atom("minf",minf));
 bytes body=atom("tkhd",tkhd);append(body,atom("mdia",media));if(unknown)append(body,atom("trgr",bytes(4)));return atom("trak",body);
}
bool observed(const bytes& b){lpb::media::vivo_modern_inventory facts;return lpb::media::observe_vivo_modern(b,facts);}
}
int main(){try {
 using namespace lpb::media;
 const auto source=mixed(R"({"com.android.camera.imageTime":41,"com.android.camera.livePhoto.firstFrame":12345,"com.android.camera.xdr.meta":[1, 2,-3],"keep":"a\u0062","com.android.camera.livephoto":"pair"})");
 vivo_modern_inventory original;need(observe_vivo_modern(source,original)&&original.binding,"source mixed owner");
 bytes neutral;need(rebuild_vivo_neutral_movie(source,original,neutral),"field-level rebuild");vivo_modern_inventory cleaned;need(observe_vivo_modern(neutral,cleaned)&&!cleaned.binding,"no binding remains");
 need(cleaned.members.size()==2&&cleaned.members[0].key=="com.android.camera.xdr.meta"&&cleaned.members[1].value=="ab","ordinary semantics preserved");
 const auto raw=std::string(neutral.begin(),neutral.end());need(raw.find(R"("keep":"a\u0062")")!=std::string::npos&&raw.find("[1, 2,-3]")!=std::string::npos,"ordinary raw value spans retained");
 auto duplicate=source;append(duplicate,source);need(!observed(duplicate),"duplicate UUID owner");
 for(const auto json:{R"({"com.android.camera.livephoto":"pair","com.android.camera.livephoto":"pair"})",R"({"com.android.camera.livephoto":"pair","com.android.camera.imageTime":-1})",R"({"com.android.camera.livephoto":"pair","com.android.camera.imageTime":18446744073709551616})",R"({"child":{"com.android.camera.livephoto":"pair"}})",R"({"com.android.camera.livephoto":"pair","bad":"\ud800"})"})need(!observed(mixed(json)),"malformed/duplicate/shadow JSON");
 need(!observed(mixed(R"({"com.android.camera.livephoto":"pair"})","other")),"footer JSON pairing mismatch");
 for(size_t i=source.size()-15;i<source.size();++i){auto invalid=source;invalid[i]^=1;need(!observed(invalid),"binary footer identity");}
 auto bad_length=source;bad_length[original.json_end+3]^=1;need(!observed(bad_length),"JSON length rejected");
 auto suffix=source;append(suffix,atom("free"));need(observed(suffix),"read-only model sees nonfinal owner");need(!rebuild_vivo_neutral_movie(suffix,original,neutral),"nonfinal rewrite cannot move media offsets");
 bytes movie=atom("mvhd");append(movie,track(1,false,false));append(movie,track(2,true));const auto empty=atom("moov",movie);empty_audio_placeholder placeholder;need(observe_empty_audio_placeholder(empty,placeholder)&&placeholder.found&&placeholder.track_id==2,"empty audio strict proof");
 auto repaired=empty;std::memcpy(repaired.data()+placeholder.begin+4,"free",4);empty_audio_placeholder after;need(observe_empty_audio_placeholder(repaired,after)&&!after.found,"same-length free no audio role");size_t changed=0;for(size_t i=0;i<empty.size();++i)if(empty[i]!=repaired[i])++changed;need(changed==3&&empty.size()==repaired.size(),"trak-to-free has three differing type bytes");for(size_t i=0;i<empty.size();++i)if(i<placeholder.begin+4||i>=placeholder.begin+8)need(empty[i]==repaired[i],"all non-type bytes retained");
 bytes unknown_movie=atom("mvhd");append(unknown_movie,track(1,false,false,true));append(unknown_movie,track(2,true));need(!observe_empty_audio_placeholder(atom("moov",unknown_movie),after),"unknown track relationships reject repair");
 bytes nonempty_movie=atom("mvhd");append(nonempty_movie,track(2,true,false));need(observe_empty_audio_placeholder(atom("moov",nonempty_movie),after)&&!after.found,"nonempty audio not repaired");
 bytes reference;put(reference,2);bytes first=track(1,false,false);bytes body(first.begin()+8,first.end());append(body,atom("tref",atom("chap",reference)));bytes with_ref=atom("mvhd");append(with_ref,atom("trak",body));append(with_ref,track(2,true));need(!observe_empty_audio_placeholder(atom("moov",with_ref),after),"incoming reference prevents placeholder repair");
 append(body,atom("tref",atom("chap",reference)));bytes duplicate_ref=atom("mvhd");append(duplicate_ref,atom("trak",body));append(duplicate_ref,track(2,true));need(!observe_empty_audio_placeholder(atom("moov",duplicate_ref),after),"duplicate tref cannot look absent");
 auto zero_clock=empty;auto at_clock=std::find_end(zero_clock.begin(),zero_clock.end(),"mdhd","mdhd"+4);need(at_clock!=zero_clock.end(),"fixture mdhd");size_t clock_pos=size_t(at_clock-zero_clock.begin());std::fill_n(zero_clock.begin()+clock_pos+16,4,uint8_t{0});need(!observe_empty_audio_placeholder(zero_clock,after),"invalid surviving clock cannot authorize repair");
 std::map<std::string,std::string> fields;const char marker[]="multi-frame: 1;\nispap:1;\nmodule: photo;";
 need(observe_raw_camera_comment({reinterpret_cast<const uint8_t*>(marker),sizeof(marker)},fields)&&fields.size()==3,"raw marker syntax");
 auto wrong=text(std::string_view(marker,sizeof(marker)));wrong.insert(wrong.begin(),{'A','S','C','I','I',0,0,0});need(!observe_raw_camera_comment(wrong,fields),"standard ASCII prefix is not raw camera comment");
 std::cout<<"P9R7 modern mixed-owner/placeholder/comment structural cases PASS\n";return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
