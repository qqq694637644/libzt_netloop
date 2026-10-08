cmake_minimum_required(VERSION 3.24)
if(POLICY CMP0141)
    cmake_policy(SET CMP0141 NEW)
endif()
project(libzt_netloop_native LANGUAGES C CXX)

option(NETLOOP_CSHARP_NATIVE "Build the shared libzt P/Invoke runtime used by C# NetLoop" ON)
if(NOT NETLOOP_CSHARP_NATIVE)
    message(FATAL_ERROR "Only the C# NetLoop native libzt runtime is supported.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_DEBUG_INFORMATION_FORMAT "$<$<CONFIG:Debug,RelWithDebInfo>:Embedded>")
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(ZTS_ENABLE_PINVOKE ON CACHE BOOL "" FORCE)
set(BUILD_STATIC_LIB OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIB ON CACHE BOOL "" FORCE)
set(BUILD_HOST_SELFTEST OFF CACHE BOOL "" FORCE)
set(BUILD_HOST_SELFTEST_ONLY OFF CACHE BOOL "" FORCE)
set(BUILD_HOST_EXAMPLES OFF CACHE BOOL "" FORCE)
set(ALLOW_INSTALL_TARGET OFF CACHE BOOL "" FORCE)
set(ZTS_DISABLE_CENTRAL_API ON CACHE BOOL "" FORCE)

# libzt's Windows CMake does not add ZeroTierOne/ext even though ZeroTierOne
# headers include bundled dependencies such as <nlohmann/json.hpp> from there.
include_directories("${CMAKE_CURRENT_SOURCE_DIR}/external/libzt/ext/ZeroTierOne/ext")

add_subdirectory(external/libzt EXCLUDE_FROM_ALL)

# ZeroTierOne's Phy.hpp references Metrics on Windows without including
# Metrics.hpp. Force-include it only for the vendor targets that compile
# ZeroTierOne/libzt sources.
if(MSVC)
    set(LIBZT_METRICS_HEADER
        "${CMAKE_CURRENT_SOURCE_DIR}/external/libzt/ext/ZeroTierOne/node/Metrics.hpp")
    foreach(LIBZT_TARGET zto_obj libzt_obj zto_pic zt_pic zt-static zt-shared)
        if(TARGET ${LIBZT_TARGET})
            target_compile_options(
                ${LIBZT_TARGET}
                PRIVATE "$<$<COMPILE_LANGUAGE:CXX>:/FI${LIBZT_METRICS_HEADER}>"
            )
        endif()
    endforeach()
endif()

# Current libzt CMake files still reference an old Windows SDK directory in
# their transitive link interface. Use SDK library names so MSVC resolves them
# from the active Windows SDK selected by the runner/developer environment.
if(MSVC)
    if(TARGET zt-static)
        set_property(
            TARGET zt-static
            PROPERTY INTERFACE_LINK_LIBRARIES "ws2_32;Shlwapi;iphlpapi"
        )
    endif()
    if(TARGET zt-shared)
        # libzt's pinned CMake still bakes an obsolete Windows SDK directory
        # into the shared target. Resolve SDK libraries through the active
        # VS/Windows SDK environment, the same way the proven static build does.
        set_property(
            TARGET zt-shared
            PROPERTY LINK_LIBRARIES
                "ws2_32;Shlwapi;iphlpapi;zt_pic;lwip_pic;zto_pic;natpmp_pic;miniupnpc_pic"
        )
    endif()
endif()
