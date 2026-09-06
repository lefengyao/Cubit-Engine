using System.Runtime.InteropServices;
using Silk.NET.Shaderc;

/// <summary>仅供桌面开发命令使用的 GLSL 到 SPIR-V 编译器。</summary>
internal static unsafe class ShaderCompiler
{
    public static byte[] CompileGlsl(string source, ShaderKind kind, string fileName)
    {
        var shaderc = Shaderc.GetApi();
        var compiler = shaderc.CompilerInitialize();
        var options = shaderc.CompileOptionsInitialize();
        try
        {
            shaderc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
            var result = shaderc.CompileIntoSpv(
                compiler, source, (nuint)source.Length, kind, "main", fileName, options);
            try
            {
                if (shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    throw new InvalidOperationException($"着色器编译失败（{fileName}）: {shaderc.ResultGetErrorMessageS(result)}");
                }

                var length = checked((int)shaderc.ResultGetLength(result));
                var bytes = new byte[length];
                Marshal.Copy((nint)shaderc.ResultGetBytes(result), bytes, 0, length);
                return bytes;
            }
            finally
            {
                shaderc.ResultRelease(result);
            }
        }
        finally
        {
            shaderc.CompilerRelease(compiler);
            shaderc.CompileOptionsRelease(options);
        }
    }
}
