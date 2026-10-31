#version 450
// Vulkan port of GlShaders.PrimFs (dual-source blend path). Rebuild the
// SPIR-V with tools/vulkan/build_shaders.py after editing.
//
// Blend state is fixed: color = src * BlendColor.rgb + dst * BlendColor.a
// (ADD, or REVERSE_SUBTRACT for the second subtractive pass). pc.blendPass
// picks the factors: 0 = all add modes in one batch, 1 = first subtractive
// pass (opaque texels only), 2 = second subtractive pass (dst - src).

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUV;
layout(location = 2) flat in ivec2 clutBase;
layout(location = 3) flat in ivec2 pageBase;
layout(location = 4) flat in int   texMode;
layout(location = 5) flat in int   vDither;
layout(location = 6) flat in int   vSemiTrans;
layout(location = 7) flat in int   vBlendMode;

layout(location = 0, index = 0) out vec4 FragColor;
layout(location = 0, index = 1) out vec4 BlendColor;

layout(set = 0, binding = 0) uniform sampler2D uVram;
layout(set = 0, binding = 1) uniform sampler2D uDest;

layout(push_constant) uniform Prim {
    vec2  posBias;
    vec2  fbInv;
    vec2  wideCore;
    float setMask;
    float filterStrength;
    ivec4 texWindow;
    int   checkMask;
    int   wideMode;
    int   filterMode;
    int   dedither;
    int   scaledDither;
    int   scale;
    int   blendPass;
    int   pad0;
} pc;

const int ditherTbl[16] = int[16](
    -4,  0, -3,  1,
     2, -2,  3, -1,
    -3,  1, -4,  0,
     3, -1,  2, -2 );

int u5(float f) { return int(floor(f * 31.0 + 0.5)); }
vec4 fetch(ivec2 c) { return texelFetch(uVram, (c & ivec2(1023, 511)) * pc.scale, 0); }
int fetch16(ivec2 c) {
    vec4 p = fetch(c);
    return u5(p.r) | (u5(p.g) << 5) | (u5(p.b) << 10) | (int(ceil(p.a)) << 15);
}
vec3 quant5(ivec3 c8) {
    if (pc.dedither != 0)
        return clamp(vec3(c8), 0.0, 255.0) / 255.0;
    if (vDither != 0) {
        ivec2 vp = pc.scaledDither != 0
            ? ivec2(floor(gl_FragCoord.xy / float(pc.scale) - pc.posBias))
            : ivec2(floor(gl_FragCoord.xy - pc.posBias * float(pc.scale)));
        c8 = clamp(c8 + ditherTbl[(vp.y & 3) * 4 + (vp.x & 3)], 0, 255);
    }
    return vec3(min(c8 >> 3, 31)) / 31.0;
}

ivec2 wrapUv(ivec2 uv) {
    uv = (uv & pc.texWindow.xy) | pc.texWindow.zw;
    return uv & ivec2(0xff);
}

bool isTransparent(vec4 t) {
    return t.rgb == vec3(0.0) && t.a < 0.5;
}

vec4 sampleNearest(ivec2 uv) {
    uv = wrapUv(uv);
    if (texMode == 0) {
        int s = fetch16(ivec2(pageBase.x + (uv.x >> 2), pageBase.y + uv.y));
        int idx = (s >> ((uv.x & 3) << 2)) & 0xf;
        return fetch(ivec2(clutBase.x + idx, clutBase.y));
    }
    if (texMode == 1) {
        int s = fetch16(ivec2(pageBase.x + (uv.x >> 1), pageBase.y + uv.y));
        int idx = (s >> ((uv.x & 1) << 3)) & 0xff;
        return fetch(ivec2(clutBase.x + idx, clutBase.y));
    }
    return fetch(ivec2(pageBase.x + uv.x, pageBase.y + uv.y));
}

ivec2 tileTap(ivec2 base, ivec2 off) {
    ivec2 t = base + off;
    if ((base.x >> 4) != (t.x >> 4) || (base.y >> 4) != (t.y >> 4))
        return base;
    return t;
}

