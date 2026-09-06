#version 450
layout(location = 0) in vec2 fragUv;
layout(location = 1) in float fragShade;
layout(location = 2) in vec4 fragModulation;
layout(location = 3) in vec4 fragVertexColor;

layout(set = 0, binding = 1) uniform sampler2D atlas;

layout(location = 0) out vec4 outColor;

void main() {
    vec4 sampled = texture(atlas, fragUv);
    if (sampled.a < 0.5) {
        discard;
    }

    outColor = sampled * vec4(vec3(fragShade), 1.0) * fragVertexColor * fragModulation;
}
