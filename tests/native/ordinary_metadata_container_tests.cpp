#include "media/ordinary_metadata_container.h"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace {
using bytes=std::vector<uint8_t>;namespace model=lpb::media::ordinary_metadata;
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
bytes load(const std::filesystem::path& path){std::ifstream f(path,std::ios::binary);require(bool(f),"Required current Neutral snapshot asset missing");return bytes((std::istreambuf_iterator<char>(f)),{});}
void run(const std::filesystem::path& jpg,const std::filesystem::path& heif,const std::filesystem::path& records,const std::filesystem::path& sef_heif,const std::filesystem::path& singleton_jpg){
 const auto jpeg=load(jpg),heic=load(heif),raw=load(records);bytes encoded,roundtrip;
 model::observed_capsule observed;
 require(model::observe_jpeg_capsule(jpeg,observed)==model::observation::absent,"Neutral JPEG has no duplicated ordinary metadata envelope");
 require(model::insert_jpeg_capsule(jpeg,raw,encoded),"Bounded ordinary metadata APP15 insertion");
 require(model::observe_jpeg_capsule(encoded,observed)==model::observation::present&&observed.raw==raw&&observed.chunks>1,"Exact full multi-chunk ordinary records inside legal primary markers");
 // Offline proof only: reconstruct all original bytes, allowing only the
 // declared MPF primary-size update. No removal API is shipped.
 size_t cursor=0;
 for(const auto& range:observed.ranges){roundtrip.insert(roundtrip.end(),encoded.begin()+static_cast<ptrdiff_t>(cursor),encoded.begin()+static_cast<ptrdiff_t>(range.first));cursor=range.second;}
 roundtrip.insert(roundtrip.end(),encoded.begin()+static_cast<ptrdiff_t>(cursor),encoded.end());
 lpb::media::jpeg_gainmap_mpf::jpeg_info original_info;
 require(lpb::media::jpeg_gainmap_mpf::parse_jpeg(jpeg,original_info)&&original_info.mpf_status==lpb::media::jpeg_gainmap_mpf::status::present&&original_info.mpf.image_count>1,"Actual Neutral JPEG multi-image MPF fixture");
 lpb::media::jpeg_gainmap_mpf::write_u32(roundtrip,original_info.mpf.entries[0].size_field_offset,static_cast<uint32_t>(original_info.eoi_end),original_info.mpf.little_endian);
 require(roundtrip==jpeg,"Every original primary/metadata/GainMap byte is retained with exact MPF relocation");
 const auto single=load(singleton_jpg);lpb::media::jpeg_gainmap_mpf::jpeg_info single_info;
 require(lpb::media::jpeg_gainmap_mpf::parse_jpeg(single,single_info)&&lpb::media::jpeg_gainmap_mpf::singleton_primary(single_info)&&single_info.eoi_end==single.size(),"Real bounded singleton self-entry fixture");
 bytes single_result;require(model::insert_jpeg_capsule(single,raw,single_result),"Existing singleton stale-self-size rule remains representable");
 lpb::media::jpeg_gainmap_mpf::jpeg_info single_after;require(lpb::media::jpeg_gainmap_mpf::parse_jpeg(single_result,single_after)&&single_after.mpf.entries[0].size==single_after.eoi_end,"Singleton output self-size equals actual encoded boundary");
 auto invalid_single=single;lpb::media::jpeg_gainmap_mpf::write_u32(invalid_single,single_info.mpf.entries[0].size_field_offset-4,0,single_info.mpf.little_endian);
 require(!model::insert_jpeg_capsule(invalid_single,raw,single_result)&&single_result.empty(),"Stale size does not authorize an unrecognized MPF shape");
 bytes second{1};require(!model::insert_jpeg_capsule(encoded,raw,second)&&second.empty(),"Materialized and already embedded metadata cannot duplicate");
 auto bad=encoded;const auto first=observed.ranges[0];
 bad[first.first+4+24]=1;require(model::observe_jpeg_capsule(bad,observed)==model::observation::malformed&&observed.raw.empty(),"Unknown flags fail closed inside owned marker");
 require(model::observe_jpeg_capsule(encoded,observed)==model::observation::present,"Restore positive ownership");
 bad=encoded;bad.insert(bad.begin()+static_cast<ptrdiff_t>(observed.ranges[0].second),{0xff,0xe0,0,4,0,0});
 require(model::observe_jpeg_capsule(bad,observed)==model::observation::malformed,"Interleaved capsule chunks rejected");
 require(model::observe_jpeg_capsule(encoded,observed)==model::observation::present,"Restore complete ranges");
 const bytes marker_group(encoded.begin()+static_cast<ptrdiff_t>(observed.ranges.front().first),encoded.begin()+static_cast<ptrdiff_t>(observed.ranges.back().second));
 bad=encoded;bad.insert(bad.begin()+2,marker_group.begin(),marker_group.end());require(model::observe_jpeg_capsule(bad,observed)==model::observation::malformed,"Duplicate marker group rejected");
 bad=jpeg;bad.insert(bad.begin()+static_cast<ptrdiff_t>(original_info.eoi_end-2),marker_group.begin(),marker_group.end());
 require(model::observe_jpeg_capsule(bad,observed)==model::observation::malformed,"Metadata after compressed scan cannot claim the pre-scan mapping");
 require(model::observe_heif_capsule(heic,observed)==model::observation::absent,"Neutral HEIF has no duplicate owned UUID");
 require(model::insert_heif_capsule(heic,raw,encoded),"Bounded ordinary metadata root UUID insertion");
 require(std::equal(heic.begin(),heic.end(),encoded.begin()),"All original HEIF item graph/extent bytes stay at their original offsets");
 require(model::observe_heif_capsule(encoded,observed)==model::observation::present&&observed.raw==raw,"Whole ordinary records in one owned root UUID");
 require(!model::insert_heif_capsule(encoded,raw,second)&&second.empty(),"Duplicate metadata owner insertion rejects");
 const auto uuid=observed.ranges[0];bad=encoded;bad.insert(bad.end(),encoded.begin()+static_cast<ptrdiff_t>(uuid.first),encoded.begin()+static_cast<ptrdiff_t>(uuid.second));
 require(model::observe_heif_capsule(bad,observed)==model::observation::malformed,"Duplicate root UUID owner rejected");
 bad=encoded;model::put32(bad,uuid.first+24,2);require(model::observe_heif_capsule(bad,observed)==model::observation::malformed,"Unknown root UUID envelope version rejected");
 bad=encoded;model::put32(bad,uuid.first+28,0xffffffffU);require(model::observe_heif_capsule(bad,observed)==model::observation::malformed,"Unbounded UUID chunk table rejected");
 bad=encoded;model::put32(bad,uuid.first+32,0xffffffffU);require(model::observe_heif_capsule(bad,observed)==model::observation::malformed,"Unbounded UUID chunk length rejected");
 bad=encoded;bad.push_back(0);require(model::observe_heif_capsule(bad,observed)==model::observation::malformed,"Unframed bytes after root UUID rejected");
 bad=heic;const size_t at=bad.size();bad.resize(at+24);model::put32(bad,at,24);bad[at+4]='m';bad[at+5]='d';bad[at+6]='a';bad[at+7]='t';std::copy(model::name_space.begin(),model::name_space.end(),bad.begin()+static_cast<ptrdiff_t>(at+8));
 require(model::observe_heif_capsule(bad,observed)==model::observation::absent,"Namespace bytes inside mdat do not authorize an ordinary metadata owner");
 const auto sef=load(sef_heif);size_t sefd=sef.size(),position=0;
 while(position<sef.size()){isobmff_box_header h{};require(try_read_box_header(sef.data(),position,sef.size(),h),"Real ordinary SEFD HEIF root graph");if(std::string_view(reinterpret_cast<const char*>(sef.data()+position+4),4)=="sefd"){require(position+h.size==sef.size(),"Real ordinary SEFD is final");sefd=position;}position+=h.size;}
 require(sefd<sef.size()&&model::insert_heif_capsule(sef,raw,encoded),"Capsule insertion preserves an existing final ordinary SEFD owner");
 require(model::observe_heif_capsule(encoded,observed)==model::observation::present&&observed.ranges[0].first==sefd,"Owned UUID precedes final SEFD");
 require(std::equal(sef.begin(),sef.begin()+static_cast<ptrdiff_t>(sefd),encoded.begin())&&std::equal(sef.begin()+static_cast<ptrdiff_t>(sefd),sef.end(),encoded.begin()+static_cast<ptrdiff_t>(sefd+encoded.size()-sef.size())),"Original item graph/extent offsets and complete ordinary SEFD bytes preserved");
 std::cout<<"PASS real Neutral JPEG+MPF/GainMap and HEIF metadata placements, exact original bytes and raw records, duplicate/shadow/truncation/version/bounds/scan-owner rejection. No CLI/device acceptance\n";
}
}
#ifdef _WIN32
int wmain(int argc,wchar_t** argv){try{require(argc==6,"Neutral JPEG, HEIF, record, ordinary SEFD and singleton JPEG assets required");run(argv[1],argv[2],argv[3],argv[4],argv[5]);return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
#else
int main(int argc,char** argv){try{require(argc==6,"Neutral JPEG, HEIF, record, ordinary SEFD and singleton JPEG assets required");run(argv[1],argv[2],argv[3],argv[4],argv[5]);return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
#endif
