#include "protocols/huawei_target.h"
#include "media/huawei_conformance.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <algorithm>
#include <cstring>
namespace {
void require(bool value,const char* why){if(!value)throw std::runtime_error(why);}
std::vector<uint8_t> load(const std::filesystem::path& p){std::ifstream f(p,std::ios::binary);require(bool(f),"fixture missing");return {std::istreambuf_iterator<char>(f),{}};}
struct planned {
 const lpb::random_access_reader& source;const lpb::target::huawei_movie_plan& plan;
 static bool read(void* p,uint64_t at,std::span<uint8_t> bytes) noexcept {const auto& s=*static_cast<planned*>(p);return s.plan.read(s.source,at,bytes);}
 lpb::random_access_reader view(){return {plan.output_length,this,read};}
};
// Independently rearrange the fixture and patch its original table entries.
// No Writer output or relocation helper constructs these input layouts.
std::vector<uint8_t> layout(const std::vector<uint8_t>& input,bool front,bool split) {
 lpb::memory_random_access memory{input};lpb::media::target_video_observation facts;lpb::media::target_video_diagnostic why;
 require(lpb::media::observe_target_video(memory.view(),true,facts,why),"layout fixture observation");
 auto movie=std::find_if(facts.media.top_level_boxes.begin(),facts.media.top_level_boxes.end(),[](const auto& b){return !std::memcmp(b.type,"moov",4);});
 require(movie!=facts.media.top_level_boxes.end(),"fixture moov");
 auto moov=facts.media.moov_bytes;std::vector<uint8_t> output;size_t moov_at=0;
 struct mapping {uint64_t first,last,new_first;};std::vector<mapping> mappings;
 const auto raw=[&](uint64_t first,uint64_t last){output.insert(output.end(),input.begin()+static_cast<ptrdiff_t>(first),input.begin()+static_cast<ptrdiff_t>(last));};
 const auto number=[&](uint32_t v){for(unsigned i=0;i<4;++i)output.push_back(static_cast<uint8_t>(v>>((3-i)*8)));};
 const auto put_movie=[&](){moov_at=output.size();output.insert(output.end(),moov.begin(),moov.end());};
 for(const auto& b:facts.media.top_level_boxes) {
  if(!std::memcmp(b.type,"moov",4))continue;
  if(!std::memcmp(b.type,"mdat",4)&&split) {
   const uint64_t first=b.offset+b.header_size,last=b.offset+b.size;uint64_t cut=0;
   for(const auto& table:facts.media.sample_tables)for(const auto& chunk:table.chunks) {
    const uint64_t possible=chunk.offset+chunk.size;if(possible<=first||possible>=last)continue;
    bool crossed=false;for(const auto& t:facts.media.sample_tables)for(const auto& c:t.chunks)
     if(c.offset<possible&&c.offset+c.size>possible)crossed=true;
    if(!crossed&&!cut)cut=possible;
   }
   require(cut>first&&cut<last,"independent multi-mdat cut");
   for(const auto range:{std::pair{first,cut},std::pair{cut,last}}) {
    number(static_cast<uint32_t>(range.second-range.first+8));output.insert(output.end(),{'m','d','a','t'});
    mappings.push_back({range.first,range.second,output.size()});raw(range.first,range.second);
   }
  }else {
   if(!std::memcmp(b.type,"mdat",4))mappings.push_back({b.offset+b.header_size,b.offset+b.size,output.size()+b.header_size});
   raw(b.offset,b.offset+b.size);
  }
  if(front&&!std::memcmp(b.type,"ftyp",4))put_movie();
 }
 if(!front)put_movie();
 // A complete, ordinary trailing UUID must remain inside the copied movie.
 number(27);output.insert(output.end(),{'u','u','i','d'});output.insert(output.end(),16,0);output.insert(output.end(),{1,2,3});
 for(const auto& table:facts.media.sample_tables)for(size_t i=0;i<table.chunks.size();++i) {
  const auto& chunk=table.chunks[i];const mapping* owner=nullptr;
  for(const auto& m:mappings)if(chunk.offset>=m.first&&chunk.offset+chunk.size<=m.last) {require(!owner,"layout unique owner");owner=&m;}
  require(owner!=nullptr,"layout chunk mapping");const uint64_t value=owner->new_first+chunk.offset-owner->first;
  const auto at=moov_at+table.chunk_offsets_entries_start+i*table.chunk_offset_width;
  for(size_t j=0;j<table.chunk_offset_width;++j)output[at+j]=static_cast<uint8_t>(value>>((table.chunk_offset_width-1-j)*8));
 }
 return output;
}
}
int main(int argc,char** argv) {
 try {
  require(argc==2,"qualified input directory required");
  auto original=load(std::filesystem::path(argv[1])/"neutral-audio.mp4");
  for(const auto& input:{original,layout(original,true,false),layout(original,false,false),layout(original,true,true),layout(original,false,true)}) {
  lpb::memory_random_access memory{input};auto reader=memory.view();
  for(const int64_t us:{0LL,100000LL,599999LL}) {
   lpb::target::huawei_movie_plan plan;std::string error;
   require(lpb::target::prepare_huawei_movie(reader,us,plan,error),error.c_str());
   planned p{reader,plan};const auto rewritten=p.view();
   lpb::media::target_video_observation before,after;lpb::media::target_video_diagnostic why;lpb::media::target_covertime_observation cover;
   require(lpb::media::observe_target_video(reader,true,before,why),"input independent observation");
   require(!lpb::media::observe_target_video(rewritten,true,after,why),"Neutral observer must reject generated live property");
   require(lpb::media::observe_target_video(rewritten,true,after,why,&cover),why.reason.c_str());
   require(cover.properties.size()==1&&cover.properties[0].owner=="moov/udta"&&cover.properties[0].type==1,"unique typed owned covertime");
   int64_t observed_us=-1;
   require(lpb::media::observe_huawei_covertime(cover,observed_us)&&observed_us==us,"independent exact millisecond scalar rule");
   auto shadow=cover;shadow.properties.push_back(shadow.properties.front());
   require(!lpb::media::observe_huawei_covertime(shadow,observed_us),"duplicate scalar owner rejected");
   shadow=cover;shadow.properties[0].owner="top/udta";
   require(!lpb::media::observe_huawei_covertime(shadow,observed_us),"wrong owner rejected");
   shadow=cover;shadow.properties[0].type=21;
   require(!lpb::media::observe_huawei_covertime(shadow,observed_us),"wrong timestamp type rejected");
   require(before.media.sample_tables.size()==after.media.sample_tables.size(),"track count preserved");
   for(size_t t=0;t<before.media.sample_tables.size();++t) {
    const auto& a=before.media.sample_tables[t];const auto& b=after.media.sample_tables[t];
    require(a.chunks.size()==b.chunks.size(),"chunk count preserved");
    for(size_t i=0;i<a.chunks.size();++i) {
     require(a.chunks[i].size==b.chunks[i].size,"chunk size preserved");
     std::vector<uint8_t> x(static_cast<size_t>(a.chunks[i].size)),y(x.size());
     require(reader.read_exact(a.chunks[i].offset,x)&&rewritten.read_exact(b.chunks[i].offset,y)&&x==y,"complete audio/video chunk byte equality");
    }
   }
   for(uint32_t variant:{1U,2U}) {
    std::array<uint8_t,60> footer{};
    require(lpb::target::make_huawei_footer(variant,plan.cover_frame,plan.total_frames,plan.output_length,footer,error),error.c_str());
    const std::string value(reinterpret_cast<const char*>(footer.data()+40),20);
    require(value.starts_with("LIVE_"+std::to_string(plan.output_length+20)),"actual rewritten movie LIVE length");
   }
  }
  lpb::target::huawei_movie_plan rejected;std::string error;
  require(!lpb::target::prepare_huawei_movie(reader,-1,rejected,error),"negative cover rejected");
  std::array<uint8_t,60> footer{};
  require(!lpb::target::make_huawei_footer(1,0,1,UINT64_MAX,footer,error),"footer overflow rejected");
  }
  lpb::media::target_covertime_observation floating;
  floating.properties.push_back({"moov/udta",0x17,0,{0x44,0xa2,0x8b,0x44}});
  int64_t observed_us=0;
  require(lpb::media::observe_huawei_covertime(floating,observed_us)&&observed_us==1300352,"float32 milliseconds decoded from observed bytes");
  floating.properties[0].value={0x7f,0xc0,0,0};
  require(!lpb::media::observe_huawei_covertime(floating,observed_us),"NaN timestamp rejected");
  isobmff_video_presentation_timeline fractional;
  fractional.presentation_order={{0,0,0,true,0,333667},{1,0,0,true,333667,333667},{2,0,0,true,1000000,333667}};
  uint32_t count=0,frame=0;
  require(lpb::media::project_huawei_legacy_cover(fractional,1,observed_us,count)&&observed_us==33367&&count==3,
      "fractional legacy boundary rounds into selected frame");
  require(lpb::media::select_huawei_frame(fractional,observed_us,frame,count)&&frame==1,"projected frame identity agrees");
  require(!lpb::media::select_huawei_frame(fractional,80000,frame,count),"modern timestamp in VFR gap rejected");
  require(!lpb::media::project_huawei_legacy_cover(fractional,3,observed_us,count),"legacy index out of range rejected");
  std::cout<<"Huawei movie rewrite, independent property ownership and complete audio/video byte checks PASS\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
