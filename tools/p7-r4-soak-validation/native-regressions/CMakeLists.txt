cmake_minimum_required(VERSION 3.24)
project(HevcNativeRegressions LANGUAGES CXX)
add_executable(HevcNativeRegressions main.cpp
  "${LPB_SOURCE_ROOT}/LivePhotoBox.Native/src/containers/isobmff.cpp"
  "${LPB_SOURCE_ROOT}/LivePhotoBox.Native/src/containers/isobmff_video_profile.cpp"
  "${LPB_SOURCE_ROOT}/LivePhotoBox.Native/src/containers/isobmff_mebx_transplant.cpp")
target_include_directories(HevcNativeRegressions PRIVATE "${LPB_SOURCE_ROOT}/LivePhotoBox.Native/src")
target_compile_features(HevcNativeRegressions PRIVATE cxx_std_20)
target_compile_options(HevcNativeRegressions PRIVATE /W4 /WX /EHsc /utf-8)
