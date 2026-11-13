# lub の外の project が、find_package(lub) で自前の player を作る検査。
# cmake -S tests/cmake/player -B <out> -Dlub_DIR=<lub の build のディレクトリ>
cmake_minimum_required(VERSION 3.22)
project(lub_export_player C CXX)
find_package(lub REQUIRED CONFIG)
add_executable(export_player main.c)
target_link_libraries(export_player PRIVATE lub::lub_vm_player)
lub_vm_player_link_options(export_player "${CMAKE_CURRENT_BINARY_DIR}/web")
