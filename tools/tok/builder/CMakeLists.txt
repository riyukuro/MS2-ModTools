# tokbuild: the 32-bit PathEngine .tok builder (see tools/tok/README.md).
#
# Needs MS2_PATHENGINE_SDK = your PathEngine SDK root (the directory holding code/externalAPI/i_pathengine.h, interface
# 18 to match the GMS2 ContentProcessing3D.dll). The SDK is used in place and never copied: the build directory gets
# small generated headers that #include the SDK files by absolute path, laid out the way the recovered client sources
# expect (pathengine-sdk/...).
cmake_minimum_required(VERSION 3.20)
project(ms2_tokbuild CXX)

set(MS2_PATHENGINE_SDK "$ENV{MS2_PATHENGINE_SDK}" CACHE PATH "PathEngine SDK root (contains code/externalAPI/i_pathengine.h); yours, not shipped")
if(NOT MS2_PATHENGINE_SDK)
    message(FATAL_ERROR "tokbuild needs your PathEngine SDK headers: -DMS2_PATHENGINE_SDK=<SDK root containing "
                        "code/externalAPI/i_pathengine.h>. Nothing PathEngine ships with this repository.")
endif()
get_filename_component(_sdk "${MS2_PATHENGINE_SDK}" ABSOLUTE)
if(NOT EXISTS "${_sdk}/code/externalAPI/i_pathengine.h")
    message(FATAL_ERROR "MS2_PATHENGINE_SDK=${MS2_PATHENGINE_SDK}: no code/externalAPI/i_pathengine.h there")
endif()
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "tokbuild must be built 32-bit (ContentProcessing3D.dll is a 32-bit DLL): on Linux use "
                        "-DCMAKE_TOOLCHAIN_FILE=cmake/toolchain-clangcl-x86.cmake, with Visual Studio use -A Win32")
endif()

get_filename_component(MS2_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../.." ABSOLUTE)
set(_pe_src "${MS2_ROOT}/glue/pathengine")   # frozen copy of the recovered client glue

# Forwarding headers (generated, build tree only). The sample headers pathengineglue.h names are forwarded when the SDK
# has them; otherwise a forward declaration of their classes is enough for what tokbuild compiles.
set(_fwd "${CMAKE_CURRENT_BINARY_DIR}/pe_include/pathengine-sdk")
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/pe_include/i_pathengine.h"
     "#pragma once\n#include \"${_sdk}/code/externalAPI/i_pathengine.h\"\n")
set(_snip "samples/Code Snippets/Building from Physics Scene Data")
foreach(_h SolidObjects.h PhysXTerrainWrapper.h CoordinateMapping.h)
    if(_h STREQUAL "SolidObjects.h")
        set(_decl "class cSolidObjects;")
    elseif(_h STREQUAL "PhysXTerrainWrapper.h")
        set(_decl "class cPhysXTerrainWrapper;")
    else()
        set(_decl "class cCoordinateMapping;")
    endif()
    if(EXISTS "${_sdk}/${_snip}/${_h}")
        file(WRITE "${_fwd}/${_snip}/${_h}" "#pragma once\n#include \"${_sdk}/${_snip}/${_h}\"\n")
    else()
        file(WRITE "${_fwd}/${_snip}/${_h}" "#pragma once\n#include \"i_pathengine.h\"\n${_decl}\n")
    endif()
endforeach()

add_executable(tokbuild
    tokbuild.cpp
    "${_pe_src}/blockconnections.cpp"   # the client's cBlockConnections, unchanged
)
target_include_directories(tokbuild PRIVATE
    "${CMAKE_CURRENT_LIST_DIR}/shim"          # stands in for the client's stdafx/hook headers
    "${CMAKE_CURRENT_BINARY_DIR}/pe_include"  # i_pathengine.h and pathengine-sdk/... forwarders
    "${_pe_src}")
target_compile_definitions(tokbuild PRIVATE _CRT_SECURE_NO_WARNINGS)
target_link_options(tokbuild PRIVATE /LARGEADDRESSAWARE)   # 4 GB under 64-bit Windows/Wine: big maps (82000000_survival) run out at 2 GB
set_property(TARGET tokbuild PROPERTY MSVC_RUNTIME_LIBRARY MultiThreaded)   # static CRT: one file, runs under Wine
if(CMAKE_CXX_COMPILER_ID STREQUAL "Clang")
    target_compile_options(tokbuild PRIVATE -Wno-microsoft-include -Wno-extra-semi)
endif()
