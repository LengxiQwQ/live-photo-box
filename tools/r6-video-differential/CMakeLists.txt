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
find_package(FFMPEG REQUIRED)

add_executable(lpb_r6_libav_probe libav_probe.cpp)
target_include_directories(lpb_r6_libav_probe PRIVATE ${FFMPEG_INCLUDE_DIRS})
target_link_directories(lpb_r6_libav_probe PRIVATE ${FFMPEG_LIBRARY_DIRS})
target_link_libraries(lpb_r6_libav_probe PRIVATE ${FFMPEG_LIBRARIES})
# Keep warnings-as-errors for the candidate.  FFmpeg's public headers are an
# external dependency and MSVC emits conversion warnings inside them under W4.
target_compile_options(lpb_r6_libav_probe PRIVATE /W4 /WX /permissive- /utf-8 /external:anglebrackets /external:W0)
