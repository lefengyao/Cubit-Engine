using System.Reflection;

namespace Cubit.Core.Shaders;

/// <summary>
/// 内嵌 SPIR-V 字节码。着色器在桌面端预编译为 .spv 并内嵌到引擎程序集，
/// 因此移动端无需携带 shaderc 原生库。
/// </summary>
public static class EmbeddedShaders
{
    public static byte[] WorldVertex() => Load("Cubit.Core.Shaders.Spv.world.vert.spv");

    public static byte[] WorldVertexColor() => Load("Cubit.Core.Shaders.Spv.world_color.vert.spv");

    public static byte[] WorldTiledVertex() => Load("Cubit.Core.Shaders.Spv.world_tiled.vert.spv");

    public static byte[] WorldFragment() => Load("Cubit.Core.Shaders.Spv.world.frag.spv");

    public static byte[] WorldTiledFragment() => Load("Cubit.Core.Shaders.Spv.world_tiled.frag.spv");

    private static byte[] Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"缺少内嵌着色器资源: {name}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
