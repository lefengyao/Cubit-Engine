#version 450
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in float inShade;
layout(location = 3) in vec4 inColor;

layout(set = 0, binding = 0) uniform CameraUBO {
    mat4 view;
    mat4 proj;
} camera;

layout(push_constant) uniform Push {
    mat4 model;
    vec4 modulation;
} push;

layout(location = 0) out vec2 fragUv;
layout(location = 1) out float fragShade;
layout(location = 2) out vec4 fragModulation;
layout(location = 3) out vec4 fragVertexColor;

void main() {
    gl_Position = camera.proj * camera.view * push.model * vec4(inPosition, 1.0);
    fragUv = inUv;
    fragShade = inShade;
    fragModulation = push.modulation;
    fragVertexColor = inColor;
}
