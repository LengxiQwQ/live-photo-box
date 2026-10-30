#include "media/opaque_camera_records.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace {
using bytes=std::vector<uint8_t>;
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
bytes load(const std::filesystem::path& path){std::ifstream f(path,std::ios::binary);require(bool(f),"Required cached original missing");return bytes((std::istreambuf_iterator<char>(f)),{});}
bytes make(std::string text,size_t area=16){bytes b(area,0x57);b.insert(b.end(),text.begin(),text.end());b.push_back(0);b.insert(b.end(),{'j','x','r','s'});const uint32_t n=static_cast<uint32_t>(text.size()+9);for(size_t i=0;i<4;++i)b.push_back(static_cast<uint8_t>(n>>(8*i)));return b;}
void run(const std::filesystem::path& first,const std::filesystem::path& second,const std::filesystem::path& third){
 lpb::media::opaque_camera_record_inventory inventory;
 const auto a=load(first),b=load(second);
 require(lpb::media::observe_opaque_camera_records(a,inventory),"Original six-record terminal directory");
 require(inventory.block_offset==a.size()-846375&&inventory.block_length==846375&&inventory.directory_length==407&&inventory.records.size()==6,"Original complete ordinary trailer partition");
 require(inventory.records[0].name=="private.emptyspace"&&inventory.records[0].length==4,"Raw first record kept, not treated as a removable box header");
 require(lpb::media::observe_opaque_camera_records(b,inventory),"Edited nine-record terminal directory");
 require(inventory.block_offset==b.size()-828956&&inventory.block_length==828956&&inventory.directory_length==618&&inventory.records.size()==9,"Edited directory spans comm and trailing 801 bytes");
 require(inventory.records[1].name=="capture.mode"&&inventory.records[1].offset==4,"comm fourcc is part of an ordinary record, not independent semantic authority");
 const auto c=load(third);require(lpb::media::observe_opaque_camera_records(c,inventory)&&inventory.block_offset==c.size()-402&&inventory.block_length==402&&inventory.records.size()==4,"Original four-record ordinary camera metadata partition");
 uint64_t video_length=0;
 require(lpb::media::bind_opaque_camera_records_source(a,4640886,8275393,inventory,video_length)&&video_length==8275393,"Original metadata starts at the declared video end");
 require(lpb::media::bind_opaque_camera_records_source(b,11355276,8767688,inventory,video_length)&&video_length==7939533,"Edited complete metadata starts at unique final comm; remaining 801 bytes included");
 require(lpb::media::bind_opaque_camera_records_source(c,2847049,3905023,inventory,video_length)&&video_length==3905023,"Third original metadata starts at video end");
 require(!lpb::media::bind_opaque_camera_records_source(b,11355276,8767687,inventory,video_length)&&video_length==0,"Off-by-one source movie boundary rejects");
 auto edited=b;edited[b.size()-828956+4]='x';require(!lpb::media::bind_opaque_camera_records_source(edited,11355276,8767688,inventory,video_length),"Unrecognized source box cannot authorize moving records out of video");
 edited=b;std::fill(edited.begin()+static_cast<ptrdiff_t>(b.size()-828956),edited.begin()+static_cast<ptrdiff_t>(b.size()-828956+4),uint8_t{0});
 require(!lpb::media::bind_opaque_camera_records_source(edited,11355276,8767688,inventory,video_length),"Implicit-to-end comm cannot authorize records movement");
 const std::string valid=R"([{"length":8,"name":"first","offset":16,"version":1},{"length":8,"name":"second","offset":8,"version":1}])";
 auto source=make(valid);require(lpb::media::observe_opaque_camera_records(source,inventory),"Synthetic exact two-record partition");
 auto prefixed=source;prefixed.insert(prefixed.begin(),13,0x42);require(lpb::media::observe_opaque_camera_records(prefixed,inventory)&&inventory.block_offset==13,"Only EOF-owned directory selects block origin");
 const auto change=[&](const std::string& from,const std::string& to){auto s=valid;const auto at=s.find(from);require(at!=std::string::npos,"Negative fixture exact field");s.replace(at,from.size(),to);return make(s);};
 std::vector<bytes> bad{
  change("\"version\":1","\"version\":2"),change("\"version\":1","\"version\":1,\"version\":1"),
  change("\"version\":1","\"version\":1,\"address\":0"),change(",\"version\":1",""),
  change("\"length\":8","\"length\":0"),change("\"length\":8","\"length\":-1"),change("\"length\":8","\"length\":08"),
  change("\"length\":8","\"length\":1.0"),change("\"length\":8","\"length\":18446744073709551616"),
  change("\"offset\":16","\"offset\":17"),change("\"offset\":16","\"offset\":15"),change("\"offset\":8","\"offset\":9"),
  change("\"length\":8","\"length\":7"),change("\"name\":\"second\"","\"name\":\"first\""),
  change("\"name\":\"first\"","\"name\":\"\""),change("\"name\":\"first\"","\"name\":\"fi\\u0072st\""),
  change("\"name\":\"first\"","\"name\":\"../file\""),change("\"name\":\"first\"","\"name\":{}"),
  make("[]"),make(valid+"extra"),make(valid.substr(0,valid.size()-1)+",]"),make(std::string(65536,' ')+valid)
 };
 auto corrupt=source;corrupt.back()=255;bad.push_back(corrupt);
 corrupt=source;corrupt[corrupt.size()-9]=1;bad.push_back(corrupt);
 corrupt=source;corrupt[corrupt.size()-8]='x';bad.push_back(corrupt);
 corrupt=source;corrupt.push_back(0);bad.push_back(corrupt);
 corrupt=source;corrupt.resize(10);bad.push_back(corrupt);
 for(const auto& value:bad){inventory.block_length=123;require(!lpb::media::observe_opaque_camera_records(value,inventory)&&inventory.block_length==0&&inventory.records.empty(),"Malformed terminal metadata clears evidence and fails closed");}
 std::string rows="[";for(size_t i=0;i<65;++i){if(i)rows+=',';rows+="{\"length\":1,\"name\":\"r"+std::to_string(i)+"\",\"offset\":"+std::to_string(65-i)+",\"version\":1}";}rows+=']';
 require(!lpb::media::observe_opaque_camera_records(make(rows,65),inventory),"Directory resource count bound");
 std::cout<<"PASS 3 original source complete record partitions, 2 synthetic partitions, "<<bad.size()+1<<" malformed/duplicate/bounds negatives; no payload paths or binding authority interpreted\n";
}
}
#ifdef _WIN32
int wmain(int argc,wchar_t** argv){try{require(argc==4,"Three required cached originals");run(argv[1],argv[2],argv[3]);return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
#else
int main(int argc,char** argv){try{require(argc==4,"Three required cached originals");run(argv[1],argv[2],argv[3]);return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
#endif
