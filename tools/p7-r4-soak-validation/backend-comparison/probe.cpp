// Offline experiment using existing DLL exports. This adds no FFmpeg backend.
#include <windows.h>
#include <filesystem>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <chrono>
#include "livephotobox_native.h"
#include "video_sidecar_abi.h"
#include "resources.h"

namespace fs = std::filesystem;
static std::string utf8(const std::wstring& value) {
    int n=WideCharToMultiByte(CP_UTF8,0,value.c_str(),-1,nullptr,0,nullptr,nullptr);
    std::string result(static_cast<size_t>(n),'\0');
    WideCharToMultiByte(CP_UTF8,0,value.c_str(),-1,result.data(),n,nullptr,nullptr);
    result.pop_back();return result;
}
static std::string json(const char* value) {
    std::string result="\"";
    for(const unsigned char* p=reinterpret_cast<const unsigned char*>(value);*p;++p){
        if(*p=='"'||*p=='\\')result+='\\';
        if(*p>=32)result+=static_cast<char>(*p);else result+=' ';
    }
    return result+'"';
}
struct file {
    HANDLE handle{INVALID_HANDLE_VALUE};
    ~file(){close();}
    void close(){if(handle!=INVALID_HANDLE_VALUE){CloseHandle(handle);handle=INVALID_HANDLE_VALUE;}}
};
static int32_t __cdecl read(void* opaque,uint8_t* destination,uint32_t capacity,uint32_t* count) {
    DWORD actual=0;*count=0;
    if(!ReadFile(static_cast<file*>(opaque)->handle,destination,capacity,&actual,nullptr))return LPB_VIDEO_SIDECAR_IO_ERROR;
    *count=actual;return actual?LPB_VIDEO_SIDECAR_IO_OK:LPB_VIDEO_SIDECAR_IO_EOF;
}
static int32_t __cdecl write(void* opaque,const uint8_t* source,uint32_t size,uint32_t* count) {
    DWORD actual=0;*count=0;
    if(!WriteFile(static_cast<file*>(opaque)->handle,source,size,&actual,nullptr))return LPB_VIDEO_SIDECAR_IO_ERROR;
    *count=actual;return actual==size?LPB_VIDEO_SIDECAR_IO_OK:LPB_VIDEO_SIDECAR_IO_ERROR;
}
static int32_t __cdecl seek(void* opaque,int64_t offset,int32_t whence,int64_t* position) {
    if(whence<0||whence>2)return LPB_VIDEO_SIDECAR_IO_ERROR;
    LARGE_INTEGER distance{},actual{};distance.QuadPart=offset;
    if(!SetFilePointerEx(static_cast<file*>(opaque)->handle,distance,&actual,static_cast<DWORD>(whence)))return LPB_VIDEO_SIDECAR_IO_ERROR;
    *position=actual.QuadPart;return LPB_VIDEO_SIDECAR_IO_OK;
}
static int32_t __cdecl size(void* opaque,uint64_t* value) {
    LARGE_INTEGER actual{};
    if(!GetFileSizeEx(static_cast<file*>(opaque)->handle,&actual)||actual.QuadPart<0)return LPB_VIDEO_SIDECAR_IO_ERROR;
    *value=static_cast<uint64_t>(actual.QuadPart);return LPB_VIDEO_SIDECAR_IO_OK;
}
static int32_t __cdecl cancelled(void*,uint32_t* value){*value=0;return LPB_VIDEO_SIDECAR_IO_OK;}

