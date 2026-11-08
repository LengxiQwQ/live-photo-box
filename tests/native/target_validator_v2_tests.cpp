#include "livephotobox_native.h"
#include <array>
#include <algorithm>
#include <string>
#include <vector>
#include "foundation/sha256_core.h"
#include <cstring>
#include <iostream>
#include <stdexcept>
namespace {
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
struct input {
 lpb_context* context{};unsigned mode{};bool callback{},reentry_rejected{};
 static lpb_result LPB_CALL read(void* user,uint64_t,uint8_t*,uint32_t count,uint32_t* got) {
  auto& s=*static_cast<input*>(user);s.callback=true;*got=0;
  if(s.mode==1)return LPB_RESULT_CANCELLED;
  if(s.mode==2){*got=count+1;return LPB_RESULT_OK;}
  if(s.mode==3){lpb_destroy_context(s.context);return LPB_RESULT_OK;}
  if(s.mode==4){
   auto* plan=reinterpret_cast<lpb_target_writer_plan*>(uintptr_t{1});
   s.reentry_rejected=lpb_target_writer_prepare_v2(s.context,nullptr,&plan)==LPB_RESULT_AUTHORITY_VIOLATION&&!plan;
   return LPB_RESULT_INTERNAL_ERROR;
  }
  return LPB_RESULT_INTERNAL_ERROR;
 }
 lpb_target_reader_v1 reader(){return {sizeof(lpb_target_reader_v1),1,32,this,read,0,0};}
};
lpb_target_validation_request_v2 request(){
 lpb_target_validation_request_v2 r{};r.struct_size=sizeof(r);r.version=2;
 r.base.struct_size=sizeof(r.base);r.base.version=1;r.base.target_profile=2;r.base.target_version=1;
 r.base.member_count=1;r.base.expected_video_codec=2;return r;
}
lpb_target_validation_result_v2 output(){lpb_target_validation_result_v2 r{};r.struct_size=sizeof(r);r.version=2;return r;}
lpb_context* create(){lpb_context_options o{};o.struct_size=sizeof(o);o.abi_version=LPB_NATIVE_ABI_VERSION;lpb_context* c=nullptr;require(lpb_create_context(&o,&c)==LPB_RESULT_OK,"current context ABI");return c;}
using bytes=std::vector<uint8_t>;
void add(bytes& a,const bytes& b){a.insert(a.end(),b.begin(),b.end());}
void marker(bytes& b,uint8_t code,bytes p){b.insert(b.end(),{255,code,uint8_t((p.size()+2)>>8),uint8_t(p.size()+2)});add(b,p);}
bytes jpeg(std::string xml={}){
 bytes b{255,216};if(!xml.empty()){constexpr char sig[]="http://ns.adobe.com/xap/1.0/";bytes p(sig,sig+sizeof(sig));p.insert(p.end(),xml.begin(),xml.end());marker(b,225,p);}
 bytes q(65,1);q[0]=0;marker(b,219,q);marker(b,192,{8,0,1,0,1,1,1,17,0});bytes h;
 for(uint8_t type:{uint8_t(0),uint8_t(16)}){h.push_back(type);h.push_back(1);h.insert(h.end(),15,0);h.push_back(0);}marker(b,196,h);marker(b,218,{1,1,0,0,63,0});b.insert(b.end(),{63,255,217});return b;
}
lpb_result LPB_CALL read(void* user,uint64_t at,uint8_t* out,uint32_t n,uint32_t* got){auto& b=*static_cast<bytes*>(user);if(at>b.size()||n>b.size()-at)return LPB_RESULT_INVALID_ARGUMENT;std::memcpy(out,b.data()+at,n);*got=n;return LPB_RESULT_OK;}
void filtered_auxiliary_guard(bool bad_hash){
auto image=jpeg(),original=jpeg();
 const bytes png{137,80,78,71,13,10,26,10,0,0,0,13,'I','H','D','R',0,0,0,1,0,0,0,1,8,6,0,0,0,31,21,196,137,0,0,0,11,'I','D','A','T',120,156,99,96,0,2,0,0,5,0,1,165,246,69,64,0,0,0,0,'I','E','N','D',174,66,96,130};
 const size_t mi=image.size()+png.size(),after=original.size();
 auto item=[](std::string s,std::string extra){return "<r:li r:parseType='Resource'><C:Item I:Semantic='"+s+"' I:Mime='image/jpeg' "+extra+"/></r:li>";};
 std::string xml="<x:xmpmeta xmlns:x='adobe:ns:meta/'><r:RDF xmlns:r='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><r:Description xmlns:C='http://ns.google.com/photos/1.0/container/' xmlns:I='http://ns.google.com/photos/1.0/container/item/' xmlns:M='http://ns.xiaomi.com/photos/1.0/camera/' M:XMPMeta=\"&lt;subimage offset=&quot;"+std::to_string(mi+after)+"&quot; length=&quot;"+std::to_string(image.size())+"&quot; /&gt;&lt;lenswatermark offset=&quot;"+std::to_string(png.size()+after)+"&quot; length=&quot;"+std::to_string(png.size())+"&quot; /&gt;\"><C:Directory><r:Seq>"+
 item("Primary","I:Padding='"+std::to_string(mi)+"'")+item("Original","I:Length='"+std::to_string(original.size())+"'")+"</r:Seq></C:Directory></r:Description></r:RDF></x:xmpmeta>";
 auto target=jpeg(xml);add(target,image);add(target,png);add(target,original);
 const bytes video(16,0);add(target,video); // Invalid movie: auxiliary checks must be safe even before later rejection.
 std::array<uint8_t,60> footer{};footer.fill(' ');std::array<std::string,3> fields{"v6_f0","0:1","LIVE_"+std::to_string(video.size()+20)};
 for(size_t i=0;i<3;++i)std::copy(fields[i].begin(),fields[i].end(),footer.begin()+i*20);target.insert(target.end(),footer.begin(),footer.end());
 lpb_target_auxiliary_expectation_v2 aux{};aux.struct_size=sizeof(aux);aux.version=2;aux.semantic=2;aux.container=1;aux.byte_length=original.size();lpb::crypto::sha256_ctx hash;hash.update(original.data(),original.size());hash.finalize(aux.sha256);if(bad_hash)aux.sha256[0]^=1;
 lpb_target_validation_request_v2 req{};req.struct_size=sizeof(req);req.version=2;req.base={sizeof(req.base),1,6,1,0,1,0,0,0,1,2,1,0,0};req.auxiliary_count=1;req.auxiliaries=&aux;
 lpb_target_reader_v1 reader{sizeof(reader),1,target.size(),&target,read,0,0};
 struct guarded{lpb_target_validation_result_v2 result;std::array<uint8_t,80> guard;} output{};output.result.struct_size=sizeof(output.result);output.result.version=2;output.guard.fill(0xA5);
 lpb_context_options opts{sizeof(opts),LPB_NATIVE_ABI_VERSION,nullptr,nullptr,nullptr};lpb_context* context=nullptr;require(lpb_create_context(&opts,&context)==LPB_RESULT_OK,"guard context");
 const auto result=lpb_target_validate_v2(context,&req,&reader,&output.result);
 const bool intact=std::all_of(output.guard.begin(),output.guard.end(),[](uint8_t b){return b==0xA5;});

 lpb_destroy_context(context);
 require(result==LPB_RESULT_OK,"guard validation invocation");
 require(intact,"skipping two private assets must not write past public result");
 require(output.result.auxiliaries[0].semantic==2&&output.result.auxiliaries[0].byte_length==original.size(),"Original must occupy first compact result slot");
 require(output.result.auxiliaries[1].semantic==0,"unused result slot stays empty");
 require(output.result.base.rule_id==(bad_hash?125U:126U),"hash mismatch rejects, otherwise reach invalid movie after safe auxiliary validation");
 require(output.result.auxiliary_count==(bad_hash?0U:1U),"only completed inventory reports count");
}

}
int main(){
 try {
  filtered_auxiliary_guard(false);filtered_auxiliary_guard(true);
  auto* c=create();input in{c};auto reader=in.reader();auto r=request();auto out=output();
  auto* invalid_plan=reinterpret_cast<lpb_target_writer_plan*>(uintptr_t{1});
  require(lpb_target_writer_prepare_v2(nullptr,nullptr,&invalid_plan)==LPB_RESULT_AUTHORITY_VIOLATION&&!invalid_plan,"v2 null request preserves context authority priority");
  require(lpb_target_writer_prepare_v2(c,nullptr,&invalid_plan)==LPB_RESULT_INVALID_ARGUMENT&&!invalid_plan,"v2 null request rejects on live context without a plan");
  r.auxiliary_count=1;r.base.expected_auxiliary_count=1;
  require(lpb_target_validate_v2(c,&r,&reader,&out)==LPB_RESULT_INVALID_ARGUMENT&&!in.callback,"null expected aux rejected before callback");
  r=request();r.base.expected_auxiliary_count=1;out=output();
  require(lpb_target_validate_v2(c,&r,&reader,&out)==LPB_RESULT_INVALID_ARGUMENT&&!in.callback,"aux cardinality mismatch rejected before callback");
  r=request();r.primary_timestamp_present=1;r.primary_timestamp_us=0;out=output();
  require(lpb_target_validate_v2(c,&r,&reader,&out)==LPB_RESULT_OK&&out.base.structural_status==2&&!in.callback,"non-OPlus original expectation rejected");
  r=request();r.base.media_format=2;out=output();
  require(lpb_target_validate_v2(c,&r,&reader,&out)==LPB_RESULT_OK&&out.base.structural_status==3&&!in.callback,"unsupported format precise status");
  r=request();in.mode=1;out=output();
  require(lpb_target_validate_v2(c,&r,&reader,&out)==LPB_RESULT_CANCELLED&&out.base.failure_category==6,"callback cancellation propagates");
  in.mode=2;out=output();
  require(lpb_target_validate_v2(c,&r,&reader,&out)==LPB_RESULT_INTERNAL_ERROR&&out.base.failure_category==5,"oversized callback result rejects");
  in.mode=4;out=output();
  require(lpb_target_validate_v2(c,&r,&reader,&out)==LPB_RESULT_INTERNAL_ERROR&&in.reentry_rejected,"v2 writer rejects callback reentry even with null request");
  lpb_destroy_context(c);
  // Callback requests destruction; outer operation/frame own deferred lifetime.
  c=create();input destroy{c,3};reader=destroy.reader();out=output();
  require(lpb_target_validate_v2(c,&r,&reader,&out)!=LPB_RESULT_OK,"callback destroy cannot produce success");
  // c has been finalized by the returning operation, intentionally no second destroy.
  std::cout<<"Target validator v2 request and reader boundary cases passed\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
