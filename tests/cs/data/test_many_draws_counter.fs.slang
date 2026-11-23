// Writes the compute result to 1 pixel: R, G = count (low, high byte), B, A =
// the number of the last dispatch (low, high byte).
StructuredBuffer<float4> counter;

struct FSIn {
  float4 pos : SV_Position;
};

[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  float4 c = counter[0];
  return float4(fmod(c.x, 256.0), floor(c.x / 256.0), fmod(c.y, 256.0),
                floor(c.y / 256.0)) /
         255.0;
}
