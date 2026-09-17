cmake_minimum_required(VERSION 3.28)
project(LivePhotoBoxR6VideoDifferential LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "The R6 libav* candidate is evaluated on Windows x64/MSVC.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
# The isolated candidate links the x64-windows-static triplet, including the
# static x265 archive, so its CRT choice must match that triplet.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# This is a research-only candidate.  It is deliberately a separate manifest
# and target, so an unselected libav* experiment can never enter the product
# dependency graph or runtime package by accident.
set(LPB_R6_MINIMAL_PREFIX "" CACHE PATH "Install prefix from Invoke-MinimalLibavBuild.ps1")
set(LPB_R6_EXTERNAL_CODEC_PREFIX "" CACHE PATH "x64-windows-static prefix containing libx264/x265")

if(LPB_R6_MINIMAL_PREFIX)
  if(NOT LPB_R6_EXTERNAL_CODEC_PREFIX)
    message(FATAL_ERROR "LPB_R6_EXTERNAL_CODEC_PREFIX is required with LPB_R6_MINIMAL_PREFIX.")
  endif()
  foreach(_lpb_r6_archive avformat avcodec avutil swscale)
    if(NOT EXISTS "${LPB_R6_MINIMAL_PREFIX}/lib/${_lpb_r6_archive}.lib")
      message(FATAL_ERROR "Minimal candidate archive is missing: ${LPB_R6_MINIMAL_PREFIX}/lib/${_lpb_r6_archive}.lib")
    endif()
  endforeach()
  set(LPB_R6_INCLUDE_DIRS "${LPB_R6_MINIMAL_PREFIX}/include")
  set(LPB_R6_LINK_LIBRARIES
    "${LPB_R6_MINIMAL_PREFIX}/lib/avformat.lib"
    "${LPB_R6_MINIMAL_PREFIX}/lib/avcodec.lib"
    "${LPB_R6_MINIMAL_PREFIX}/lib/swscale.lib"
    "${LPB_R6_MINIMAL_PREFIX}/lib/avutil.lib"
    "${LPB_R6_EXTERNAL_CODEC_PREFIX}/lib/libx264.lib"
    "${LPB_R6_EXTERNAL_CODEC_PREFIX}/lib/x265-static.lib"
    secur32 ncrypt crypt32 ws2_32 advapi32 ole32 user32 bcrypt)
else()
  find_package(FFMPEG REQUIRED)
  set(LPB_R6_INCLUDE_DIRS ${FFMPEG_INCLUDE_DIRS})
  # FFmpeg's CMake helper can list swresample whenever an older installed port
  # exposed it.  This candidate packet-copies audio, so reject that unused
  # archive at the final link boundary as well as in the research manifest.
  set(_lpb_r6_remaining_libraries ${FFMPEG_LIBRARIES})
  while(_lpb_r6_remaining_libraries)
    list(POP_FRONT _lpb_r6_remaining_libraries _lpb_r6_item)
    if(_lpb_r6_item MATCHES "^(optimized|debug|general)$")
      list(POP_FRONT _lpb_r6_remaining_libraries _lpb_r6_library)
      if(NOT _lpb_r6_library MATCHES "[/\\\\]swresample\\.lib$")
        list(APPEND LPB_R6_LINK_LIBRARIES ${_lpb_r6_item} ${_lpb_r6_library})
      endif()
    else()
      list(APPEND LPB_R6_LINK_LIBRARIES ${_lpb_r6_item})
    endif()
  endwhile()
endif()

function(lpb_r6_configure_candidate_target target)
  target_include_directories(${target} PRIVATE ${LPB_R6_INCLUDE_DIRS})
  target_link_libraries(${target} PRIVATE ${LPB_R6_LINK_LIBRARIES})
  # Keep warnings-as-errors for the candidate.  FFmpeg's public headers are
  # external dependencies and MSVC emits conversion warnings inside them under W4.
  target_compile_options(${target} PRIVATE /W4 /WX /permissive- /utf-8 /external:anglebrackets /external:W0)
  target_link_options(${target} PRIVATE $<$<CONFIG:Release>:/OPT:REF> $<$<CONFIG:Release>:/OPT:ICF>)
endfunction()

add_executable(lpb_r6_libav_probe libav_probe.cpp)
lpb_r6_configure_candidate_target(lpb_r6_libav_probe)

# This DLL is a measurement target only: it has the same narrow C ABI a future
# Native integration would require, but is never referenced by product CMake.
add_library(lpb_r6_minimal_libav_backend SHARED libav_probe.cpp)
target_compile_definitions(lpb_r6_minimal_libav_backend PRIVATE LPB_R6_BACKEND_LIBRARY=1)
lpb_r6_configure_candidate_target(lpb_r6_minimal_libav_backend)

add_executable(lpb_r6_libav_backend_smoke libav_backend_smoke.cpp)
target_link_libraries(lpb_r6_libav_backend_smoke PRIVATE lpb_r6_minimal_libav_backend)
target_compile_options(lpb_r6_libav_backend_smoke PRIVATE /W4 /WX /permissive- /utf-8)
