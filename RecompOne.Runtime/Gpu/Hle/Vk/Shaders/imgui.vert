#version 450
// Dear ImGui vertex shader (as imgui_impl_vulkan): pixel positions to clip space.

layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec4 aColor;

layout(push_constant) uniform ImGuiPush {
    vec2 scale;
    vec2 translate;
} pc;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUV;

void main() {
    vColor = aColor;
    vUV = aUV;
    gl_Position = vec4(aPos * pc.scale + pc.translate, 0.0, 1.0);
}
