// Offline diagnostic DLL only. No product build or distribution references it.
// Hooks record a fixed-capacity raw event stream, without heap allocation,
// symbolization, JSON, files or locks inside the allocation/free hooks.
#include <windows.h>
#include <tlhelp32.h>
#include <detours.h>
#include <cstdint>
#include <vector>
#include <string>

using Allocate = LONG (NTAPI*)(HANDLE, PVOID*, ULONG_PTR, PSIZE_T, ULONG, ULONG);
using Free = LONG (NTAPI*)(HANDLE, PVOID*, PSIZE_T, ULONG);
using PcBase = PVOID (NTAPI*)(PVOID, PVOID*);
static Allocate original_allocate;
static Free original_free;
static decltype(&LoadLibraryExW) original_load = LoadLibraryExW;
static PcBase pc_base;
struct Record {
    uint64_t sequence;
    uint32_t kind;
    int32_t status;
    uint64_t address, size, flags, allocation_base;
    uint64_t begin_clock, end_clock;
    uint64_t frames[12], bases[12];
    wchar_t module[260];
};
static Record* records;
static uint64_t capacity;
static volatile LONG active;
static volatile LONG in_flight;
static volatile LONG64 next_record, dropped, suppressed;
static volatile LONG64 event_clock;
struct Baseline { uint64_t address, size, allocation_base; };
static Baseline* baseline;
static uint64_t baseline_count;
// Reading a DLL's C++ thread_local can itself initialize TLS and allocate on
// a fresh codec thread. A fixed thread-ID guard works even during TLS setup.
static volatile LONG guard_slots[8192];
struct Reentry {
    volatile LONG* slot{};
    Reentry() noexcept {
        const LONG tid = static_cast<LONG>(GetCurrentThreadId());
        for (uint32_t n = 0; n < 8192; ++n) {
            auto* item = &guard_slots[(static_cast<uint32_t>(tid) + n) % 8192];
            const LONG previous = InterlockedCompareExchange(item, tid, 0);
            if (previous == tid) return;
            if (previous == 0) { slot = item; return; }
        }
        InterlockedIncrement64(&dropped);
    }
    ~Reentry() noexcept { if (slot) InterlockedExchange(slot, 0); }
    explicit operator bool() const noexcept { return slot != nullptr; }
};
static HANDLE fault_file = INVALID_HANDLE_VALUE;
static PVOID fault_handler;
static LONG CALLBACK observe_fault(PEXCEPTION_POINTERS exception) {
    if (exception->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && fault_file != INVALID_HANDLE_VALUE) {
        Reentry guard;
        const auto& error = *exception->ExceptionRecord;
        const uint64_t data[] = {error.ExceptionCode, reinterpret_cast<uint64_t>(error.ExceptionAddress),
            GetCurrentThreadId(), static_cast<uint64_t>(next_record), error.NumberParameters,
            error.ExceptionInformation[0], error.ExceptionInformation[1], exception->ContextRecord->Rip};
        DWORD written{}; WriteFile(fault_file, data, sizeof(data), &written, nullptr);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}
extern "C" __declspec(dllexport) LONG TraceFaultLog(LPCWSTR path) {
    fault_file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr);
    if (fault_file == INVALID_HANDLE_VALUE) return GetLastError();
    fault_handler = AddVectoredExceptionHandler(1, observe_fault);
    return fault_handler ? 0 : ERROR_INVALID_FUNCTION;
}

