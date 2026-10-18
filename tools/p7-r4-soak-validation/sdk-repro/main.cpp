// Standalone Windows SDK diagnostic. No LivePhotoBox headers, libraries, parsers,
// encoder, workspace, sidecar or managed runtime are linked or called.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <psapi.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <vector>
#include <cstdint>

using Microsoft::WRL::ComPtr;

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

static void snapshot(const char* kind, int iteration, HRESULT hr, unsigned samples) {
    DWORD handles = 0;
    GetProcessHandleCount(GetCurrentProcess(), &handles);
    PROCESS_MEMORY_COUNTERS_EX memory{};
    memory.cb = sizeof(memory);
    GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory));
    std::printf("{\"kind\":\"%s\",\"iteration\":%d,\"hr\":%ld,\"decodedSamples\":%u,\"handles\":%lu,\"semaphores\":%d,\"privateBytes\":%llu,\"tickMilliseconds\":%llu}\n",
        kind, iteration, static_cast<long>(hr), samples, static_cast<unsigned long>(handles), semaphore_count(),
        static_cast<unsigned long long>(memory.PrivateUsage), static_cast<unsigned long long>(GetTickCount64()));
    std::fflush(stdout);
}

static HRESULT decode(const wchar_t* path, bool full, unsigned& samples) {
    ComPtr<IMFAttributes> attributes;
    HRESULT hr = MFCreateAttributes(&attributes, 3);
    if (SUCCEEDED(hr)) hr = attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE);
    if (SUCCEEDED(hr)) hr = attributes->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE);
    if (SUCCEEDED(hr)) hr = attributes->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, FALSE);
    ComPtr<IMFSourceReader> reader;
    if (SUCCEEDED(hr)) hr = MFCreateSourceReaderFromURL(path, attributes.Get(), &reader);
    if (SUCCEEDED(hr)) hr = reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
    if (SUCCEEDED(hr)) hr = reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), TRUE);
    ComPtr<IMFMediaType> type;
    if (SUCCEEDED(hr)) hr = MFCreateMediaType(&type);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (SUCCEEDED(hr)) hr = reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, type.Get());
    if (FAILED(hr)) return hr;
    for (unsigned attempt = 0; attempt < 1000000U; ++attempt) {
        DWORD stream = 0, flags = 0;
        LONGLONG timestamp = 0;
        ComPtr<IMFSample> sample;
        hr = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &stream, &flags, &timestamp, &sample);
        if (FAILED(hr)) return hr;
        if (flags & MF_SOURCE_READERF_ERROR) return E_FAIL;
        if (sample) ++samples;
        if ((!full && samples) || (flags & MF_SOURCE_READERF_ENDOFSTREAM)) {
            sample.Reset();
            const HRESULT flush_hr = reader->Flush(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS));
            return samples ? flush_hr : E_FAIL;
        }
    }
    return E_FAIL;
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 5) return 2; // input, repetitions, first|full, quiet seconds
    const int repetitions = _wtoi(argv[2]);
    const int quiet = _wtoi(argv[4]);
    if (repetitions < 1 || repetitions > 1000 || quiet < 0 || quiet > 120) return 2;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return 3;
    hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(hr)) { CoUninitialize(); return 4; }
    snapshot("baseline", -1, hr, 0);
    bool passed = true;
    for (int iteration = 0; iteration < repetitions; ++iteration) {
        unsigned samples = 0;
        hr = decode(argv[1], std::wcscmp(argv[3], L"full") == 0, samples);
        snapshot("after-release", iteration, hr, samples);
        if (FAILED(hr)) { passed = false; break; }
    }
    hr = MFShutdown();
    CoUninitialize();
    snapshot("after-runtime", -1, hr, 0);
    for (int second = 0; second < quiet; ++second) {
        Sleep(1000);
        snapshot("quiet", second + 1, S_OK, 0);
    }
    return passed && SUCCEEDED(hr) ? 0 : 5;
}
