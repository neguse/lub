-- Exercise real control routing and sequence transitions with deterministic input.
return function(g, check)
	CraneGame23 = g
	local input, size, capture = lub.input, lub.gfx.size, lub.ui.want_capture_mouse
	local saved = {}
	for _, name in ipairs({ "mouse_pos", "mouse_pressed", "mouse_down", "key_pressed", "key_down" }) do
		saved[name] = input[name]
	end
	local mx, my, down, pressed, key = 0, 0, false, false, nil
	local width, height = 960, 720
	input.mouse_pos = function()
		return mx, my
	end
	input.mouse_pressed = function()
		return pressed
	end
	input.mouse_down = function()
		return down
	end
	input.key_pressed = function(k)
		return key == k
	end
	input.key_down = function(k)
		return key == k
	end
	lub.gfx.size = function()
		return width, height
	end
	lub.ui.want_capture_mouse = function()
		return false
	end
	local ok, err = pcall(function()
		local w = lub.phys3d.world("crane_controls", {
			gravity = { x = 0, y = -9.81, z = 0 },
			fixed_dt = 1 / 60,
			substeps = 8,
			max_steps = 1,
		})
		local function tick()
			g.read_controls()
			g.simulate_tick(w)
			pressed, key = false, nil
		end
		local function press_button(button)
			local r = g.control_rect(button)
			mx, my = r.x + r.w / 2, r.y + r.h / 2
			down, pressed = true, true
			tick()
		end
		for _ = 1, 300 do
			tick()
		end
		check(g.state == 0 and g.plays == 0, "idle must wait for the player; demo is opt-in")
		-- Scene and disabled second-button clicks cannot start a play.
		mx, my, pressed, down = 480, 250, true, true
		tick()
		check(g.state == 0, "scene click started a play")
		down = false
		tick()
		press_button(2)
		check(g.state == 0, "inactive second button started a play")
		down = false
		tick()
		local x = g.cx
		press_button(1)
		for _ = 1, 20 do
			tick()
		end
		check(g.state == 1 and g.cx < x, "first button must move along -X (screen right) while held")
		-- Leaving the button releases it; holding does not activate button 2.
		mx, my = 10, 250
		tick()
		check(g.state == 2, "leaving first button must stop and wait for second press")
		for _ = 1, 8 do
			tick()
		end
		check(g.state == 2, "held pointer accidentally started the next axis")
		down = false
		tick()
		press_button(1)
		check(g.state == 2, "inactive first button started second axis")
		down = false
		tick()
		local z = g.cz
		press_button(2)
		for _ = 1, 20 do
			tick()
		end
		check(g.state == 3 and g.cz < z, "second button must move toward the back while held")
		down = false
		tick()
		check(g.state == 4, "second release must trigger descent")
		local old_side = g.side_view
		key = "tab"
		tick()
		check(g.side_view ~= old_side, "Tab must switch the view without changing the play")
		key = "f2"
		tick()
		check(g.settings_open, "F2 must open settings")
		for _ = 1, 1100 do
			tick()
		end
		check(g.state == 0 and g.plays == 1, "manual play must return to idle without starting a demo")
		mx, my, pressed, down = 70, 660, true, true
		tick()
		check(g.demo_enabled and g.auto_play, "demo button must start automatic play")
		down = false
		tick()
		pressed, down = true, true
		tick()
		check(not g.demo_enabled and g.auto_play, "stopping demo must finish the current play")
		down = false
		tick()
		for _ = 1, 1100 do
			tick()
		end
		check(g.state == 0 and not g.auto_play, "stopped demo must return control to player")
		key = "space"
		tick()
		check(g.state == 1, "Space must operate the first control")
		for _ = 1, 8 do
			tick()
		end
		check(g.state == 2, "releasing Space must stop the first axis")
		key = "space"
		tick()
		for _ = 1, 8 do
			tick()
		end
		check(g.state == 4, "second Space release must start descent")
		for _ = 1, 1100 do
			tick()
		end
		width, height = 640, 480
		tick()
		press_button(1)
		check(g.state == 1 and g.control_held, "resized control hitbox must match drawing")
	end)
	for name, fn in pairs(saved) do
		input[name] = fn
	end
	lub.gfx.size, lub.ui.want_capture_mouse = size, capture
	check(ok, "controls: " .. tostring(err))
	print("CRANE_CONTROLS checked manual holds, releases, view, settings, resize, idle")
end
