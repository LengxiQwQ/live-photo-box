cmake_minimum_required(VERSION 3.24)
project(R4AllocationProbe LANGUAGES CXX)
if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "Offline Windows x64 diagnostic only")
endif()
set(DETOURS_SOURCE "" CACHE PATH "Verified local Microsoft Detours 4.0.1 source")
add_library(R4AllocationProbe SHARED probe.cpp
  ${DETOURS_SOURCE}/detours.cpp ${DETOURS_SOURCE}/modules.cpp
  ${DETOURS_SOURCE}/disasm.cpp ${DETOURS_SOURCE}/image.cpp
  ${DETOURS_SOURCE}/disolx86.cpp
  ${DETOURS_SOURCE}/disolx64.cpp ${DETOURS_SOURCE}/disolia64.cpp
  ${DETOURS_SOURCE}/disolarm.cpp ${DETOURS_SOURCE}/disolarm64.cpp)
target_include_directories(R4AllocationProbe PRIVATE ${DETOURS_SOURCE})
target_compile_features(R4AllocationProbe PRIVATE cxx_std_20)
target_compile_definitions(R4AllocationProbe PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(R4AllocationProbe PRIVATE /Zi)
target_link_options(R4AllocationProbe PRIVATE /DEBUG /MAP)
