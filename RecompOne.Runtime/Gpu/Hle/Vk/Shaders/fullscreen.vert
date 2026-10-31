#version 450
// One triangle covering the viewport. vUv matches GlShaders.FullscreenVs:
// 0 at clip -1, 1 at clip +1 (Vulkan clip -1 is the top row).

layout(location = 0) out vec2 vUv;

void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vUv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
