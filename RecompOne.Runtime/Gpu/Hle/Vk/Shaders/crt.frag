#version 450
// Vulkan port of GlCrt.CrtFs. Keep in step with the GL shader.
// MattiasCRT - adapted from crtemu.h (TV) in CRT View by Mattias Gustavsson.
// MIT License, Copyright (c) 2016 Mattias Gustavsson.
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

layout(set = 0, binding = 0) uniform sampler2D uFrame;
layout(set = 0, binding = 1) uniform sampler2D uState;
layout(set = 0, binding = 2) uniform sampler2D uGhost;

layout(push_constant) uniform Crt {
    vec2  vec0;
    vec2  resolution;
    float k;
    int   mode;
    float time;
    float lines;
    float flipY;
    float pad0;
} pc;

#define uResolution pc.resolution
#define uTime pc.time
#define uLines pc.lines

vec3 fetch(sampler2D s, vec2 tc) {
    // Black outside the picture, like crtemu's GL_CLAMP_TO_BORDER.
    float edge = 0.502 - max(abs(0.5 - tc.x), abs(0.5 - tc.y));
    return texture(s, vec2(tc.x, 1.0 - tc.y)).rgb * clamp(edge * 400.0, 0.0, 1.0);
}

vec3 tsample(vec2 tc) {
    vec3 s = max(fetch(uFrame, tc), fetch(uState, tc) * 0.32);
    return pow(abs(s), vec3(2.2)) * 1.25;
}

vec3 gsample(vec2 tc) {
    return pow(abs(fetch(uGhost, tc)), vec3(2.2)) * 1.25;
}

vec3 filmic(vec3 linearColor) {
    vec3 x = max(vec3(0.0), linearColor - vec3(0.004));
    return (x * (6.2 * x + 0.5)) / (x * (6.2 * x + 1.7) + 0.06);
}

vec2 curve(vec2 uv) {
    uv = (uv - 0.5) * 2.0;
    uv *= vec2(1.049, 1.042);
    uv -= vec2(-0.008, 0.008);
    uv.x *= 1.0 + pow(abs(uv.y) / 5.0, 2.0);
    uv.y *= 1.0 + pow(abs(uv.x) / 4.0, 2.0);
    return uv / 2.0 + 0.5;
}

float rand(vec2 co) {
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453);
}

void main() {
    /* Curve */
    vec2 curved_uv = mix(curve(vUv), vUv, 0.4);
    vec2 scuv = curved_uv;

    /* Main color, Bleed */
    vec3 col;
    float x = sin(0.1 * uTime + curved_uv.y * 13.0) * sin(0.23 * uTime + curved_uv.y * 19.0)
            * sin(0.3 + 0.11 * uTime + curved_uv.y * 23.0) * 0.0012;
    float o = sin(gl_FragCoord.y / 1.5) / uResolution.x;
    x += o * 0.25;
    x *= 0.2;
    col.r = tsample(vec2(x + scuv.x + 0.0009, scuv.y + 0.0009)).x + 0.02;
    col.g = tsample(vec2(x + scuv.x + 0.0000, scuv.y - 0.0011)).y + 0.02;
    col.b = tsample(vec2(x + scuv.x - 0.0015, scuv.y + 0.0000)).z + 0.02;
    float i = clamp(col.r * 0.299 + col.g * 0.587 + col.b * 0.114, 0.0, 1.0);
    i = pow(1.0 - pow(i, 2.0), 1.0);
    i = (1.0 - i) * 0.85 + 0.15;

    /* Ghosting */
    float ghs = 0.15;
    vec3 r = gsample(vec2(x - 0.014, -0.027) * 0.85 + 0.007 * vec2(0.35 * sin(1.0 / 7.0 + 15.0 * curved_uv.y + 0.9 * uTime),
        0.35 * sin(2.0 / 7.0 + 10.0 * curved_uv.y + 1.37 * uTime)) + vec2(scuv.x + 0.001, scuv.y + 0.001)) * vec3(0.5, 0.25, 0.25);
    vec3 g = gsample(vec2(x - 0.019, -0.020) * 0.85 + 0.007 * vec2(0.35 * cos(1.0 / 9.0 + 15.0 * curved_uv.y + 0.5 * uTime),
        0.35 * sin(2.0 / 9.0 + 10.0 * curved_uv.y + 1.50 * uTime)) + vec2(scuv.x + 0.000, scuv.y - 0.002)) * vec3(0.25, 0.5, 0.25);
    vec3 b = gsample(vec2(x - 0.017, -0.003) * 0.85 + 0.007 * vec2(0.35 * sin(2.0 / 3.0 + 15.0 * curved_uv.y + 0.7 * uTime),
        0.35 * cos(2.0 / 3.0 + 10.0 * curved_uv.y + 1.63 * uTime)) + vec2(scuv.x - 0.002, scuv.y + 0.000)) * vec3(0.25, 0.25, 0.5);

    col += vec3(ghs * (1.0 - 0.299)) * pow(clamp(vec3(3.0) * r, vec3(0.0), vec3(1.0)), vec3(2.0)) * vec3(i);
    col += vec3(ghs * (1.0 - 0.587)) * pow(clamp(vec3(3.0) * g, vec3(0.0), vec3(1.0)), vec3(2.0)) * vec3(i);
    col += vec3(ghs * (1.0 - 0.114)) * pow(clamp(vec3(3.0) * b, vec3(0.0), vec3(1.0)), vec3(2.0)) * vec3(i);

    /* Level adjustment (curves) */
    col *= vec3(0.95, 1.05, 0.95);
    col = clamp(col * 1.3 + 0.75 * col * col + 1.25 * col * col * col * col * col, vec3(0.0), vec3(10.0));

    /* Vignette */
    float vig = 0.1 + 16.0 * curved_uv.x * curved_uv.y * (1.0 - curved_uv.x) * (1.0 - curved_uv.y);
    vig = 1.3 * pow(max(vig, 0.0), 0.5);
    col *= vig;

    /* Scanlines: one per source line, fading out below ~2 screen pixels per line */
    float lineAmp = smoothstep(1.5, 3.0, uResolution.y / uLines);
    float scans = clamp(0.35 + 0.18 * lineAmp * sin(6.0 * uTime + curved_uv.y * uLines * 6.2831853), 0.0, 1.0);
    col *= vec3(pow(scans, 0.9));

    /* Vertical lines (shadow mask) */
    col *= 1.0 - 0.23 * clamp(mod(gl_FragCoord.x, 3.0) / 2.0, 0.0, 1.0);

    /* Tone map */
    col = filmic(col);

    /* Noise */
    vec2 seed = curved_uv * uResolution;
    col -= 0.015 * pow(vec3(rand(seed + uTime), rand(seed + uTime * 2.0), rand(seed + uTime * 3.0)), vec3(1.5));

    /* Flicker */
    col *= 1.0 - 0.004 * (sin(50.0 * uTime + curved_uv.y * 2.0) * 0.5 + 0.5);

    /* Clamp */
    if (curved_uv.x < 0.0 || curved_uv.x > 1.0 || curved_uv.y < 0.0 || curved_uv.y > 1.0)
        col = vec3(0.0);
    oColor = vec4(col, 1.0);
}
