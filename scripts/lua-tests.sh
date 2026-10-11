# tests/lua の runtime テスト (描画 golden を持たず、終了 code で合否が決まる
# もの)。scripts/native-gate.sh と scripts/apple-gate.sh が source する。
# shellcheck shell=bash
lua_runtime_tests=(
  tests/lua/test_physics_box2d.lua
  tests/lua/test_physics_box2d_phase2.lua
  tests/lua/test_physics_box2d_phase3.lua
  tests/lua/test_physics_box2d_debug.lua
  tests/lua/test_physics_box2d_joints.lua
  tests/lua/test_physics_box2d_callbacks.lua
  tests/lua/test_physics_box2d_lifetime.lua
  tests/lua/test_resource_revision.lua
  tests/lua/test_resource_handles.lua
  tests/lua/test_io_text.lua
  tests/lua/test_atlas.lua
  tests/lua/test_lubx_fixedstep_edges.lua
  tests/lua/test_lubx_mesh_reassert.lua
  tests/lua/test_lubx_game_helpers.lua
  tests/lua/test_audio.lua
  tests/lua/test_font.lua
  tests/lua/test_api_surface.lua
  tests/lua/test_rotation_convention.lua
  tests/lua/test_dispatch_after_readback.lua
  tests/lua/test_readback_on_quit.lua
  tests/lua/test_vertex_texture.lua
  tests/lua/test_arena_error_release.lua
  tests/lua/test_arena_nested_call.lua
  tests/lua/test_gfx_in_on_init.lua
  tests/lua/test_instance_count_zero.lua
  tests/lua/test_swept_ref_error.lua
  tests/lua/test_dup_binding.lua
  tests/lua/test_texture_recording_order.lua
  tests/lua/test_many_draws.lua
  tests/lua/test_unbound_bindings.lua
  tests/lua/test_compute_uniform_block.lua
  tests/lua/test_xr_inactive.lua
  tests/lua/test_math_determinism.lua
)
