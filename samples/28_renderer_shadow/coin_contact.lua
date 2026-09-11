-- repo root から起動。COIN_MODE の各条件は README.md を参照。
local x = dofile("samples/lubx.lua")
-- 18_coin_pusher の 120 秒時点から抜き出した、物理を更新しない 2 枚。
local scene = {
	{
		mesh = "coin",
		m = {
			0.15531313419342041,
			-1.0582063623587601e-05,
			0.069121845066547394,
			0.76173245906829834,
			-2.2827518478152342e-05,
			-0.070000030100345612,
			-1.1913269190699793e-05,
			0.13473168015480042,
			0.069121845066547394,
			6.5980975705315359e-07,
			-0.15531320869922638,
			-0.23614029586315155,
			0,
			0,
			0,
			1,
		},
		tint = { 0.85000002384185791, 0.68000000715255737, 0.20000000298023224, 1 },
		blend = 1,
	},
	{
		mesh = "coin",
		m = {
			-0.12388506531715393,
			5.8496341807767749e-05,
			0.1164151057600975,
			0.76000642776489258,
			-1.2325385796430055e-05,
			0.069999940693378448,
			-0.00022056885063648224,
			0.20329612493515015,
			-0.11641518771648407,
			-6.9661000452470034e-05,
			-0.12388496845960617,
			-0.22444543242454529,
			0,
			0,
			0,
			1,
		},
		tint = { 0.85000002384185791, 0.68000000715255737, 0.20000000298023224, 1 },
		blend = 1,
	},
}
local mode = os.getenv("COIN_MODE") or "baseline"
if mode == "one_tap" then
	x.Renderer3d.lit_fs = x.Renderer3d.lit_fs
		:gsub("int y = %-1; y <= 1", "int y = 0; y <= 0")
		:gsub("int x = %-1; x <= 1", "int x = 0; x <= 0")
		:gsub("return lit / 9.0f;", "return lit;")
end
if mode == "receiver_plane" or (mode == "weighted" or mode == "weighted_only") then
	local fs = x.Renderer3d.lit_fs
	fs = fs:gsub(
		"float texel = f.shadow_p.x;",
		[=[float texel = f.shadow_p.x;
 float3 dx=ddx(float3(uv,ndc.z)),dy=ddy(float3(uv,ndc.z));
 float det=dx.x*dy.y-dx.y*dy.x;
 float2 dz=abs(det)>1e-15f?float2(dx.z*dy.y-dy.z*dx.y,dx.x*dy.z-dy.x*dx.z)/det:float2(0,0);
 ]=]
	)
	fs = fs:gsub(
		"float bias = f.shadow_p.y %* %(1.0f %+ %(1.0f %- saturate%(ndl%)%) %* 3.0f%);",
		"float bias = 0.000005f;"
	)
	fs = fs:gsub(
		"float closest =",
		"float2 suv=(floor(uv/texel)+float2(float(x),float(y))+0.5f)*texel;\n      float closest ="
	)
	fs = fs:gsub("uv %+ float2%(float%(x%), float%(y%)%) %* texel", "suv")
	fs = fs:gsub("ndc.z %- bias <= closest", "ndc.z + dot(dz,suv-uv) - bias <= closest")
	x.Renderer3d.lit_fs = fs
end
if mode == "weighted" or mode == "weighted_only" then
	local fs = x.Renderer3d.lit_fs
	fs = fs:gsub("int y = %-1; y <= 1", "int y = -1; y <= 2"):gsub("int x = %-1; x <= 1", "int x = -1; x <= 2")
	fs = fs:gsub("floor%(uv/texel%)", "floor(uv/texel-0.5f)")
	fs = fs:gsub(
		"lit %+= %(ndc.z %+ dot%(dz,suv%-uv%) %- bias <= closest%) %? 1.0f : 0.0f;",
		[=[float2 f=frac(uv/texel-0.5f);
 float wx=x==-1?1.0f-f.x:(x==2?f.x:1.0f);
 float wy=y==-1?1.0f-f.y:(y==2?f.y:1.0f);
 lit += (ndc.z + dot(dz,suv-uv) - bias <= closest) ? wx*wy : 0.0f;]=]
	)
	x.Renderer3d.lit_fs = fs
end
if mode == "weighted_only" then
	x.Renderer3d.lit_fs = x.Renderer3d.lit_fs
		:gsub("float bias = 0.000005f;", "float bias = f.shadow_p.y * (1.0f + (1.0f - saturate(ndl)) * 3.0f);")
		:gsub("ndc.z %+ dot%(dz,suv%-uv%) %- bias", "ndc.z - bias")