int wmain(int argc,wchar_t** argv) {
    // backend(mf|libav), DLL, source, fresh output directory, target, container,
    // actual MEBX flag, measured repetitions, post-run quiet seconds.
    if(argc!=10)return 2;
    const bool libav=std::wcscmp(argv[1],L"libav")==0;
    const bool hevc=std::wcscmp(argv[5],L"hevc")==0;
    const bool mov=std::wcscmp(argv[6],L"mov")==0;
    const int mebx=_wtoi(argv[7]),iterations=_wtoi(argv[8]),quiet=_wtoi(argv[9]);
    if(iterations<1||iterations>200||quiet<0||quiet>120)return 2;
    fs::path dll=fs::absolute(argv[2]),source=fs::absolute(argv[3]),dest=fs::absolute(argv[4]);
    if(!CreateDirectoryW(dest.c_str(),nullptr))return 3;
    HMODULE module=LoadLibraryExW(dll.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!module)return 4;
    auto query=reinterpret_cast<lpb_video_sidecar_query_fn_v1>(GetProcAddress(module,"lpb_video_sidecar_query_abi_v1"));
    auto transcode=reinterpret_cast<lpb_video_sidecar_transcode_fn_v1>(GetProcAddress(module,"lpb_video_sidecar_transcode_v1"));
    auto create=reinterpret_cast<decltype(&lpb_create_context)>(GetProcAddress(module,"lpb_create_context"));
    auto destroy=reinterpret_cast<decltype(&lpb_destroy_context)>(GetProcAddress(module,"lpb_destroy_context"));
    auto native=reinterpret_cast<decltype(&lpb_transcode_video_v2)>(GetProcAddress(module,"lpb_transcode_video_v2"));
    auto error=reinterpret_cast<decltype(&lpb_get_last_error)>(GetProcAddress(module,"lpb_get_last_error"));
    if(libav){
        if(!query||!transcode)return 5;
        lpb_video_sidecar_abi_info_v1 info{};info.struct_size=sizeof(info);
        const int status=query(&info);
        std::printf("{\"kind\":\"sidecar-identity\",\"status\":%d,\"major\":%u,\"minor\":%u,\"capabilities\":%u,\"maximumDepth\":%u,\"version\":%s,\"avcodec\":%u,\"avformat\":%u}\n",status,info.abi_major,info.abi_minor,info.capability_flags,info.maximum_pixel_depth,json(info.library_version).c_str(),info.avcodec_version,info.avformat_version);
        if(status||info.abi_major!=LPB_VIDEO_SIDECAR_ABI_MAJOR)return 6;
    }else if(!create||!destroy||!native||!error)return 5;
    resources("baseline",-1);
    unsigned successful=0,failed=0;
    for(int iteration=0;iteration<iterations+3;++iteration){
        const fs::path output=dest/(std::to_wstring(iteration)+(mov?L".mov":L".mp4"));
        const ULONGLONG start=GetTickCount64();
        int status=0;
        if(libav){
            lpb_video_sidecar_result_v1 result{};result.struct_size=sizeof(result);
            {
                file input,staging;
                input.handle=CreateFileW(source.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
                staging.handle=CreateFileW(output.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
                if(input.handle==INVALID_HANDLE_VALUE||staging.handle==INVALID_HANDLE_VALUE)return 7;
                lpb_video_sidecar_io_v1 io{};io.struct_size=sizeof(io);
                io.input_context=&input;io.input_read=read;io.input_seek=seek;io.input_size=size;
                io.output_context=&staging;io.output_write=write;io.output_seek=seek;io.output_size=size;io.is_cancelled=cancelled;
                lpb_video_sidecar_request_v1 request{};request.struct_size=sizeof(request);
                request.target_codec=hevc?LPB_VIDEO_SIDECAR_CODEC_HEVC:LPB_VIDEO_SIDECAR_CODEC_H264;
                request.target_container=mov?LPB_VIDEO_SIDECAR_CONTAINER_MOV:LPB_VIDEO_SIDECAR_CONTAINER_MP4;
                request.crf=23;request.flags=mebx?LPB_VIDEO_SIDECAR_REQUEST_NATIVE_TRANSPLANTS_MEBX:0;
                status=transcode(&request,&io,&result);
                FlushFileBuffers(staging.handle);
            }
            std::printf("{\"kind\":\"conversion\",\"iteration\":%d,\"warmup\":%s,\"status\":%d,\"resultStatus\":%d,\"libraryError\":%d,\"depth\":%u,\"frames\":%llu,\"audioPackets\":%llu,\"encoder\":%s,\"color\":[%d,%d,%d,%d],\"milliseconds\":%llu}\n",iteration,iteration<3?"true":"false",status,result.status,result.library_error,result.output_pixel_depth,static_cast<unsigned long long>(result.video_frames),static_cast<unsigned long long>(result.audio_packets),json(result.encoder_name).c_str(),result.color_primaries,result.color_transfer,result.color_matrix,result.color_range,static_cast<unsigned long long>(GetTickCount64()-start));
        }else{
            lpb_context* context=nullptr;
            status=create(nullptr,&context);if(status||!context)return 8;
            lpb_video_backend_diagnostics diagnostic{};diagnostic.struct_size=sizeof(diagnostic);
            status=native(context,utf8(source.wstring()).c_str(),utf8(output.wstring()).c_str(),mov?LPB_VIDEO_CONTAINER_MOV:LPB_VIDEO_CONTAINER_MP4,hevc?LPB_VIDEO_CODEC_HEVC:LPB_VIDEO_CODEC_H264,23,&diagnostic);
            char reason[2048]{};size_t required=0;error(context,reason,sizeof(reason),&required);
            destroy(context);
            std::printf("{\"kind\":\"conversion\",\"iteration\":%d,\"warmup\":%s,\"status\":%d,\"backend\":%d,\"hardware\":%d,\"fallback\":%d,\"encoder\":%s,\"reason\":%s,\"milliseconds\":%llu}\n",iteration,iteration<3?"true":"false",status,static_cast<int>(diagnostic.backend),static_cast<int>(diagnostic.hardware_mode),diagnostic.hardware_fallback_occurred,json(diagnostic.selected_encoder).c_str(),json(reason).c_str(),static_cast<unsigned long long>(GetTickCount64()-start));
        }
        if(!status)++successful;else ++failed;
        const bool retain=!status&&(iteration==3||iteration==iterations+2);
        // These are exclusively CREATE_NEW files in a freshly created offline
        // experiment directory, never a source or published user artifact.
        if(!retain&&fs::exists(output)&&!DeleteFileW(output.c_str()))return 9;
        resources(iteration<3?"warmup":"after-release",iteration);
    }
    resources("before-module-unload",-1);
    FreeLibrary(module);
    resources("after-module-unload",-1);
    for(int second=0;second<quiet;++second){Sleep(1000);resources("quiet",second+1);}
    std::printf("{\"kind\":\"complete\",\"successes\":%u,\"failures\":%u,\"productionDispatchChanged\":false}\n",successful,failed);
    return 0; // Operational failures are explicit rows, not hidden process success.
}
