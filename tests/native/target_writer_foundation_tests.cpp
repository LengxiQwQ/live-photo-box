#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include "livephotobox_native.h"
#include <array>
#include <atomic>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>
#include <cstring>
#include <algorithm>
namespace fs=std::filesystem;
#define REQUIRE(x) do { if(!(x))throw std::runtime_error(std::string("line ")+std::to_string(__LINE__)+": "+#x); } while(false)
lpb_result prepare_positive(lpb_context* owner,const lpb_target_writer_request_v1* request,lpb_target_writer_plan** plan) {
    const auto result=lpb_target_writer_prepare_v1(owner,request,plan);
    if(result!=LPB_RESULT_OK || !plan || !*plan) {
        std::array<char,8192> error{};size_t required=0;
        const auto error_result=lpb_get_last_error(owner,error.data(),error.size(),&required);
        std::cerr<<"positive prepare failed: result="<<static_cast<int>(result)
            <<" error_query="<<static_cast<int>(error_result)<<" required="<<required<<" error="<<error.data()<<'\n';
        if(request&&request->artifacts)for(uint32_t i=0;i<request->artifact_count;++i)
            std::cerr<<"artifact["<<i<<"]="<<request->artifacts[i].path_utf8<<'\n';
    }
    return result;
}
struct handle {
    HANDLE value{INVALID_HANDLE_VALUE};
    explicit handle(HANDLE h=INVALID_HANDLE_VALUE):value(h){}
    ~handle(){if(value!=INVALID_HANDLE_VALUE&&value)CloseHandle(value);}
};
std::string utf8(const fs::path& path) {
    const auto s=path.u8string();return std::string(reinterpret_cast<const char*>(s.data()),s.size());
}
void path_field(char* destination,size_t capacity,const fs::path& path) {
    const auto text=utf8(fs::absolute(path));REQUIRE(text.size()<capacity);
    std::memcpy(destination,text.data(),text.size());
}
lpb_file_identity identity(const fs::path& path) {
    handle file(CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,0,nullptr));
    REQUIRE(file.value!=INVALID_HANDLE_VALUE);BY_HANDLE_FILE_INFORMATION info{};REQUIRE(GetFileInformationByHandle(file.value,&info));
    lpb_file_identity value{};value.volume_serial=info.dwVolumeSerialNumber;
    value.file_index=(static_cast<uint64_t>(info.nFileIndexHigh)<<32)|info.nFileIndexLow;
    value.file_size=(static_cast<uint64_t>(info.nFileSizeHigh)<<32)|info.nFileSizeLow;value.link_count=info.nNumberOfLinks;return value;
}
std::array<uint8_t,32> hash_file(const fs::path& path) {
    BCRYPT_ALG_HANDLE algorithm{};REQUIRE(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0);
    DWORD size=0,written=0;REQUIRE(BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&size),sizeof(size),&written,0)>=0);
    std::vector<uint8_t> object(size);BCRYPT_HASH_HANDLE hash{};
    REQUIRE(BCryptCreateHash(algorithm,&hash,object.data(),size,nullptr,0,0)>=0);
    std::ifstream file(path,std::ios::binary);REQUIRE(file.good());std::array<char,65536> bytes{};
    while(file){file.read(bytes.data(),bytes.size());const auto n=file.gcount();if(n>0)REQUIRE(BCryptHashData(hash,reinterpret_cast<PUCHAR>(bytes.data()),static_cast<ULONG>(n),0)>=0);}
    std::array<uint8_t,32> result{};REQUIRE(BCryptFinishHash(hash,result.data(),32,0)>=0);
    BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);return result;
}
std::vector<uint8_t> read_bytes(const fs::path& path) {
    std::ifstream file(path,std::ios::binary);REQUIRE(file.good());
    return {std::istreambuf_iterator<char>(file),std::istreambuf_iterator<char>()};
}
void write_bytes(const fs::path& path,const std::vector<uint8_t>& bytes) {
    // Explicit test-owned derivative under native-fixtures/native-tests.
    std::ofstream file(path,std::ios::binary|std::ios::trunc);REQUIRE(file.good());
    file.write(reinterpret_cast<const char*>(bytes.data()),static_cast<std::streamsize>(bytes.size()));REQUIRE(file.good());
}
std::vector<uint8_t> app1(const std::vector<uint8_t>& jpeg,const std::vector<uint8_t>& payload) {
    REQUIRE(jpeg.size()>2&&jpeg[0]==0xff&&jpeg[1]==0xd8&&payload.size()<=65533);
    const size_t length=payload.size()+2;
    std::vector<uint8_t> output{0xff,0xd8,0xff,0xe1,static_cast<uint8_t>(length>>8),static_cast<uint8_t>(length)};
    output.insert(output.end(),payload.begin(),payload.end());output.insert(output.end(),jpeg.begin()+2,jpeg.end());return output;
}
std::vector<uint8_t> xmp_payload(const std::string& xml) {
    constexpr char prefix[]="http://ns.adobe.com/xap/1.0/";
    std::vector<uint8_t> output(prefix,prefix+sizeof(prefix));output.insert(output.end(),xml.begin(),xml.end());return output;
}
std::vector<uint8_t> post_scan_app1(const std::vector<uint8_t>& jpeg,const std::vector<uint8_t>& payload) {
    // Construct an actual marker between the final entropy scan and EOI,
    // retaining all original scan bytes, tables and frame headers.
    REQUIRE(jpeg.size()>=4&&jpeg[0]==0xff&&jpeg[1]==0xd8);
    REQUIRE(jpeg[jpeg.size()-2]==0xff&&jpeg.back()==0xd9&&payload.size()<=65533);
    bool saw_scan=false;
    for(size_t p=2;p+1<jpeg.size();) {
        REQUIRE(jpeg[p++]==0xff);
        while(p<jpeg.size()&&jpeg[p]==0xff)++p;
        REQUIRE(p<jpeg.size());const uint8_t marker=jpeg[p++];
        if(marker==0xda){saw_scan=true;break;}
        REQUIRE(marker!=0xd9&&p+2<=jpeg.size());
        if(marker==0x01)continue;
        const size_t length=(static_cast<size_t>(jpeg[p])<<8)|jpeg[p+1];
        REQUIRE(length>=2&&length<=jpeg.size()-p);p+=length;
    }
    REQUIRE(saw_scan);
    std::vector<uint8_t> output(jpeg.begin(),jpeg.end()-2);
    const size_t length=payload.size()+2;
    output.insert(output.end(),{0xff,0xe1,static_cast<uint8_t>(length>>8),static_cast<uint8_t>(length)});
    output.insert(output.end(),payload.begin(),payload.end());output.insert(output.end(),{0xff,0xd9});return output;
}
void bind_primary(lpb_target_writer_artifact_v1& artifact,const fs::path& path) {
    artifact.expected_identity=identity(path);const auto hash=hash_file(path);std::memcpy(artifact.sha256,hash.data(),32);
    std::memset(artifact.path_utf8,0,sizeof(artifact.path_utf8));path_field(artifact.path_utf8,sizeof(artifact.path_utf8),path);
}struct fixture {
    std::array<lpb_target_writer_artifact_v1,2> artifacts{};
    lpb_target_writer_request_v1 request{};
    explicit fixture(const fs::path& directory) {
        const std::array<fs::path,2> files{directory/L"neutral.jpg",directory/L"neutral.mp4"};
        REQUIRE(fs::is_regular_file(files[0]));REQUIRE(fs::is_regular_file(files[1]));
        for(uint32_t i=0;i<2;++i){auto& a=artifacts[i];a.struct_size=sizeof(a);a.version=1;a.role=i;a.container=1;
            a.video_codec=i?LPB_VIDEO_CODEC_H264:0;a.expected_identity=identity(files[i]);
            const auto hash=hash_file(files[i]);std::memcpy(a.sha256,hash.data(),hash.size());path_field(a.path_utf8,sizeof(a.path_utf8),files[i]);}
        request.struct_size=sizeof(request);request.version=1;request.target_profile=LPB_TARGET_GOOGLE_V1;request.target_version=1;
        request.artifact_count=2;request.artifacts=artifacts.data();path_field(request.staging_directory_utf8,sizeof(request.staging_directory_utf8),directory);
    }
};
struct callbacks {
    lpb_context* context{};lpb_target_writer_plan* plan{};
    std::atomic<int> mode{};std::atomic<int> nested_stage{-1},nested_release{-1};
    HANDLE entered{},resume{};
};
thread_local bool hold_nonwriter_lease=false;
int32_t LPB_CALL cancel(void* user) {
    auto& c=*static_cast<callbacks*>(user);const int mode=c.mode.load();
    if(hold_nonwriter_lease) {SetEvent(c.entered);WaitForSingleObject(c.resume,10000);return 1;}
    if(mode==1)return 1;
    if(mode==2||mode==3||mode==4||mode==5) {
        c.nested_stage=lpb_target_writer_stage_v1(c.context,c.plan);
        c.nested_release=lpb_target_writer_release_v1(c.context,c.plan);
        if(mode==4||mode==5){SetEvent(c.entered);WaitForSingleObject(c.resume,10000);}
        if(mode==3||mode==4)lpb_destroy_context(c.context);
        return mode==3||mode==4?1:0;
    }
    return 0;
}
lpb_context* context(callbacks* callback=nullptr) {
    lpb_context_options options{};options.struct_size=sizeof(options);options.abi_version=16;
    if(callback){options.cancel_callback=cancel;options.user_data=callback;}
    lpb_context* result{};REQUIRE(lpb_create_context(&options,&result)==LPB_RESULT_OK);
    if(callback)callback->context=result;return result;
}
void exclusive_write_probe(const fs::path& path) {
    handle file(CreateFileW(path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr));
    REQUIRE(file.value!=INVALID_HANDLE_VALUE);
}
struct detached_reader {
    std::vector<uint8_t> bytes;
    static lpb_result LPB_CALL read(void* user,uint64_t offset,uint8_t* destination,uint32_t capacity,uint32_t* count) {
        const auto& bytes=static_cast<detached_reader*>(user)->bytes;*count=0;
        if(offset>bytes.size()||capacity>bytes.size()-offset)return LPB_RESULT_INVALID_ARGUMENT;
        if(capacity)std::memcpy(destination,bytes.data()+static_cast<size_t>(offset),capacity);
        *count=capacity;return LPB_RESULT_OK;
    }
};
void detached_v2_authority_tests(const fs::path& directory,fixture& source) {
    const auto auxiliary_path=directory/L"v2-original.jpg",backup=directory/L"v2-original-before-replacement.jpg";
    const std::string source_path=source.artifacts[0].path_utf8;
    const auto original=read_bytes(fs::path(std::u8string(reinterpret_cast<const char8_t*>(source_path.data()),source_path.size())));
    write_bytes(auxiliary_path,original);
    lpb_target_auxiliary_v2 auxiliary{};auxiliary.struct_size=sizeof(auxiliary);auxiliary.version=2;
    auxiliary.representation=2;auxiliary.byte_length=original.size();
    std::memcpy(auxiliary.stable_identity,"test:v2:original:0",sizeof("test:v2:original:0"));
    std::memcpy(auxiliary.owner_identity,"primary:0",sizeof("primary:0"));
    std::memcpy(auxiliary.semantic,"Original",sizeof("Original"));
    std::memcpy(auxiliary.relationship,"Original",sizeof("Original"));
    auxiliary.materialized=source.artifacts[0];bind_primary(auxiliary.materialized,auxiliary_path);
    std::memcpy(auxiliary.sha256,auxiliary.materialized.sha256,32);
    lpb_target_writer_request_v2 request{};request.struct_size=sizeof(request);request.version=2;request.base=source.request;
    std::memset(request.base.staging_directory_utf8,0,sizeof(request.base.staging_directory_utf8));
    path_field(request.base.staging_directory_utf8,sizeof(request.base.staging_directory_utf8),directory);
    request.base.target_profile=LPB_TARGET_GOOGLE_V2;request.auxiliary_count=1;request.auxiliaries=&auxiliary;
    std::memcpy(request.primary_identity,"primary:0",sizeof("primary:0"));
    auto* owner=context();lpb_target_writer_plan* plan=nullptr;
    // Real object replacement, with the old object retained so its FileId
    // cannot be recycled into the new object at the same path.
    if(fs::exists(backup))REQUIRE(fs::remove(backup));
    fs::rename(auxiliary_path,backup);write_bytes(auxiliary_path,original);
    REQUIRE(identity(auxiliary_path).file_index!=auxiliary.materialized.expected_identity.file_index);
    REQUIRE(lpb_target_writer_prepare_v2(owner,&request,&plan)==LPB_RESULT_SOURCE_CHANGED&&!plan);
    bind_primary(auxiliary.materialized,auxiliary_path);
    const auto expected_hash=hash_file(auxiliary_path);
    {
        handle writer(CreateFileW(auxiliary_path.c_str(),GENERIC_READ|GENERIC_WRITE,
            FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,0,nullptr));
        REQUIRE(writer.value!=INVALID_HANDLE_VALUE);
        REQUIRE(lpb_target_writer_prepare_v2(owner,&request,&plan)==LPB_RESULT_INTERNAL_ERROR&&!plan);
    }
    exclusive_write_probe(auxiliary_path);
    const auto files=[&](){std::vector<fs::path> names;for(const auto& entry:fs::directory_iterator(directory))names.push_back(entry.path());std::sort(names.begin(),names.end());return names;};
    const auto before=files();
    REQUIRE(lpb_target_writer_prepare_v2(owner,&request,&plan)==LPB_RESULT_OK&&plan);
    {
        handle writer(CreateFileW(auxiliary_path.c_str(),GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,
            nullptr,OPEN_EXISTING,0,nullptr));const DWORD error=GetLastError();
        REQUIRE(writer.value==INVALID_HANDLE_VALUE&&error==ERROR_SHARING_VIOLATION);
    }
    // Caller storage is mutable; issued authority must retain the prepared
    // Original identity rather than reinterpret later changes to this POD.
    std::memset(auxiliary.semantic,0,sizeof(auxiliary.semantic));std::memcpy(auxiliary.semantic,"GainMap",sizeof("GainMap"));
    std::memset(auxiliary.relationship,0,sizeof(auxiliary.relationship));std::memcpy(auxiliary.relationship,"gain-map",sizeof("gain-map"));
    REQUIRE(lpb_target_writer_stage_v1(owner,plan)==LPB_RESULT_OK);
    REQUIRE(lpb_target_writer_stage_v1(owner,plan)==LPB_RESULT_PLAN_REPLAYED);
    lpb_target_writer_output_v1 artifact{};artifact.struct_size=sizeof(artifact);artifact.version=1;
    lpb_target_writer_status_v1 status{};status.struct_size=sizeof(status);status.version=1;uint32_t count=0;
    REQUIRE(lpb_target_writer_get_outputs_v1(owner,plan,&artifact,1,&count,&status)==LPB_RESULT_OK&&count==1&&status.state==2);
    REQUIRE(artifact.byte_length<=UINT32_MAX);
    detached_reader snapshot;snapshot.bytes.resize(static_cast<size_t>(artifact.byte_length));
    REQUIRE(lpb_target_writer_read_stage_v1(owner,plan,artifact.stage_id,0,snapshot.bytes.data(),
        static_cast<uint32_t>(snapshot.bytes.size()),&count)==LPB_RESULT_OK&&count==snapshot.bytes.size());
    lpb_target_reader_v1 reader{sizeof(reader),1,snapshot.bytes.size(),&snapshot,detached_reader::read,0,0};
    lpb_target_auxiliary_expectation_v2 expected{};expected.struct_size=sizeof(expected);expected.version=2;
    expected.semantic=2;expected.container=1;expected.byte_length=original.size();std::memcpy(expected.sha256,expected_hash.data(),32);
    lpb_target_validation_request_v2 validation{};validation.struct_size=sizeof(validation);validation.version=2;
    validation.base.struct_size=sizeof(validation.base);validation.base.version=1;validation.base.target_profile=2;
    validation.base.target_version=1;validation.base.member_count=1;validation.base.expected_video_codec=2;
    validation.base.expected_auxiliary_count=1;validation.auxiliary_count=1;validation.auxiliaries=&expected;
    lpb_target_validation_result_v2 observed{};observed.struct_size=sizeof(observed);observed.version=2;
    auto* checker=context();REQUIRE(lpb_target_validate_v2(checker,&validation,&reader,&observed)==LPB_RESULT_OK);
    REQUIRE(observed.base.structural_status==1&&observed.base.generic_media_status==1&&observed.auxiliary_count==1&&observed.auxiliaries[0].semantic==2);
    lpb_destroy_context(checker);
    REQUIRE(lpb_target_writer_release_v1(owner,plan)==LPB_RESULT_OK);
    REQUIRE(lpb_target_writer_release_v1(owner,plan)==LPB_RESULT_AUTHORITY_VIOLATION);
    REQUIRE(files()==before&&hash_file(auxiliary_path)==expected_hash);exclusive_write_probe(auxiliary_path);
    lpb_destroy_context(owner);
    std::cout<<"P9R3_V2MaterializedAuthority: real replacement/share-lock/pinning/frozen auxiliary/replay/cleanup PASS\n";
}
int main(int argc,char** argv) {
    try {
        REQUIRE(argc==2);
        const std::string directory_utf8=argv[1];
        const fs::path directory=std::u8string(reinterpret_cast<const char8_t*>(directory_utf8.data()),directory_utf8.size());
        fixture f(directory);
        REQUIRE(lpb_get_abi_version()==16);REQUIRE(sizeof(lpb_target_writer_artifact_v1)==1112);
        REQUIRE(sizeof(lpb_target_writer_request_v1)==1080);REQUIRE(sizeof(lpb_target_writer_output_v1)==144);REQUIRE(sizeof(lpb_target_writer_status_v1)==32);
        auto* owner=context();auto* other=context();lpb_target_writer_plan* plan{};
        // Null context and unissued tokens cannot borrow any Writer authority.
        for(auto* invalid_context:std::array<lpb_context*,2>{nullptr,owner}) {
            for(auto* invalid_plan:std::array<lpb_target_writer_plan*,2>{nullptr,reinterpret_cast<lpb_target_writer_plan*>(1)}) {
                REQUIRE(lpb_target_writer_stage_v1(invalid_context,invalid_plan)==LPB_RESULT_AUTHORITY_VIOLATION);
                REQUIRE(lpb_target_writer_release_v1(invalid_context,invalid_plan)==LPB_RESULT_AUTHORITY_VIOLATION);
                uint32_t invalid_count=99;lpb_target_writer_status_v1 invalid_status{sizeof(invalid_status),1,0,0,0,0,0};
                REQUIRE(lpb_target_writer_get_outputs_v1(invalid_context,invalid_plan,nullptr,0,&invalid_count,&invalid_status)==LPB_RESULT_AUTHORITY_VIOLATION&&invalid_count==0);
                invalid_count=99;
                REQUIRE(lpb_target_writer_read_stage_v1(invalid_context,invalid_plan,1,0,nullptr,0,&invalid_count)==LPB_RESULT_AUTHORITY_VIOLATION&&invalid_count==0);
            }
        }
        plan=reinterpret_cast<lpb_target_writer_plan*>(1);
        REQUIRE(lpb_target_writer_prepare_v1(nullptr,&f.request,&plan)==LPB_RESULT_AUTHORITY_VIOLATION&&!plan);
        REQUIRE(lpb_target_writer_prepare_v1(owner,nullptr,&plan)==LPB_RESULT_INVALID_ARGUMENT&&!plan);
        REQUIRE(prepare_positive(owner,&f.request,&plan)==LPB_RESULT_OK&&plan);
        REQUIRE(lpb_target_writer_stage_v1(other,plan)==LPB_RESULT_AUTHORITY_VIOLATION);
        REQUIRE(lpb_target_writer_stage_v1(owner,plan)==LPB_RESULT_UNSUPPORTED);
        REQUIRE(lpb_target_writer_stage_v1(owner,plan)==LPB_RESULT_PLAN_REPLAYED);
        lpb_target_writer_status_v1 status{sizeof(status),1,0,0,0,0,0};uint32_t count=99;
        REQUIRE(lpb_target_writer_get_outputs_v1(owner,plan,nullptr,0,&count,&status)==LPB_RESULT_OK);
        REQUIRE(count==0&&status.state==3&&status.failure_category==1&&status.generation!=0);
        REQUIRE(lpb_target_writer_read_stage_v1(owner,plan,1,0,nullptr,0,&count)==LPB_RESULT_UNSUPPORTED&&count==0);
        REQUIRE(lpb_target_writer_release_v1(owner,plan)==LPB_RESULT_OK);
        REQUIRE(lpb_target_writer_release_v1(owner,plan)==LPB_RESULT_AUTHORITY_VIOLATION);
        const fs::path derivatives=directory/L"native-tests";fs::create_directories(derivatives);
        detached_v2_authority_tests(derivatives,f);
        const auto image=read_bytes(directory/L"neutral.jpg");
        const auto original_primary=f.artifacts[0];
        const std::string benign="<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\" dc:format=\"image/jpeg\"/></rdf:RDF></x:xmpmeta>";
        const std::string live="<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" GCamera:MicroVideo=\"1\"/></rdf:RDF></x:xmpmeta>";
        const auto benign_image=app1(image,xmp_payload(benign));
        // Synthetic derivatives exercise the production prepare boundary, not
        // injected failures or claims of original-camera RealSample coverage.
        const std::vector<uint8_t> empty_exif{'E','x','i','f',0,0,'I','I',42,0,8,0,0,0,0,0,0,0,0,0};
        const std::vector<uint8_t> maker_exif{'E','x','i','f',0,0,'I','I',42,0,8,0,0,0,
            1,0,0x69,0x87,4,0,1,0,0,0,26,0,0,0,0,0,0,0,
            1,0,0x7c,0x92,7,0,4,0,0,0,'A','B','C','D',0,0,0,0};
        const auto expect_rejected=[&](const wchar_t* name,const std::vector<uint8_t>& data,lpb_result expected) {
            const auto path=derivatives/name;write_bytes(path,data);bind_primary(f.artifacts[0],path);
            const auto actual=lpb_target_writer_prepare_v1(owner,&f.request,&plan);
            if(actual!=expected||plan)std::wcerr<<L"prepare rejection "<<name<<L" actual="<<actual<<L" expected="<<expected<<L" plan="<<plan<<L'\n';
            REQUIRE(actual==expected&&!plan);
            REQUIRE(read_bytes(path)==data);exclusive_write_probe(path);
        };
        expect_rejected(L"post-scan-live-xmp.jpg",post_scan_app1(image,xmp_payload(live)),LPB_RESULT_UNSUPPORTED);
        expect_rejected(L"post-scan-benign-xmp.jpg",post_scan_app1(image,xmp_payload(benign)),LPB_RESULT_UNSUPPORTED);
        expect_rejected(L"post-scan-exif.jpg",post_scan_app1(image,empty_exif),LPB_RESULT_UNSUPPORTED);
        expect_rejected(L"post-scan-makernote.jpg",post_scan_app1(image,maker_exif),LPB_RESULT_UNSUPPORTED);
        expect_rejected(L"pre-post-duplicate-xmp.jpg",post_scan_app1(benign_image,xmp_payload(benign)),LPB_RESULT_UNSUPPORTED);
        expect_rejected(L"pre-post-duplicate-exif.jpg",post_scan_app1(app1(image,empty_exif),empty_exif),LPB_RESULT_UNSUPPORTED);
        // R3 now structurally supports an ordinary opaque, correctly owned
        // MakerNote. Require actual prepare success and exact source retention;
        // the post-scan and malformed-owner rejections above/below remain.
        const auto opaque_image=app1(image,maker_exif);
        write_bytes(derivatives/L"pre-scan-makernote.jpg",opaque_image);bind_primary(f.artifacts[0],derivatives/L"pre-scan-makernote.jpg");
        REQUIRE(prepare_positive(owner,&f.request,&plan)==LPB_RESULT_OK&&plan);
        REQUIRE(lpb_target_writer_release_v1(owner,plan)==LPB_RESULT_OK);
        REQUIRE(read_bytes(derivatives/L"pre-scan-makernote.jpg")==opaque_image);
        auto missing_ifd=empty_exif;missing_ifd.resize(14);missing_ifd[10]=0;
        expect_rejected(L"missing-ifd0.jpg",app1(image,missing_ifd),LPB_RESULT_INVALID_ARGUMENT);
        auto header_ifd=empty_exif;header_ifd[10]=4;
        expect_rejected(L"header-overlap-ifd.jpg",app1(image,header_ifd),LPB_RESULT_INVALID_ARGUMENT);
        const std::vector<uint8_t> overlapping_value{'E','x','i','f',0,0,'I','I',42,0,8,0,0,0,
            1,0,0x0e,0x01,2,0,6,0,0,0,8,0,0,0,0,0,0,0};
        expect_rejected(L"ifd-value-overlap.jpg",app1(image,overlapping_value),LPB_RESULT_INVALID_ARGUMENT);
        constexpr char extension_prefix[]="http://ns.adobe.com/xmp/extension/";
        const std::vector<uint8_t> truncated_extension(extension_prefix,extension_prefix+sizeof(extension_prefix));
        expect_rejected(L"truncated-extended-xmp.jpg",app1(image,truncated_extension),LPB_RESULT_INVALID_ARGUMENT);
        const std::string entity_live="<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:g=\"http://ns.google.com/photos/1.0/camer&#97;/\" g:MicroVideo=\"1\"/></rdf:RDF></x:xmpmeta>";
        expect_rejected(L"entity-namespace-live-xmp.jpg",app1(image,xmp_payload(entity_live)),LPB_RESULT_UNSUPPORTED);
        const std::string unbound_live="<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" g:MicroVideo=\"1\"/>";
        expect_rejected(L"unbound-prefix-xmp.jpg",app1(image,xmp_payload(unbound_live)),LPB_RESULT_INVALID_ARGUMENT);
        expect_rejected(L"multiple-roots-xmp.jpg",app1(image,xmp_payload(benign+benign)),LPB_RESULT_INVALID_ARGUMENT);
        // Ordinary JPEG scans and ordinary pre-scan Exif continue to qualify.
        const auto exif_image=app1(image,empty_exif);
        write_bytes(derivatives/L"nonlive-exif.jpg",exif_image);bind_primary(f.artifacts[0],derivatives/L"nonlive-exif.jpg");
        REQUIRE(prepare_positive(owner,&f.request,&plan)==LPB_RESULT_OK&&plan);
        REQUIRE(lpb_target_writer_release_v1(owner,plan)==LPB_RESULT_OK);
        REQUIRE(read_bytes(derivatives/L"nonlive-exif.jpg")==exif_image);
        write_bytes(derivatives/L"nonlive-xmp.jpg",benign_image);bind_primary(f.artifacts[0],derivatives/L"nonlive-xmp.jpg");
        REQUIRE(prepare_positive(owner,&f.request,&plan)==LPB_RESULT_OK);
        REQUIRE(lpb_target_writer_release_v1(owner,plan)==LPB_RESULT_OK);
        REQUIRE(read_bytes(derivatives/L"nonlive-xmp.jpg")==benign_image);
        write_bytes(derivatives/L"live-xmp.jpg",app1(image,xmp_payload(live)));bind_primary(f.artifacts[0],derivatives/L"live-xmp.jpg");
        REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_UNSUPPORTED&&!plan);
        write_bytes(derivatives/L"duplicate-xmp.jpg",app1(benign_image,xmp_payload(benign)));bind_primary(f.artifacts[0],derivatives/L"duplicate-xmp.jpg");
        REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_INVALID_ARGUMENT&&!plan);
        write_bytes(derivatives/L"malformed-xml.jpg",app1(image,xmp_payload("<x:xmpmeta>")));bind_primary(f.artifacts[0],derivatives/L"malformed-xml.jpg");
        REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_INVALID_ARGUMENT&&!plan);
        const std::vector<uint8_t> bad_exif{'E','x','i','f',0,0,'I','I',42,0,0xff,0xff,0xff,0x7f};
        write_bytes(derivatives/L"malformed-exif.jpg",app1(image,bad_exif));bind_primary(f.artifacts[0],derivatives/L"malformed-exif.jpg");
        REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_INVALID_ARGUMENT&&!plan);
        write_bytes(derivatives/L"same-bytes-foreign.jpg",image);f.artifacts[0]=original_primary;
        std::memset(f.artifacts[0].path_utf8,0,sizeof(f.artifacts[0].path_utf8));path_field(f.artifacts[0].path_utf8,sizeof(f.artifacts[0].path_utf8),derivatives/L"same-bytes-foreign.jpg");
        REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_SOURCE_CHANGED&&!plan);
        f.artifacts[0]=original_primary;
        f.artifacts[1].container=LPB_VIDEO_CONTAINER_MOV;
        REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_INVALID_ARGUMENT&&!plan);f.artifacts[1].container=LPB_VIDEO_CONTAINER_MP4;
        f.artifacts[1].video_codec=LPB_VIDEO_CODEC_HEVC;
        REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_INVALID_ARGUMENT&&!plan);f.artifacts[1].video_codec=LPB_VIDEO_CODEC_H264;
        f.artifacts[1].role=0;REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_INVALID_ARGUMENT&&!plan);f.artifacts[1].role=1;
        // A fresh success supplies the previous token used by nonreuse checks.
        REQUIRE(prepare_positive(owner,&f.request,&plan)==LPB_RESULT_OK);
        REQUIRE(lpb_target_writer_release_v1(owner,plan)==LPB_RESULT_OK);        const auto previous=plan;
        f.artifacts[0].sha256[0]^=1;plan=reinterpret_cast<lpb_target_writer_plan*>(1);
        REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_SOURCE_CHANGED&&!plan);f.artifacts[0].sha256[0]^=1;
        f.artifacts[0].expected_identity.file_index^=1;
        REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_SOURCE_CHANGED&&!plan);f.artifacts[0].expected_identity.file_index^=1;
        f.request.target_profile=LPB_TARGET_APPLE;
        REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_UNSUPPORTED&&!plan);f.request.target_profile=LPB_TARGET_GOOGLE_V1;
        f.request.reserved0=1;REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_INVALID_ARGUMENT&&!plan);f.request.reserved0=0;
        f.request.cover_timestamp_us=1;REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_INVALID_ARGUMENT&&!plan);f.request.cover_timestamp_us=0;
        {handle locked(CreateFileW((directory/L"neutral.mp4").c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr));REQUIRE(locked.value!=INVALID_HANDLE_VALUE);
            REQUIRE(lpb_target_writer_prepare_v1(owner,&f.request,&plan)==LPB_RESULT_INTERNAL_ERROR&&!plan);}
        REQUIRE(prepare_positive(owner,&f.request,&plan)==LPB_RESULT_OK&&plan!=previous);
        std::array<lpb_result,2> race{};std::thread a([&]{race[0]=lpb_target_writer_stage_v1(owner,plan);});
        std::thread b([&]{race[1]=lpb_target_writer_stage_v1(owner,plan);});a.join();b.join();
        REQUIRE((race[0]==LPB_RESULT_UNSUPPORTED&&race[1]==LPB_RESULT_PLAN_REPLAYED)||(race[1]==LPB_RESULT_UNSUPPORTED&&race[0]==LPB_RESULT_PLAN_REPLAYED));
        REQUIRE(lpb_target_writer_release_v1(owner,plan)==LPB_RESULT_OK);lpb_destroy_context(other);lpb_destroy_context(owner);
        callbacks cb{};owner=context(&cb);REQUIRE(prepare_positive(owner,&f.request,&cb.plan)==LPB_RESULT_OK);
        cb.mode=1;REQUIRE(lpb_target_writer_stage_v1(owner,cb.plan)==LPB_RESULT_CANCELLED);
        REQUIRE(lpb_target_writer_stage_v1(owner,cb.plan)==LPB_RESULT_PLAN_REPLAYED);cb.mode=0;
        REQUIRE(lpb_target_writer_release_v1(owner,cb.plan)==LPB_RESULT_OK);lpb_destroy_context(owner);
        owner=context(&cb);REQUIRE(prepare_positive(owner,&f.request,&cb.plan)==LPB_RESULT_OK);cb.mode=2;
        REQUIRE(lpb_target_writer_stage_v1(owner,cb.plan)==LPB_RESULT_UNSUPPORTED);
        REQUIRE(cb.nested_stage==LPB_RESULT_AUTHORITY_VIOLATION&&cb.nested_release==LPB_RESULT_AUTHORITY_VIOLATION);
        cb.mode=0;REQUIRE(lpb_target_writer_release_v1(owner,cb.plan)==LPB_RESULT_OK);lpb_destroy_context(owner);
        // An external release during a claimed stage defers resource disposal
        // until the active operation completes, then invalidates the token.
        owner=context(&cb);REQUIRE(prepare_positive(owner,&f.request,&cb.plan)==LPB_RESULT_OK);
        {
            handle entered_release(CreateEventW(nullptr,TRUE,FALSE,nullptr)),resume_release(CreateEventW(nullptr,TRUE,FALSE,nullptr));
            cb.entered=entered_release.value;cb.resume=resume_release.value;cb.mode=5;
            std::atomic<int> result{-1};std::thread active([&]{result=lpb_target_writer_stage_v1(owner,cb.plan);});
            const DWORD entered_result=WaitForSingleObject(entered_release.value,10000);
            const lpb_result release_result=lpb_target_writer_release_v1(owner,cb.plan);
            SetEvent(resume_release.value);active.join();
            REQUIRE(entered_result==WAIT_OBJECT_0&&release_result==LPB_RESULT_OK);
            REQUIRE(result==LPB_RESULT_UNSUPPORTED);
            REQUIRE(lpb_target_writer_stage_v1(owner,cb.plan)==LPB_RESULT_AUTHORITY_VIOLATION);
            exclusive_write_probe(directory/L"neutral.jpg");cb.mode=0;lpb_destroy_context(owner);
        }
        owner=context(&cb);REQUIRE(prepare_positive(owner,&f.request,&cb.plan)==LPB_RESULT_OK);cb.mode=3;
        REQUIRE(lpb_target_writer_stage_v1(owner,cb.plan)==LPB_RESULT_CANCELLED);
        // Context is destroyed by last outer lease: no retry and no after-free API.
        cb.context=nullptr;cb.plan=nullptr;cb.mode=0;exclusive_write_probe(directory/L"neutral.jpg");exclusive_write_probe(directory/L"neutral.mp4");
        // Keep an existing non-Writer cleanup API lease alive while a Writer
        // callback requests destruction. Cancellation precedes plan lookup or
        // any sink creation; this exercises lifetime ordering, not cleanup authority.
        owner=context(&cb);REQUIRE(prepare_positive(owner,&f.request,&cb.plan)==LPB_RESULT_OK);
        {
            handle entered_legacy(CreateEventW(nullptr,TRUE,FALSE,nullptr)),resume_legacy(CreateEventW(nullptr,TRUE,FALSE,nullptr));
            cb.entered=entered_legacy.value;cb.resume=resume_legacy.value;
            std::atomic<int> legacy_result{-1};
            const auto source=utf8(directory/L"neutral.jpg"),never_output=utf8(derivatives/L"never-created.jpg");
            std::thread legacy([&]{hold_nonwriter_lease=true;size_t n=99;
                legacy_result=lpb_clean_source_protocol_with_cleanup_plan(owner,reinterpret_cast<lpb_cleanup_plan*>(1),1,
                    source.c_str(),nullptr,nullptr,never_output.c_str(),nullptr,nullptr,0,&n);hold_nonwriter_lease=false;});
            const DWORD entered_result=WaitForSingleObject(entered_legacy.value,10000);
            cb.mode=3;const auto outer_result=lpb_target_writer_stage_v1(owner,cb.plan);
            handle pinned(CreateFileW((directory/L"neutral.jpg").c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr));
            const DWORD pinned_error=GetLastError();
            SetEvent(resume_legacy.value);legacy.join();
            REQUIRE(entered_result==WAIT_OBJECT_0&&outer_result==LPB_RESULT_CANCELLED&&legacy_result==LPB_RESULT_CANCELLED);
            REQUIRE(pinned.value==INVALID_HANDLE_VALUE&&pinned_error==ERROR_SHARING_VIOLATION);
            cb.context=nullptr;cb.plan=nullptr;cb.mode=0;
            exclusive_write_probe(directory/L"neutral.jpg");exclusive_write_probe(directory/L"neutral.mp4");
            REQUIRE(!fs::exists(derivatives/L"never-created.jpg"));
        }
        owner=context(&cb);REQUIRE(prepare_positive(owner,&f.request,&cb.plan)==LPB_RESULT_OK);
        handle entered(CreateEventW(nullptr,TRUE,FALSE,nullptr)),resume(CreateEventW(nullptr,TRUE,FALSE,nullptr));
        cb.entered=entered.value;cb.resume=resume.value;cb.mode=4;std::atomic<int> stage_result{-1};
        std::thread worker([&]{stage_result=lpb_target_writer_stage_v1(owner,cb.plan);});
        REQUIRE(WaitForSingleObject(entered.value,10000)==WAIT_OBJECT_0);
        std::thread destroyer([&]{lpb_destroy_context(owner);});
        const ULONGLONG deadline=GetTickCount64()+8000;
        lpb_result gate=LPB_RESULT_OK;
        while(GetTickCount64()<deadline) {
            status={sizeof(status),1,0,0,0,0,0};
            gate=lpb_target_writer_get_outputs_v1(owner,cb.plan,nullptr,0,&count,&status);
            if(gate==LPB_RESULT_AUTHORITY_VIOLATION)break;std::this_thread::yield();
        }
        SetEvent(resume.value);worker.join();destroyer.join();REQUIRE(gate==LPB_RESULT_AUTHORITY_VIOLATION);
        REQUIRE(stage_result==LPB_RESULT_CANCELLED);cb.context=nullptr;cb.plan=nullptr;cb.mode=0;
        exclusive_write_probe(directory/L"neutral.mp4");
        const auto image_hash=hash_file(directory/L"neutral.jpg");REQUIRE(std::memcmp(image_hash.data(),f.artifacts[0].sha256,32)==0);
        const auto hash=hash_file(directory/L"neutral.mp4");REQUIRE(std::memcmp(hash.data(),f.artifacts[1].sha256,32)==0);
        std::cout<<"P9R1_TargetWriterFoundation: production identity/hash/profile/singleclaim/cancel/reentry/deferred-destroy/share-lock rows PASS\n";
        return 0;
    } catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
}
