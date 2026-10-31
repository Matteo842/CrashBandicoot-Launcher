#version 450
// Dear ImGui fragment shader (as imgui_impl_vulkan).

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUV;
layout(location = 0) out vec4 oColor;

layout(set = 0, binding = 0) uniform sampler2D uTexture;

void main() {
    oColor = vColor * texture(uTexture, vUV);
}
