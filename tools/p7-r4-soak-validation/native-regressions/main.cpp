#include "containers/isobmff_video_profile.h"
#include "containers/isobmff_mebx_transplant.h"
#include "media/video_sei_preservation.h"
#include "media/h264_order_proof.h"
#include <cstdio>
#include <fstream>
#include <filesystem>
#include <iterator>
#include <cstring>
#include <algorithm>

struct box { size_t start,size,header; };
static uint32_t u32(const std::vector<uint8_t>& d,size_t p) {
    return (uint32_t(d.at(p))<<24)|(uint32_t(d.at(p+1))<<16)|(uint32_t(d.at(p+2))<<8)|d.at(p+3);
}
static void put32(std::vector<uint8_t>& d,size_t p,uint32_t n) {
    for(size_t i=0;i<4;++i)d.at(p+i)=static_cast<uint8_t>(n>>(24-8*i));
}
static void require(bool v,const char* message){if(!v)throw std::runtime_error(message);}
static std::vector<box> children(const std::vector<uint8_t>& d,size_t a,size_t end,const char* type) {
    std::vector<box> result;
    while(a<end){
        require(end-a>=8,"Truncated box");uint64_t n=u32(d,a);size_t h=8;
        if(n==1){require(end-a>=16,"Truncated extended box");n=(uint64_t(u32(d,a+8))<<32)|u32(d,a+12);h=16;}
        if(n==0)n=end-a;
        require(n>=h&&n<=end-a,"Invalid box size");
        if(std::memcmp(d.data()+a+4,type,4)==0)result.push_back({a,static_cast<size_t>(n),h});
        a+=static_cast<size_t>(n);
    }
    return result;
}
static box one(const std::vector<uint8_t>& d,box parent,const char* name){
    auto list=children(d,parent.start+parent.header,parent.start+parent.size,name);
    require(list.size()==1,"Expected unique child");return list.front();
}
static lpb::random_access_reader reader(std::vector<uint8_t>& data){
    return {data.size(),&data,[](void* c,uint64_t p,std::span<uint8_t> out) noexcept{
        auto& d=*static_cast<std::vector<uint8_t>*>(c);
        if(p>d.size()||out.size()>d.size()-p)return false;
        std::memcpy(out.data(),d.data()+p,out.size());return true;
    }};
}
static bool profile_valid(std::vector<uint8_t>& d,lpb::media::isobmff_video_profile& p){
    lpb::media::video_profile_failure f{};return lpb::media::inspect_isobmff_video_profile(reader(d),p,f);
}
static void order_regress() {
    struct writer {
        std::vector<uint8_t> data;unsigned count{};
        void bit(unsigned v){if(count%8==0)data.push_back(0);data.back()|=static_cast<uint8_t>(v<<(7-count%8));++count;}
        void n(uint32_t v,unsigned length){while(length)bit((v>>--length)&1);}
        void ue(uint32_t v){++v;unsigned len=0;for(auto x=v;x;x>>=1)++len;for(unsigned i=1;i<len;++i)bit(0);n(v,len);}
        std::vector<uint8_t> nal(uint8_t type){bit(1);while(count%8)bit(0);std::vector<uint8_t> out{0,0,0,1,type};
            unsigned z=0;for(auto v:data){if(z>=2&&v<=3){out.push_back(3);z=0;}out.push_back(v);z=v==0?z+1:0;}return out;}
    };
    writer s;s.n(66,8);s.n(0,8);s.n(30,8);s.ue(0);s.ue(4);s.ue(2);s.ue(1);s.bit(0);s.ue(79);s.ue(44);s.bit(1);
    auto config=s.nal(0x67);
    writer p;p.ue(0);p.ue(0);p.bit(0);p.bit(0);p.ue(0);p.ue(0);p.ue(0);p.bit(0);p.n(0,2);p.ue(0);p.ue(0);p.ue(0);p.bit(1);p.bit(0);p.bit(0);
    auto pps=p.nal(0x68);config.insert(config.end(),pps.begin(),pps.end());
    auto picture=[&](uint32_t frame,uint32_t slice,bool adaptive=false){writer w;w.ue(0);w.ue(slice);w.ue(0);w.n(frame,8);
        if(frame==0)w.ue(0);if(slice%5==0){w.bit(0);w.bit(0);}if(frame==0){w.bit(0);w.bit(0);}else w.bit(adaptive?1:0);
        return w.nal(frame==0?0x65:0x41);};
    lpb::media::h264_order_proof valid;
    require(valid.configure(config)&&valid.access_unit(picture(0,7))&&valid.access_unit(picture(1,5)),"Coded Baseline POC2 order proof");
    auto bad=config;bad[5]=100;lpb::media::h264_order_proof high;require(!high.configure(bad),"Declared Base cannot bless High SPS");
    lpb::media::h264_order_proof reorder;require(reorder.configure(config)&&reorder.access_unit(picture(0,7))&&!reorder.access_unit(picture(2,5)),"Frame gaps/reordering refused");
    lpb::media::h264_order_proof b;require(b.configure(config)&&b.access_unit(picture(0,7))&&!b.access_unit(picture(1,6)),"Actual B slice refused");
    lpb::media::h264_order_proof reset;require(reset.configure(config)&&reset.access_unit(picture(0,7))&&!reset.access_unit(picture(1,5,true)),"MMCO POC reset refused");
    auto twice=picture(0,7);auto next=picture(1,5);twice.insert(twice.end(),next.begin(),next.end());
    lpb::media::h264_order_proof multi;require(multi.configure(config)&&!multi.access_unit(twice),"Two pictures in one sample refused");
    auto timing=picture(0,7);timing.insert(timing.end(),{0,0,0,1,6,1,1,0,0x80});
    lpb::media::h264_order_proof sei;require(sei.configure(config)&&!sei.access_unit(timing),"SEI display timing cannot authorize rewrite");
    auto opaque=picture(0,7);opaque.insert(opaque.end(),{0,0,0,1,6,0x80,1,0,0x80});
    lpb::media::h264_order_proof unknown;require(unknown.configure(config)&&!unknown.access_unit(opaque),"Opaque AVC SEI namespace cannot authorize rewrite");
    writer drop;drop.ue(0);drop.ue(7);drop.ue(0);drop.n(0,8);drop.ue(0);drop.bit(1);drop.bit(0);
    lpb::media::h264_order_proof prior;require(prior.configure(config)&&prior.access_unit(picture(0,7))&&
        prior.access_unit(picture(1,5))&&!prior.access_unit(drop.nal(0x65)),"Internal IDR cannot discard prior display pictures");
    lpb::media::h264_order_proof truncated;config.resize(6);require(!truncated.configure(config),"Truncated config refused");
    std::puts("{\"codedOrderRegressions\":\"PASS\",\"category\":\"Synthetic\"}");
}
static void regress(std::vector<uint8_t>& source,std::vector<uint8_t>& stage){
    using namespace lpb::media;
    std::vector<preserved_sei_message> messages{{4U,{0x26U,0U,4U,0U,5U,1U,0U,0U,0U,2U}},
        {137U,std::vector<uint8_t>(24U,0U)},{144U,{3U,0x84U,0U,0U}}};
    std::vector<uint8_t> sei_nal;require(make_preserved_sei_nal(messages,true,sei_nal),"HEVC metadata SEI encode");
    std::vector<uint8_t> sei_packet(4U);put32(sei_packet,0U,static_cast<uint32_t>(sei_nal.size()));
    sei_packet.insert(sei_packet.end(),sei_nal.begin(),sei_nal.end());
    std::vector<preserved_sei_message> parsed;
    require(collect_packet_sei(sei_packet,4U,true,parsed)&&parsed.size()==messages.size(),"SEI escaped metadata roundtrip");
    for(size_t i=0;i<messages.size();++i)require(messages[i].type==parsed[i].type&&messages[i].payload==parsed[i].payload,"SEI exact payload bytes");
    auto bad_sei=sei_packet;bad_sei.pop_back();require(!collect_packet_sei(bad_sei,4U,true,parsed),"Truncated SEI must fail");
    bad_sei=sei_packet;bad_sei[4]=0x7CU;require(!collect_packet_sei(bad_sei,4U,true,parsed),"Dolby/reserved RPU cannot be silently discarded");
    bad_sei=sei_packet;bad_sei[4]=0x52U;require(!collect_packet_sei(bad_sei,4U,true,parsed),"Unknown HEVC extension NAL cannot disappear");
    require(!sei_metadata_supports_target(messages,false)&&sei_metadata_supports_target(messages,true),
        "Raw HDR Vivid refuses AVC even when decoder supplies no AVFrame side data");
    std::vector<isobmff_mebx_track_facts> original,moved;
    require(inspect_isobmff_mebx_tracks(source,original)&&original.size()==3,"Real Apple three-track inventory");
    std::vector<uint8_t> out;
    require(transplant_isobmff_mebx_tracks(source,stage,out,moved),"Real three-track transplant");
    require(validate_isobmff_mebx_transplants(source,out,moved),"Real exact readback");
    std::vector<uint8_t> static_output;bool static_changed=false;
    require(preserve_isobmff_static_metadata(source,out,static_output,static_changed)&&static_changed,
        "Real movie/video metadata and aperture restoration");
    require(validate_isobmff_static_metadata(source,static_output),"Exact static metadata readback");
    require(validate_isobmff_mebx_transplants(source,static_output,moved),"Metadata relocation retains all MEBX payloads");
    std::vector<uint8_t> idempotent;bool second_changed=true;
    require(preserve_isobmff_static_metadata(source,static_output,idempotent,second_changed)&&
        !second_changed&&idempotent==static_output,"Static metadata preservation is idempotent");
    auto source_movie=children(source,0,source.size(),"moov").at(0);
    auto source_meta=one(source,source_movie,"meta");
    auto keys=one(source,source_meta,"keys");
    auto malformed=source;put32(malformed,keys.start+keys.header+4,0xFFFFFFFFU);
    std::vector<uint8_t> refused;bool refused_changed=false;
    require(!preserve_isobmff_static_metadata(malformed,out,refused,refused_changed)&&refused.empty(),
        "Malformed metadata key graph must fail closed");
    auto static_movie=children(static_output,0,static_output.size(),"moov").at(0);
    auto static_meta=one(static_output,static_movie,"meta");
    auto static_items=one(static_output,static_meta,"ilst");
    auto metadata_tamper=static_output;metadata_tamper.at(static_items.start+static_items.size-1)^=1;
    require(!validate_isobmff_static_metadata(source,metadata_tamper),"Static metadata payload tamper must fail");
    auto changed=out;changed.back()^=1;
    require(!validate_isobmff_mebx_transplants(source,changed,moved),"Payload tamper must fail");
    std::vector<uint8_t> rejected;std::vector<isobmff_mebx_track_facts> rejected_facts;
    require(!transplant_isobmff_mebx_tracks(source,out,rejected,rejected_facts),"Existing metadata StageA must fail");
    auto moovs=children(out,0,out.size(),"moov");require(moovs.size()==1,"Unique movie");
    auto tracks=children(out,moovs[0].start+moovs[0].header,moovs[0].start+moovs[0].size,"trak");
    size_t modified=0;
    for(auto track:tracks){
        auto tkhd=one(out,track,"tkhd");size_t body=tkhd.start+tkhd.header;
        size_t id=body+(out.at(body)==1?20:12);if(u32(out,id)!=original.front().track_id)continue;
        changed=out;put32(changed,id,1);std::vector<isobmff_mebx_track_facts> bad;
        require(!inspect_isobmff_mebx_tracks(changed,bad),"Duplicate track ID must fail");
        auto tref=one(out,track,"tref");auto cdsc=one(out,tref,"cdsc");
        changed=out;put32(changed,cdsc.start+cdsc.header,9999);
        require(!inspect_isobmff_mebx_tracks(changed,bad),"Dangling video reference must fail");
        auto extra_ref=source;
        auto sm=children(source,0,source.size(),"moov").at(0);
        auto original_tracks=children(source,sm.start+sm.header,sm.start+sm.size,"trak");
        auto original_track=original_tracks.at(2);auto original_ref=one(source,original_track,"tref");
        const size_t insert=original_ref.start+original_ref.size;
        const std::vector<uint8_t> dependency{0,0,0,12,'d','p','n','d',0,0,0,1};
        extra_ref.insert(extra_ref.begin()+insert,dependency.begin(),dependency.end());
        put32(extra_ref,sm.start,static_cast<uint32_t>(sm.size+dependency.size()));
        put32(extra_ref,original_track.start,static_cast<uint32_t>(original_track.size+dependency.size()));
        put32(extra_ref,original_ref.start,static_cast<uint32_t>(original_ref.size+dependency.size()));
        require(!inspect_isobmff_mebx_tracks(extra_ref,bad),"Additional unsupported track references must fail");
        ++modified;
    }
    require(modified==1,"Tamper targets exact metadata track");
    isobmff_video_profile valid{};require(profile_valid(stage,valid),"Real x264 High AVC configuration must parse");
    require(valid.bit_depth_luma==8&&valid.bit_depth_chroma==8,"Real x264 depth8");
    require(select_video_conversion_owner(valid,requested_video_codec::hevc)==video_conversion_owner::minimal_libav_sidecar,"HEVC target owner");
    require(select_video_conversion_owner(valid,requested_video_codec::copy)==video_conversion_owner::project_remux,"COPY owner unchanged");
    auto movie=children(stage,0,stage.size(),"moov").at(0);
    auto avtracks=children(stage,movie.start+movie.header,movie.start+movie.size,"trak");
    size_t avcc_checked=0;
    for(auto track:avtracks){
        auto stbl=one(stage,one(stage,one(stage,track,"mdia"),"minf"),"stbl");auto stsd=one(stage,stbl,"stsd");
        size_t entry=stsd.start+stsd.header+8;
        if(std::memcmp(stage.data()+entry+4,"avc1",4)!=0)continue;
        auto configs=children(stage,entry+8+78,entry+u32(stage,entry),"avcC");require(configs.size()==1,"Unique avcC");
        auto config=configs[0];size_t p=config.start+config.header+6;
        unsigned sps=stage.at(config.start+config.header+5)&31;
        for(unsigned i=0;i<sps;++i){size_t n=(size_t(stage.at(p))<<8)|stage.at(p+1);p+=2+n;}
        unsigned pps=stage.at(p++);
        for(unsigned i=0;i<pps;++i){size_t n=(size_t(stage.at(p))<<8)|stage.at(p+1);p+=2+n;}
        require(p+4<=config.start+config.size,"Real High profile extension present");
        changed=stage;changed.at(p+1)^=1;isobmff_video_profile bad{};
        require(!profile_valid(changed,bad),"Conflicting extension depth must fail");++avcc_checked;
    }
    require(avcc_checked==1,"High profile extension regression exercised");
    std::puts("{\"regressions\":\"PASS\",\"realMebxTracks\":3,\"payloadTamperRejected\":true,\"duplicateIdRejected\":true,\"danglingReferenceRejected\":true,\"avccDepthConflictRejected\":true}");
}

