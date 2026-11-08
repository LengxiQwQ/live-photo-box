#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include "platform/windows_filesystem.h"
#include <windows.h>
#include <span>
#include <limits>

// A view of the already-pinned read-only object. No second pathname open and
// no complete input snapshot allocation; Windows pages data on demand.
class windows_readonly_mapping {
    HANDLE mapping_{};
    const uint8_t* view_{};
    size_t size_{};
public:
    windows_readonly_mapping() = default;
    windows_readonly_mapping(const windows_readonly_mapping&) = delete;
    windows_readonly_mapping& operator=(const windows_readonly_mapping&) = delete;
    ~windows_readonly_mapping() { if(view_)UnmapViewOfFile(view_);if(mapping_)CloseHandle(mapping_); }
    bool open(const windows_readonly_source& source) noexcept {
        return open_handle(source.handle(),source.size());
    }
    // The caller retains a validated read-only file handle for the complete
    // observation lifetime, including sharing restrictions against writers.
    bool open_handle(void* handle,uint64_t length) noexcept {
        // Match the existing complete-primary validator's structural budget.
        // Paging replaces the old 32MiB heap snapshot; it is not permission
        // for unbounded serializers to allocate an arbitrary-size target.
        constexpr uint64_t primary_budget = 64ULL * 1024 * 1024;
        if(mapping_ || length==0 || length>primary_budget ||
            length>std::numeric_limits<size_t>::max())return false;
        mapping_=CreateFileMappingW(static_cast<HANDLE>(handle),nullptr,PAGE_READONLY,0,0,nullptr);
        if(!mapping_)return false;
        size_=static_cast<size_t>(length);
        view_=static_cast<const uint8_t*>(MapViewOfFile(mapping_,FILE_MAP_READ,0,0,size_));
        return view_!=nullptr;
    }
    std::span<const uint8_t> bytes() const noexcept { return {view_,view_?size_:0}; }
};