end
if mode == "reference" then
	x.Renderer3d.lit_fs = [=[
bool hit_coin(float3 o, float3 d) {
 float lo=0.0f,hi=10000.0f;
 for(int k=0;k<26;k++) {
  float a=(float(k)+0.5f)*6.28318530718f/24.0f;
  float3 n=k<24?float3(cos(a),0,sin(a)):float3(0,k==24?1.0f:-1.0f,0);
  float bound=k<24?0.991444861f:0.5f;
  float den=dot(n,d), num=bound-dot(n,o);
  if(abs(den)<1e-7f) { if(num<0) return false; }
  else {float t=num/den; if(den<0) lo=max(lo,t); else hi=min(hi,t);}
 }
 return hi>lo && hi>0;
}
float exact_shadow(float3 p,float3 l) {
 p+=l*0.00001f;
 if(hit_coin(float3(dot(float4(5.37415736325f,-0.0007898792256f,2.3917584676f,-3.5287731298f),float4(p,1.0f)),dot(float4(-0.00215960390203f,-14.285707815f,0.000134654847676f,1.92641425395f),float4(p,1.0f)),dot(float4(2.39175839783f,-0.000412223528135f,-5.37415490072f,-3.09087899458f),float4(p,1.0f))),float3(dot(float4(5.37415736325f,-0.0007898792256f,2.3917584676f,-3.5287731298f),float4(l,0.0f)),dot(float4(-0.00215960390203f,-14.285707815f,0.000134654847676f,1.92641425395f),float4(l,0.0f)),dot(float4(2.39175839783f,-0.000412223528135f,-5.37415490072f,-3.09087899458f),float4(l,0.0f)))))return 0.0f;
 if(hit_coin(float3(dot(float4(-4.28667986871f,-0.000426482962358f,-4.02820618523f,2.35387847718f),float4(p,1.0f)),dot(float4(0.0119380263656f,14.2857022653f,-0.0142165296498f,-2.91649172442f),float4(p,1.0f)),dot(float4(4.02820306762f,-0.00763213834253f,-4.2866752686f,-4.02203332391f),float4(p,1.0f))),float3(dot(float4(-4.28667986871f,-0.000426482962358f,-4.02820618523f,2.35387847718f),float4(l,0.0f)),dot(float4(0.0119380263656f,14.2857022653f,-0.0142165296498f,-2.91649172442f),float4(l,0.0f)),dot(float4(4.02820306762f,-0.00763213834253f,-4.2866752686f,-4.02203332391f),float4(l,0.0f)))))return 0.0f;
return 1.0f;
}
]=] .. x.Renderer3d.lit_fs
	x.Renderer3d.lit_fs =
		x.Renderer3d.lit_fs:gsub("shadow_factor%(i.lpos, ndl%)", "((f.shadow_p.z<0.5f)?1.0f:exact_shadow(i.wp,l))")
end
local r, coin
local shadows = mode ~= "shadow_off"
local ao = mode ~= "ao_off"
local upper = mode ~= "single"
return {
	on_init = function()
		print("coin_contact mode=" .. mode .. " (S: shadow, A: SSAO, O: upper coin)")
		lub.config({ backend = os.getenv("LUB_BACKEND") or "vulkan", width = 1280, height = 720 })
		r = x.Renderer3d.new("coin_contact")
		coin = x.Mesh3d.new("coin")
	end,
	on_frame = function()
		if not coin:ready() then
			coin:rebuild(x.Shapes3d.cylinder(24))
		end
		if lub.input.key_pressed("s") then
			shadows = not shadows
		end
		if lub.input.key_pressed("a") then
			ao = not ao
		end
		if mode ~= "reference" and lub.input.key_pressed("o") then
			upper = not upper
		end
		r.light.dir = x.Vec3.new(-0.25, 1, 0.5)
		r.light.intensity = 1.2
		r.sky.top = x.Color.rgb(0.32, 0.35, 0.44)
		r.sky.bottom = x.Color.rgb(0.10, 0.10, 0.12)
		r.sky.intensity = 0.45
		r.background = x.Color.rgb(0.035, 0.045, 0.06)
		r.shadow.size = mode == "high_res" and 8192 or 2048
		r.shadow.extent = 3
		r.shadow.bias = mode == "bias_high" and 0.004
			or (mode == "bias_low" and 0.0001 or (mode == "bias_zero" and 0 or 0.001))
		r.shadow.enabled = shadows and mode ~= "clean"
		r.ssao.enabled = ao and mode ~= "clean"
		r.bloom.enabled = false
		r.aa.enabled = false
		r.dither = false
		r:begin({
			eye = x.Vec3.new(0.76173, 1.63473, 1.76386),
			target = x.Vec3.new(0.76173, 0.13473, -0.23614),
			fov = 20,
			near = 0.1,
			far = 50,
		})
		for n = 1, #scene do
			local i = mode == "reverse" and #scene + 1 - n or n
			local d = scene[i]
			if upper or i == 1 then
				local m = x.Mat4.new()
				m.m = d.m
				r:draw(coin, m, { tint = x.Color.rgb(table.unpack(d.tint)), blend = d.blend })
			end
		end
		r:end_()
	end,
}