static std::vector<uint8_t> bytes(const wchar_t* path) {
    std::ifstream file(std::filesystem::path(path), std::ios::binary);
    if(!file) throw std::runtime_error("Input cannot be opened");
    return {std::istreambuf_iterator<char>(file),std::istreambuf_iterator<char>()};
}
int wmain(int argc,wchar_t**argv) {
    try {
        if(argc==2&&std::wcscmp(argv[1],L"order-regress")==0){order_regress();return 0;}
        if(argc<3)return 2;
        auto source=bytes(argv[2]);
        if(std::wcscmp(argv[1],L"static")==0&&argc==4){
            auto stage=bytes(argv[3]);std::vector<uint8_t> output;bool changed=false;
            bool ok=lpb::media::preserve_isobmff_static_metadata(source,stage,output,changed);
            std::printf("{\"staticMetadata\":%s,\"changed\":%s}\n",ok?"true":"false",changed?"true":"false");return ok?0:1;
        }
        if(std::wcscmp(argv[1],L"regress")==0&&argc==4){auto stage=bytes(argv[3]);regress(source,stage);return 0;}
        if(std::wcscmp(argv[1],L"profile")==0){
            lpb::media::isobmff_video_profile profile{};
            lpb::media::video_profile_failure failure{};
            lpb::media::video_profile_diagnostic diagnostic{};
            lpb::random_access_reader reader{source.size(),&source,
                [](void* context,uint64_t offset,std::span<uint8_t> output) noexcept {
                    const auto& data=*static_cast<std::vector<uint8_t>*>(context);
                    if(offset>data.size()||output.size()>data.size()-offset)return false;
                    std::memcpy(output.data(),data.data()+offset,output.size());return true;
                }};
            bool ok=lpb::media::inspect_isobmff_video_profile(reader,profile,failure,&diagnostic);
            std::printf("{\"valid\":%s,\"failure\":%u,\"diagnostic\":%u,\"depth\":%u}\n",ok?"true":"false",static_cast<unsigned>(failure),static_cast<unsigned>(diagnostic),static_cast<unsigned>(profile.bit_depth_luma));
            return ok?0:1;
        }
        return 2;
    }catch(const std::exception& e){std::fprintf(stderr,"%s\n",e.what());return 3;}
}
