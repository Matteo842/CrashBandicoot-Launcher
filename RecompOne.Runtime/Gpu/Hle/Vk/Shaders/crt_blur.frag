#version 450
// Vulkan port of GlCrt.BlurFs: crtemu's 9-tap Gaussian. Mode 1 folds in the
// persistence update max(cur, blur * k); mode 2 blurs max(cur, src * k).

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

layout(set = 0, binding = 0) uniform sampler2D uSrc;
layout(set = 0, binding = 1) uniform sampler2D uCur;
layout(set = 0, binding = 2) uniform sampler2D uUnused2;

layout(push_constant) uniform Crt {
    vec2  vec0;      // uDir
    vec2  resolution;
    float k;
    int   mode;
    float time;
    float lines;
    float flipY;
    float pad0;
} pc;

vec3 tap(vec2 uv) {
    vec3 s = texture(uSrc, uv).rgb;
    return pc.mode == 2 ? max(texture(uCur, uv).rgb, s * pc.k) : s;
}

void main() {
    vec2 dir = pc.vec0;
    vec3 sum = tap(vUv) * 0.2270270270;
    sum += (tap(vUv - dir) + tap(vUv + dir)) * 0.1945945946;
    sum += (tap(vUv - 2.0 * dir) + tap(vUv + 2.0 * dir)) * 0.1216216216;
    sum += (tap(vUv - 3.0 * dir) + tap(vUv + 3.0 * dir)) * 0.0540540541;
    sum += (tap(vUv - 4.0 * dir) + tap(vUv + 4.0 * dir)) * 0.0162162162;
    if (pc.mode == 1) sum = max(texture(uCur, vUv).rgb, sum * pc.k);
    oColor = vec4(sum, 1.0);
}
