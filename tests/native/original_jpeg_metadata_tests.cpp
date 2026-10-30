#include "containers/original_jpeg_metadata.h"
#include "media/jpeg_marker.h"
#include "foundation/sha256_core.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace m=lpb::media::original_jpeg_metadata;
using bytes=std::vector<uint8_t>;
void require(bool yes,const char* why){if(!yes)throw std::runtime_error(why);}
void put(bytes& b,size_t at,uint64_t value,size_t width){for(size_t i=0;i<width;++i)b[at+i]=static_cast<uint8_t>(value>>((width-i-1)*8));}
bytes base(){bytes b; m::append(b,24,4);b.insert(b.end(),{'f','t','y','p','h','e','i','c',0,0,0,0,'m','i','f','1','h','e','i','c'});return b;}
void run(const std::filesystem::path& file) {
 std::ifstream input(file,std::ios::binary);require(bool(input),"real ordinary source required");bytes source((std::istreambuf_iterator<char>(input)),{});
 std::vector<m::marker> markers;lpb::memory_random_access memory{source};uint64_t eoi{};
 require(lpb::media::walk_jpeg_markers(memory.view(),eoi,[&](const lpb::media::jpeg_marker_range& r){
  if(r.marker==0xe4||r.marker==0xe6)markers.push_back({r.start,bytes(source.begin()+static_cast<size_t>(r.start),source.begin()+static_cast<size_t>(r.payload+r.length))});return true;
 }),"real complete JPEG marker traversal");require(eoi==source.size(),"real fixture whole JPEG");
 uint8_t hash[32]{};lpb::crypto::sha256_buffer(source.data(),source.size(),hash);bytes record;
 require(m::encode(source.size(),std::span<const uint8_t,32>(hash),markers,record),"actual original marker record encode");
 auto heif=base();require(m::attach(heif,record),"record rootUUID attach");bytes actual;
 require(m::observe(heif,actual)&&actual==record,"actual original marker record exact roundtrip");
 if(markers.empty()){require(record.empty(),"absent metadata stays absent");std::cout<<"real metadata-free absence passed\n";return;}
 require(m::valid(record)&&m::integer(record,24,8)==source.size()&&m::integer(record,64,4)==markers.size(),"actual source length/count context exact");
 size_t p=72;for(const auto& marker:markers){require(m::integer(record,p,8)==marker.offset&&m::integer(record,p+8,4)==marker.bytes.size(),"original offset/length remains original context");require(std::equal(marker.bytes.begin(),marker.bytes.end(),record.begin()+p+16),"every complete raw marker exact");p+=16+marker.bytes.size();}require(p==record.size(),"no unaccounted record bytes");
 std::vector<bytes> invalid;
 for(const auto field:std::array<size_t,5>{16,20,24,64,68}){auto bad=record;put(bad,field,field==16?2:field==24||field==64?0:1,field==24?8:4);invalid.push_back(std::move(bad));}
 {auto bad=record;put(bad,64,513,4);invalid.push_back(std::move(bad));}
 {auto bad=record;put(bad,24,32ULL*1024*1024+1,8);invalid.push_back(std::move(bad));}
 {auto bad=record;put(bad,72,source.size(),8);invalid.push_back(std::move(bad));}
 {auto bad=record;put(bad,72,1,8);invalid.push_back(std::move(bad));}
 {auto bad=record;put(bad,80,0,4);invalid.push_back(std::move(bad));}
 {auto bad=record;put(bad,80,UINT32_MAX,4);invalid.push_back(std::move(bad));}
 {auto bad=record;put(bad,84,1,4);invalid.push_back(std::move(bad));}
 for(size_t at:{88U,89U,90U}){auto bad=record;bad[at]=0;invalid.push_back(std::move(bad));}
 {auto bad=record;bad.pop_back();invalid.push_back(std::move(bad));}
 {auto bad=record;bad.push_back(0);invalid.push_back(std::move(bad));}
 if(markers.size()>1){auto bad=record;put(bad,72+16+markers[0].bytes.size(),markers[0].offset,8);invalid.push_back(std::move(bad));}
 for(const auto& bad:invalid)require(!m::valid(bad),"malformed/version/reserved/bounds/overlap/marker structure rejects");
 auto duplicate=heif;require(m::attach(duplicate,record)&&!m::observe(duplicate,actual)&&actual.empty(),"duplicate namespace owner rejects without partial record");
 auto truncated=heif;truncated.pop_back();require(!m::observe(truncated,actual),"truncated outerbox rejects");
 auto implicit=heif;put(implicit,24,0,4);require(!m::observe(implicit,actual),"implicit-to-EOF UUID cannot establish owned record");
 auto foreign=heif;foreign[32]^=1;require(m::observe(foreign,actual)&&actual.empty(),"foreign UUID is not elevated into original-image evidence");
 auto changed=record;changed[32]^=1;require(m::valid(changed)&&changed!=record,"context digest substitution is not structural verification; consumer must compare actual bound source inventory");
 std::cout<<"real "<<markers.size()<<" original metadata markers exact, "<<invalid.size()<<" malformed records and root owner/absence/context negatives passed\n";
}
#ifdef _WIN32
int wmain(int argc,wchar_t** argv){try{require(argc==4,"three real neutral JPEGs required");for(int i=1;i<argc;++i)run(argv[i]);return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
#else
int main(int argc,char** argv){try{require(argc==4,"three real neutral JPEGs required");for(int i=1;i<argc;++i)run(argv[i]);return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
#endif
