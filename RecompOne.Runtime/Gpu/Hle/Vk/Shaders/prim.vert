#version 450
// Vulkan port of GlShaders.PrimVs. Rebuild the SPIR-V with
// tools/vulkan/build_shaders.py after editing.

layout(location = 0) in vec2  inPos;
layout(location = 1) in uint  inColor;
layout(location = 2) in int   inClut;
layout(location = 3) in int   inTexpage;
layout(location = 4) in vec2  inUV;
layout(location = 5) in float inInvZ;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUV;
layout(location = 2) flat out ivec2 clutBase;
layout(location = 3) flat out ivec2 pageBase;
layout(location = 4) flat out int   texMode;
layout(location = 5) flat out int   vDither;
layout(location = 6) flat out int   vSemiTrans;
layout(location = 7) flat out int   vBlendMode;

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

void main() {
    vec2 p = (inPos + pc.posBias) * pc.fbInv - 1.0;
    // Same depth mapping as GL, but Vulkan clip space Z is already 0..1.
    float depth = inInvZ > 0.0 ? 1.0 - clamp(inInvZ * 64.0, 0.0, 1.0) : 0.5;
    gl_Position = vec4(p, depth, 1.0);

    vColor = vec4(float(inColor & 0xFFu), float((inColor >> 8) & 0xFFu), float((inColor >> 16) & 0xFFu), 0.0) / 255.0;
    vDither = (inTexpage >> 10) & 1;
    vSemiTrans = (inTexpage >> 12) & 1;
    vBlendMode = (inTexpage >> 13) & 3;
    vUV = inUV;
    clutBase = ivec2(0);
    pageBase = ivec2(0);

    if ((inTexpage & 0x8000) != 0) {
        texMode = 4;
    } else {
        texMode = (inTexpage >> 7) & 3;
        pageBase = ivec2((inTexpage & 0xf) * 64, ((inTexpage >> 4) & 1) * 256);
        clutBase = ivec2((inClut & 0x3f) * 16, (inClut >> 6) & 0x1ff);
    }
}
