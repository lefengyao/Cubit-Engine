using System.Numerics;
using System.Runtime.InteropServices;
using Cubit.Core.Rendering;
using Hexa.NET.ImGui;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using ImGuiApi = Hexa.NET.ImGui.ImGui;

namespace Cubit.ImGui.Rendering;

/// <summary>静态纹理的采样方式。</summary>
public enum ImGuiTextureFilter
{
    Linear,
    Nearest,
}

/// <summary>由 <see cref="ImGuiRenderer"/> 持有的静态纹理标识。</summary>
public readonly record struct ImGuiTextureHandle(ulong TextureId)
{
    public bool IsValid => TextureId != 0;
}

/// <summary>CubeMap 单面的 RGBA8 数据；六面顺序由调用方按 X+/X-/Y+/Y-/Z+/Z- 提供。</summary>
public readonly record struct ImGuiCubeMapFace(ReadOnlyMemory<byte> Rgba8)
{
    public int Length => Rgba8.Length;
}

/// <summary>由 <see cref="ImGuiRenderer"/> 持有的 CubeMap 标识。</summary>
public readonly record struct ImGuiCubeMapHandle(ulong TextureId)
{
    public bool IsValid => TextureId != 0;
}

/// <summary>
/// ImGui 的 Vulkan 渲染后端（借鉴 imgui_impl_vulkan）：字体图集上传、描述符池、管线、每帧绘制数据提交。
/// 绘制发生在 VulkanContext.Overlay 回调（场景渲染结束后、提交前），与交换链同步。
/// </summary>
public sealed unsafe class ImGuiRenderer : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly uint _queueFamily;
    private readonly CommandPool _commandPool;

    private RenderPass _renderPass;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private DescriptorPool _descriptorPool;
    private uint* _glyphRangesPtr;

    private long _frame;
    private readonly List<(Buffer Buffer, DeviceMemory Memory, long Frame)> _retired = [];
    private readonly List<(FontTextureResource Resource, long Frame)> _retiredFonts = [];
    private readonly Dictionary<int, FontTextureResource> _fontTextures = [];
    private readonly List<(FontTextureResource Resource, long Frame)> _retiredStaticTextures = [];
    private readonly Dictionary<ulong, FontTextureResource> _staticTextures = [];
    private readonly Dictionary<ulong, CubeMapResource> _cubeMaps = [];
    private DescriptorSetLayout _textureLayout;
    private DescriptorSetLayout _cubeMapLayout;
    private PipelineLayout _cubeMapPipelineLayout;
    private Pipeline _cubeMapPipeline;
    private readonly Dictionary<ulong, DescriptorSet> _textureSets = [];
    private readonly Dictionary<(ulong TextureId, uint FrameSlot), DescriptorSet> _frameTextureSets = [];
    private readonly FrameBuffers[] _frameBuffers;
    private const int FontBytesPerPixel = 4;
    private bool _disposed;

    private sealed class FrameBuffers
    {
        public Buffer VertexBuffer;
        public DeviceMemory VertexMemory;
        public ulong VertexCapacity;
        public Buffer IndexBuffer;
        public DeviceMemory IndexMemory;
        public ulong IndexCapacity;
    }

    private sealed class FontTextureResource
    {
        public required ulong TextureId;
        public required int Width;
        public required int Height;
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public Sampler Sampler;
        public DescriptorSet Set;
    }

    private sealed class CubeMapResource
    {
        public required ulong TextureId;
        public required int Width;
        public required int Height;
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public Sampler Sampler;
        public DescriptorSet Set;
        public float Yaw;
        public bool Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CubeMapPushConstants
    {
        public float Yaw;
        public float Aspect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ImGuiPushConstants
    {
        public Vector2 Scale;
        public Vector2 Translate;
    }

    public ImGuiRenderer(VulkanContext context)
    {
        _context = context;
        _context.SwapchainFormatChanged += RecreateForSwapchainFormat;
        _vk = context.VkApi;
        _device = context.Device;
        _queue = context.GraphicsQueue;
        _queueFamily = context.GraphicsFamily;
        _commandPool = context.CommandPool;
        _frameBuffers = new FrameBuffers[context.FramesInFlight];
        for (var i = 0; i < _frameBuffers.Length; i++)
        {
            _frameBuffers[i] = new FrameBuffers();
        }

        ImGuiApi.CreateContext();
        var io = ImGuiApi.GetIO();
        io.IniFilename = null;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;
        io.DisplayFramebufferScale = new Vector2(1f, 1f);
        LoadChineseFont(io);

        CreateRenderPass();
        CreateDescriptorPool();
        CreateTextureSetLayout();
        CreatePipeline();
        CreateCubeMapLayout();
        CreateCubeMapPipeline();
    }

    /// <summary>创建不依赖临时 ImTextureData 内存的外部纹理引用。</summary>
    public static ImTextureRef CreateExternalTextureRef(ulong textureId)
        => new(null, new ImTextureID(textureId));

    /// <summary>上传独立拥有的 RGBA8 静态纹理，供调用方以 ImTextureRef 绘制。</summary>
    public ImGuiTextureHandle CreateStaticTexture(
        ReadOnlySpan<byte> rgba8,
        int width,
        int height,
        ImGuiTextureFilter filter = ImGuiTextureFilter.Linear,
        bool isColorTexture = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var expectedLength = checked(width * height * FontBytesPerPixel);
        if (rgba8.Length != expectedLength)
        {
            throw new ArgumentException($"静态纹理必须是完整 RGBA8 像素数据，期望 {expectedLength} 字节，实际 {rgba8.Length} 字节", nameof(rgba8));
        }

        var resource = CreateTextureResource(rgba8, width, height, filter, "static texture", isColorTexture);
        _staticTextures.Add(resource.TextureId, resource);
        _textureSets.Add(resource.TextureId, resource.Set);
        return new ImGuiTextureHandle(resource.TextureId);
    }

    /// <summary>原位更新同尺寸的静态纹理，保留描述符和 GPU 资源身份。</summary>
    public void UpdateStaticTexture(ImGuiTextureHandle handle, ReadOnlySpan<byte> rgba8)
    {
        if (!handle.IsValid || !_staticTextures.TryGetValue(handle.TextureId, out var resource))
        {
            throw new InvalidOperationException("静态纹理句柄无效或已释放");
        }

        var expectedLength = checked(resource.Width * resource.Height * FontBytesPerPixel);
        if (rgba8.Length != expectedLength)
        {
            throw new ArgumentException($"静态纹理更新必须是完整 RGBA8 像素数据，期望 {expectedLength} 字节，实际 {rgba8.Length} 字节", nameof(rgba8));
        }

        UploadPixels(rgba8, resource, "ImGui static texture update");
    }

    /// <summary>创建通用 CubeMap；六面顺序固定为 X+/X-/Y+/Y-/Z+/Z-。</summary>
    public ImGuiCubeMapHandle CreateCubeMapTexture(
        IReadOnlyList<ImGuiCubeMapFace> faces,
        int width,
        int height,
        ImGuiTextureFilter filter = ImGuiTextureFilter.Linear)
    {
        ArgumentNullException.ThrowIfNull(faces);
        if (faces.Count != 6)
        {
            throw new ArgumentException("CubeMap 必须提供六个面", nameof(faces));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var faceSize = checked(width * height * FontBytesPerPixel);
        if (faces.Any(face => face.Length != faceSize))
        {
            throw new ArgumentException($"CubeMap 每个面必须是 {faceSize} 字节的 RGBA8 数据", nameof(faces));
        }

        var resource = CreateCubeMapResource(faces, width, height, filter);
        _cubeMaps.Add(resource.TextureId, resource);
        return new ImGuiCubeMapHandle(resource.TextureId);
    }

    /// <summary>更新 CubeMap 的视角状态；实际投影在 GPU 绘制阶段连续完成。</summary>
    public void SetCubeMap(ImGuiCubeMapHandle handle, float yawRadians, bool visible)
    {
        if (!handle.IsValid || !_cubeMaps.TryGetValue(handle.TextureId, out var resource))
        {
            throw new InvalidOperationException("CubeMap 句柄无效或已释放");
        }

        if (!float.IsFinite(yawRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(yawRadians), "CubeMap yaw 必须是有限值");
        }

        resource.Yaw = yawRadians;
        resource.Visible = visible;
    }

    /// <summary>在当前交换链颜色附件上绘制所有可见 CubeMap，使用 uniform 更新而不重建图片。</summary>
    public void DrawCubeMaps(CommandBuffer commandBuffer, Framebuffer framebuffer, Extent2D extent)
    {
        if (_cubeMaps.Values.All(resource => !resource.Visible))
        {
            return;
        }

        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D { Offset = default, Extent = extent },
            ClearValueCount = 0,
            PClearValues = null,
        };
        _vk.CmdBeginRenderPass(commandBuffer, &begin, SubpassContents.Inline);
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _cubeMapPipeline);
        var viewport = new Viewport
        {
            X = 0,
            Y = 0,
            Width = extent.Width,
            Height = extent.Height,
            MinDepth = 0,
            MaxDepth = 1,
        };
        var scissor = new Rect2D { Offset = default, Extent = extent };
        _vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        var aspect = extent.Height == 0 ? 1f : extent.Width / (float)extent.Height;
        foreach (var resource in _cubeMaps.Values)
        {
            if (!resource.Visible)
            {
                continue;
            }

            var descriptorSet = resource.Set;
            _vk.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Graphics,
                _cubeMapPipelineLayout,
                0,
                1,
                &descriptorSet,
                0,
                null);
            var push = new CubeMapPushConstants { Yaw = resource.Yaw, Aspect = aspect };
            _vk.CmdPushConstants(
                commandBuffer,
                _cubeMapPipelineLayout,
                ShaderStageFlags.FragmentBit,
                0,
                (uint)sizeof(CubeMapPushConstants),
                &push);
            _vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        }

        _vk.CmdEndRenderPass(commandBuffer);
    }

    /// <summary>释放 CubeMap 资源。</summary>
    public void ReleaseCubeMap(ImGuiCubeMapHandle handle)
    {
        if (!handle.IsValid || !_cubeMaps.Remove(handle.TextureId, out var resource))
        {
            return;
        }

        DestroyCubeMapResource(resource, freeDescriptorSet: true);
    }

    /// <summary>延迟释放静态纹理，确保已提交的帧不再引用其描述符。</summary>
    public void ReleaseStaticTexture(ImGuiTextureHandle handle)
    {
        if (!handle.IsValid || !_staticTextures.Remove(handle.TextureId, out var resource))
        {
            return;
        }

        _textureSets.Remove(handle.TextureId);
        _retiredStaticTextures.Add((resource, _frame));
    }

    /// <summary>每帧开始：由编辑器在更新阶段调用（NewFrame 前喂输入）。</summary>
    public void NewFrame(ImGuiIOPtr io, double delta)
    {
        io.DeltaTime = (float)Math.Clamp(delta, 1.0 / 240.0, 1.0 / 10.0);
        var (width, height) = _context.SwapchainSize;
        io.DisplaySize = new Vector2(width, height);
        io.DisplayFramebufferScale = new Vector2(1f, 1f);
        ImGuiApi.NewFrame();
    }

    /// <summary>结束一帧：生成绘制数据（在 Overlay 回调前调用）。</summary>
    public void Render() => ImGuiApi.Render();

    /// <summary>在 VulkanContext.Overlay 中调用：把 ImGui 绘制数据提交到当前 command buffer。</summary>
    public void Draw(CommandBuffer commandBuffer, Framebuffer framebuffer, Extent2D extent, uint frameSlot)
    {
        var drawData = ImGuiApi.GetDrawData();

        if (drawData.IsNull || drawData.CmdListsCount == 0)
        {
            return;
        }

        if (frameSlot >= _frameBuffers.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameSlot), frameSlot, "ImGui 帧槽超出引擎在途帧数量");
        }

        var buffers = _frameBuffers[frameSlot];
        UpdateFontTextures(drawData);
        UploadBuffers(drawData, buffers);
        _frame++;

        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D { Offset = default, Extent = extent },
            ClearValueCount = 0,
            PClearValues = null,
        };
        _vk.CmdBeginRenderPass(commandBuffer, &begin, SubpassContents.Inline);
        RenderDrawData(commandBuffer, drawData, extent, buffers, frameSlot);
        _vk.CmdEndRenderPass(commandBuffer);

        DestroyRetired(_frame - 3);
        DestroyRetiredStaticTextures();
    }

    private void RenderDrawData(
        CommandBuffer commandBuffer,
        ImDrawDataPtr drawData,
        Extent2D extent,
        FrameBuffers buffers,
        uint frameSlot)
    {
        var fbWidth = (uint)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        var fbHeight = (uint)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (fbWidth == 0 || fbHeight == 0)
        {
            return;
        }

        drawData.ScaleClipRects(ImGuiApi.GetIO().DisplayFramebufferScale);

        var push = new ImGuiPushConstants
        {
            Scale = new Vector2(2f / drawData.DisplaySize.X, 2f / drawData.DisplaySize.Y),
            Translate = new Vector2(
                -1f - drawData.DisplayPos.X * (2f / drawData.DisplaySize.X),
                -1f - drawData.DisplayPos.Y * (2f / drawData.DisplaySize.Y)),
        };

        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
        var viewport = new Viewport
        {
            X = 0,
            Y = 0,
            Width = fbWidth,
            Height = fbHeight,
            MinDepth = 0,
            MaxDepth = 1,
        };
        _vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);

        var vertexOffset = 0ul;
        var indexOffset = 0ul;
        for (var listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            var cmdList = drawData.CmdLists[listIndex];
            for (var cmdIndex = 0; cmdIndex < cmdList.CmdBuffer.Size; cmdIndex++)
            {
                var pcmd = cmdList.CmdBuffer[cmdIndex];
                if (pcmd.UserCallback != null)
                {
                    continue;
                }

                var texId = (ulong)pcmd.GetTexID();
                if (!_frameTextureSets.TryGetValue((texId, frameSlot), out var cmdSet)
                    && !_textureSets.TryGetValue(texId, out cmdSet))
                {
                    continue;
                }

                _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &cmdSet, 0, null);

                var clip = pcmd.ClipRect;
                var scissor = new Rect2D
                {
                    Offset = new Offset2D { X = (int)Math.Max(0, clip.X), Y = (int)Math.Max(0, clip.Y) },
                    Extent = new Extent2D
                    {
                        Width = (uint)Math.Max(0, clip.Z - clip.X),
                        Height = (uint)Math.Max(0, clip.W - clip.Y),
                    },
                };
                if (scissor.Extent.Width == 0 || scissor.Extent.Height == 0)
                {
                    continue;
                }

                _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
                _vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(ImGuiPushConstants), &push);

                var vertexBuffer = buffers.VertexBuffer;
                var vtxBufOffset = vertexOffset + (ulong)pcmd.VtxOffset * (ulong)sizeof(ImDrawVert);
                _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &vtxBufOffset);
                _vk.CmdBindIndexBuffer(commandBuffer, buffers.IndexBuffer, indexOffset + (ulong)pcmd.IdxOffset * sizeof(ushort), IndexType.Uint16);
                _vk.CmdDrawIndexed(commandBuffer, pcmd.ElemCount, 1, 0, 0, 0);
            }

            vertexOffset += (ulong)cmdList.VtxBuffer.Size * (ulong)sizeof(ImDrawVert);
            indexOffset += (ulong)cmdList.IdxBuffer.Size * sizeof(ushort);
        }
    }

    private void UploadBuffers(ImDrawDataPtr drawData, FrameBuffers buffers)
    {
        var vertexBytes = (ulong)drawData.TotalVtxCount * (ulong)sizeof(ImDrawVert);
        var indexBytes = (ulong)drawData.TotalIdxCount * sizeof(ushort);
        if (vertexBytes == 0 || indexBytes == 0)
        {
            return;
        }

        EnsureVertexCapacity(buffers, vertexBytes);
        EnsureIndexCapacity(buffers, indexBytes);

        void* vtxMapped;
        ThrowOnError(_vk.MapMemory(_device, buffers.VertexMemory, 0, buffers.VertexCapacity, 0, &vtxMapped), "MapMemory(ImGui vertex)");
        void* idxMapped;
        ThrowOnError(_vk.MapMemory(_device, buffers.IndexMemory, 0, buffers.IndexCapacity, 0, &idxMapped), "MapMemory(ImGui index)");
        var vtxDst = (byte*)vtxMapped;
        var idxDst = (byte*)idxMapped;
        var vertexCursor = vtxDst;
        var indexCursor = idxDst;
        for (var i = 0; i < drawData.CmdListsCount; i++)
        {
            var cmdList = drawData.CmdLists[i];
            System.Buffer.MemoryCopy((void*)cmdList.VtxBuffer.Data, vertexCursor, vertexBytes, (ulong)cmdList.VtxBuffer.Size * (ulong)sizeof(ImDrawVert));
            System.Buffer.MemoryCopy((void*)cmdList.IdxBuffer.Data, indexCursor, indexBytes, (ulong)cmdList.IdxBuffer.Size * sizeof(ushort));
            vertexCursor += (ulong)cmdList.VtxBuffer.Size * (ulong)sizeof(ImDrawVert);
            indexCursor += (ulong)cmdList.IdxBuffer.Size * sizeof(ushort);
        }

        _vk.UnmapMemory(_device, buffers.VertexMemory);
        _vk.UnmapMemory(_device, buffers.IndexMemory);
    }

    private void EnsureVertexCapacity(FrameBuffers buffers, ulong bytes)
    {
        if (buffers.VertexBuffer.Handle != 0 && buffers.VertexCapacity >= bytes)
        {
            return;
        }

        RetireBuffer(buffers.VertexBuffer, buffers.VertexMemory);
        (buffers.VertexBuffer, buffers.VertexMemory, buffers.VertexCapacity) = CreateHostBuffer(bytes, BufferUsageFlags.VertexBufferBit);
    }

    private void EnsureIndexCapacity(FrameBuffers buffers, ulong bytes)
    {
        if (buffers.IndexBuffer.Handle != 0 && buffers.IndexCapacity >= bytes)
        {
            return;
        }

        RetireBuffer(buffers.IndexBuffer, buffers.IndexMemory);
        (buffers.IndexBuffer, buffers.IndexMemory, buffers.IndexCapacity) = CreateHostBuffer(bytes, BufferUsageFlags.IndexBufferBit);
    }

    private void RetireBuffer(Buffer buffer, DeviceMemory memory)
    {
        if (buffer.Handle == 0)
        {
            return;
        }

        _retired.Add((buffer, memory, _frame));
    }

    private void DestroyRetired(long upToFrame)
    {
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Frame <= upToFrame)
            {
                _vk.DestroyBuffer(_device, _retired[i].Buffer, null);
                _vk.FreeMemory(_device, _retired[i].Memory, null);
                _retired.RemoveAt(i);
            }
        }
    }

    private (Buffer Buffer, DeviceMemory Memory, ulong Capacity) CreateHostBuffer(ulong bytes, BufferUsageFlags usage)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = bytes,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        _vk.CreateBuffer(_device, &bufferInfo, null, out var buffer);

        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        _vk.AllocateMemory(_device, &allocInfo, null, out var memory);
        _vk.BindBufferMemory(_device, buffer, memory, 0);
        return (buffer, memory, bytes);
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_context.PhysicalDevice, out var memoryProperties);
        for (var i = 0; i < memoryProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 && (memoryProperties.MemoryTypes[i].PropertyFlags & properties) == properties)
            {
                return (uint)i;
            }
        }

        throw new InvalidOperationException("找不到满足要求的内存类型");
    }

    /// <summary>加载系统中文字体（微软雅黑/黑体），支持中文 UI 显示；失败时退回默认字体。</summary>
    private void LoadChineseFont(ImGuiIOPtr io)
    {
        var fontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var fontPath = Path.Combine(fontsDirectory, "simhei.ttf");
        if (!File.Exists(fontPath))
        {
            fontPath = Path.Combine(fontsDirectory, "msyh.ttc");
        }

        if (!File.Exists(fontPath))
        {
            io.Fonts.AddFontDefault();
            return;
        }

        var ranges = new ushort[] { 0x20, 0x7E, 0x3000, 0x303F, 0x4E00, 0x9FFF, 0xFF00, 0xFFEF, 0 };
        _glyphRangesPtr = (uint*)Marshal.AllocHGlobal(ranges.Length * sizeof(ushort));
        for (var i = 0; i < ranges.Length; i++)
        {
            ((ushort*)_glyphRangesPtr)[i] = ranges[i];
        }

        var cfg = ImGuiApi.ImFontConfig();
        cfg.SizePixels = 18f;
        cfg.GlyphRanges = _glyphRangesPtr;
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(fontPath + "\0");
        fixed (byte* p = pathBytes)
        {
            ImGuiApi.AddFontFromFileTTF(io.Fonts, p, 18f, cfg, _glyphRangesPtr);
        }

        cfg.Destroy();
    }

    private void CreateTextureSetLayout()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        ThrowOnError(_vk.CreateDescriptorSetLayout(_device, &info, null, out _textureLayout), "CreateDescriptorSetLayout(ImGui texture)");
    }

    private void CreateCubeMapLayout()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        ThrowOnError(_vk.CreateDescriptorSetLayout(_device, &info, null, out _cubeMapLayout), "CreateDescriptorSetLayout(ImGui CubeMap)");
    }

    /// <summary>注册外部纹理（如编辑器场景视口）：texId 与 ImDrawCmd 的 TexID 对应。</summary>
    public void SetTextureSet(ulong texId, uint frameSlot, ImageView view, Sampler sampler)
    {
        var key = (texId, frameSlot);
        if (_frameTextureSets.TryGetValue(key, out var existing))
        {
            var info = new DescriptorImageInfo
            {
                Sampler = sampler,
                ImageView = view,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = existing,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &info,
            };
            _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
            return;
        }

        var textureLayout = _textureLayout;
        var alloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &textureLayout,
        };
        ThrowOnError(_vk.AllocateDescriptorSets(_device, &alloc, out var set), "AllocateDescriptorSets(ImGui texture)");
        var imageInfo = new DescriptorImageInfo
        {
            Sampler = sampler,
            ImageView = view,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var write2 = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo,
        };
        _vk.UpdateDescriptorSets(_device, 1, &write2, 0, null);
        _frameTextureSets[key] = set;
    }

    private void CreateRenderPass()
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = VulkanRenderPassPolicy.CreateSwapchainOverlayColorAttachment(_context.SwapchainFormat);
        attachments[1] = VulkanRenderPassPolicy.CreateOverlayDepthAttachment();
        var colorReference = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };
        var depthReference = new AttachmentReference
        {
            Attachment = 1,
            Layout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorReference,
            PDepthStencilAttachment = &depthReference,
        };
        var createInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
        };
        ThrowOnError(_vk.CreateRenderPass(_device, &createInfo, null, out _renderPass), "CreateRenderPass(ImGui)");
    }

    private void RecreateForSwapchainFormat(Format format)
    {
        _vk.DestroyPipeline(_device, _pipeline, null);
        _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk.DestroyPipeline(_device, _cubeMapPipeline, null);
        _vk.DestroyPipelineLayout(_device, _cubeMapPipelineLayout, null);
        _vk.DestroyRenderPass(_device, _renderPass, null);
        CreateRenderPass();
        CreatePipeline();
        CreateCubeMapPipeline();
    }

    private void CreateDescriptorPool()
    {
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 64,
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 64,
        };
        ThrowOnError(_vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool), "CreateDescriptorPool(ImGui)");
    }

    private void UpdateFontTextures(ImDrawDataPtr drawData)
    {
        var textures = drawData.Textures;
        for (var i = 0; i < textures.Size; i++)
        {
            var tex = textures[i];
            var uniqueId = tex.UniqueID;
            if (tex.Status == ImTextureStatus.WantCreate)
            {
                if (_fontTextures.Remove(uniqueId, out var replaced))
                {
                    RetireFontResource(replaced);
                }

                var created = CreateFontResource(tex);
                _fontTextures[uniqueId] = created;
                _textureSets[created.TextureId] = created.Set;
                tex.SetTexID(new ImTextureID(created.TextureId));
                tex.SetStatus(ImTextureStatus.Ok);
            }
            else if (tex.Status == ImTextureStatus.WantUpdates)
            {
                if (!_fontTextures.TryGetValue(uniqueId, out var resource)
                    || resource.Width != tex.Width
                    || resource.Height != tex.Height)
                {
                    if (resource is not null)
                    {
                        _fontTextures.Remove(uniqueId);
                        RetireFontResource(resource);
                    }

                    resource = CreateFontResource(tex);
                    _fontTextures[uniqueId] = resource;
                    _textureSets[resource.TextureId] = resource.Set;
                }
                else
                {
                    UploadFontPixels(tex, resource);
                }

                tex.SetTexID(new ImTextureID(resource.TextureId));
                tex.SetStatus(ImTextureStatus.Ok);
            }
            else if (tex.Status == ImTextureStatus.WantDestroy)
            {
                if (_fontTextures.Remove(uniqueId, out var resource))
                {
                    RetireFontResource(resource);
                }

                tex.SetTexID(new ImTextureID(0));
                tex.SetStatus(ImTextureStatus.Destroyed);
            }
        }

        DestroyRetiredFonts();
    }

    private void RetireFontResource(FontTextureResource resource)
    {
        _textureSets.Remove(resource.TextureId);
        _retiredFonts.Add((resource, _frame));
    }

    private void DestroyRetiredFonts()
    {
        var threshold = _frame - 3;
        for (var i = _retiredFonts.Count - 1; i >= 0; i--)
        {
            if (_retiredFonts[i].Frame <= threshold)
            {
                DestroyFontResource(_retiredFonts[i].Resource, freeDescriptorSet: true);
                _retiredFonts.RemoveAt(i);
            }
        }
    }

    private void DestroyRetiredStaticTextures()
    {
        var threshold = _frame - 3;
        for (var i = _retiredStaticTextures.Count - 1; i >= 0; i--)
        {
            if (_retiredStaticTextures[i].Frame <= threshold)
            {
                DestroyFontResource(_retiredStaticTextures[i].Resource, freeDescriptorSet: true);
                _retiredStaticTextures.RemoveAt(i);
            }
        }
    }

    private CubeMapResource CreateCubeMapResource(
        IReadOnlyList<ImGuiCubeMapFace> faces,
        int width,
        int height,
        ImGuiTextureFilter filter)
    {
        var faceSize = checked(width * height * FontBytesPerPixel);
        var pixels = new byte[checked(faceSize * 6)];
        for (var faceIndex = 0; faceIndex < faces.Count; faceIndex++)
        {
            var source = faces[faceIndex].Rgba8.Span;
            var destination = pixels.AsSpan(faceIndex * faceSize, faceSize);
            var rowSize = checked(width * FontBytesPerPixel);
            for (var row = 0; row < height; row++)
            {
                // Minecraft CubeMapTexture.copyRect(..., false, true) flips each face vertically before upload.
                source.Slice((height - row - 1) * rowSize, rowSize)
                    .CopyTo(destination.Slice(row * rowSize, rowSize));
            }
        }

        var imageSize = (ulong)pixels.Length;
        var staging = CreateHostBuffer(imageSize, BufferUsageFlags.TransferSrcBit);
        void* mapped;
        ThrowOnError(_vk.MapMemory(_device, staging.Memory, 0, imageSize, 0, &mapped), "MapMemory(ImGui CubeMap)");
        fixed (byte* source = pixels)
        {
            System.Buffer.MemoryCopy(source, mapped, imageSize, imageSize);
        }

        _vk.UnmapMemory(_device, staging.Memory);
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Srgb,
            Extent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 6,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        ThrowOnError(_vk.CreateImage(_device, &imageInfo, null, out var image), "CreateImage(ImGui CubeMap)");
        _vk.GetImageMemoryRequirements(_device, image, out var requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        ThrowOnError(_vk.AllocateMemory(_device, &allocInfo, null, out var memory), "AllocateMemory(ImGui CubeMap)");
        _vk.BindImageMemory(_device, image, memory, 0);

        var cmd = BeginSingleTimeCommands();
        try
        {
            TransitionImageLayout(cmd, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 6);
            var regions = stackalloc BufferImageCopy[6];
            for (var faceIndex = 0; faceIndex < 6; faceIndex++)
            {
                regions[faceIndex] = new BufferImageCopy
                {
                    BufferOffset = (ulong)(faceIndex * faceSize),
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = (uint)faceIndex,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
                };
            }

            _vk.CmdCopyBufferToImage(cmd, staging.Buffer, image, ImageLayout.TransferDstOptimal, 6, regions);
            TransitionImageLayout(cmd, image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, 6);
            EndSingleTimeCommands(cmd);
        }
        catch
        {
            EndSingleTimeCommands(cmd);
            throw;
        }

        _vk.DestroyBuffer(_device, staging.Buffer, null);
        _vk.FreeMemory(_device, staging.Memory, null);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.TypeCube,
            Format = Format.R8G8B8A8Srgb,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 6,
            },
        };
        ThrowOnError(_vk.CreateImageView(_device, &viewInfo, null, out var view), "CreateImageView(ImGui CubeMap)");

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter == ImGuiTextureFilter.Nearest ? Filter.Nearest : Filter.Linear,
            MinFilter = filter == ImGuiTextureFilter.Nearest ? Filter.Nearest : Filter.Linear,
            MipmapMode = filter == ImGuiTextureFilter.Nearest ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 1f,
        };
        ThrowOnError(_vk.CreateSampler(_device, &samplerInfo, null, out var sampler), "CreateSampler(ImGui CubeMap)");

        var textureLayout = _cubeMapLayout;
        var alloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &textureLayout,
        };
        ThrowOnError(_vk.AllocateDescriptorSets(_device, &alloc, out var set), "AllocateDescriptorSets(ImGui CubeMap)");
        var imageDescriptor = new DescriptorImageInfo
        {
            Sampler = sampler,
            ImageView = view,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageDescriptor,
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);

        return new CubeMapResource
        {
            TextureId = set.Handle,
            Width = width,
            Height = height,
            Image = image,
            Memory = memory,
            View = view,
            Sampler = sampler,
            Set = set,
        };
    }

    private void DestroyCubeMapResource(CubeMapResource resource, bool freeDescriptorSet)
    {
        _vk.DestroySampler(_device, resource.Sampler, null);
        _vk.DestroyImageView(_device, resource.View, null);
        _vk.DestroyImage(_device, resource.Image, null);
        _vk.FreeMemory(_device, resource.Memory, null);
        if (freeDescriptorSet)
        {
            var set = resource.Set;
            ThrowOnError(_vk.FreeDescriptorSets(_device, _descriptorPool, 1, &set), "FreeDescriptorSets(ImGui CubeMap)");
        }
    }

    private FontTextureResource CreateFontResource(ImTextureDataPtr texData)
    {
        var width = texData.Width;
        var height = texData.Height;
        var bytesPerPixel = texData.BytesPerPixel;
        if (bytesPerPixel != 4)
        {
            throw new InvalidOperationException($"ImGui 字体图集必须是 RGBA8，实际 bpp={bytesPerPixel}");
        }

        return CreateTextureResource(
            new ReadOnlySpan<byte>(texData.Pixels, checked(width * height * bytesPerPixel)),
            width,
            height,
            ImGuiTextureFilter.Linear,
            "font",
            isColorTexture: false);
    }

    private FontTextureResource CreateTextureResource(
        ReadOnlySpan<byte> rgba8,
        int width,
        int height,
        ImGuiTextureFilter filter,
        string textureKind,
        bool isColorTexture)
    {
        var textureFormat = isColorTexture ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
        var imageSize = (ulong)width * (ulong)height * FontBytesPerPixel;
        var staging = CreateHostBuffer(imageSize, BufferUsageFlags.TransferSrcBit);
        void* mapped;
        ThrowOnError(_vk.MapMemory(_device, staging.Memory, 0, imageSize, 0, &mapped), $"MapMemory(ImGui {textureKind})");
        fixed (byte* pixels = rgba8)
        {
            System.Buffer.MemoryCopy(pixels, mapped, imageSize, imageSize);
        }
        _vk.UnmapMemory(_device, staging.Memory);

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = textureFormat,
            Extent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        ThrowOnError(_vk.CreateImage(_device, &imageInfo, null, out var fontImage), $"CreateImage(ImGui {textureKind})");
        _vk.GetImageMemoryRequirements(_device, fontImage, out var requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        ThrowOnError(_vk.AllocateMemory(_device, &allocInfo, null, out var fontMemory), $"AllocateMemory(ImGui {textureKind})");
        _vk.BindImageMemory(_device, fontImage, fontMemory, 0);

        var cmd = BeginSingleTimeCommands();
        try
        {
            TransitionImageLayout(cmd, fontImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
            };
            _vk.CmdCopyBufferToImage(cmd, staging.Buffer, fontImage, ImageLayout.TransferDstOptimal, 1, &region);
            TransitionImageLayout(cmd, fontImage, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            EndSingleTimeCommands(cmd);
        }
        catch
        {
            EndSingleTimeCommands(cmd);
            throw;
        }

        _vk.DestroyBuffer(_device, staging.Buffer, null);
        _vk.FreeMemory(_device, staging.Memory, null);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = fontImage,
            ViewType = ImageViewType.Type2D,
            Format = textureFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        ThrowOnError(_vk.CreateImageView(_device, &viewInfo, null, out var fontView), $"CreateImageView(ImGui {textureKind})");

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter == ImGuiTextureFilter.Nearest ? Filter.Nearest : Filter.Linear,
            MinFilter = filter == ImGuiTextureFilter.Nearest ? Filter.Nearest : Filter.Linear,
            MipmapMode = filter == ImGuiTextureFilter.Nearest ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MaxLod = 1f,
        };
        ThrowOnError(_vk.CreateSampler(_device, &samplerInfo, null, out var fontSampler), $"CreateSampler(ImGui {textureKind})");

        var textureLayout = _textureLayout;
        var alloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &textureLayout,
        };
        ThrowOnError(_vk.AllocateDescriptorSets(_device, &alloc, out var fontSet), "AllocateDescriptorSets(ImGui)");

        var imageInfo2 = new DescriptorImageInfo
        {
            Sampler = fontSampler,
            ImageView = fontView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = fontSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo2,
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);

        return new FontTextureResource
        {
            TextureId = fontSet.Handle,
            Width = width,
            Height = height,
            Image = fontImage,
            Memory = fontMemory,
            View = fontView,
            Sampler = fontSampler,
            Set = fontSet,
        };
    }

    private void UploadFontPixels(ImTextureDataPtr texData, FontTextureResource resource)
    {
        UploadPixels(
            new ReadOnlySpan<byte>(texData.Pixels, checked(resource.Width * resource.Height * FontBytesPerPixel)),
            resource,
            "ImGui font update");
    }

    private void UploadPixels(ReadOnlySpan<byte> rgba8, FontTextureResource resource, string operation)
    {
        var imageSize = (ulong)rgba8.Length;
        var staging = CreateHostBuffer(imageSize, BufferUsageFlags.TransferSrcBit);
        void* mapped;
        ThrowOnError(_vk.MapMemory(_device, staging.Memory, 0, imageSize, 0, &mapped), $"MapMemory({operation})");
        fixed (byte* source = rgba8)
        {
            System.Buffer.MemoryCopy(source, mapped, imageSize, imageSize);
        }
        _vk.UnmapMemory(_device, staging.Memory);

        var cmd = BeginSingleTimeCommands();
        try
        {
            TransitionImageLayout(cmd, resource.Image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D { Width = (uint)resource.Width, Height = (uint)resource.Height, Depth = 1 },
            };
            _vk.CmdCopyBufferToImage(cmd, staging.Buffer, resource.Image, ImageLayout.TransferDstOptimal, 1, &region);
            TransitionImageLayout(cmd, resource.Image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            EndSingleTimeCommands(cmd);
        }
        catch
        {
            EndSingleTimeCommands(cmd);
            throw;
        }

        _vk.DestroyBuffer(_device, staging.Buffer, null);
        _vk.FreeMemory(_device, staging.Memory, null);
    }

    private void DestroyFontResource(FontTextureResource resource, bool freeDescriptorSet)
    {
        _vk.DestroySampler(_device, resource.Sampler, null);
        _vk.DestroyImageView(_device, resource.View, null);
        _vk.DestroyImage(_device, resource.Image, null);
        _vk.FreeMemory(_device, resource.Memory, null);
        if (freeDescriptorSet)
        {
            var set = resource.Set;
            ThrowOnError(_vk.FreeDescriptorSets(_device, _descriptorPool, 1, &set), "FreeDescriptorSets(ImGui font)");
        }
    }

    private void CreatePipeline()
    {
        // 入口名 "main\\0"：必须与管线创建同栈作用域（stackalloc 返回后即失效）
        var mainName = stackalloc byte[5] { 109, 97, 105, 110, 0 };
        var vertexSpv = EmbeddedImGuiShaders.Vertex();
        var fragmentSpv = EmbeddedImGuiShaders.Fragment();

        var vertModule = CreateShaderModule(vertexSpv);
        var fragModule = CreateShaderModule(fragmentSpv);
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertModule,
                PName = mainName,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragModule,
                PName = mainName,
            };

            var binding = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = (uint)sizeof(ImDrawVert),
                InputRate = VertexInputRate.Vertex,
            };
            var attributes = stackalloc VertexInputAttributeDescription[3];
            attributes[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 };
            attributes[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 8 };
            attributes[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R8G8B8A8Unorm, Offset = 16 };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions = attributes,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewport = new Viewport { Width = 0, Height = 0, MinDepth = 0, MaxDepth = 1 };
            var scissor = new Rect2D();
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor,
            };
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                DepthBiasEnable = false,
                LineWidth = 1f,
            };
            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var depthStencil = VulkanRenderPassPolicy.CreateOverlayDepthStencilState();
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var colorBlending = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };

            var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            var textureLayout = _textureLayout;
            var pushConstant = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit,
                Offset = 0,
                Size = (uint)sizeof(ImGuiPushConstants),
            };
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &textureLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstant,
            };
            ThrowOnError(_vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelineLayout), "CreatePipelineLayout(ImGui)");

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlending,
                PDynamicState = &dynamicState,
                Layout = _pipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
            ThrowOnError(_vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, out _pipeline), "CreateGraphicsPipelines(ImGui)");
        }
        finally
        {
            _vk.DestroyShaderModule(_device, vertModule, null);
            _vk.DestroyShaderModule(_device, fragModule, null);
        }
    }

    private void CreateCubeMapPipeline()
    {
        var mainName = stackalloc byte[5] { 109, 97, 105, 110, 0 };
        var vertexModule = CreateShaderModule(EmbeddedImGuiShaders.CubeVertex());
        var fragmentModule = CreateShaderModule(EmbeddedImGuiShaders.CubeFragment());
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertexModule,
                PName = mainName,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragmentModule,
                PName = mainName,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewport = new Viewport { Width = 0, Height = 0, MinDepth = 0, MaxDepth = 1 };
            var scissor = new Rect2D();
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor,
            };
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var depthStencil = VulkanRenderPassPolicy.CreateOverlayDepthStencilState();
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = false,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                    ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var colorBlending = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };
            var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };
            var cubeMapLayout = _cubeMapLayout;
            var pushConstants = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)sizeof(CubeMapPushConstants),
            };
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &cubeMapLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstants,
            };
            ThrowOnError(_vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _cubeMapPipelineLayout), "CreatePipelineLayout(ImGui CubeMap)");

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlending,
                PDynamicState = &dynamicState,
                Layout = _cubeMapPipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
            ThrowOnError(_vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, out _cubeMapPipeline), "CreateGraphicsPipelines(ImGui CubeMap)");
        }
        finally
        {
            _vk.DestroyShaderModule(_device, vertexModule, null);
            _vk.DestroyShaderModule(_device, fragmentModule, null);
        }
    }

    private ShaderModule CreateShaderModule(byte[] spv)
    {
        fixed (byte* code = spv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spv.Length,
                PCode = (uint*)code,
            };
            ThrowOnError(_vk.CreateShaderModule(_device, &info, null, out var module), "CreateShaderModule(ImGui)");
            return module;
        }
    }

    private CommandBuffer BeginSingleTimeCommands()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        _vk.AllocateCommandBuffers(_device, &allocInfo, out var cmd);
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(cmd, &begin);
        return cmd;
    }

    private void EndSingleTimeCommands(CommandBuffer cmd)
    {
        _vk.EndCommandBuffer(cmd);
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        _vk.QueueSubmit(_queue, 1, &submit, default);
        _vk.QueueWaitIdle(_queue);
        _vk.FreeCommandBuffers(_device, _commandPool, 1, &cmd);
    }

    private void TransitionImageLayout(
        CommandBuffer cmd,
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        uint layerCount = 1)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = uint.MaxValue,
            DstQueueFamilyIndex = uint.MaxValue,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = layerCount,
            },
            SrcAccessMask = oldLayout switch
            {
                ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
                ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
                _ => AccessFlags.None,
            },
            DstAccessMask = newLayout switch
            {
                ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
                ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
                _ => AccessFlags.None,
            },
        };
        var srcStage = oldLayout switch
        {
            ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
            ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags.FragmentShaderBit,
            _ => PipelineStageFlags.TopOfPipeBit,
        };
        var dstStage = newLayout switch
        {
            ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
            ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags.FragmentShaderBit,
            _ => PipelineStageFlags.BottomOfPipeBit,
        };
        _vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private static void ThrowOnError(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} 失败: {result}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context.SwapchainFormatChanged -= RecreateForSwapchainFormat;
        _vk.DeviceWaitIdle(_device);
        foreach (var (buffer, memory, _) in _retired)
        {
            _vk.DestroyBuffer(_device, buffer, null);
            _vk.FreeMemory(_device, memory, null);
        }

        _retired.Clear();
        foreach (var buffers in _frameBuffers)
        {
            if (buffers.VertexBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, buffers.VertexBuffer, null);
                _vk.FreeMemory(_device, buffers.VertexMemory, null);
            }

            if (buffers.IndexBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, buffers.IndexBuffer, null);
                _vk.FreeMemory(_device, buffers.IndexMemory, null);
            }
        }

        foreach (var resource in _fontTextures.Values)
        {
            DestroyFontResource(resource, freeDescriptorSet: false);
        }
        _fontTextures.Clear();
        foreach (var (resource, _) in _retiredFonts)
        {
            DestroyFontResource(resource, freeDescriptorSet: false);
        }
        _retiredFonts.Clear();
        foreach (var resource in _staticTextures.Values)
        {
            DestroyFontResource(resource, freeDescriptorSet: false);
        }
        _staticTextures.Clear();
        foreach (var (resource, _) in _retiredStaticTextures)
        {
            DestroyFontResource(resource, freeDescriptorSet: false);
        }
        _retiredStaticTextures.Clear();
        foreach (var resource in _cubeMaps.Values)
        {
            DestroyCubeMapResource(resource, freeDescriptorSet: false);
        }
        _cubeMaps.Clear();
        _textureSets.Clear();
        _frameTextureSets.Clear();
        _vk.DestroyPipeline(_device, _pipeline, null);
        _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk.DestroyPipeline(_device, _cubeMapPipeline, null);
        _vk.DestroyPipelineLayout(_device, _cubeMapPipelineLayout, null);
        _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk.DestroyDescriptorSetLayout(_device, _textureLayout, null);
        _vk.DestroyDescriptorSetLayout(_device, _cubeMapLayout, null);
        _vk.DestroyRenderPass(_device, _renderPass, null);
        if (_glyphRangesPtr != null)
        {
            Marshal.FreeHGlobal((nint)_glyphRangesPtr);
            _glyphRangesPtr = null;
        }

        ImGuiApi.DestroyContext();
    }

}

