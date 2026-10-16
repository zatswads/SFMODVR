cmake_minimum_required(VERSION 3.20)
project(a2f_bake CXX)

# A2F_SDK: the SDK's built package (<sdk>/_build/release/audio2x-sdk); CUDA_PATH: the CUDA 12.x toolkit (headers only).
set(A2F_SDK "D:/A2F/sdk/_build/release/audio2x-sdk" CACHE PATH "Audio2Face SDK package")
set(CUDA_ROOT "$ENV{CUDA_PATH}" CACHE PATH "CUDA toolkit")

set(CMAKE_CXX_STANDARD 17)
add_executable(a2f-bake a2f_bake.cpp)
target_include_directories(a2f-bake PRIVATE "${A2F_SDK}/include" "${CUDA_ROOT}/include")
target_link_libraries(a2f-bake PRIVATE "${A2F_SDK}/lib/audio2x.lib")
if(MSVC)
  target_compile_options(a2f-bake PRIVATE /W3 /utf-8 /EHsc)
endif()