vec4 sampleBilinearF(vec2 uvf, vec2 f) {
    ivec2 i00 = ivec2(floor(uvf));
    vec4 c00 = sampleNearest(i00);
    vec4 c10 = sampleNearest(tileTap(i00, ivec2(1, 0)));
    vec4 c01 = sampleNearest(tileTap(i00, ivec2(0, 1)));
    vec4 c11 = sampleNearest(tileTap(i00, ivec2(1, 1)));

    float w00 = (1.0 - f.x) * (1.0 - f.y) * (isTransparent(c00) ? 0.0 : 1.0);
    float w10 = f.x * (1.0 - f.y) * (isTransparent(c10) ? 0.0 : 1.0);
    float w01 = (1.0 - f.x) * f.y * (isTransparent(c01) ? 0.0 : 1.0);
    float w11 = f.x * f.y * (isTransparent(c11) ? 0.0 : 1.0);
    float wsum = w00 + w10 + w01 + w11;
    if (wsum < 1e-4) return vec4(0.0);

    return (c00 * w00 + c10 * w10 + c01 * w01 + c11 * w11) / wsum;
}

vec4 sampleBilinear(vec2 uvf) {
    return sampleBilinearF(uvf, fract(uvf));
}

vec4 sampleSharpBilinear(vec2 uvf) {
    vec2 f = fract(uvf);
    f = clamp((f - 0.5) * 2.0 + 0.5, 0.0, 1.0);
    return sampleBilinearF(uvf, f);
}

vec4 sampleSoftSmooth(vec2 uvf) {
    vec2 f = fract(uvf);
    f = f * f * (3.0 - 2.0 * f);
    return sampleBilinearF(uvf, f);
}

vec4 applyFilter(vec2 uv, vec4 nearest, float strength) {
    if (pc.filterMode == 0 || strength <= 0.001) return nearest;
    vec4 filtered = nearest;
    if (pc.filterMode == 1)
        filtered = sampleBilinear(uv);
    else if (pc.filterMode == 2)
        filtered = sampleSharpBilinear(uv);
    else if (pc.filterMode == 3)
        filtered = sampleSoftSmooth(uv);
    if (isTransparent(filtered)) return nearest;
    return mix(nearest, filtered, clamp(strength, 0.0, 1.0));
}

vec4 blendFactors(bool stp) {
    bool semi = vSemiTrans != 0 && stp;
    if (pc.blendPass == 1) return semi ? vec4(0.0, 0.0, 0.0, 1.0) : vec4(1.0, 1.0, 1.0, 0.0);
    if (pc.blendPass == 2) return semi ? vec4(1.0) : vec4(0.0, 0.0, 0.0, 1.0);
    if (!semi) return vec4(1.0, 1.0, 1.0, 0.0);
    if (vBlendMode == 0) return vec4(0.5);
    if (vBlendMode == 3) return vec4(0.25, 0.25, 0.25, 1.0);
    return vec4(1.0);
}

void main() {
    if (pc.wideMode == 1) {
        float x1 = gl_FragCoord.x / float(pc.scale);
        if (x1 >= pc.wideCore.x && x1 < pc.wideCore.y) discard;
    }
    if (pc.checkMask != 0 && texelFetch(uDest, ivec2(gl_FragCoord.xy), 0).a >= 0.5) discard;

    if (texMode == 4) {
        FragColor = vec4(quant5(ivec3(vColor.rgb * 255.0 + 0.5)), pc.setMask);
        BlendColor = blendFactors(true);
        return;
    }

    // Affine UVs (PS1).
    vec2 uv = vUV;

    int rawU = dFdx(uv.x) < 0.0 ? int(ceil(uv.x - 0.0001)) : int(floor(uv.x + 0.0001));
    int rawV = dFdy(uv.y) < 0.0 ? int(ceil(uv.y - 0.0001)) : int(floor(uv.y + 0.0001));
    vec4 nearest = sampleNearest(ivec2(rawU, rawV));
    if (isTransparent(nearest)) discard;

    vec4 texel = applyFilter(uv, nearest, pc.filterStrength);
    ivec3 c8;
    if (pc.dedither != 0) {
        vec3 t8 = texel.rgb * 255.0;
        c8 = ivec3(t8 * vec3(vColor.rgb * 255.0) / 128.0 + 0.5);
    } else {
        ivec3 t8 = ivec3(texel.rgb * 31.0 + 0.5) << 3;
        c8 = (t8 * ivec3(vColor.rgb * 255.0 + 0.5)) >> 7;
    }
    FragColor = vec4(quant5(c8), max(texel.a, pc.setMask));
    BlendColor = blendFactors(texel.a >= 0.5);
}
