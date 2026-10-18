#pragma once
#include <windows.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <cstdint>
#include <vector>
#include <cstring>
struct handle_entry {
    uintptr_t object, process, handle;
    ULONG access;
    USHORT creator, type;
    ULONG attributes, reserved;
};
static_assert(sizeof(handle_entry) == 40);

static int semaphore_count() {
    HANDLE probe = CreateSemaphoreW(nullptr, 0, 1, nullptr);
    if (!probe) return -1;
    using query_fn = LONG (NTAPI*)(ULONG, void*, ULONG, ULONG*);
    auto query = reinterpret_cast<query_fn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation"));
    int result = -1;
    if (query) {
        std::vector<uint8_t> bytes(1024U * 1024U);
        ULONG required = 0;
        LONG status = query(64U, bytes.data(), static_cast<ULONG>(bytes.size()), &required);
        if (static_cast<ULONG>(status) == 0xC0000004U && required < 64U * 1024U * 1024U) {
            bytes.resize(static_cast<size_t>(required) + 65536U);
            status = query(64U, bytes.data(), static_cast<ULONG>(bytes.size()), &required);
        }
        if (status == 0 && bytes.size() >= 16U) {
            uintptr_t count = 0;
            std::memcpy(&count, bytes.data(), sizeof(count));
            if (count <= (bytes.size() - 16U) / sizeof(handle_entry)) {
                USHORT semaphore_type = 0;
                for (uintptr_t i = 0; i < count; ++i) {
                    handle_entry row{};
                    std::memcpy(&row, bytes.data() + 16U + i * sizeof(row), sizeof(row));
                    if (row.process == GetCurrentProcessId() && row.handle == reinterpret_cast<uintptr_t>(probe)) semaphore_type = row.type;
                }
                if (semaphore_type) {
                    result = -1; // Exclude our own probe.
                    for (uintptr_t i = 0; i < count; ++i) {
                        handle_entry row{};
                        std::memcpy(&row, bytes.data() + 16U + i * sizeof(row), sizeof(row));
                        if (row.process == GetCurrentProcessId() && row.type == semaphore_type) ++result;
                    }
                }
            }
        }
    }
    CloseHandle(probe);
    return result;
}


static void resources(const char* phase,int iteration) {
 DWORD handles=0;GetProcessHandleCount(GetCurrentProcess(),&handles);
 PROCESS_MEMORY_COUNTERS_EX memory{};memory.cb=sizeof(memory);
 GetProcessMemoryInfo(GetCurrentProcess(),reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory),sizeof(memory));
 unsigned threads=0;HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);THREADENTRY32 entry{};entry.dwSize=sizeof(entry);
 if(snap!=INVALID_HANDLE_VALUE){if(Thread32First(snap,&entry)){do{if(entry.th32OwnerProcessID==GetCurrentProcessId())++threads;}while(Thread32Next(snap,&entry));}CloseHandle(snap);}
 std::printf("{\"kind\":\"resources\",\"phase\":\"%s\",\"iteration\":%d,\"tick\":%llu,\"handles\":%lu,\"semaphores\":%d,\"threads\":%u,\"privateBytes\":%llu,\"workingSet\":%llu}\n",phase,iteration,static_cast<unsigned long long>(GetTickCount64()),static_cast<unsigned long>(handles),semaphore_count(),threads,static_cast<unsigned long long>(memory.PrivateUsage),static_cast<unsigned long long>(memory.WorkingSetSize));std::fflush(stdout);
}
