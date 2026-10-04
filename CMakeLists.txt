cmake_minimum_required(VERSION 3.24)
if(POLICY CMP0141)
    cmake_policy(SET CMP0141 NEW)
endif()
project(libzt_netloop LANGUAGES C CXX)

if(NOT WIN32)
    message(FATAL_ERROR "libzt_netloop currently supports Windows only")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_DEBUG_INFORMATION_FORMAT "$<$<CONFIG:Debug,RelWithDebInfo>:Embedded>")
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(NETLOOP_VERSION "0.1.0" CACHE STRING "libzt_netloop version")
option(NETLOOP_CSHARP_NATIVE "Build only the shared libzt P/Invoke runtime used by the C# rewrite" OFF)

if(NETLOOP_CSHARP_NATIVE)
    set(ZTS_ENABLE_PINVOKE ON CACHE BOOL "" FORCE)
    set(BUILD_STATIC_LIB OFF CACHE BOOL "" FORCE)
    set(BUILD_SHARED_LIB ON CACHE BOOL "" FORCE)
else()
    set(BUILD_STATIC_LIB ON CACHE BOOL "" FORCE)
    set(BUILD_SHARED_LIB OFF CACHE BOOL "" FORCE)
endif()
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
# Metrics.hpp. Force-include it only for the vendor C++ targets that compile
# ZeroTierOne/libzt sources; keep our own targets free of this workaround.
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

if(NETLOOP_CSHARP_NATIVE)
    return()
endif()

add_library(netloop_common STATIC
    src/common.cpp
    src/asio_relay.cpp
)
target_include_directories(netloop_common
    PUBLIC
        src
        external/libzt/include
    PRIVATE
        external/asio/include
)
target_link_libraries(netloop_common
    PUBLIC
        zt-static
        ws2_32
)
target_compile_definitions(netloop_common PUBLIC ADD_EXPORTS=1)
target_compile_definitions(
    netloop_common
    PRIVATE
        ASIO_STANDALONE
        WIN32_LEAN_AND_MEAN
        NOMINMAX
)
target_compile_options(netloop_common PRIVATE /W4 /permissive-)

add_executable(zt_netloop_client src/client.cpp)
target_link_libraries(zt_netloop_client PRIVATE netloop_common)
target_compile_options(zt_netloop_client PRIVATE /W4 /permissive-)
target_compile_definitions(zt_netloop_client PRIVATE NETLOOP_VERSION="${NETLOOP_VERSION}")

add_executable(zt_netloop_server src/server.cpp)
target_link_libraries(zt_netloop_server PRIVATE netloop_common)
target_compile_options(zt_netloop_server PRIVATE /W4 /permissive-)
target_compile_definitions(zt_netloop_server PRIVATE NETLOOP_VERSION="${NETLOOP_VERSION}")

set_target_properties(
    zt_netloop_client
    zt_netloop_server
    PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
