#include "media/ordinary_metadata_capsule.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace {
using bytes=std::vector<uint8_t>;
using chunks=std::vector<bytes>;
namespace codec=lpb::media::ordinary_metadata;
void require(bool v,const char* message){if(!v)throw std::runtime_error(message);}
bytes load(const std::filesystem::path& path){std::ifstream f(path,std::ios::binary);require(bool(f),"Required cached original missing");return bytes((std::istreambuf_iterator<char>(f)),{});}
std::vector<std::span<const uint8_t>> views(const chunks& values){std::vector<std::span<const uint8_t>> out;for(const auto& part:values)out.emplace_back(part);return out;}
bool decode(const chunks& parts,bytes& raw){return codec::decode_chunks(views(parts),raw);}
void run(const std::vector<std::filesystem::path>& paths){
 chunks reference;bytes reference_raw;
 for(const auto& path:paths){
  const auto original=load(path);lpb::media::opaque_camera_record_inventory inventory;
  require(lpb::media::observe_opaque_camera_records(original,inventory),"Real terminal records required");
  bytes raw(original.begin()+static_cast<ptrdiff_t>(inventory.block_offset),original.end());chunks encoded;bytes decoded;
  require(codec::encode_chunks(raw,encoded)&&!encoded.empty(),"Real metadata envelope encoding");
  require(encoded.size()==(raw.size()+codec::chunk_bytes-1)/codec::chunk_bytes,"Exact canonical chunk count");
  for(size_t i=0;i<encoded.size();++i)require(encoded[i].size()<=65533&&codec::get32(encoded[i],72)==i&&codec::get32(encoded[i],76)==encoded.size(),"JPEG marker payload bound and unique index");
  require(decode(encoded,decoded)&&decoded==raw,"Whole raw records and self-relative directory unchanged");
  auto with_prefix=raw;with_prefix.insert(with_prefix.begin(),0x51);chunks invalid;
  require(!codec::encode_chunks(with_prefix,invalid)&&invalid.empty(),"Unaccounted prefix is not a complete record block");
  raw.push_back(0);require(!codec::encode_chunks(raw,invalid)&&invalid.empty(),"Unaccounted suffix is not authorized");
  if(encoded.size()>1){reference=std::move(encoded);reference_raw=std::move(decoded);}
 }
 require(reference.size()>2,"Multi-chunk real fixture required");
 std::vector<chunks> invalid;
 const auto field=[&](size_t part,size_t at,uint32_t value){auto bad=reference;codec::put32(bad[part],at,value);invalid.push_back(std::move(bad));};
 field(0,16,0);field(0,16,2);field(0,20,0);field(0,20,2);field(0,24,1);field(0,28,1);
 field(0,72,1);field(0,76,0);field(0,76,0xffffffffU);field(1,72,0);field(1,76,1);
 auto bad=reference;codec::put64(bad[0],32,0);invalid.push_back(bad);
 bad=reference;codec::put64(bad[0],32,codec::maximum_raw+1);invalid.push_back(bad);
 bad=reference;codec::put64(bad[1],32,reference_raw.size()-1);invalid.push_back(bad);
 bad=reference;bad[0][40]^=1;invalid.push_back(bad);
 bad=reference;bad[1][40]^=1;invalid.push_back(bad);
 bad=reference;bad[0][0]^=1;invalid.push_back(bad);
 bad=reference;bad[0][codec::header_size]^=1;invalid.push_back(bad);
 bad=reference;bad[0].resize(codec::header_size-1);invalid.push_back(bad);
 bad=reference;bad[0].pop_back();invalid.push_back(bad);
 bad=reference;bad.back().push_back(0);invalid.push_back(bad);
 bad=reference;bad[0].resize(65534);invalid.push_back(bad);
 bad=reference;bad.erase(bad.begin()+1);invalid.push_back(bad);
 bad=reference;bad.insert(bad.begin()+1,bad[0]);invalid.push_back(bad);
 bad=reference;std::swap(bad[0],bad[1]);invalid.push_back(bad);
 invalid.emplace_back();
 for(const auto& value:invalid){bytes result{1,2,3};require(!decode(value,result)&&result.empty(),"Malformed/truncated/duplicate/reordered/unknown envelope clears output and rejects");}
 // A correct outer hash must not turn a malformed record model into evidence.
 bad=reference;bytes malformed=reference_raw;malformed[malformed.size()-9]=1;
 const auto hash=codec::digest(malformed);
 for(size_t i=0;i<bad.size();++i){std::copy(hash.begin(),hash.end(),bad[i].begin()+40);const size_t offset=i*codec::chunk_bytes;std::copy_n(malformed.data()+offset,bad[i].size()-codec::header_size,bad[i].data()+codec::header_size);}
 bytes result;require(!decode(bad,result)&&result.empty(),"Outer hash does not authorize malformed inner records");
 std::cout<<"PASS 3 original opaque envelopes with exact raw records, canonical chunking and "<<invalid.size()+1<<" malformed/duplicate/bounds/hash/model negatives; no target image placement acceptance\n";
}
}
#ifdef _WIN32
int wmain(int argc,wchar_t** argv){try{require(argc==4,"Three original source paths required");run({argv[1],argv[2],argv[3]});return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
#else
int main(int argc,char** argv){try{require(argc==4,"Three original source paths required");run({argv[1],argv[2],argv[3]});return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
#endif
