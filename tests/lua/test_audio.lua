-- audio core API の lifecycle テスト。実デバイスが無い環境では miniaudio の
-- null backend にフォールバックし、mixer は同じように回る。音を出さないよう
-- 全 voice を volume 0 で扱う (lifecycle は volume と独立)。
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

local snd_long -- 2s loop 用
local snd_short -- ~2ms oneshot
local snd_freed
local baseline_voices = 0

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 320, height = 180 })
end

local function set_phase(next_phase)
	phase = next_phase
	phase_frame = 0
end

function M.onFrame()
	frame = frame + 1
	phase_frame = phase_frame + 1
	begin_pass({ target = main_tex, clear_color = { 0.02, 0.03, 0.04, 1.0 } })
	end_pass()

	if phase == "init" then
		snd_long = audio_pcm(sine(220, 48000, 96000), 1, 48000)
		snd_short = audio_pcm(sine(440, 48000, 100), 1, 48000)
		expect(snd_long ~= 0 and snd_short ~= 0, "snd creation failed")
		expect(snd_long ~= snd_short, "distinct content must get distinct snd")
		-- 内容 dedupe: 同じ波形を作り直しても同じ handle (hot reload の要)
		local again = audio_pcm(sine(220, 48000, 96000), 1, 48000)
		expect(again == snd_long, "content dedupe failed: " .. tostring(again) .. " vs " .. tostring(snd_long))
		expect(audio_play(999999) == false, "play with bogus snd must fail")

		-- decode 純関数: wav bytes -> pcm -> snd
		local wav = make_wav(sine(330, 22050, 2205), 22050)
		local bytes, ch, rate = audio_decode(wav)
		expect(bytes ~= nil, "decode failed")
		expect(ch == 1 and rate == 22050, "decode meta mismatch: ch=" .. tostring(ch) .. " rate=" .. tostring(rate))
		local snd_decoded = audio_pcm(bytes, ch, rate)
		expect(snd_decoded ~= 0, "pcm from decoded bytes failed")
		expect(audio_decode("not a sound file") == nil, "bogus decode must return nil")

		local info = audio_info()
		print("AUDIO_SMOKE_INFO device=" .. tostring(info.device) .. " rate=" .. tostring(info.rate))
		set_phase("oneshot")
		return
	end

	if phase == "oneshot" then
		-- 短い oneshot はサンプル末尾で自動解放される
		if phase_frame == 1 then
			baseline_voices = audio_info().voices
			expect(audio_play(snd_short, { volume = 0 }), "oneshot play failed")
			expect(audio_info().voices == baseline_voices + 1, "oneshot voice not spawned")
			return
		end
		if audio_info().voices <= baseline_voices then
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
		audio_voice("bgm", snd_long, { loop = true, volume = 0, pitch = 1 + 0.001 * phase_frame })
		audio_voice("scratch", snd_long, { loop = true, volume = 0, pitch = -0.5 })
		if phase_frame == 1 then
			expect(audio_info().voices >= 2, "declared voices not spawned")
			return
		end
		if phase_frame >= 20 then
			expect(audio_info().voices >= 2, "declared voices died while declared")
			set_phase("undeclare")
		end
		return
	end

	if phase == "undeclare" then
		-- "bgm" だけ宣言し続け、"scratch" は宣言をやめる → fade out して解放
		audio_voice("bgm", snd_long, { loop = true, volume = 0 })
		if audio_info().voices == 1 then
			set_phase("tombstone")
			return
		end
		if phase_frame > 600 then
			fail("undeclared voice did not fade out: voices=" .. audio_info().voices)
		end
		return
	end

	if phase == "tombstone" then
		-- 非 loop の宣言 voice が自然終了したら、同じ key を宣言し続けても
		-- 再発火しない (スクラッチの retrigger は key を変えて行う)。
		-- snd_short は 100 frames @48kHz ≈ 2ms だが、audio callback の周期は
		-- 負荷次第で伸びるので「終わるまで」はフレーム数でなく voice 数で待つ。
		audio_voice("bgm", snd_long, { loop = true, volume = 0 })
		audio_voice("blip", snd_short, { volume = 0 })
		if audio_info().voices == 1 then
			set_phase("tombstone_hold")
			return
		end
		if phase_frame > 600 then
			fail("blip voice did not end: voices=" .. audio_info().voices)
		end
		return
	end

	if phase == "tombstone_hold" then
		-- 終了後も宣言し続ける。retrigger は audio_voice 呼び出しと同期して
		-- game thread 側で起きるので、voices が 1 のままなら再発火していない
		audio_voice("bgm", snd_long, { loop = true, volume = 0 })
		audio_voice("blip", snd_short, { volume = 0 })
		expect(audio_info().voices == 1, "ended declared key must not retrigger: voices=" .. audio_info().voices)
		if phase_frame >= 60 then
			set_phase("free")
		end
		return
	end

	if phase == "free" then
		audio_voice("bgm", snd_long, { loop = true, volume = 0 })
		if phase_frame == 1 then
			snd_freed = audio_pcm(sine(550, 48000, 4800), 1, 48000)
			expect(snd_freed ~= 0, "snd for free test failed")
			expect(audio_play(snd_freed, { volume = 0 }), "play before free failed")
			expect(audio_free(snd_freed), "free failed")
			expect(audio_play(snd_freed) == false, "play after free must fail")
			expect(audio_free(snd_freed) == false, "double free must fail")
			return
		end
		-- freed snd を参照していた voice は audio 側で落ち、PCM は frame_end で
		-- 回収されて snds 数が戻る
		local info = audio_info()
		if info.voices == 1 and info.snds == 3 then
			print("AUDIO_SMOKE_OK frame=" .. frame)
			quit()
			return
		end
		if phase_frame > 600 then
			fail("freed snd not reclaimed: voices=" .. info.voices .. " snds=" .. info.snds)
		end
		return
	end
end

return M
