#include "livephotobox_native.h"
#include <array>
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
lpb_context* create(){lpb_context_options o{};o.struct_size=sizeof(o);o.abi_version=16;lpb_context* c=nullptr;require(lpb_create_context(&o,&c)==LPB_RESULT_OK,"context ABI16");return c;}
}
int main(){
 try {
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
