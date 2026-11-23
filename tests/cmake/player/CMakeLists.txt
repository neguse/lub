# Check that a project outside lub can build its own player with find_package(lub).
# cmake -S tests/cmake/player -B <out> -Dlub_DIR=<lub build directory>
cmake_minimum_required(VERSION 3.22)
project(lub_export_player C CXX)
find_package(lub REQUIRED CONFIG)
add_executable(export_player main.c)
target_link_libraries(export_player PRIVATE lub::lub_vm_player)
lub_vm_player_link_options(export_player "${CMAKE_CURRENT_BINARY_DIR}/web")
