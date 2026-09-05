-- lub の公式 script API 面。
-- C runtime が expose する flat global (begin_pass, phys2d_*, ...) を
-- namespace table に組み立てる。boot.lua が entry require の前に読み込むため、
-- 全 authoring 言語 (raw Lua / Haxe / TinyC#) が同じ面を見る。
-- 末尾の `lub` table が正の入口で、小文字の namespace (lub.gfx.begin_pass) が
-- C# (tcs) と raw Lua の面。PascalCase の global (Gfx.begin_pass) と
-- lub.Gfx は Haxe extern の emit 形用 alias で、実体は同一 table。
-- Phys2d / Phys3d / Ui / Audio / Font / Host は flat global 名 (phys2d_world)
-- と短名 (world) の両方を持つ。C# は短名側に落ちる。

Lub = { config = config, quit = quit }

Gfx = {
	begin_pass = begin_pass,
	end_pass = end_pass,
	use_shader = use_shader,
	use_shader_compute = use_shader_compute,
	use_buffer = use_buffer,
	use_texture = use_texture,
	draw = draw,
	dispatch = dispatch,
	readback = readback,
	main_tex = main_tex,
	size = gfx_size,
	VERTEX = VERTEX,
	INDEX = INDEX,
	UNIFORM = UNIFORM,
	STORAGE = STORAGE,
	RGBA8 = RGBA8,
	R8 = R8,
	RG8 = RG8,
	R16F = R16F,
	RG16F = RG16F,
	R32F = R32F,
	RGBA16F = RGBA16F,
	RGBA32F = RGBA32F,
	DEPTH16 = DEPTH16,
	DEPTH24_STENCIL8 = DEPTH24_STENCIL8,
	DEPTH32F = DEPTH32F,
	CLEAR = CLEAR,
	LOAD = LOAD,
	DONTCARE = DONTCARE,
	DONT_CARE = DONTCARE,
	STORE = STORE,
	NONE = NONE,
	ALPHA = ALPHA,
	ADDITIVE = ADDITIVE,
	MULTIPLY = MULTIPLY,
	BACK = BACK,
	FRONT = FRONT,
	TRIANGLES = TRIANGLES,
	TRIANGLE_STRIP = TRIANGLE_STRIP,
	LINES = LINES,
	LINE_STRIP = LINE_STRIP,
	POINTS = POINTS,
	LINEAR = LINEAR,
	NEAREST = NEAREST,
	REPEAT = REPEAT,
	CLAMP = CLAMP,
}

do
	local P = {}
	for _, n in ipairs({
		"world",
		"begin",
		"world_info",
		"body",
		"box",
		"circle",
		"capsule",
		"segment",
		"polygon",
		"chain",
		"chain_segments",
		"joint",
		"joint_info",
		"joint_force",
		"joint_torque",
		"joint_angle",
		"joint_translation",
		"joint_speed",
		"joint_length",
		"joint_motor_force",
		"joint_motor_torque",
		"joint_set_motor",
		"joint_set_limit",
		"joint_set_spring",
		"joint_set_target",
		"step",
		"pose",
		"velocity",
		"mass",
		"center",
		"world_point",
		"local_point",
		"velocity_at",
		"body_shapes",
		"body_joints",
		"body_contacts",
		"shape_test_point",
		"shape_raycast",
		"shape_closest_point",
		"shape_aabb",
		"shape_info",
		"shape_set_material",
		"shape_set_filter",
		"shape_set_events",
		"contacts",
		"body_events",
		"sensors",
		"raycast",
		"overlap_aabb",
		"shape_cast",
		"cast_mover",
		"collide_mover",
		"explode",
		"debug",
		"profile",
		"counters",
		"add_force",
		"add_force_center",
		"add_impulse",
		"add_impulse_center",
		"add_torque",
		"add_angular_impulse",
		"set_velocity",
		"teleport",
		"set_target",
		"set_mass_data",
	}) do
		P["phys2d_" .. n] = _G["phys2d_" .. n]
		P[n] = _G["phys2d_" .. n]
	end
	P.STATIC = STATIC
	P.KINEMATIC = KINEMATIC
	P.DYNAMIC = DYNAMIC
	Phys2d = P
end

do
	local P = {}
	for _, n in ipairs({
		"world",
		"begin",
		"world_info",
		"body",
		"sphere",
		"box",
		"capsule",
		"cylinder",
		"cone",
		"hull",
		"mesh",
		"height_field",
		"compound",
		"joint",
		"joint_info",
		"joint_force",
		"joint_torque",
		"joint_angle",
		"joint_translation",
		"joint_speed",
		"joint_length",
		"joint_motor_force",
		"joint_motor_torque",
		"joint_set_motor",
		"joint_set_limit",
		"joint_set_spring",
		"joint_set_target",
		"step",
		"pose",
		"velocity",
		"mass",
		"center",
		"world_point",
		"local_point",
		"velocity_at",
		"body_shapes",
		"body_joints",
		"body_contacts",
		"shape_raycast",
		"shape_closest_point",
		"shape_aabb",
		"shape_info",
		"shape_set_material",
		"shape_set_filter",
		"shape_set_events",
		"contacts",
		"body_events",
		"sensors",
		"joint_events",
		"raycast",
		"overlap_aabb",
		"overlap_shape",
		"shape_cast",
		"cast_mover",
		"collide_mover",
		"profile",
		"counters",
		"add_force",
		"add_force_center",
		"add_impulse",
		"add_impulse_center",
		"add_torque",
		"add_angular_impulse",
		"set_velocity",
		"teleport",
		"set_target",
	}) do
		P["phys3d_" .. n] = _G["phys3d_" .. n]
		P[n] = _G["phys3d_" .. n]
	end
	P.STATIC = STATIC
	P.KINEMATIC = KINEMATIC
	P.DYNAMIC = DYNAMIC
	Phys3d = P
end

Input = {
	key_down = key_down,
	key_pressed = key_pressed,
	key_released = key_released,
	mouse_delta = mouse_delta,
	mouse_down = mouse_down,
	mouse_pressed = mouse_pressed,
	mouse_released = mouse_released,
	mouse_pos = mouse_pos,
}

Profiler = {
	enabled = profile_enabled,
	begin_scope = profile_begin,
	end_scope = profile_end,
	reset = profile_reset,
	report = profile_report,
}

Mesh = { surface_nets = surface_nets, sdf_mesh = sdf_mesh }

Font = {
	font_metrics = font_metrics,
	font_glyph = font_glyph,
	font_glyph_mesh = font_glyph_mesh,
	font_kern = font_kern,
	metrics = font_metrics,
	glyph = font_glyph,
	glyph_mesh = font_glyph_mesh,
	kern = font_kern,
}

Ui = {
	ui_render = ui_render,
	ui_begin = ui_begin,
	ui_end = ui_end,
	ui_text = ui_text,
	ui_button = ui_button,
	ui_checkbox = ui_checkbox,
	ui_slider_float = ui_slider_float,
	ui_slider_int = ui_slider_int,
	ui_drag_float = ui_drag_float,
	ui_color_edit3 = ui_color_edit3,
	ui_separator = ui_separator,
	ui_same_line = ui_same_line,
	ui_tree_node = ui_tree_node,
	ui_tree_pop = ui_tree_pop,
	ui_set_next_window = ui_set_next_window,
	ui_want_capture_mouse = ui_want_capture_mouse,
	render = ui_render,
	begin_window = ui_begin,
	end_window = ui_end,
	text = ui_text,
	button = ui_button,
	checkbox = ui_checkbox,
	slider_float = ui_slider_float,
	slider_int = ui_slider_int,
	drag_float = ui_drag_float,
	color_edit3 = ui_color_edit3,
	separator = ui_separator,
	same_line = ui_same_line,
	tree_node = ui_tree_node,
	tree_pop = ui_tree_pop,
	set_next_window = ui_set_next_window,
	want_capture_mouse = ui_want_capture_mouse,
}

Host = {
	host_available = host_available,
	host_send = host_send,
	host_poll = host_poll,
	available = host_available,
	send = host_send,
	poll = host_poll,
}

Audio = {
	audio_pcm = audio_pcm,
	audio_decode = audio_decode,
	audio_play = audio_play,
	audio_voice = audio_voice,
	audio_free = audio_free,
	audio_master_volume = audio_master_volume,
	audio_info = audio_info,
	pcm = audio_pcm,
	decode = audio_decode,
	play = audio_play,
	voice = audio_voice,
	free = audio_free,
	master_volume = audio_master_volume,
	info = audio_info,
}

Sys = {
	file_mtime = file_mtime,
	request_file = request_file,
	is_web = is_web,
	fnv1a64 = fnv1a64,
	actual_fps = actual_fps,
}

Io = require("lub_io")
-- lubx_png は Haxe だと @:luaRequire で個別 require されるが、
-- TinyC# / raw Lua には global で見せる (C# stub の Png class が対応)
Png = require("lubx_png")

lub = lub or {}
lub.config = config
lub.quit = quit
lub.gfx = Gfx
lub.phys2d = Phys2d
lub.phys3d = Phys3d
lub.input = Input
lub.profiler = Profiler
lub.mesh = Mesh
lub.font = Font
lub.ui = Ui
lub.host = Host
lub.audio = Audio
lub.sys = Sys
lub.io = Io
lub.png = Png
-- Haxe extern の emit 形 (lub.Gfx.begin_pass) 用 alias
lub.Lub = Lub
lub.Gfx = Gfx
lub.Phys2d = Phys2d
lub.Phys3d = Phys3d
lub.Input = Input
lub.Profiler = Profiler
lub.Mesh = Mesh
lub.Font = Font
lub.Ui = Ui
lub.Host = Host
lub.Audio = Audio
lub.Sys = Sys

return true
