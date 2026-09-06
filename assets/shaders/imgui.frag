#version 450
layout(location = 0) in vec2 in_uv;
layout(location = 1) in vec4 in_color;
layout(set = 0, binding = 0) uniform sampler2D font_sampler;
layout(location = 0) out vec4 out_color;
vec3 srgbToLinear(vec3 color) {
    return mix(color / 12.92, pow((color + 0.055) / 1.055, vec3(2.4)), greaterThanEqual(color, vec3(0.04045)));
}
void main() {
    vec3 rgb = srgbToLinear(in_color.rgb);
    out_color = vec4(rgb, in_color.a) * texture(font_sampler, in_uv);
}
