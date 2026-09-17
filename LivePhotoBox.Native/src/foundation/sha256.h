#pragma once

#include "foundation/sha256_core.h"
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace lpb::crypto {

bool sha256_file(HANDLE file_handle, uint8_t out_hash[32]) noexcept;
bool sha256_path(const wchar_t* path, uint8_t out_hash[32]) noexcept;

} // namespace lpb::crypto
