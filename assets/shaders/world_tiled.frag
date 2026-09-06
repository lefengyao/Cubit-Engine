#version 450
layout(location = 0) in vec2 fragUv;
layout(location = 1) in float fragShade;
layout(location = 2) in vec4 fragModulation;
layout(location = 3) in vec4 fragVertexColor;
layout(location = 4) flat in uint fragTile;

layout(set = 0, binding = 1) uniform sampler2D atlas;

layout(location = 0) out vec4 outColor;

void main() {
    const int tilePixels = 16;
    ivec2 tileCounts = max(textureSize(atlas, 0) / tilePixels, ivec2(1));
    uint columns = uint(tileCounts.x);
    uvec2 tile = uvec2(fragTile % columns, fragTile / columns);
    vec2 atlasUv = (vec2(tile) + fract(fragUv)) / vec2(tileCounts);
    vec4 sampled = texture(atlas, atlasUv);
    if (sampled.a < 0.5) {
        discard;
    }

    outColor = sampled * vec4(vec3(fragShade), 1.0) * fragVertexColor * fragModulation;
}
