// Research-only loader for the production-shaped candidate DLL.  This target
// proves the same UTF-16 C ABI that a future Native integration would use;
// it is not part of any product runtime path.
#include <cstdio>
#include <cwchar>
#include <windows.h>

using TranscodeVideoUtf16 = int(__cdecl*)(const wchar_t*, const wchar_t*, const wchar_t*);

int wmain(int argc, wchar_t** argv) {
    if (argc != 5) {
        std::fputs("usage: lpb_r6_libav_backend_smoke <backend.dll> <input> <output.mov|output.mp4> <h264|hevc>\n", stderr);
        return 64;
    }
    HMODULE module = LoadLibraryW(argv[1]);
    if (module == nullptr) {
        std::fwprintf(stderr, L"LoadLibraryW failed (%lu) for '%ls'.\n", GetLastError(), argv[1]);
        return 2;
    }
    const auto transcode = reinterpret_cast<TranscodeVideoUtf16>(GetProcAddress(module, "lpb_r6_transcode_video_utf16"));
    if (transcode == nullptr) {
        std::fputs("Candidate backend DLL has no lpb_r6_transcode_video_utf16 export.\n", stderr);
        FreeLibrary(module);
        return 3;
    }
    const int result = transcode(argv[2], argv[3], argv[4]);
    FreeLibrary(module);
    return result;
}
