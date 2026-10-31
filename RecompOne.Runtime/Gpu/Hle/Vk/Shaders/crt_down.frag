#version 450
// Vulkan port of GlCrt.DownFs: the frame scaled down to the native line count.

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

layout(set = 0, binding = 0) uniform sampler2D uSrc;
layout(set = 0, binding = 1) uniform sampler2D uUnused1;
layout(set = 0, binding = 2) uniform sampler2D uUnused2;

layout(push_constant) uniform Crt {
    vec2  vec0;      // uSpan
    vec2  resolution;
    float k;
    int   mode;
    float time;
    float lines;
    float flipY;
    float pad0;
} pc;

void main() {
    // Four bilinear taps inside one destination texel: enough averaging for
    // a buffer that is only ever blurred further.
    vec2 d = pc.vec0 * 0.25;
    vec3 c = texture(uSrc, vUv + vec2(-d.x, -d.y)).rgb
           + texture(uSrc, vUv + vec2( d.x, -d.y)).rgb
           + texture(uSrc, vUv + vec2(-d.x,  d.y)).rgb
           + texture(uSrc, vUv + vec2( d.x,  d.y)).rgb;
    oColor = vec4(c * 0.25, 1.0);
}
