#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <iostream>
#include <string>
int wmain(int argc,wchar_t** argv) {
    if(argc!=3)return 2;
    HMODULE module=LoadLibraryExW(argv[1],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!module)return 3;
    std::string symbol;
    for(const wchar_t* p=argv[2];*p;++p){if(*p>127){FreeLibrary(module);return 4;}symbol.push_back(static_cast<char>(*p));}
    using invoke=int(__cdecl*)();
    const auto run=reinterpret_cast<invoke>(GetProcAddress(module,symbol.c_str()));
    if(!run){FreeLibrary(module);return 5;}
    int result=1;
    try {result=run();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';}
    FreeLibrary(module);return result;
}
