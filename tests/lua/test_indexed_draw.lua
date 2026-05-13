-- 4 頂点 quad を 6 index で indexed draw する最小テスト。
-- vertex 重複なしで quad を成立させられることが indexed draw の動作確認になる。

local sg_io = dofile("samples/sg_io.lua")

function on_init()
   config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end

function on_frame(t)
   local vs, ver_vs = sg_io.load_text("tests/lua/test_indexed_draw.vs.slang")
   local fs, ver_fs = sg_io.load_text("tests/lua/test_indexed_draw.fs.slang")
   use_shader("sh", vs, fs, ver_vs ~ ver_fs)

   local verts = { -0.6,-0.6,  0.6,-0.6,  0.6,0.6,  -0.6,0.6 }
   use_buffer("vb", VERTEX, verts, 1)
   local indices = { 0,1,2, 0,2,3 }
   use_buffer("ib", INDEX, indices, 1)

   begin_pass({ target = main_tex, clear_color = {0.05, 0.05, 0.1, 1} })
   draw(6, { verts = "vb", indices = "ib" },
        { shader = "sh", depth = false, cull = "NONE" })
   end_pass()
end
