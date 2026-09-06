using System.Reflection;

namespace Cubit.ImGui.Rendering;

/// <summary>内嵌的预编译 ImGui SPIR-V。</summary>
internal static class EmbeddedImGuiShaders
{
    public static byte[] Vertex() => Load("Cubit.ImGui.Shaders.Spv.imgui.vert.spv");

    public static byte[] Fragment() => Load("Cubit.ImGui.Shaders.Spv.imgui.frag.spv");

    public static byte[] CubeVertex() => Load("Cubit.ImGui.Shaders.Spv.panorama.vert.spv");

    public static byte[] CubeFragment() => Load("Cubit.ImGui.Shaders.Spv.panorama.frag.spv");

    private static byte[] Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"缺少内嵌 ImGui 着色器资源: {name}");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