static Record* reserve(uint32_t kind) noexcept {
    const auto index = InterlockedIncrement64(&next_record) - 1;
    if (static_cast<uint64_t>(index) >= capacity) { InterlockedIncrement64(&dropped); return nullptr; }
    auto* value = &records[index];
    value->sequence = index;
    value->kind = kind;
    value->begin_clock = InterlockedIncrement64(&event_clock);
    return value;
}
static void stack(Record& value) noexcept {
    void* frames[12]{};
    const auto count = RtlCaptureStackBackTrace(2, 12, frames, nullptr);
    for (USHORT i = 0; i < count; ++i) {
        value.frames[i] = reinterpret_cast<uint64_t>(frames[i]);
        PVOID base{};
        pc_base(frames[i], &base);
        value.bases[i] = reinterpret_cast<uint64_t>(base);
    }
}
static LONG NTAPI allocate_hook(HANDLE process, PVOID* base, ULONG_PTR zero, PSIZE_T size, ULONG type, ULONG protection) {
    InterlockedIncrement(&in_flight);
    Reentry guard;
    if (!active || !guard || process != GetCurrentProcess()) {
        if (active && !guard) InterlockedIncrement64(&suppressed);
        const LONG status = original_allocate(process, base, zero, size, type, protection);
        InterlockedDecrement(&in_flight);
        return status;
    }
    auto* row = reserve(1);
    const LONG status = original_allocate(process, base, zero, size, type, protection);
    const auto completed = InterlockedIncrement64(&event_clock);
    if (row) {
        row->end_clock = completed;
        row->status = status; row->address = reinterpret_cast<uint64_t>(*base);
        row->size = *size; row->flags = type; stack(*row);
        MEMORY_BASIC_INFORMATION info{};
        if (status >= 0 && VirtualQuery(*base, &info, sizeof(info)))
            row->allocation_base = reinterpret_cast<uint64_t>(info.AllocationBase);
    }
    InterlockedDecrement(&in_flight);
    return status;
}
static LONG NTAPI free_hook(HANDLE process, PVOID* base, PSIZE_T size, ULONG type) {
    InterlockedIncrement(&in_flight);
    Reentry guard;
    if (!active || !guard || process != GetCurrentProcess()) {
        if (active && !guard) InterlockedIncrement64(&suppressed);
        const LONG status = original_free(process, base, size, type);
        InterlockedDecrement(&in_flight);
        return status;
    }
    const auto address = reinterpret_cast<uint64_t>(*base);
    const auto length = *size;
    auto* row = reserve(2);
    MEMORY_BASIC_INFORMATION info{};
    VirtualQuery(*base, &info, sizeof(info));
    const LONG status = original_free(process, base, size, type);
    const auto completed = InterlockedIncrement64(&event_clock);
    if (row) {
        row->end_clock = completed;
        row->status = status;
        row->address = status >= 0 && type == MEM_DECOMMIT ? reinterpret_cast<uint64_t>(*base) : address;
        row->size = status >= 0 && type == MEM_DECOMMIT ? *size : length;
        row->flags = type;
        row->allocation_base = reinterpret_cast<uint64_t>(info.AllocationBase);
        stack(*row); // Attribute diagnostic decommit/release, including GC/finalizers.
    }
    InterlockedDecrement(&in_flight);
    return status;
}
static HMODULE WINAPI load_hook(LPCWSTR path, HANDLE file, DWORD flags) {
    InterlockedIncrement(&in_flight);
    // Do not suppress allocations made by the loader itself.
    const auto module = original_load(path, file, flags);
    Reentry guard;
    if (active && guard && module) {
        if (auto* row = reserve(3)) {
            row->address = reinterpret_cast<uint64_t>(module);
            GetModuleFileNameW(module, row->module, 260);
            row->end_clock = InterlockedIncrement64(&event_clock);
        }
    }
    InterlockedDecrement(&in_flight);
    return module;
}
static LONG transaction(bool attach) {
    LONG status = DetourTransactionBegin();
    if (status != NO_ERROR) return status;
    std::vector<HANDLE> handles;
    handles.reserve(512);
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) { DetourTransactionAbort(); return GetLastError(); }
    THREADENTRY32 entry{sizeof(entry)};
    for (BOOL ok = Thread32First(snapshot, &entry); ok; ok = Thread32Next(snapshot, &entry)) {
        if (entry.th32OwnerProcessID != GetCurrentProcessId()) continue;
        if (entry.th32ThreadID == GetCurrentThreadId()) {
            status = DetourUpdateThread(GetCurrentThread());
            if (status != NO_ERROR) break;
            continue;
        }
        if (handles.size() == handles.capacity()) { status = ERROR_NOT_ENOUGH_MEMORY; break; }
        HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, entry.th32ThreadID);
        if (!thread) { status = GetLastError(); break; }
        handles.push_back(thread);
        status = DetourUpdateThread(thread);
        if (status != NO_ERROR) break;
    }
    CloseHandle(snapshot);
    if (status == NO_ERROR) {
        auto operation = attach ? DetourAttach : DetourDetach;
        status = operation(reinterpret_cast<PVOID*>(&original_allocate), reinterpret_cast<PVOID>(allocate_hook));
        if (status == NO_ERROR) status = operation(reinterpret_cast<PVOID*>(&original_free), reinterpret_cast<PVOID>(free_hook));
        if (status == NO_ERROR) status = operation(reinterpret_cast<PVOID*>(&original_load), reinterpret_cast<PVOID>(load_hook));
    }
    if (status == NO_ERROR) status = DetourTransactionCommit(); else DetourTransactionAbort();
    for (auto thread : handles) CloseHandle(thread);
    return status;
}
extern "C" __declspec(dllexport) LONG TraceStart(uint64_t requested) {
    if (records || requested == 0 || requested > 131072) return ERROR_INVALID_PARAMETER;
    auto ntdll = GetModuleHandleW(L"ntdll.dll");
    original_allocate = reinterpret_cast<Allocate>(GetProcAddress(ntdll, "NtAllocateVirtualMemory"));
    original_free = reinterpret_cast<Free>(GetProcAddress(ntdll, "NtFreeVirtualMemory"));
    pc_base = reinterpret_cast<PcBase>(GetProcAddress(ntdll, "RtlPcToFileHeader"));
    if (!original_allocate || !original_free || !pc_base) return ERROR_PROC_NOT_FOUND;
    capacity = requested;
    records = static_cast<Record*>(VirtualAlloc(nullptr, capacity * sizeof(Record), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!records) return GetLastError();
    baseline = static_cast<Baseline*>(VirtualAlloc(nullptr, 32768 * sizeof(Baseline), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!baseline) return GetLastError();
    Record warm{}; stack(warm);
    const LONG status = transaction(true);
    if (status != NO_ERROR) return status;
    MEMORY_BASIC_INFORMATION region{};
    uintptr_t address = 0;
    while (VirtualQuery(reinterpret_cast<void*>(address), &region, sizeof(region))) {
        if (region.State == MEM_COMMIT && region.Type == MEM_PRIVATE) {
            if (baseline_count == 32768) return ERROR_NOT_ENOUGH_MEMORY;
            baseline[baseline_count++] = {reinterpret_cast<uint64_t>(region.BaseAddress), region.RegionSize,
                reinterpret_cast<uint64_t>(region.AllocationBase)};
        }
        const auto next = reinterpret_cast<uintptr_t>(region.BaseAddress) + region.RegionSize;
        if (next <= address) return ERROR_INVALID_DATA;
        address = next;
    }
    InterlockedExchange(&active, 1);
    return 0;
}
extern "C" __declspec(dllexport) void TraceMark(int round, int stage) {
    if (auto* row = reserve(4)) {
        row->address = static_cast<uint64_t>(round); row->size = static_cast<uint64_t>(stage);
        row->end_clock = InterlockedIncrement64(&event_clock);
    }
}
extern "C" __declspec(dllexport) LONG TraceControl() {
    TraceMark(-2, 0);
    void* p = VirtualAlloc(nullptr, 2097152, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!p) return GetLastError();
    const BOOL decommit = VirtualFree(p, 1048576, MEM_DECOMMIT);
    const BOOL recommit = VirtualAlloc(p, 1048576, MEM_COMMIT, PAGE_READWRITE) == p;
    const BOOL repeat_commit = VirtualAlloc(p, 1048576, MEM_COMMIT, PAGE_READWRITE) == p;
    const BOOL release = VirtualFree(p, 0, MEM_RELEASE);
    void* reused = VirtualAlloc(p, 2097152, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    const BOOL released_reuse = reused == p && VirtualFree(reused, 0, MEM_RELEASE);
    TraceMark(-2, 1);
    return decommit && recommit && repeat_commit && release && released_reuse ? 0 : ERROR_INVALID_DATA;
}
extern "C" __declspec(dllexport) LONG TraceStop(LPCWSTR path) {
    InterlockedExchange(&active, 0);
    while (InterlockedCompareExchange(&in_flight, 0, 0) != 0) Sleep(1);
    LONG status = transaction(false);
    if (status != NO_ERROR) return status;
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError();
    const uint64_t count = static_cast<uint64_t>(next_record) < capacity ? static_cast<uint64_t>(next_record) : capacity;
    const uint64_t header[] = {0x5234564d54524143ULL, sizeof(Record), count, static_cast<uint64_t>(dropped), static_cast<uint64_t>(suppressed), capacity};
    DWORD written{};
    BOOL ok = WriteFile(file, header, sizeof(header), &written, nullptr) && written == sizeof(header);
    if (ok) ok = WriteFile(file, records, static_cast<DWORD>(count * sizeof(Record)), &written, nullptr) && written == count * sizeof(Record);
    if (ok) ok = FlushFileBuffers(file);
    CloseHandle(file);
    const auto baseline_path = std::wstring(path) + L".baseline.bin";
    file = CreateFileW(baseline_path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError();
    ok = ok && WriteFile(file, &baseline_count, sizeof(baseline_count), &written, nullptr) && written == sizeof(baseline_count);
    if (ok) ok = WriteFile(file, baseline, static_cast<DWORD>(baseline_count * sizeof(Baseline)), &written, nullptr) && written == baseline_count * sizeof(Baseline);
    if (ok) ok = FlushFileBuffers(file);
    CloseHandle(file);
    return ok ? 0 : ERROR_WRITE_FAULT;
}
