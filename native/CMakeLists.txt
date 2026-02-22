cmake_minimum_required(VERSION 3.20)
project(dotnetdbg C)

add_library(dotnetdbg SHARED src/ptrace_wrapper.c)

set_target_properties(dotnetdbg PROPERTIES
    OUTPUT_NAME "dotnetdbg"
    C_VISIBILITY_PRESET hidden
    POSITION_INDEPENDENT_CODE ON
)

target_compile_options(dotnetdbg PRIVATE -fvisibility=hidden)

# Output directly to a lib/ directory at repo root for easy discovery by build.sh
set_target_properties(dotnetdbg PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../lib"
)
