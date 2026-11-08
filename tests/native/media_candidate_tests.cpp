#include "livephotobox_native.h"
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <array>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace {
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
int32_t LPB_CALL cancelled(void* state) { return *static_cast<bool*>(state) ? 1 : 0; }
void put32(uint8_t* p, uint32_t value) { for (unsigned i=0;i<4;++i) p[i]=static_cast<uint8_t>(value>>(24-i*8)); }
}
int main() {
    std::filesystem::path owned;
    lpb_context* context = nullptr;
    try {
        wchar_t temp[MAX_PATH]{};
        require(GetTempFileNameW(L".", L"lpc", 0, temp) != 0, "create owned fixture");
        owned = temp;
        constexpr uint32_t file_size = 32 * 1024 * 1024;
        std::array<uint8_t,24> header{0,0,0,16,'f','t','y','p','i','s','o','m',0,0,0,0,0,0,0,0,'m','d','a','t'};
        put32(header.data()+16,file_size-16);
        {
            std::ofstream output(owned, std::ios::binary | std::ios::trunc);
            output.write(reinterpret_cast<const char*>(header.data()),header.size());
            // This fake binding is media payload, never metadata ownership.
            output << "uuidvivoMediaExtInfovivo{\"com.android.camera.livephoto\":\"fake\"}";
            output.seekp(file_size-1); output.put('\0');
            require(output.good(), "write large payload fixture");
        }
        bool stop = false;
        lpb_context_options options{sizeof(options), LPB_NATIVE_ABI_VERSION, nullptr, cancelled, &stop};
        require(lpb_create_context(&options,&context)==LPB_RESULT_OK,"ABI18 context");
        const auto utf8 = std::filesystem::absolute(owned).u8string();
        const auto* path = reinterpret_cast<const char*>(utf8.c_str());
        lpb_media_candidate_v1 result{}; result.struct_size=sizeof(result); result.api_version=1;
        require(lpb_discover_media_candidate_v1(context,path,&result)==LPB_RESULT_OK,"bounded candidate discovery");
        require(result.file_size==file_size && result.bytes_read<1024*1024,"mdat must be skipped");
        require(result.flags==1 && result.identifier[0]==0 && result.protocol==0,"payload string is not pairing proof");
        result.struct_size=sizeof(result);result.api_version=2;
        require(lpb_discover_media_candidate_v2(context,path,1,&result)==LPB_RESULT_OK,"v2 selected Apple discovery");
        require(result.flags==1 && result.bytes_read<=64 && result.reserved<=4,"v2 reads box headers and skips mdat");
        require(result.identifier[0]==0,"v2 media payload cannot provide an ID");
        result.struct_size=sizeof(result);result.api_version=2;
        require(lpb_discover_media_candidate_v2(context,path,3,&result)==LPB_RESULT_INVALID_ARGUMENT,"v2 rejects eager combined capabilities");
        stop=true; result.struct_size=sizeof(result); result.api_version=1;
        require(lpb_discover_media_candidate_v1(context,path,&result)==LPB_RESULT_CANCELLED,"read cancellation");
        stop=false; result.struct_size=sizeof(result)-1; result.api_version=1;
        require(lpb_discover_media_candidate_v1(context,path,&result)==LPB_RESULT_INVALID_ARGUMENT,"reject mismatched POD size");
        lpb_destroy_context(context); context=nullptr;
        std::filesystem::remove(owned);
        std::cout << "candidate bounds, payload ownership, cancellation and ABI checks passed\n";
        return 0;
    } catch (const std::exception& error) {
        if (context) lpb_destroy_context(context);
        if (!owned.empty()) { std::error_code ignored; std::filesystem::remove(owned,ignored); }
        std::cerr << error.what() << '\n'; return 1;
    }
}
