#include "media/compound_primary.h"
#include "media/target_writer.h"
#include "foundation/sha256_core.h"
#include <map>
#include <mutex>

namespace {
static_assert(sizeof(lpb_compound_primary_observation_v1)==56);
static_assert(sizeof(lpb_target_writer_request_v3)==1272);
static_assert(sizeof(lpb_target_validation_request_v3)==176);
std::mutex registry_mutex;
std::map<uint64_t,std::shared_ptr<lpb::media::compound_primary_proof>> registry;
bool same_identity(const lpb_file_identity& a,const lpb_file_identity& b) {
 return a.volume_serial==b.volume_serial&&a.file_index==b.file_index&&a.file_size==b.file_size&&a.link_count==b.link_count;
}
bool read_registered(lpb_context* context,const char* path,const lpb_file_identity& expected,std::vector<uint8_t>& bytes) {
 const auto p=utf8_to_path(path);
 HANDLE h=CreateFileW(p.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,
     FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
 if(h==INVALID_HANDLE_VALUE)return false;
 struct close_handle {HANDLE value;~close_handle(){CloseHandle(value);}} closer{h};
 BY_HANDLE_FILE_INFORMATION info{};if(!GetFileInformationByHandle(h,&info)||(info.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT))return false;
 lpb_file_identity actual{info.dwVolumeSerialNumber,(uint64_t(info.nFileIndexHigh)<<32)|info.nFileIndexLow,
     (uint64_t(info.nFileSizeHigh)<<32)|info.nFileSizeLow,info.nNumberOfLinks};
 if(!same_identity(actual,expected)||actual.link_count!=1||actual.file_size>32ULL*1024*1024)return false;
 bytes.resize(static_cast<size_t>(actual.file_size));size_t at=0;
 while(at<bytes.size()){DWORD n=0;const DWORD want=static_cast<DWORD>(std::min<size_t>(65536,bytes.size()-at));
  if(lpb_context_check_cancelled(context)!=LPB_RESULT_OK)return false;
  if(!ReadFile(h,bytes.data()+at,want,&n,nullptr)||n!=want)return false;at+=n;}
 return true;
}
std::array<uint8_t,32> hash(std::span<const uint8_t> bytes) {
 std::array<uint8_t,32> out{};lpb::crypto::sha256_ctx h;h.update(bytes.data(),bytes.size());h.finalize(out.data());return out;
}
}
namespace lpb::media {
std::shared_ptr<compound_primary_proof> find_compound_primary(uint64_t token) noexcept {
 try{std::scoped_lock lock(registry_mutex);const auto found=registry.find(token);return found==registry.end()?nullptr:found->second;}catch(...){return {};}
}
bool validate_compound_primary(std::span<const uint8_t> bytes,const compound_primary_proof& proof,
 target_primary_observation& primary,std::string& error) noexcept {
 try {
  if(proof.revoked||bytes.size()!=proof.byte_length||!proof.known_length||proof.known_length>=bytes.size()||
     proof.opaque_length!=bytes.size()-proof.known_length||hash(bytes)!=proof.sha256||
     hash(bytes.subspan(static_cast<size_t>(proof.known_length)))!=proof.opaque_sha256) {
   error="Compound primary identity, partition or opaque bytes differ from Native lineage";return false;
  }
  if(!observe_target_primary(bytes.first(static_cast<size_t>(proof.known_length)),primary,error)||
     target_primary_has_live_binding(primary)||primary.xmp.foreign_claim||
     (!primary.exif_payload.empty()&&observe_target_neutral_exif(primary.exif_payload)!=LPB_RESULT_OK)) {
   error="Compound primary known image graph retains a live binding or is invalid";return false;
  }
  return true;
 }catch(...){error="Compound primary observation resource failure";return false;}
}
}
lpb_result LPB_CALL lpb_compound_primary_from_cleanup_v1(lpb_context* context,lpb_cleanup_plan* handle,
 uint64_t generation,const char* stage,uint64_t* output) {
 if(output)*output=0;
 if(!context||!handle||!stage||!output||lpb_writer_callback_active(context))return LPB_RESULT_INVALID_ARGUMENT;
 lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
 try {
  lpb_source_media_facts facts{};lpb_published_artifact_record original{},created{};
  {
   std::scoped_lock lock(context->plan_mutex);
   const uint64_t token=static_cast<uint64_t>(reinterpret_cast<uintptr_t>(handle));
   auto* plan=find_cleanup_plan_locked(context,token);
   if(!plan||plan->owner_context!=context||plan->generation!=generation||plan->native_call_active||!plan->native_clean_succeeded||
      (plan->state!=lpb_plan_state::Consumed&&!(plan->state==lpb_plan_state::Claimed&&plan->managed_claim_active))||plan->compound_proof_issued) {
    set_error(context,"Compound lineage requires a successfully executed owned cleanup attempt");return LPB_RESULT_AUTHORITY_VIOLATION;
   }
   facts=plan->facts;
   if(facts.protocol!=LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO&&facts.protocol!=LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO)return LPB_RESULT_UNSUPPORTED;
   size_t originals=0,outputs=0;
   for(const auto& a:plan->artifacts)if(a.artifact_role==LPB_ARTIFACT_PRIMARY_IMAGE){original=a;++originals;}
   const auto path=utf8_to_path(stage).wstring();
   for(const auto& a:context->cleaner_staged_outputs)if(a.artifact_role==LPB_ARTIFACT_PRIMARY_IMAGE&&a.final_path==path&&!a.output_receipt_owner_token){created=a;++outputs;}
   if(originals!=1||outputs!=1){set_error(context,"Compound lineage requires unique extraction and creating-handle output records");return LPB_RESULT_AUTHORITY_VIOLATION;}
  }
  const lpb_preservation_carrier_facts* opaque=nullptr;
  for(uint32_t i=0;i<facts.preservation_carrier_count;++i) {
   const auto& c=facts.preservation_carriers[i];
   if(c.kind==LPB_PRESERVATION_CARRIER_OPAQUE_FRAGMENT&&c.source_index==0&&c.artifact_role==LPB_ARTIFACT_PRIMARY_IMAGE&&
      std::string_view(c.semantic)=="OpaqueSourcePrefix"&&std::string_view(c.owner_identity)=="primary:0"&&
      std::string_view(c.relationship)=="preserved-source-prefix") {if(opaque)return LPB_RESULT_INVALID_ARGUMENT;opaque=&c;}
  }
  if(!opaque)return LPB_RESULT_UNSUPPORTED;
  created.byte_length=created.identity.file_size;
  if(original.byte_length!=created.byte_length||facts.primary_image.file_range.offset||
     !opaque->file_range.offset||opaque->file_range.offset>=created.byte_length||
     opaque->file_range.length!=created.byte_length-opaque->file_range.offset)return LPB_RESULT_SOURCE_CHANGED;
  auto proof=std::make_shared<lpb::media::compound_primary_proof>();
  proof->identity=created.identity;proof->byte_length=created.byte_length;proof->sha256=original.sha256;
  proof->known_length=opaque->file_range.offset;proof->opaque_length=opaque->file_range.length;
  std::copy_n(opaque->sha256,32,proof->opaque_sha256.begin());proof->facts=facts;
  std::vector<uint8_t> bytes;lpb::media::target_primary_observation primary;std::string error;
  if(!read_registered(context,stage,created.identity,bytes)||!lpb::media::validate_compound_primary(bytes,*proof,primary,error)) {
   if(lpb_context_check_cancelled(context)!=LPB_RESULT_OK)return LPB_RESULT_CANCELLED;
   set_error(context,error.c_str());return LPB_RESULT_SOURCE_CHANGED;
  }
  if(!lpb_generate_unique_plan_token(proof->token))return LPB_RESULT_INTERNAL_ERROR;
  {
   std::scoped_lock lock(context->plan_mutex);
   auto* plan=find_cleanup_plan_locked(context,static_cast<uint64_t>(reinterpret_cast<uintptr_t>(handle)));
   if(!plan||plan->owner_context!=context||plan->generation!=generation||plan->native_call_active||!plan->native_clean_succeeded||
      (plan->state!=lpb_plan_state::Consumed&&!(plan->state==lpb_plan_state::Claimed&&plan->managed_claim_active))||
      plan->compound_proof_issued)return LPB_RESULT_AUTHORITY_VIOLATION;
   if(lpb_context_check_cancelled(context)!=LPB_RESULT_OK)return LPB_RESULT_CANCELLED;
   std::scoped_lock registry_lock(registry_mutex);
   if(!registry.emplace(proof->token,proof).second)return LPB_RESULT_INTERNAL_ERROR;
   plan->compound_proof_issued=true;
  }
  *output=proof->token;return LPB_RESULT_OK;
 }catch(...){return LPB_RESULT_INTERNAL_ERROR;}
}
lpb_result LPB_CALL lpb_compound_primary_release_v1(uint64_t token) {
 try{std::scoped_lock lock(registry_mutex);const auto found=registry.find(token);if(found==registry.end())return LPB_RESULT_AUTHORITY_VIOLATION;
  found->second->revoked=true;registry.erase(found);return LPB_RESULT_OK;}catch(...){return LPB_RESULT_INTERNAL_ERROR;}
}
lpb_result LPB_CALL lpb_compound_primary_inspect_v1(lpb_context* context,uint64_t token,const char* path,
 lpb_source_media_facts* output,lpb_compound_primary_observation_v1* observed) {
 if(!context||!path||!output||output->struct_size!=sizeof(*output)||!observed||observed->struct_size!=sizeof(*observed)||observed->version!=1)
  return LPB_RESULT_INVALID_ARGUMENT;
 lpb_context_operation operation(context);if(!operation.acquired())return LPB_RESULT_AUTHORITY_VIOLATION;
 try {
  const auto proof=lpb::media::find_compound_primary(token);if(!proof)return LPB_RESULT_AUTHORITY_VIOLATION;
  std::vector<uint8_t> bytes;lpb::media::target_primary_observation primary;std::string error;
  if(!read_registered(context,path,proof->identity,bytes)||!lpb::media::validate_compound_primary(bytes,*proof,primary,error)) {
   if(lpb_context_check_cancelled(context)!=LPB_RESULT_OK)return LPB_RESULT_CANCELLED;
   set_error(context,error.c_str());return LPB_RESULT_SOURCE_CHANGED;
  }
  if(proof->revoked)return LPB_RESULT_AUTHORITY_VIOLATION;
  if(lpb_context_check_cancelled(context)!=LPB_RESULT_OK)return LPB_RESULT_CANCELLED;
  *output=proof->facts;output->protocol=LPB_SOURCE_PROTOCOL_NON_LIVE;
  output->primary_image.file_range={0,proof->byte_length};output->motion_video={};output->protocol_tail_range={};
  std::memset(output->pairing_identifier,0,sizeof(output->pairing_identifier));std::memset(output->secondary_sha256,0,32);
  std::copy(proof->sha256.begin(),proof->sha256.end(),output->primary_sha256);
  *observed={sizeof(*observed),1,proof->known_length,proof->opaque_length,{}};
  std::copy(proof->opaque_sha256.begin(),proof->opaque_sha256.end(),observed->opaque_sha256);
  return LPB_RESULT_OK;
 }catch(...){return LPB_RESULT_INTERNAL_ERROR;}
}
