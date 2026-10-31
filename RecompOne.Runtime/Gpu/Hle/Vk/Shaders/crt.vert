#version 450
// Vulkan port of GlCrt.CrtVs. vUv follows crtemu: (0,0) is the bottom-left of
// the picture. Rows map as in GL (vUv.y 0 = row 0), so the same flip applies.

layout(location = 0) out vec2 vUv;

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

void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vUv = p;
    if (pc.flipY > 0.5) vUv.y = 1.0 - vUv.y;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
