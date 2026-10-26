#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include "livephotobox_native.h"
#include "binary/portable_io.h"
#include "containers/isobmff.h"
#include <array>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <vector>
#include <cstring>
#include <algorithm>
#include <atomic>
#include <thread>
#include <utility>
namespace fs=std::filesystem;
#define REQUIRE(x) do{if(!(x))throw std::runtime_error(std::string("line ")+std::to_string(__LINE__)+": "+#x);}while(false)
std::vector<uint8_t> load(const fs::path& p){std::ifstream f(p,std::ios::binary);REQUIRE(f.good());return {std::istreambuf_iterator<char>(f),{}};}
lpb_context* make_context(){lpb_context_options o{};o.struct_size=sizeof(o);o.abi_version=15;lpb_context* c{};REQUIRE(lpb_create_context(&o,&c)==LPB_RESULT_OK);return c;}
lpb_target_validation_request_v1 request(){return {sizeof(lpb_target_validation_request_v1),1,2,1,0,1,0,0,0,0,2,0,0,0};}
struct memory {
 std::vector<uint8_t> bytes;lpb_context* context{};int mode{};bool tested{};
 static lpb_result LPB_CALL read(void* user,uint64_t at,uint8_t* out,uint32_t n,uint32_t* got){
  auto& m=*static_cast<memory*>(user);*got=0;
  if(m.mode==1)return LPB_RESULT_CANCELLED;
  if(m.mode==2)return LPB_RESULT_OK;
  if(m.mode==3){*got=n+1;return LPB_RESULT_OK;}
  if(m.mode==4&&!m.tested){m.tested=true;auto r=request();auto reader=m.reader();lpb_target_validation_result_v1 result{};result.struct_size=sizeof(result);result.version=1;
   REQUIRE(lpb_target_validate_v1(m.context,&r,&reader,&result)==LPB_RESULT_AUTHORITY_VIOLATION);
   REQUIRE(lpb_target_writer_release_v1(m.context,nullptr)==LPB_RESULT_AUTHORITY_VIOLATION);
   size_t written=0;REQUIRE(lpb_jpeg_inject_xmp(m.context,m.bytes.data(),m.bytes.size(),nullptr,0,nullptr,0,&written)==LPB_RESULT_AUTHORITY_VIOLATION);
  }
  if(m.mode==5&&!m.tested){m.tested=true;lpb_destroy_context(m.context);}
  if(at>m.bytes.size()||n>m.bytes.size()-at)return LPB_RESULT_INTERNAL_ERROR;
  if(n)std::memcpy(out,m.bytes.data()+static_cast<size_t>(at),n);*got=n;return LPB_RESULT_OK;
 }
 lpb_target_reader_v1 reader(){return {sizeof(lpb_target_reader_v1),1,bytes.size(),this,read,0,0};}
};
lpb_target_validation_result_v1 validate(memory& m,lpb_target_validation_request_v1 r,lpb_result expected=LPB_RESULT_OK){
 auto input=m.reader();lpb_target_validation_result_v1 out{};out.struct_size=sizeof(out);out.version=1;
 const auto actual=lpb_target_validate_v1(m.context,&r,&input,&out);
 if(actual!=expected){std::array<char,4096> error{};size_t needed=0;
  if(m.mode!=5)static_cast<void>(lpb_get_last_error(m.context,error.data(),error.size(),&needed));
  std::cerr<<"validate invocation actual="<<actual<<" expected="<<expected<<" mode="<<m.mode<<" status="<<out.structural_status
   <<" category="<<out.failure_category<<" stage="<<out.failure_stage<<" rule="<<out.rule_id<<" error="<<error.data()<<'\n';}
 REQUIRE(actual==expected);return out;
}
std::vector<uint8_t> packet(const std::vector<uint8_t>& jpeg,const std::string& xml){
 constexpr char h[]="http://ns.adobe.com/xap/1.0/";const size_t length=2+sizeof(h)+xml.size();REQUIRE(length<=65535);
 std::vector<uint8_t> b{0xff,0xd8,0xff,0xe1,static_cast<uint8_t>(length>>8),static_cast<uint8_t>(length)};
 b.insert(b.end(),h,h+sizeof(h));b.insert(b.end(),xml.begin(),xml.end());b.insert(b.end(),jpeg.begin()+2,jpeg.end());return b;
}
std::string independent_xml(uint64_t length){
 return "<m:xmpmeta xmlns:m=\"adobe:ns:meta/\"><r:RDF xmlns:r=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><r:Description xmlns:C=\"http://ns.google.com/photos/1.0/camera/\" xmlns:D=\"http://ns.google.com/photos/1.0/container/\" xmlns:I=\"http://ns.google.com/photos/1.0/container/item/\" C:MotionPhoto=\"1\" C:MotionPhotoVersion=\"1\" C:MotionPhotoPresentationTimestampUs=\"-1\"><D:Directory><r:Seq><r:li r:parseType=\"Resource\"><D:Item I:Semantic=\"Primary\" I:Mime=\"image/jpeg\"/></r:li><r:li r:parseType=\"Resource\"><D:Item I:Length=\""+std::to_string(length)+"\" I:Semantic=\"MotionPhoto\" I:Mime=\"video/mp4\"/></r:li></r:Seq></D:Directory></r:Description></r:RDF></m:xmpmeta>";
}
void replace(std::string& xml,const std::string& old,const std::string& next){const auto at=xml.find(old);REQUIRE(at!=std::string::npos);xml.replace(at,old.size(),next);}
std::array<uint8_t,32> hash(const fs::path& path){
 BCRYPT_ALG_HANDLE alg{};REQUIRE(BCryptOpenAlgorithmProvider(&alg,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0);DWORD n=0,w=0;
 REQUIRE(BCryptGetProperty(alg,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&n),sizeof(n),&w,0)>=0);std::vector<uint8_t> obj(n);BCRYPT_HASH_HANDLE h{};
 REQUIRE(BCryptCreateHash(alg,&h,obj.data(),n,nullptr,0,0)>=0);const auto bytes=load(path);
 REQUIRE(bytes.size()<=ULONG_MAX);REQUIRE(BCryptHashData(h,const_cast<PUCHAR>(bytes.data()),static_cast<ULONG>(bytes.size()),0)>=0);
 std::array<uint8_t,32> result{};REQUIRE(BCryptFinishHash(h,result.data(),32,0)>=0);BCryptDestroyHash(h);BCryptCloseAlgorithmProvider(alg,0);return result;
}
lpb_file_identity identity(const fs::path& path){
 HANDLE h=CreateFileW(path.c_str(),GENERIC_READ,7,nullptr,OPEN_EXISTING,0,nullptr);REQUIRE(h!=INVALID_HANDLE_VALUE);BY_HANDLE_FILE_INFORMATION info{};
 REQUIRE(GetFileInformationByHandle(h,&info));CloseHandle(h);lpb_file_identity out{};out.volume_serial=info.dwVolumeSerialNumber;
 out.file_index=(static_cast<uint64_t>(info.nFileIndexHigh)<<32)|info.nFileIndexLow;out.file_size=(static_cast<uint64_t>(info.nFileSizeHigh)<<32)|info.nFileSizeLow;out.link_count=info.nNumberOfLinks;return out;
}
void path_to(char* output,size_t capacity,const fs::path& path){const auto u=fs::absolute(path).u8string();REQUIRE(u.size()<capacity);std::memcpy(output,u.data(),u.size());}
uint32_t be32(const std::vector<uint8_t>& b,size_t p){REQUIRE(p<=b.size()&&b.size()-p>=4);return (static_cast<uint32_t>(b[p])<<24)|(b[p+1]<<16)|(b[p+2]<<8)|b[p+3];}
void put32(std::vector<uint8_t>& b,size_t p,uint32_t n){REQUIRE(p<=b.size()&&b.size()-p>=4);for(unsigned i=0;i<4;++i)b[p+i]=static_cast<uint8_t>(n>>(24-i*8));}
void append64(std::vector<uint8_t>& b,uint64_t n){for(unsigned i=0;i<8;++i)b.push_back(static_cast<uint8_t>(n>>(56-i*8)));}
bool box_type(const std::vector<uint8_t>& b,size_t p,const char* name){return p<=b.size()&&b.size()-p>=8&&!std::memcmp(b.data()+p+4,name,4);}
bool container(const std::vector<uint8_t>& b,size_t p){return box_type(b,p,"moov")||box_type(b,p,"trak")||box_type(b,p,"mdia")||box_type(b,p,"minf")||box_type(b,p,"stbl");}
size_t audio_entry(const std::vector<uint8_t>& b,size_t first,size_t last){
 for(size_t p=first;p<last;){const uint32_t n=be32(b,p);REQUIRE(n>=8&&n<=last-p);
  if(box_type(b,p,"mp4a"))return p;
  if(container(b,p)||box_type(b,p,"stsd")){const size_t found=audio_entry(b,p+(box_type(b,p,"stsd")?16:8),p+n);if(found!=SIZE_MAX)return found;}
  p+=n;
 }return SIZE_MAX;
}
size_t co64_growth(const std::vector<uint8_t>& b,size_t first,size_t last){
 size_t growth=0;
 for(size_t p=first;p<last;){const uint32_t n=be32(b,p);REQUIRE(n>=8&&n<=last-p);
  if(box_type(b,p,"stco")){REQUIRE(n>=16);const uint32_t count=be32(b,p+12);REQUIRE(count<=(n-16)/4);growth+=static_cast<size_t>(count)*4;}
  else if(container(b,p))growth+=co64_growth(b,p+8,p+n);
  p+=n;
 }return growth;
}
std::vector<uint8_t> widen_boxes(const std::vector<uint8_t>& b,size_t first,size_t last,uint64_t shift,size_t old_moov_end,size_t growth){
 std::vector<uint8_t> out;
 for(size_t p=first;p<last;){const uint32_t n=be32(b,p);REQUIRE(n>=8&&n<=last-p);const size_t at=out.size();
  if(box_type(b,p,"stco")){
   const uint32_t count=be32(b,p+12);out.insert(out.end(),b.begin()+static_cast<ptrdiff_t>(p),b.begin()+static_cast<ptrdiff_t>(p+16));
   std::memcpy(out.data()+at+4,"co64",4);
   for(uint32_t i=0;i<count;++i){const uint64_t raw=be32(b,p+16+static_cast<size_t>(i)*4);append64(out,raw+shift+(raw>=old_moov_end?growth:0));}
  }else if(container(b,p)){
   out.insert(out.end(),b.begin()+static_cast<ptrdiff_t>(p),b.begin()+static_cast<ptrdiff_t>(p+8));
   const auto children=widen_boxes(b,p+8,p+n,shift,old_moov_end,growth);out.insert(out.end(),children.begin(),children.end());
  }else out.insert(out.end(),b.begin()+static_cast<ptrdiff_t>(p),b.begin()+static_cast<ptrdiff_t>(p+n));
  REQUIRE(out.size()-at<=UINT32_MAX);put32(out,at,static_cast<uint32_t>(out.size()-at));p+=n;
 }return out;
}
struct sparse_video {
 std::vector<uint8_t> bytes;uint64_t split{},gap{1ULL<<32};unsigned reads{};
 static bool read(void* user,uint64_t at,std::span<uint8_t> out) noexcept {
  auto& s=*static_cast<sparse_video*>(user);++s.reads;
  if(at>s.bytes.size()+s.gap||out.size()>s.bytes.size()+s.gap-at)return false;
  const std::array<uint8_t,16> free_header{0,0,0,1,'f','r','e','e',0,0,0,1,0,0,0,0};
  for(auto& value:out){
   if(at<s.split)value=s.bytes[static_cast<size_t>(at)];
   else if(at-s.split<s.gap)value=at-s.split<free_header.size()?free_header[static_cast<size_t>(at-s.split)]:0;
   else value=s.bytes[static_cast<size_t>(at-s.gap)];
   ++at;
  }return true;
 }
 lpb::random_access_reader view(){return {bytes.size()+gap,this,read};}
 static lpb_result LPB_CALL cancellable(void* user,uint64_t at,uint8_t* out,uint32_t n,uint32_t* got){
  auto& s=*static_cast<sparse_video*>(user);*got=0;if(s.reads>=3)return LPB_RESULT_CANCELLED;
  if(!read(user,at,std::span<uint8_t>(out,n)))return LPB_RESULT_INTERNAL_ERROR;*got=n;return LPB_RESULT_OK;
 }
};
size_t owned_child(const std::vector<uint8_t>& bytes,size_t first,size_t last,const char* type){
 size_t found=SIZE_MAX;
 for(size_t at=first;at<last;){const uint32_t size=be32(bytes,at);REQUIRE(size>=8&&size<=last-at);
  if(box_type(bytes,at,type)){REQUIRE(found==SIZE_MAX);found=at;}at+=size;
 }return found;
}
void reader_sync_sample_tests(const std::vector<uint8_t>& original){
 isobmff_reader_observation observed;isobmff_reader_diagnostic failure;lpb::memory_random_access input{original};
 REQUIRE(inspect_isobmff_reader(input.view(),true,observed,failure));REQUIRE(observed.sample_tables.size()==1);
 auto opaque=original;opaque.insert(opaque.end(),{0,0,0,12,'c','o','m','m',1,2,3,4});
 lpb::memory_random_access opaque_reader{opaque};
 REQUIRE(inspect_isobmff_reader(opaque_reader.view(),true,observed,failure)&&observed.opaque_metadata);
 opaque.pop_back();lpb::memory_random_access truncated_opaque{opaque};
 REQUIRE(!inspect_isobmff_reader(truncated_opaque.view(),true,observed,failure)&&failure.failure==isobmff_reader_failure::malformed);
 REQUIRE(inspect_isobmff_reader(input.view(),true,observed,failure));
 const uint32_t samples=observed.sample_tables.front().sample_count;REQUIRE(samples>=2&&samples<UINT32_MAX);
 // Resolve only legal direct children through the exact single-track ownership chain.
 std::array<size_t,5> ancestors{};size_t first=0,last=original.size();
 const std::array<const char*,5> hierarchy{"moov","trak","mdia","minf","stbl"};
 for(size_t i=0;i<hierarchy.size();++i){const size_t at=owned_child(original,first,last,hierarchy[i]);REQUIRE(at!=SIZE_MAX);
  ancestors[i]=at;first=at+8;last=at+be32(original,at);}
 auto good=original;size_t sync=owned_child(original,first,last,"stss");
 if(sync==SIZE_MAX){
  // This fixture omits stss. Insert a valid [1,2] table, preserving all bytes and
  // relocating the single-track chunk offsets when the earlier moov grows.
  constexpr uint32_t growth=24;std::vector<uint8_t> table(growth);
  put32(table,0,growth);std::memcpy(table.data()+4,"stss",4);put32(table,12,2);put32(table,16,1);put32(table,20,2);
  const size_t offsets=owned_child(original,first,last,"stco");REQUIRE(offsets!=SIZE_MAX);
  const uint32_t chunks=be32(original,offsets+12);REQUIRE(be32(original,offsets)==16ULL+4ULL*chunks);
  for(uint32_t i=0;i<chunks;++i){const size_t field=offsets+16+static_cast<size_t>(i)*4;const uint32_t value=be32(original,field);
   if(value>=last){REQUIRE(value<=UINT32_MAX-growth);put32(good,field,value+growth);}}
  for(const size_t at:ancestors){const uint32_t size=be32(original,at);REQUIRE(size<=UINT32_MAX-growth);put32(good,at,size+growth);}
  sync=last;good.insert(good.begin()+static_cast<ptrdiff_t>(last),table.begin(),table.end());
 }
 lpb::memory_random_access valid{good};REQUIRE(inspect_isobmff_reader(valid.view(),true,observed,failure));
 const uint32_t entries=be32(good,sync+12);REQUIRE(entries>=1&&be32(good,sync)==16ULL+4ULL*entries);
 const auto malformed=[&](const std::vector<uint8_t>& bytes){lpb::memory_random_access bad{bytes};
  REQUIRE(!inspect_isobmff_reader(bad.view(),true,observed,failure)&&failure.failure==isobmff_reader_failure::malformed);};
 auto bad=good;put32(bad,sync+16,0);malformed(bad);
 bad=good;put32(bad,sync+16,samples+1);malformed(bad);
 bad=good;put32(bad,sync+12,entries+1);malformed(bad);
 bad=good;put32(bad,sync+8,1);malformed(bad); // Nonzero FullBox flags.
 bad=good;put32(bad,sync+8,0x01000000);malformed(bad); // Unsupported version.
 REQUIRE(entries>=2);bad=good;put32(bad,sync+20,be32(good,sync+16));malformed(bad);
 bad=good;put32(bad,sync+16,2);put32(bad,sync+20,1);malformed(bad);
 // The positive remains valid after every independent mutation copy.
 REQUIRE(inspect_isobmff_reader(valid.view(),true,observed,failure));
 std::cout<<"reader_owned_stss_valid_and_malformed_references PASS\n";
}
void reader_structure_tests(const fs::path& directory,const std::vector<uint8_t>& original,lpb_context* context){
 // Independent video reader checks are separate from target XMP/Writer rules.
 auto sound=load(directory/L"neutral-audio.mp4");const auto original_sound=sound;lpb::memory_random_access sound_memory{sound};
 isobmff_reader_observation observed;isobmff_reader_diagnostic failure;
 REQUIRE(inspect_isobmff_reader(sound_memory.view(),true,observed,failure));REQUIRE(observed.sample_tables.size()>=2);
 const size_t audio=audio_entry(sound,0,sound.size());REQUIRE(audio!=SIZE_MAX);
 auto wrong_audio=original_sound;wrong_audio[audio+25]=wrong_audio[audio+25]==1?2:1;lpb::memory_random_access wrong_memory{wrong_audio};
 REQUIRE(!inspect_isobmff_reader(wrong_memory.view(),true,observed,failure)&&failure.failure==isobmff_reader_failure::malformed);
 wrong_audio=original_sound;wrong_memory.bytes=wrong_audio;put32(wrong_audio,audio+32,32000U<<16);
 REQUIRE(!inspect_isobmff_reader(wrong_memory.view(),true,observed,failure)&&failure.failure==isobmff_reader_failure::malformed);
 REQUIRE(inspect_isobmff_reader(sound_memory.view(),true,observed,failure));
 const auto boxes=scan_top_level_boxes(sound_memory.view());uint64_t moov=UINT64_MAX;
 for(const auto& b:boxes)if(!std::memcmp(b.type,"moov",4))moov=b.offset;REQUIRE(moov!=UINT64_MAX);
 const auto& first=observed.sample_tables[0];const auto& second=observed.sample_tables[1];REQUIRE(!first.chunks.empty()&&!second.chunks.empty());
 const uint64_t wrong=first.chunks[0].offset;const size_t field=static_cast<size_t>(moov)+second.chunk_offsets_entries_start;
 REQUIRE(second.chunk_offset_width==4&&wrong<=UINT32_MAX);put32(sound,field,static_cast<uint32_t>(wrong));
 REQUIRE(!inspect_isobmff_reader(sound_memory.view(),true,observed,failure)&&failure.failure==isobmff_reader_failure::malformed);
 lpb::memory_random_access input{original};const auto top=scan_top_level_boxes(input.view());size_t old_moov_end=0,split=0;
 for(const auto& box:top){if(!std::memcmp(box.type,"moov",4))old_moov_end=static_cast<size_t>(box.offset+box.size);if(!std::memcmp(box.type,"ftyp",4))split=static_cast<size_t>(box.offset+box.size);}
 REQUIRE(old_moov_end&&split);const size_t growth=co64_growth(original,0,original.size());REQUIRE(growth>0);
 sparse_video sparse{widen_boxes(original,0,original.size(),1ULL<<32,old_moov_end,growth),split};
 REQUIRE(inspect_isobmff_reader(sparse.view(),true,observed,failure));REQUIRE(observed.sample_tables.size()>0&&observed.sample_tables[0].chunk_offset_width==8);
 REQUIRE(observed.sample_tables[0].chunks[0].offset>(1ULL<<32));
 // New public Validator starts hashing actual uint64 bytes and honours midstream cancellation.
 sparse.reads=0;lpb_target_reader_v1 reader{sizeof(reader),1,sparse.view().length,&sparse,sparse_video::cancellable,0,0};
 auto r=request();lpb_target_validation_result_v1 result{};result.struct_size=sizeof(result);result.version=1;
 REQUIRE(lpb_target_validate_v1(context,&r,&reader,&result)==LPB_RESULT_CANCELLED&&result.failure_category==6&&result.structural_status!=1&&sparse.reads==3);
 reader_sync_sample_tests(original);
}
struct event_handle {
 HANDLE value{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
 event_handle(){REQUIRE(value!=nullptr);}
 ~event_handle(){CloseHandle(value);}
 event_handle(const event_handle&)=delete;event_handle& operator=(const event_handle&)=delete;
};
struct context_owner {
 lpb_context* value{};
 ~context_owner(){if(value)lpb_destroy_context(value);}
};
// Always unblock callbacks before joining, including assertion/exception unwinds.
struct callback_worker {
 HANDLE resume;std::thread worker;
 template<class F> callback_worker(HANDLE event,F&& f):resume(event),worker(std::forward<F>(f)){}
 ~callback_worker(){SetEvent(resume);if(worker.joinable())worker.join();}
 void join(){SetEvent(resume);worker.join();}
};
std::vector<fs::path> native_stages(const fs::path& directory){
 std::vector<fs::path> result;
 for(const auto& entry:fs::directory_iterator(directory))
  if(entry.path().extension()==L".stage")result.push_back(entry.path());
 return result;
}
struct lifecycle_callback {
 lpb_context* context{};lpb_target_writer_plan* plan{};fs::path directory;
 event_handle entered,resume;std::atomic<int> mode{},calls{},late_calls{};
 std::atomic<bool> returned{},held{},callback_failed{};
 std::atomic<int> nested_stage{-1},nested_release{-1},nested_validate{-1};
 bool hold() noexcept {
  if(held.exchange(true))return true;
  nested_stage=lpb_target_writer_stage_v1(context,plan);
  nested_release=lpb_target_writer_release_v1(context,plan);
  auto r=request();lpb_target_reader_v1 reader{sizeof(reader),1,0,nullptr,nullptr,0,0};
  lpb_target_validation_result_v1 result{sizeof(result),1};
  nested_validate=lpb_target_validate_v1(context,&r,&reader,&result);
  SetEvent(entered.value);
  if(WaitForSingleObject(resume.value,10000)!=WAIT_OBJECT_0){callback_failed=true;return false;}
  return true;
 }
 static int32_t LPB_CALL cancel(void* user) noexcept {
  auto& c=*static_cast<lifecycle_callback*>(user);++c.calls;if(c.returned)++c.late_calls;
  try {
   const int mode=c.mode.load();
   // The stage row blocks only after the real serializer created its owned file.
   if(mode==2||(mode==1&&!native_stages(c.directory).empty()))return c.hold()?0:1;
   return 0;
  }catch(...){c.callback_failed=true;return 1;}
 }
 void require_reentry_rejected(){
  REQUIRE(!callback_failed&&nested_stage==LPB_RESULT_AUTHORITY_VIOLATION&&
   nested_release==LPB_RESULT_AUTHORITY_VIOLATION&&nested_validate==LPB_RESULT_AUTHORITY_VIOLATION);
 }
};
struct writer_fixture {
 std::array<lpb_target_writer_artifact_v1,2> artifacts{};lpb_target_writer_request_v1 writer{};
 explicit writer_fixture(const fs::path& inputs,const fs::path& staging){
  fs::create_directories(staging);REQUIRE(fs::is_empty(staging));
  for(uint32_t i=0;i<2;++i){const auto path=inputs/(i?L"neutral.mp4":L"neutral.jpg");auto& a=artifacts[i];
   a.struct_size=sizeof(a);a.version=1;a.role=i;a.container=1;a.video_codec=i?2U:0U;
   a.expected_identity=identity(path);const auto digest=hash(path);std::memcpy(a.sha256,digest.data(),32);path_to(a.path_utf8,sizeof(a.path_utf8),path);}
  writer.struct_size=sizeof(writer);writer.version=1;writer.target_profile=2;writer.target_version=1;
  writer.artifact_count=2;writer.artifacts=artifacts.data();path_to(writer.staging_directory_utf8,sizeof(writer.staging_directory_utf8),staging);
 }
};
lpb_context* callback_context(lifecycle_callback& callback){
 lpb_context_options options{};options.struct_size=sizeof(options);options.abi_version=15;
 options.cancel_callback=lifecycle_callback::cancel;options.user_data=&callback;
 lpb_context* result{};REQUIRE(lpb_create_context(&options,&result)==LPB_RESULT_OK);callback.context=result;return result;
}
lpb_target_writer_output_v1 sealed_output(lpb_context* context,lpb_target_writer_plan* plan){
 lpb_target_writer_output_v1 out{sizeof(out),1};lpb_target_writer_status_v1 status{sizeof(status),1};uint32_t count=0;
 REQUIRE(lpb_target_writer_get_outputs_v1(context,plan,&out,1,&count,&status)==LPB_RESULT_OK&&count==1&&status.state==2);
 REQUIRE(out.byte_length>0&&out.stage_id!=0);return out;
}
void validator_external_destroy_waits_for_host_read(const std::vector<uint8_t>& bytes){
 struct host_read {
  const std::vector<uint8_t>& bytes;event_handle entered,resume;std::atomic<int> calls{},late{};
  std::atomic<bool> returned{},timed_out{};
  static lpb_result LPB_CALL read(void* user,uint64_t at,uint8_t* out,uint32_t n,uint32_t* got) noexcept {
   auto& h=*static_cast<host_read*>(user);++h.calls;if(h.returned)++h.late;*got=0;
   SetEvent(h.entered.value);
   if(WaitForSingleObject(h.resume.value,10000)!=WAIT_OBJECT_0){h.timed_out=true;return LPB_RESULT_CANCELLED;}
   if(at>h.bytes.size()||n>h.bytes.size()-at)return LPB_RESULT_INTERNAL_ERROR;
   if(n)std::memcpy(out,h.bytes.data()+static_cast<size_t>(at),n);*got=n;return LPB_RESULT_OK;
  }
 } host{bytes};
 context_owner owner{make_context()};auto* context=owner.value;auto r=request();
 lpb_target_reader_v1 reader{sizeof(reader),1,bytes.size(),&host,host_read::read,0,0};
 lpb_target_validation_result_v1 result{sizeof(result),1};std::atomic<int> validation{-1};
 callback_worker active(host.resume.value,[&]{validation=lpb_target_validate_v1(context,&r,&reader,&result);host.returned=true;});
 REQUIRE(WaitForSingleObject(host.entered.value,5000)==WAIT_OBJECT_0);
 event_handle destroyed;owner.value=nullptr;
 callback_worker destroyer(host.resume.value,[&]{lpb_destroy_context(context);SetEvent(destroyed.value);});
 // While the read holds its lease, context remains valid for this rejection probe.
 const ULONGLONG deadline=GetTickCount64()+5000;lpb_result gate=LPB_RESULT_OK;
 // Null request ordinarily returns InvalidArgument after acquiring an operation.
 // AuthorityViolation here establishes that synchronous destroy closed the lease gate.
 while(GetTickCount64()<deadline){lpb_target_writer_plan* unused{};
  gate=lpb_target_writer_prepare_v1(context,nullptr,&unused);
  if(gate==LPB_RESULT_AUTHORITY_VIOLATION)break;std::this_thread::yield();}
 const DWORD still_waiting=WaitForSingleObject(destroyed.value,0);
 active.join();destroyer.join();
 REQUIRE(gate==LPB_RESULT_AUTHORITY_VIOLATION&&still_waiting==WAIT_TIMEOUT);
 REQUIRE(validation==LPB_RESULT_CANCELLED&&result.structural_status!=1&&result.failure_category==6);
 REQUIRE(!host.timed_out&&host.calls==1&&host.late==0);
 std::cout<<"validator_external_destroy_waits_for_host_read PASS\n";
}
void writer_release_during_real_stage_and_read(const fs::path& inputs){
 for(const bool reading:{false,true}){
  const auto staging=inputs/L"native-lifecycle"/(reading?L"release-read":L"release-stage");writer_fixture fixture(inputs,staging);
  lifecycle_callback callback;callback.directory=staging;context_owner owner{callback_context(callback)};
  REQUIRE(lpb_target_writer_prepare_v1(owner.value,&fixture.writer,&callback.plan)==LPB_RESULT_OK);
  lpb_target_writer_output_v1 output{};
  if(reading){REQUIRE(lpb_target_writer_stage_v1(owner.value,callback.plan)==LPB_RESULT_OK);output=sealed_output(owner.value,callback.plan);}
  callback.mode=reading?2:1;std::atomic<int> operation{-1};uint32_t got=99;std::array<uint8_t,16> bytes{};
  callback_worker active(callback.resume.value,[&]{
   operation=reading?lpb_target_writer_read_stage_v1(owner.value,callback.plan,output.stage_id,0,bytes.data(),static_cast<uint32_t>(bytes.size()),&got):
    lpb_target_writer_stage_v1(owner.value,callback.plan);callback.returned=true;
  });
  REQUIRE(WaitForSingleObject(callback.entered.value,5000)==WAIT_OBJECT_0);
  const auto stages=native_stages(staging);REQUIRE(stages.size()==1);const auto owned=stages.front();
  const auto released=lpb_target_writer_release_v1(owner.value,callback.plan);
  const bool retained=fs::exists(owned);uint32_t count=99;lpb_target_writer_status_v1 status{sizeof(status),1};
  const auto query=lpb_target_writer_get_outputs_v1(owner.value,callback.plan,nullptr,0,&count,&status);
  active.join();callback.mode=0;callback.require_reentry_rejected();
  REQUIRE(released==LPB_RESULT_OK&&retained&&query==LPB_RESULT_OK&&count==0&&status.state==5);
  REQUIRE(operation==(reading?LPB_RESULT_OK:LPB_RESULT_CANCELLED));
  if(reading)REQUIRE(got==bytes.size()&&bytes[0]==0xff&&bytes[1]==0xd8);
  REQUIRE(native_stages(staging).empty()&&!fs::exists(owned));
  count=99;REQUIRE(lpb_target_writer_read_stage_v1(owner.value,callback.plan,output.stage_id,0,nullptr,0,&count)==LPB_RESULT_AUTHORITY_VIOLATION&&count==0);
  REQUIRE(callback.late_calls==0);
  // Destroy remains callback-free because no log callback is installed.
  lpb_destroy_context(owner.value);owner.value=nullptr;REQUIRE(callback.late_calls==0);
  std::cout<<(reading?"writer_release_during_real_read":"writer_release_after_real_stage_created")<<" PASS\n";
 }
}
void native_stage_rename_substitution_preserves_foreign(const fs::path& inputs){
 const auto staging=inputs/L"native-lifecycle"/L"rename-substitution";writer_fixture fixture(inputs,staging);context_owner owner{make_context()};
 lpb_target_writer_plan* plan{};REQUIRE(lpb_target_writer_prepare_v1(owner.value,&fixture.writer,&plan)==LPB_RESULT_OK);
 REQUIRE(lpb_target_writer_stage_v1(owner.value,plan)==LPB_RESULT_OK);const auto output=sealed_output(owner.value,plan);
 const auto stages=native_stages(staging);REQUIRE(stages.size()==1);const auto original=stages.front(),renamed=staging/L"owned-renamed.bin";
 REQUIRE(!fs::exists(renamed));
 // Real OS calls against the production guard: write is denied, shareDELETE permits rename.
 HANDLE writer=CreateFileW(original.c_str(),GENERIC_WRITE,7,nullptr,OPEN_EXISTING,0,nullptr);const DWORD write_error=GetLastError();
 if(writer!=INVALID_HANDLE_VALUE)CloseHandle(writer);
 REQUIRE(writer==INVALID_HANDLE_VALUE&&write_error==ERROR_SHARING_VIOLATION);
 REQUIRE(MoveFileExW(original.c_str(),renamed.c_str(),0)!=FALSE);
 const std::array<uint8_t,8> sentinel{'F','O','R','E','I','G','N','!'};
 HANDLE foreign=CreateFileW(original.c_str(),GENERIC_WRITE,7,nullptr,CREATE_NEW,0,nullptr);REQUIRE(foreign!=INVALID_HANDLE_VALUE);
 DWORD written=0;const BOOL stored=WriteFile(foreign,sentinel.data(),static_cast<DWORD>(sentinel.size()),&written,nullptr);CloseHandle(foreign);
 REQUIRE(stored&&written==sentinel.size());const auto foreign_identity=identity(original);const auto foreign_hash=hash(original);
 std::array<uint8_t,16> bytes{};uint32_t got=99;
 REQUIRE(lpb_target_writer_read_stage_v1(owner.value,plan,output.stage_id,0,bytes.data(),static_cast<uint32_t>(bytes.size()),&got)==LPB_RESULT_SOURCE_CHANGED&&got==0);
 REQUIRE(lpb_target_writer_release_v1(owner.value,plan)==LPB_RESULT_OK);
 REQUIRE(!fs::exists(renamed)&&identity(original).file_index==foreign_identity.file_index&&hash(original)==foreign_hash);
 REQUIRE(load(original)==std::vector<uint8_t>(sentinel.begin(),sentinel.end()));
 lpb_destroy_context(owner.value);owner.value=nullptr;REQUIRE(hash(original)==foreign_hash);
 // Remove only the sentinel this test just created after verifying its exact identity and bytes.
 REQUIRE(DeleteFileW(original.c_str())!=FALSE);REQUIRE(fs::is_empty(staging));
 std::cout<<"native_stage_rename_substitution_preserves_foreign PASS\n";
}
void raw_destroy_cleans_successful_native_stage(const fs::path& inputs){
 const auto staging=inputs/L"native-lifecycle"/L"raw-destroy";writer_fixture fixture(inputs,staging);context_owner owner{make_context()};
 lpb_target_writer_plan* plan{};REQUIRE(lpb_target_writer_prepare_v1(owner.value,&fixture.writer,&plan)==LPB_RESULT_OK);
 REQUIRE(lpb_target_writer_stage_v1(owner.value,plan)==LPB_RESULT_OK);const auto output=sealed_output(owner.value,plan);
 REQUIRE(output.byte_length>0&&native_stages(staging).size()==1);
 lpb_destroy_context(owner.value);owner.value=nullptr;REQUIRE(fs::is_empty(staging));
 std::cout<<"raw_destroy_cleans_successful_native_stage PASS\n";
}
int main(int argc,char** argv){try{
 REQUIRE(argc==2);const std::string arg=argv[1];const fs::path directory=std::u8string(reinterpret_cast<const char8_t*>(arg.data()),arg.size());
 const auto jpeg=load(directory/L"neutral.jpg"),video=load(directory/L"neutral.mp4");auto* context=make_context();
 REQUIRE(sizeof(lpb_target_reader_v1)==48&&sizeof(lpb_target_validation_request_v1)==64&&sizeof(lpb_target_validation_result_v1)==176);
 REQUIRE(offsetof(lpb_target_validation_result_v1,observed_sha256)==144&&offsetof(lpb_target_validation_request_v1,cover_timestamp_us)==32);
 auto xml=independent_xml(video.size());
 const auto make=[&](const std::string& text){auto b=packet(jpeg,text);b.insert(b.end(),video.begin(),video.end());return b;};
 memory m{make(xml),context};auto r=request();auto valid=validate(m,r);
 REQUIRE(valid.structural_status==1&&valid.generic_media_status==1&&valid.preservation_status==0&&valid.device_status==0);
 REQUIRE(valid.video_length==video.size()&&valid.video_offset==m.bytes.size()-video.size()&&valid.cover_encoding==2);
 for(const auto& bad:std::array<std::string,5>{"<?xml garbage?>"+xml,"<??>"+xml,"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><?xml version=\"1.0\"?></x:xmpmeta>",xml+"]]>",xml.substr(0,xml.find("</r:Description>"))+"]]>"+xml.substr(xml.find("</r:Description>"))}) {
  m.bytes=make(bad);REQUIRE(validate(m,r).structural_status==2);
 }
 m.bytes=make("<?xml version=\"1.0\" encoding=\"UTF-8\"?><?xpacket begin=\"\" id=\"ordinary\"?>"+xml+"<?xpacket end=\"w\"?>");
 REQUIRE(validate(m,r).structural_status==1);
 auto duplicate=xml;replace(duplicate,"</r:Seq>","<r:li r:parseType=\"Resource\"><D:Item I:Semantic=\"Primary\" I:Mime=\"image/jpeg\"/></r:li></r:Seq>");
 m.bytes=make(duplicate);REQUIRE(validate(m,r).structural_status==2);
 auto invalid_utf8=xml;replace(invalid_utf8,"</r:Description>",std::string("\xc0\x80")+"</r:Description>");m.bytes=make(invalid_utf8);REQUIRE(validate(m,r).structural_status==2);
 auto wrapper=xml;replace(wrapper,"<m:xmpmeta xmlns:m=\"adobe:ns:meta/\">","<foo>");replace(wrapper,"</m:xmpmeta>","</foo>");m.bytes=make(wrapper);REQUIRE(validate(m,r).structural_status==2);
 for(const auto& ns:std::array<std::string,2>{"http://www.w3.org/XML/1998/namespace","http://www.w3.org/2000/xmlns/"}){
  auto bad=xml;replace(bad,"xmlns:C=\"http://ns.google.com/photos/1.0/camera/\"","xmlns:C=\"http://ns.google.com/photos/1.0/camera/\" xmlns:foreign=\""+ns+"\"");
  m.bytes=make(bad);REQUIRE(validate(m,r).structural_status==2);
 }
 for(const auto& uri:std::array<std::string,3>{"http://ns.google.com/photos/1.0/camera/","http://ns.google.com/photos/1.0/container/","http://ns.google.com/photos/1.0/container/item/"}) {
  auto bad=xml;replace(bad,uri,"https://"+uri.substr(7));m.bytes=make(bad);REQUIRE(validate(m,r).structural_status!=1);
  bad=xml;replace(bad,uri," "+uri+" ");m.bytes=make(bad);REQUIRE(validate(m,r).structural_status!=1);
 }
 for(const auto& change:std::array<std::pair<std::string,std::string>,4>{{{"MotionPhotoVersion=\"1\"","MotionPhotoVersion=\"2\""},{"I:Mime=\"video/mp4\"","I:Mime=\"video/quicktime\""},{"I:Semantic=\"MotionPhoto\"","I:Semantic=\"Primary\""},{"PresentationTimestampUs=\"-1\"","PresentationTimestampUs=\"0\""}}}){
  auto bad=xml;replace(bad,change.first,change.second);m.bytes=make(bad);REQUIRE(validate(m,r).structural_status!=1);
 }
 m.bytes=packet(packet(jpeg,xml),xml);m.bytes.insert(m.bytes.end(),video.begin(),video.end());REQUIRE(validate(m,r).structural_status!=1);
 m.bytes=make(xml);m.bytes.pop_back();REQUIRE(validate(m,r).structural_status!=1);
 m.bytes=make(xml);m.mode=1;REQUIRE(validate(m,r,LPB_RESULT_CANCELLED).failure_category==6);
 m.mode=2;REQUIRE(validate(m,r,LPB_RESULT_INTERNAL_ERROR).failure_category==5);
 m.mode=3;REQUIRE(validate(m,r,LPB_RESULT_INTERNAL_ERROR).failure_category==5);
 m.mode=4;REQUIRE(validate(m,r).structural_status==1&&m.tested);m.mode=0;
 auto bad_request=r;bad_request.reserved=1;REQUIRE(validate(m,bad_request,LPB_RESULT_INVALID_ARGUMENT).structural_status!=1);
 // R3 activates V1: actual V2 bytes are now a conformance failure for that
 // active target, rather than the historical inactive-capability result.
 bad_request=r;bad_request.target_profile=1;REQUIRE(validate(m,bad_request).structural_status==2);
 reader_structure_tests(directory,video,context);
 // Actual Native Writer stage -> transported bytes -> independent callback reader.
 std::array<lpb_target_writer_artifact_v1,2> artifacts{};
 for(uint32_t i=0;i<2;++i){const auto path=directory/(i?L"neutral.mp4":L"neutral.jpg");auto& a=artifacts[i];
  a.struct_size=sizeof(a);a.version=1;a.role=i;a.container=1;a.video_codec=i?2U:0U;a.expected_identity=identity(path);const auto digest=hash(path);std::memcpy(a.sha256,digest.data(),32);path_to(a.path_utf8,sizeof(a.path_utf8),path);}
 lpb_target_writer_request_v1 writer{};writer.struct_size=sizeof(writer);writer.version=1;writer.target_profile=2;writer.target_version=1;writer.artifact_count=2;writer.artifacts=artifacts.data();path_to(writer.staging_directory_utf8,sizeof(writer.staging_directory_utf8),directory);
 lpb_target_writer_plan* plan{};REQUIRE(lpb_target_writer_prepare_v1(context,&writer,&plan)==LPB_RESULT_OK);REQUIRE(lpb_target_writer_stage_v1(context,plan)==LPB_RESULT_OK);
 lpb_target_writer_status_v1 status{sizeof(status),1};lpb_target_writer_output_v1 output{sizeof(output),1};uint32_t count=0;
 REQUIRE(lpb_target_writer_get_outputs_v1(context,plan,&output,1,&count,&status)==LPB_RESULT_OK&&count==1&&status.state==2);
 m.bytes.resize(static_cast<size_t>(output.byte_length));REQUIRE(m.bytes.size()<=UINT32_MAX);
 REQUIRE(lpb_target_writer_read_stage_v1(context,plan,output.stage_id,0,m.bytes.data(),static_cast<uint32_t>(m.bytes.size()),&count)==LPB_RESULT_OK&&count==m.bytes.size());
 REQUIRE(validate(m,r).structural_status==1);count=99;
 REQUIRE(lpb_target_writer_read_stage_v1(context,plan,output.stage_id+1,0,nullptr,0,&count)==LPB_RESULT_AUTHORITY_VIOLATION&&count==0);
 REQUIRE(lpb_target_writer_stage_v1(context,plan)==LPB_RESULT_PLAN_REPLAYED);REQUIRE(lpb_target_writer_release_v1(context,plan)==LPB_RESULT_OK);
 REQUIRE(lpb_target_writer_read_stage_v1(context,plan,output.stage_id,0,nullptr,0,&count)==LPB_RESULT_AUTHORITY_VIOLATION);
 lpb_destroy_context(context);
 // Callback destroy has no surviving context access after outer return.
 memory dying{make(xml),make_context(),5};REQUIRE(validate(dying,r,LPB_RESULT_CANCELLED).structural_status!=1);
 validator_external_destroy_waits_for_host_read(make(xml));
 writer_release_during_real_stage_and_read(directory);
 native_stage_rename_substitution_preserves_foreign(directory);
 raw_destroy_cleans_successful_native_stage(directory);
 std::cout<<"R2 independent validator, strict URI, callbacks and real native stage checks passed\n";return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
