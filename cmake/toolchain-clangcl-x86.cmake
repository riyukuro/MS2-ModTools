# Cross-compile Windows x86 (32-bit) on Linux with clang-cl + lld-link against the MSVC 14.16 (VS2017 15.9)
# headers/libs of an msvc-wine install (https://github.com/mstorsjo/msvc-wine).
# Set MS2_MSVC_ROOT (environment or -D) to that install. MS2_MSVC_VC_VER / MS2_WINSDK_VER override the versions below.
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR X86)

# The environment also reaches CMake's try-compile runs, which don't see -D values.
if(MS2_MSVC_ROOT)
    set(ENV{MS2_MSVC_ROOT} "${MS2_MSVC_ROOT}")
endif()
set(MSVC_ROOT "$ENV{MS2_MSVC_ROOT}")
if(DEFINED ENV{MS2_MSVC_VC_VER})
    set(MSVC_VC "${MSVC_ROOT}/VC/Tools/MSVC/$ENV{MS2_MSVC_VC_VER}")
else()
    set(MSVC_VC "${MSVC_ROOT}/VC/Tools/MSVC/14.16.27023")
endif()
set(WINSDK "${MSVC_ROOT}/Windows Kits/10")
if(DEFINED ENV{MS2_WINSDK_VER})
    set(WINSDK_VER "$ENV{MS2_WINSDK_VER}")
else()
    set(WINSDK_VER 10.0.19041.0)
endif()

set(CMAKE_C_COMPILER clang-cl)
set(CMAKE_CXX_COMPILER clang-cl)
set(CMAKE_LINKER lld-link)
set(CMAKE_AR llvm-lib)
set(CMAKE_RC_COMPILER llvm-rc)
set(CMAKE_MT llvm-mt)
set(CMAKE_C_COMPILER_TARGET i686-pc-windows-msvc)
set(CMAKE_CXX_COMPILER_TARGET i686-pc-windows-msvc)

# Native Windows host: clang-cl reads the INCLUDE/LIB of the vcvars environment (v141 + SDK 10.0.19041), so the msvc-wine
# directories below are only added on other hosts.
if(CMAKE_HOST_WIN32)
    set(_flags "-fms-compatibility-version=19.16 --target=i686-pc-windows-msvc -D_HAS_AUTO_PTR_ETC=1")
    set(CMAKE_C_FLAGS_INIT "${_flags}")
    set(CMAKE_CXX_FLAGS_INIT "${_flags}")
    set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreadedDLL)
    return()
endif()

if(NOT MSVC_ROOT)
    message(FATAL_ERROR "MS2_MSVC_ROOT is not set: cross-compiling needs an msvc-wine install (MSVC 14.16 + Windows SDK). "
                        "Set the environment variable or pass -DMS2_MSVC_ROOT=<dir>.")
endif()
if(NOT EXISTS "${MSVC_VC}/include")
    message(FATAL_ERROR "MS2_MSVC_ROOT=${MSVC_ROOT}: no ${MSVC_VC}/include (other MSVC version? set MS2_MSVC_VC_VER)")
endif()
if(NOT EXISTS "${WINSDK}/Include/${WINSDK_VER}")
    message(FATAL_ERROR "MS2_MSVC_ROOT=${MSVC_ROOT}: no ${WINSDK}/Include/${WINSDK_VER} (other SDK version? set MS2_WINSDK_VER)")
endif()

set(_inc
    "${MSVC_VC}/include" "${MSVC_VC}/atlmfc/include"
    "${WINSDK}/Include/${WINSDK_VER}/ucrt" "${WINSDK}/Include/${WINSDK_VER}/um"
    "${WINSDK}/Include/${WINSDK_VER}/shared" "${WINSDK}/Include/${WINSDK_VER}/winrt")
set(_flags "-fms-compatibility-version=19.16 --target=i686-pc-windows-msvc -D_HAS_AUTO_PTR_ETC=1")
foreach(d ${_inc})
    string(APPEND _flags " /imsvc \"${d}\"")
endforeach()
set(CMAKE_C_FLAGS_INIT "${_flags}")
set(CMAKE_CXX_FLAGS_INIT "${_flags}")
set(CMAKE_RC_FLAGS_INIT "/I \"${MSVC_VC}/atlmfc/include\" /I \"${WINSDK}/Include/${WINSDK_VER}/um\" /I \"${WINSDK}/Include/${WINSDK_VER}/shared\"")

set(_libs "/LIBPATH:\"${MSVC_VC}/lib/x86\" /LIBPATH:\"${MSVC_VC}/atlmfc/lib/x86\" /LIBPATH:\"${WINSDK}/Lib/${WINSDK_VER}/ucrt/x86\" /LIBPATH:\"${WINSDK}/Lib/${WINSDK_VER}/um/x86\"")
set(CMAKE_EXE_LINKER_FLAGS_INIT "${_libs}")
set(CMAKE_SHARED_LINKER_FLAGS_INIT "${_libs}")
set(CMAKE_MODULE_LINKER_FLAGS_INIT "${_libs}")

set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreadedDLL)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
