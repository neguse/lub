-- audio core API の lifecycle テスト。実デバイスが無い環境では miniaudio の
-- null backend にフォールバックし、mixer は同じように回る。音を出さないよう
-- 全 voice を volume 0 で扱う (lifecycle は volume と独立)。
-- snd は key で毎フレーム宣言する resource で、宣言が途切れると
-- resource_sweep_after_frames 後に sweep される。
local M = {}

local frame = 0
local phase = "init"
local phase_frame = 0

local function fail(message)
	print("AUDIO_SMOKE_FAIL " .. phase .. ": " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function sine(freq, rate, frames)
	local t = {}
	for i = 1, frames do
		t[i] = math.sin(2 * math.pi * freq * (i - 1) / rate) * 0.5
	end
	return t
end

local function make_wav(samples, rate)
	local pcm = {}
	for i, s in ipairs(samples) do
		local v = math.floor(s * 32767 + 0.5)
		if v > 32767 then
			v = 32767
		elseif v < -32768 then
			v = -32768
		end
		pcm[i] = string.pack("<i2", v)
	end
	local data = table.concat(pcm)
	return "RIFF"
		.. string.pack("<I4", 36 + #data)
		.. "WAVE"
		.. "fmt "
		.. string.pack("<I4I2I2I4I4I2I2", 16, 1, 1, rate, rate * 2, 2, 16)
		.. "data"
		.. string.pack("<I4", #data)
		.. data
end

local SWEEP_FRAMES = 10

local pcm_long -- 2s loop 用
local pcm_short -- ~2ms oneshot
local wav -- decode 純関数の入力
local snd_long
local snd_short
local snd_decoded
local snd_swept
local baseline_voices = 0

function M.on_init()
	lub.config({
		backend = os.getenv("LUB_BACKEND") or "sdlgpu",
		width = 320,
		height = 180,
		resource_sweep_after_frames = SWEEP_FRAMES,
	})
	pcm_long = sine(220, 48000, 96000)
	pcm_short = sine(440, 48000, 100)
	wav = make_wav(sine(330, 22050, 2205), 22050)
end

-- 毎フレームの宣言。version が同じなら runtime は data を読まない。
local function declare_snds()
	snd_long = lub.audio.snd("long", pcm_long, 1, 48000, 1)
	snd_short = lub.audio.snd("short", pcm_short, 1, 48000, 1)
	local bytes, ch, rate = lub.audio.decode(wav)
	snd_decoded = lub.audio.snd_bytes("decoded", bytes, ch, rate, 1)
end

local function set_phase(next_phase)
	phase = next_phase
	phase_frame = 0
end

function M.on_frame()
	frame = frame + 1
	phase_frame = phase_frame + 1
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.02, 0.03, 0.04, 1.0 } })
	lub.gfx.end_pass()
	declare_snds()

	if phase == "init" then
		expect(snd_long ~= 0 and snd_short ~= 0, "snd creation failed")
		expect(snd_long ~= snd_short, "distinct content must get distinct snd")
		-- 内容 dedupe: 別 key で同じ波形を宣言しても同じ handle (hot reload の要)
		local again = lub.audio.snd("long_again", sine(220, 48000, 96000), 1, 48000)
		expect(again == snd_long, "content dedupe failed: " .. tostring(again) .. " vs " .. tostring(snd_long))
		expect(lub.audio.play(999999) == false, "play with bogus snd must fail")

		-- decode 純関数: wav bytes -> pcm (frame 有効の view) -> snd
		local bytes, ch, rate = lub.audio.decode(wav)
		expect(bytes ~= nil, "decode failed")
		expect(ch == 1 and rate == 22050, "decode meta mismatch: ch=" .. tostring(ch) .. " rate=" .. tostring(rate))
		expect(snd_decoded ~= 0, "snd from decoded bytes failed")
		expect(lub.audio.decode("not a sound file") == nil, "bogus decode must return nil")
		expect(lub.audio.info().snds == 3, "expected 3 snds, got " .. lub.audio.info().snds)

		local info = lub.audio.info()
		print("AUDIO_SMOKE_INFO device=" .. tostring(info.device) .. " rate=" .. tostring(info.rate))
		set_phase("oneshot")
		return
	end

	if phase == "oneshot" then
		-- 短い oneshot はサンプル末尾で自動解放される
		if phase_frame == 1 then
			baseline_voices = lub.audio.info().voices
			expect(lub.audio.play(snd_short, { volume = 0 }), "oneshot play failed")
			expect(lub.audio.info().voices == baseline_voices + 1, "oneshot voice not spawned")
			return
		end
		if lub.audio.info().voices <= baseline_voices then
			set_phase("declared")
			return
		end
		if phase_frame > 600 then
			fail("oneshot voice did not end")
		end
		return
	end

	if phase == "declared" then
		-- loop する宣言 voice は宣言が続く限り生き、pitch/volume を毎フレーム
		-- 更新できる (負 pitch = 逆再生も宣言できることをここで通す)
		lub.audio.voice("bgm", snd_long, { loop = true, volume = 0, pitch = 1 + 0.001 * phase_frame })
		lub.audio.voice("scratch", snd_long, { loop = true, volume = 0, pitch = -0.5 })
		if phase_frame == 1 then
			expect(lub.audio.info().voices >= 2, "declared voices not spawned")
			return
		end
		if phase_frame >= 20 then
			expect(lub.audio.info().voices >= 2, "declared voices died while declared")
			set_phase("undeclare")
		end
		return
	end

	if phase == "undeclare" then
		-- "bgm" だけ宣言し続け、"scratch" は宣言をやめる → fade out して解放
		lub.audio.voice("bgm", snd_long, { loop = true, volume = 0 })
		if lub.audio.info().voices == 1 then
			set_phase("tombstone")
			return
		end
		if phase_frame > 600 then
			fail("undeclared voice did not fade out: voices=" .. lub.audio.info().voices)
		end
		return
	end

	if phase == "tombstone" then
		-- 非 loop の宣言 voice が自然終了したら、同じ key を宣言し続けても
		-- 再発火しない (スクラッチの retrigger は key を変えて行う)。
		-- snd_short は 100 frames @48kHz ≈ 2ms だが、audio callback の周期は
		-- 負荷次第で伸びるので「終わるまで」はフレーム数でなく voice 数で待つ。
		lub.audio.voice("bgm", snd_long, { loop = true, volume = 0 })
		lub.audio.voice("blip", snd_short, { volume = 0 })
		if lub.audio.info().voices == 1 then
			set_phase("tombstone_hold")
			return
		end
		if phase_frame > 600 then
			fail("blip voice did not end: voices=" .. lub.audio.info().voices)
		end
		return
	end

	if phase == "tombstone_hold" then
		-- 終了後も宣言し続ける。retrigger は audio_voice 呼び出しと同期して
		-- game thread 側で起きるので、voices が 1 のままなら再発火していない
		lub.audio.voice("bgm", snd_long, { loop = true, volume = 0 })
		lub.audio.voice("blip", snd_short, { volume = 0 })
		expect(
			lub.audio.info().voices == 1,
			"ended declared key must not retrigger: voices=" .. lub.audio.info().voices
		)
		if phase_frame >= 60 then
			-- "long_again" は init 以来宣言していないが、同じ snd を "long" が
			-- 宣言し続けているので sweep されても snd は生きている
			expect(lub.audio.info().snds == 3, "shared snd must survive: snds=" .. lub.audio.info().snds)
			set_phase("sweep")
		end
		return
	end

	if phase == "sweep" then
		lub.audio.voice("bgm", snd_long, { loop = true, volume = 0 })
		if phase_frame == 1 then
			-- 1 frame だけ宣言して鳴らす。宣言が途切れた snd は sweep で退役し、
			-- 鳴っている voice が終わってから PCM が回収される
			snd_swept = lub.audio.snd("swept", sine(550, 48000, 4800), 1, 48000)
			expect(snd_swept ~= 0, "snd for sweep test failed")
			expect(lub.audio.play(snd_swept, { volume = 0 }), "play before sweep failed")
			expect(lub.audio.info().snds == 4, "expected 4 snds, got " .. lub.audio.info().snds)
			return
		end
		if phase_frame <= SWEEP_FRAMES then
			expect(lub.audio.play(snd_swept, { volume = 0 }), "play within the sweep window must succeed")
			return
		end
		local info = lub.audio.info()
		if info.voices == 1 and info.snds == 3 then
			expect(lub.audio.play(snd_swept) == false, "play after sweep must fail")
			print("AUDIO_SMOKE_OK frame=" .. frame)
			lub.quit()
			return
		end
		if phase_frame > 600 then
			fail("swept snd not reclaimed: voices=" .. info.voices .. " snds=" .. info.snds)
		end
		return
	end
end

return M
