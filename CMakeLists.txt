cmake_minimum_required(VERSION 3.28)
project(LivePhotoBoxNative LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "R3 production build requires Windows x64/MSVC")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(LPB_NATIVE_OUTPUT_ROOT "${CMAKE_SOURCE_DIR}/artifacts/native" CACHE PATH "Native artifact root")
set(LPB_MANIFEST "${CMAKE_SOURCE_DIR}/LivePhotoBox/Package.appxmanifest")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${LPB_MANIFEST}")
file(READ "${LPB_MANIFEST}" LPB_MANIFEST_TEXT)
string(REGEX MATCH "<Identity[^>]*Version=\"([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)\"" LPB_VERSION_MATCH "${LPB_MANIFEST_TEXT}")
if(NOT LPB_VERSION_MATCH)
  message(FATAL_ERROR "Unable to read Native version from Package.appxmanifest")
endif()
set(LPB_PRODUCT_VERSION "${CMAKE_MATCH_1}")
configure_file("${CMAKE_SOURCE_DIR}/LivePhotoBox.Native/src/foundation/native_version.h.in"
  "${CMAKE_CURRENT_BINARY_DIR}/generated/livephotobox_native_version.h" @ONLY)

# Explicit groups are the build authority; adding a production source requires editing this graph.
set(LPB_PORTABLE_SOURCES
  LivePhotoBox.Native/src/containers/isobmff.cpp
  LivePhotoBox.Native/src/metadata/exif_rewrite.cpp)
set(LPB_PROTOCOL_METADATA_SOURCES
  LivePhotoBox.Native/src/containers/heif.cpp
  LivePhotoBox.Native/src/containers/mp4_strip.cpp
  LivePhotoBox.Native/src/foundation/residue_fingerprint.cpp
  LivePhotoBox.Native/src/foundation/sha256.cpp
  LivePhotoBox.Native/src/metadata/exif.cpp
  LivePhotoBox.Native/src/metadata/jpeg.cpp
  LivePhotoBox.Native/src/protocols/apple.cpp
  LivePhotoBox.Native/src/protocols/apple_mebx.cpp
  LivePhotoBox.Native/src/protocols/clean/heif_cleaner.cpp
  LivePhotoBox.Native/src/protocols/clean/jpeg_structure_cleaner.cpp
  LivePhotoBox.Native/src/protocols/clean/samsung_sef_cleaner.cpp
  LivePhotoBox.Native/src/protocols/clean/xmp_cleaner.cpp
  LivePhotoBox.Native/src/protocols/huawei.cpp
  LivePhotoBox.Native/src/protocols/samsung_sef.cpp
  LivePhotoBox.Native/src/protocols/vivo_legacy.cpp)
set(LPB_WINDOWS_PLATFORM_SOURCES
  LivePhotoBox.Native/src/foundation/context.cpp
  LivePhotoBox.Native/src/platform/windows_filesystem.cpp)
set(LPB_MEDIA_SOURCES
  LivePhotoBox.Native/src/media/image_converter.cpp
  LivePhotoBox.Native/src/media/media_api.cpp
  LivePhotoBox.Native/src/media/media_cleaner.cpp
  LivePhotoBox.Native/src/media/media_extractor.cpp
  LivePhotoBox.Native/src/media/media_inspector.cpp
  LivePhotoBox.Native/src/media/preservation_observation.cpp
  LivePhotoBox.Native/src/media/video_converter.cpp)
source_group("Portable core" FILES ${LPB_PORTABLE_SOURCES})
source_group("Protocol and metadata (Windows-bound remaining)" FILES ${LPB_PROTOCOL_METADATA_SOURCES})
source_group("Windows platform" FILES ${LPB_WINDOWS_PLATFORM_SOURCES})
source_group("Media backend" FILES ${LPB_MEDIA_SOURCES})

function(lpb_native_target target_name output_name harness)
  add_library(${target_name} SHARED
    ${LPB_PORTABLE_SOURCES} ${LPB_PROTOCOL_METADATA_SOURCES}
    ${LPB_WINDOWS_PLATFORM_SOURCES} ${LPB_MEDIA_SOURCES})
  target_include_directories(${target_name} PRIVATE
    "${CMAKE_SOURCE_DIR}/LivePhotoBox.Native/include"
    "${CMAKE_SOURCE_DIR}/LivePhotoBox.Native/src"
    "${CMAKE_CURRENT_BINARY_DIR}/generated")
  target_compile_definitions(${target_name} PRIVATE LPB_NATIVE_BUILD _WINDOWS
    $<$<CONFIG:Debug>:_DEBUG> $<$<NOT:$<CONFIG:Debug>>:NDEBUG>)
  if(harness)
    target_compile_definitions(${target_name} PRIVATE LPB_NATIVE_TEST_HARNESS)
    # MSVC 19.51 intermittently crashes in its shared type-server PDB for this
    # large test-only target; /Z7 retains symbols without that compiler server.
    set_property(TARGET ${target_name} PROPERTY MSVC_DEBUG_INFORMATION_FORMAT
      "$<$<CONFIG:Debug>:Embedded>")
    set(artifact_subdir "TestHarness/$<CONFIG>/win-x64")
  else()
    set(artifact_subdir "$<CONFIG>/win-x64")
  endif()
  target_compile_options(${target_name} PRIVATE /W4 /WX /sdl /permissive- /utf-8 /EHsc /guard:cf
    $<$<CONFIG:Release>:/O2> $<$<CONFIG:Release>:/Oi> $<$<CONFIG:Release>:/Gy> $<$<CONFIG:Release>:/GL>)
  target_link_options(${target_name} PRIVATE /guard:cf /DEBUG
    $<$<CONFIG:Release>:/OPT:REF> $<$<CONFIG:Release>:/OPT:ICF> $<$<CONFIG:Release>:/LTCG>)
  target_link_libraries(${target_name} PRIVATE bcrypt windowscodecs ole32 shlwapi
    mfplat mfreadwrite mfuuid)
  set_target_properties(${target_name} PROPERTIES
    OUTPUT_NAME "${output_name}"
    RUNTIME_OUTPUT_DIRECTORY "${LPB_NATIVE_OUTPUT_ROOT}/${artifact_subdir}"
    LIBRARY_OUTPUT_DIRECTORY "${LPB_NATIVE_OUTPUT_ROOT}/${artifact_subdir}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/import/${artifact_subdir}")
endfunction()

lpb_native_target(LivePhotoBoxNative "LivePhotoBox.Native" FALSE)
option(LPB_BUILD_TEST_HARNESS "Build a separate test-only ABI DLL" OFF)
if(LPB_BUILD_TEST_HARNESS)
  lpb_native_target(LivePhotoBoxNativeTestHarness "LivePhotoBox.Native.TestHarness" TRUE)
endif()

include(CTest)
if(BUILD_TESTING)
  add_executable(lpb_portable_io_smoke tests/native/portable_io_smoke.cpp
    LivePhotoBox.Native/src/containers/isobmff.cpp)
  target_include_directories(lpb_portable_io_smoke PRIVATE LivePhotoBox.Native/src)
  target_compile_options(lpb_portable_io_smoke PRIVATE /W4 /WX /utf-8 /EHsc)
  add_test(NAME portable_io_smoke COMMAND lpb_portable_io_smoke
    "${CMAKE_CURRENT_BINARY_DIR}/portable-io-fixture.bin")
endif()
