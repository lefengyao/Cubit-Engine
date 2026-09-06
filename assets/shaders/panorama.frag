#version 450
layout(location = 0) in vec2 in_position;
layout(set = 0, binding = 0) uniform samplerCube panorama_sampler;
layout(push_constant) uniform PushConstants {
    float yaw;
    float aspect;
} pc;
layout(location = 0) out vec4 out_color;
void main() {
    vec2 plane = vec2(in_position.x * pc.aspect, in_position.y) * 0.82;
    float c = cos(pc.yaw);
    float s = sin(pc.yaw);
    vec3 ray = normalize(vec3(c * plane.x + s, plane.y, -s * plane.x + c));
    out_color = vec4(texture(panorama_sampler, ray).rgb * 0.82, 1.0);
}
