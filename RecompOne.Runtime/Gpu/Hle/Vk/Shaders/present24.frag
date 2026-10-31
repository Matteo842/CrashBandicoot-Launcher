#version 450
// Vulkan port of GlShaders.Present24Fs (24-bit display mode, FMV).

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

layout(set = 0, binding = 0) uniform sampler2D uVram;
layout(set = 0, binding = 1) uniform sampler2D uUnused;

layout(push_constant) uniform Present {
    vec2 origin;
    vec2 size;
    vec2 texSize;
    int  scale;
    int  pad0;
} pc;

int u5(float f) { return int(floor(f * 31.0 + 0.5)); }
int texel16(int lin) {
    vec4 p = texelFetch(uVram, ivec2((lin & 1023) * pc.scale, ((lin >> 10) & 511) * pc.scale), 0);
    return u5(p.r) | (u5(p.g) << 5) | (u5(p.b) << 10) | (int(ceil(p.a)) << 15);
}
int byteAt(int b) {
    int t = texel16(b >> 1);
    return (b & 1) == 0 ? (t & 0xff) : ((t >> 8) & 0xff);
}
void main() {
    int px = int(floor(vUv.x * pc.size.x));
    int py = int(floor(vUv.y * pc.size.y));
    int ty = int(pc.origin.y) + py;
    int base = (ty * 1024 + int(pc.origin.x)) * 2 + px * 3;
    oColor = vec4(float(byteAt(base)) / 255.0, float(byteAt(base + 1)) / 255.0,
                  float(byteAt(base + 2)) / 255.0, 1.0);
}
