#version 450
// Vulkan port of GlShaders.PresentFs. Draws straight into the swapchain at
// the fitted output rectangle: a box filter when the source is larger than
// the output, otherwise one tap (bilinear unless pc.nearest).

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 oColor;

layout(set = 0, binding = 0) uniform sampler2D uNearest;
layout(set = 0, binding = 1) uniform sampler2D uLinear;

layout(push_constant) uniform Present {
    vec2 origin;
    vec2 size;
    vec2 texSize;
    int  nearest;
    int  pad0;
} pc;

void main() {
    vec2 t = (pc.origin + vUv * pc.size) / pc.texSize;
    vec2 span = vec2(abs(dFdx(t).x), abs(dFdy(t).y));
    ivec2 n = clamp(ivec2(ceil(span * vec2(textureSize(uNearest, 0)) - 0.01)), ivec2(1), ivec2(8));
    if (n.x * n.y == 1) {
        oColor = vec4(pc.nearest != 0 ? texture(uNearest, t).rgb : texture(uLinear, t).rgb, 1.0);
        return;
    }
    vec2 stepUv = span / vec2(n);
    vec2 first = t - 0.5 * span + 0.5 * stepUv;
    vec3 sum = vec3(0.0);
    for (int y = 0; y < n.y; y++)
        for (int x = 0; x < n.x; x++)
            sum += texture(uNearest, first + stepUv * vec2(float(x), float(y))).rgb;
    oColor = vec4(sum / float(n.x * n.y), 1.0);
}
