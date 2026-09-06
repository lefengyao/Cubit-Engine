using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Cubit.Core.Imaging;
using Cubit.Core.Platform;
using Cubit.Core.Shaders;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Cubit.Core.Rendering;

/// <summary>
/// Vulkan 上下文：实例/设备/交换链/渲染管线/帧循环 + 通用 RID 网格绘制。
/// 平台无关：窗口与表面通过 <see cref="IGameWindow"/> 注入。
/// </summary>
public sealed unsafe class VulkanContext : IDisposable, IRenderBackend
{
    private const int MaxFramesInFlight = 2;
    private const uint UniformBufferSize = 128; // view + proj 两个 mat4

    private readonly IGameWindow _window;
    private readonly Vk _vk = Vk.GetApi();
    private readonly KhrSurface _khrSurface;
    private readonly KhrSwapchain _khrSwapchain;

    private Instance _instance;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private string _physicalDeviceName = "Unknown Vulkan device";
    private uint _physicalDeviceVendorId;
    private uint _physicalDeviceApiVersion;
    private uint _physicalDeviceDriverVersion;
    private Device _device;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private uint _graphicsFamily;
    private uint _presentFamily;

    private SwapchainKHR _swapchain;
    private Format _swapchainFormat;
    private Extent2D _extent;
    private Image[] _swapchainImages = [];
    private ImageView[] _imageViews = [];
    private Image _depthImage;
    private DeviceMemory _depthImageMemory;
    private ImageView _depthImageView;
    private Framebuffer[] _framebuffers = [];

    private RenderPass _renderPass;
    private RenderPass _offscreenRenderPass;
    private PipelineLayout _pipelineLayout;
    private Pipeline _backgroundPipeline;
    private Pipeline _backgroundAlphaBlendPipeline;
    private Pipeline _opaquePipeline;
    private Pipeline _overlayPipeline;
    private Pipeline _alphaBlendPipeline;
    private Pipeline _backgroundVertexColorPipeline;
    private Pipeline _backgroundAlphaBlendVertexColorPipeline;
    private Pipeline _opaqueVertexColorPipeline;
    private Pipeline _overlayVertexColorPipeline;
    private Pipeline _alphaBlendVertexColorPipeline;
    private Pipeline _backgroundTiledPipeline;
    private Pipeline _backgroundAlphaBlendTiledPipeline;
    private Pipeline _opaqueTiledPipeline;
    private Pipeline _overlayTiledPipeline;
    private Pipeline _alphaBlendTiledPipeline;
    private ShaderModule _vertShader;
    private ShaderModule _vertexColorShader;
    private ShaderModule _fragShader;
    private ShaderModule _tiledVertexShader;
    private ShaderModule _tiledFragShader;

    private Image _fallbackImage;
    private DeviceMemory _fallbackMemory;
    private ImageView _fallbackView;
    private Sampler _fallbackSampler;

    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet[] _descriptorSets = new DescriptorSet[MaxFramesInFlight];
    private Buffer[] _uboBuffers = new Buffer[MaxFramesInFlight];
    private DeviceMemory[] _uboMemories = new DeviceMemory[MaxFramesInFlight];
    private void*[] _uboMapped = new void*[MaxFramesInFlight];
    private Matrix4x4 _cameraView;
    private Matrix4x4 _cameraProjection;

    private CommandPool _commandPool;
    private CommandBuffer[] _commandBuffers = new CommandBuffer[MaxFramesInFlight];
    // acquire semaphore 不与 frame slot 固定绑定：Godot 按命令队列维护动态池，
    // 只有对应提交 fence 完成后才允许把 semaphore 放回空闲列表。
    private readonly List<Semaphore> _imageAvailable = [];
    private readonly List<int> _imageAvailableFree = [];
    private readonly int[] _frameAcquireSemaphore = Enumerable.Repeat(-1, MaxFramesInFlight).ToArray();
    private Semaphore[] _renderFinished = [];
    private readonly List<Semaphore[]> _retiredRenderFinished = [];
    private Fence[] _inFlightFences = new Fence[MaxFramesInFlight];
    // 每个交换链图像最后一次提交使用的 fence。帧槽 fence 完成不代表任意 acquired image 都可安全复用。
    private Fence[] _imageInFlightFences = [];

    private readonly Dictionary<ulong, GpuMesh> _meshes = [];
    private readonly Dictionary<ulong, GpuTexture> _textures = [];
    private readonly Dictionary<ulong, GpuMaterial> _materials = [];
    private readonly VulkanFrameRetirementQueue<GpuMesh> _retiredMeshes = new(MaxFramesInFlight);
    private readonly VulkanFrameRetirementQueue<GpuMaterial> _retiredMaterials = new(MaxFramesInFlight);
    private readonly VulkanFrameRetirementQueue<GpuTexture> _retiredTextures = new(MaxFramesInFlight);

    private int _currentFrame;
    private long _frameNumber;
    private string? _screenshotPath;
    private bool _captureRequested;
    private Buffer _captureBuffer;
    private DeviceMemory _captureMemory;
    private bool _resizeRequested;
    private bool _frameSubmitted;
    private bool _surfaceLost;
    private float _surfaceRotationDegrees;
    private long _lastSurfaceRecreateTicks;
    private long _retryAfterTicks;
    private readonly bool[] _fencePending = new bool[MaxFramesInFlight];
    private bool _warnedSuboptimal;
    private bool _firstFrameDiagnosticLogged;
    private readonly DisplaySettings _display;
    private Vector4 _sceneClearColor = DefaultSceneClearColor;
    private Vector4 _environmentModulation = Vector4.One;
    private bool _disposed;

    private struct GpuMesh
    {
        public Buffer VertexBuffer;
        public DeviceMemory VertexMemory;
        public Buffer IndexBuffer;
        public DeviceMemory IndexMemory;
        public uint IndexCount;
        public long VertexCount;
        public Vector3 Translation;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public Vector3 LocalBoundsMin;
        public Vector3 LocalBoundsMax;
        public RID Material;
        public MeshVertexLayout VertexLayout;
        public Matrix4x4 ModelMatrix;
        public Vector4 Modulation;
    }

    private struct GpuTexture
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public Sampler Sampler;
    }

    private sealed class GpuMaterial
    {
        public required RID Texture { get; init; }

        public required MaterialBlendMode BlendMode { get; init; }

        public required DescriptorSet[] DescriptorSets { get; init; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public Matrix4x4 Model;
        public Vector4 Modulation;
    }

public VulkanContext(IGameWindow window, DisplaySettings display)
    {
        _display = display;
        _window = window;
        _khrSurface = new KhrSurface(_vk.Context);
        _khrSwapchain = new KhrSwapchain(_vk.Context);
        _window.Resize += (_, _) => _resizeRequested = true;
        CreateInstance();
        _surface = _window.CreateVulkanSurface(_vk, _instance);
        PickPhysicalDevice();
        CreateDevice();
        CreateSwapchain();
        CreateDepthResources();
        CreateRenderPass();
        CreateFramebuffers();
        CreateUniformBuffers();
        CreateCommandPoolAndBuffers();
        CreateFallbackTextureResources();
        CreateDescriptorObjects();
        CreatePipeline();
        CreateSyncObjects();
        PrimeGraphicsQueue();
    }

    public (int Width, int Height) FramebufferSize
    {
        get
        {
            if (_extent.Width == 0 || _extent.Height == 0)
            {
                return _window.FramebufferSize;
            }

            var rect = _display.ComputeRenderRect((int)_extent.Width, (int)_extent.Height);
            return (rect.Width, rect.Height);
        }
    }

    /// <summary>已上传的区块网格数量（调试用）。</summary>
    private static float SurfaceTransformDegrees(SurfaceTransformFlagsKHR transform)
    {
        return transform switch
        {
            SurfaceTransformFlagsKHR.Rotate90BitKhr => 90f,
            SurfaceTransformFlagsKHR.Rotate180BitKhr => 180f,
            SurfaceTransformFlagsKHR.Rotate270BitKhr => 270f,
            _ => 0f,
        };
    }

    /// <summary>表面预旋转补偿：按设备旋转角在裁剪空间旋转投影，保证输出始终正向（Android 自动旋转支持，Godot 同思路）。</summary>
    private Matrix4x4 ApplySurfaceRotation(Matrix4x4 projection)
    {
        // 存储矩阵按行主序、GPU 按列主序读取（等效转置），因此右侧乘 R^T 即可让 GPU 端得到 R * proj。
        // R 为裁剪空间旋转：90° 时顶部→左侧，抵消合成器的 +90° 旋转。
        var rotT = _surfaceRotationDegrees switch
        {
            90f => new Matrix4x4(0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
            180f => new Matrix4x4(-1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
            270f => new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
            _ => Matrix4x4.Identity,
        };
        return projection * rotT;
    }

    /// <summary>编辑器/工具访问：物理设备。</summary>
    public PhysicalDevice PhysicalDevice => _physicalDevice;

    /// <summary>物理设备名称，供项目调试呈现读取；不携带平台或游戏语义。</summary>
    public string PhysicalDeviceName => _physicalDeviceName;

    /// <summary>物理设备 PCI vendor ID，供调试呈现和诊断记录使用。</summary>
    public uint PhysicalDeviceVendorId => _physicalDeviceVendorId;

    /// <summary>物理设备报告的 Vulkan API 版本。</summary>
    public uint PhysicalDeviceApiVersion => _physicalDeviceApiVersion;

    /// <summary>物理设备驱动版本。</summary>
    public uint PhysicalDeviceDriverVersion => _physicalDeviceDriverVersion;

    /// <summary>编辑器/工具访问：Vulkan 设备句柄（ImGui 后端等创建资源用）。</summary>
    public Device Device => _device;

    /// <summary>编辑器/工具访问：Vulkan API 入口。</summary>
    public Vk VkApi => _vk;

    /// <summary>编辑器/工具访问：图形队列。</summary>
    public Queue GraphicsQueue => _graphicsQueue;

    /// <summary>编辑器/工具访问：图形队列族。</summary>
    public uint GraphicsFamily => _graphicsFamily;

    /// <summary>编辑器/工具访问：交换链图像格式。</summary>
    public Format SwapchainFormat => _swapchainFormat;

    /// <summary>编辑器/工具访问：命令池（ImGui 后端上传字体等）。</summary>
    public CommandPool CommandPool => _commandPool;
    /// <summary>允许并行提交的帧槽数量，覆盖层必须按帧槽隔离动态缓冲。</summary>
    public int FramesInFlight => MaxFramesInFlight;

    /// <summary>交换链像素尺寸，供覆盖层在 NewFrame 前设置显示尺寸。</summary>
    public (int Width, int Height) SwapchainSize => ((int)_extent.Width, (int)_extent.Height);

    /// <summary>默认场景背景色；项目可在主线程更新，Core 不解释其领域含义。</summary>
    public static Vector4 DefaultSceneClearColor => new(0.45f, 0.68f, 0.95f, 1f);

    /// <summary>下一帧场景颜色附件的通用清屏色。不能由后台线程调用。</summary>
    public Vector4 SceneClearColor
    {
        get => _sceneClearColor;
        set => SetSceneClearColor(value);
    }

    public void SetSceneClearColor(Vector4 color)
    {
        if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) || !float.IsFinite(color.W) ||
            color.X < 0f || color.X > 1f || color.Y < 0f || color.Y > 1f ||
            color.Z < 0f || color.Z > 1f || color.W < 0f || color.W > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(color), "场景清屏色必须为 0..1 的有限 RGBA 值");
        }

        _sceneClearColor = color;
    }

    /// <summary>UI 叠加回调：末参数为当前在途帧槽，不是交换链图像索引。</summary>
    public event Action<CommandBuffer, Framebuffer, Extent2D, Format, uint>? Overlay;

    /// <summary>交换链颜色格式变化时通知后端重建格式相关管线。</summary>
    public event Action<Format>? SwapchainFormatChanged;

    /// <summary>平台宿主可订阅的低频 Vulkan 诊断；Core 不依赖 Android 或其他平台日志 API。</summary>
    public event Action<string>? Diagnostic;

    /// <summary>编辑器模式：场景渲染到离屏纹理（供编辑器视口显示），UI 渲染到交换链。</summary>
    public bool RenderSceneOffscreen { get; set; }

    private sealed class OffscreenTarget
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public Sampler Sampler;
        public Image DepthImage;
        public DeviceMemory DepthMemory;
        public ImageView DepthView;
        public Framebuffer Framebuffer;
        public int Width;
        public int Height;
        public Format Format;
        public ulong Version;
    }

    private readonly OffscreenTarget[] _offscreenTargets =
        Enumerable.Range(0, MaxFramesInFlight).Select(_ => new OffscreenTarget()).ToArray();
    private ulong _offscreenVersion;

    /// <summary>当前将要录制的帧槽。</summary>
    public uint CurrentFrameSlot => (uint)_currentFrame;

    /// <summary>取得指定帧槽的离屏纹理；帧槽在自身 fence 完成后才会被复用。</summary>
    public OffscreenTextureInfo GetOffscreenTexture(uint frameSlot)
    {
        if (frameSlot >= _offscreenTargets.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        }

        var target = _offscreenTargets[frameSlot];
        return new OffscreenTextureInfo(target.Version, target.View, target.Sampler, target.Width, target.Height);
    }
    /// <summary>请求重建交换链与表面（Android 恢复前台时调用；下一帧执行，可安全重试）。</summary>
    public void InvalidateSurface()
    {
        _resizeRequested = true;
    }

    public int MeshCount => _meshes.Count;

    private long _totalVertices;

    /// <summary>已上传的总顶点数（调试用）。</summary>
    public long TotalVertices => _totalVertices;

    /// <summary>已渲染帧数。</summary>
    public long FrameCount => _frameNumber;

    /// <summary>待延迟销毁的旧网格数量。</summary>
    public int RetiredMeshCount => _retiredMeshes.Count;

    /// <summary>设置相机（每帧调用；下一帧渲染使用）。</summary>
    public void UpdateCamera(in Matrix4x4 view, in Matrix4x4 projection)
    {
        _cameraView = view;
        _cameraProjection = ApplySurfaceRotation(projection);
    }

    public void SetTexture(RID rid, TextureData texture)
    {
        if (!rid.IsValid)
        {
            throw new ArgumentException("纹理 RID 必须有效", nameof(rid));
        }

        if (_textures.Remove(rid.Value, out var old))
        {
            RetireTexture(old);
        }

        _textures.Add(rid.Value, CreateTextureResources(texture));
    }

    public void RemoveTexture(RID rid)
    {
        if (rid.IsValid && _textures.Remove(rid.Value, out var texture))
        {
            RetireTexture(texture);
        }
    }

    public void SetMaterial(RID rid, MaterialData material)
    {
        if (!rid.IsValid || !_textures.TryGetValue(material.AlbedoTexture.Value, out var texture))
        {
            throw new InvalidOperationException($"材质引用了未知纹理 RID: {material.AlbedoTexture}");
        }

        if (_materials.Remove(rid.Value, out var old))
        {
            RetireMaterial(old);
        }

        var descriptorSets = AllocateMaterialDescriptorSets(texture);
        _materials.Add(rid.Value, new GpuMaterial
        {
            Texture = material.AlbedoTexture,
            BlendMode = material.BlendMode,
            DescriptorSets = descriptorSets,
        });
    }

    public void RemoveMaterial(RID rid)
    {
        if (rid.IsValid && _materials.Remove(rid.Value, out var material))
        {
            RetireMaterial(material);
        }
    }

    /// <summary>创建或替换指定 RID 的 GPU 网格。</summary>
    public void SetMesh(RID rid, MeshData meshData)
    {
        if (!rid.IsValid)
        {
            throw new ArgumentException("网格 RID 必须有效", nameof(rid));
        }

        ArgumentNullException.ThrowIfNull(meshData);
        if (_meshes.Remove(rid.Value, out var old))
        {
            // 旧缓冲可能仍在 GPU 上使用：延迟到 2 帧安全期后销毁，避免卡顿与设备丢失
            _totalVertices -= old.VertexCount;
            RetireMesh(old);
        }

        var mesh = new GpuMesh
        {
            VertexBuffer = CreateHostBuffer(meshData.Vertices, BufferUsageFlags.VertexBufferBit, out var vertexMemory),
            IndexBuffer = CreateHostBuffer(meshData.Indices, BufferUsageFlags.IndexBufferBit, out var indexMemory),
            VertexMemory = vertexMemory,
            IndexMemory = indexMemory,
            IndexCount = (uint)meshData.Indices.Length,
            VertexCount = meshData.Vertices.Length / meshData.VertexStride,
            Translation = meshData.Translation,
            BoundsMin = meshData.BoundsMin,
            BoundsMax = meshData.BoundsMax,
            LocalBoundsMin = meshData.BoundsMin,
            LocalBoundsMax = meshData.BoundsMax,
            Material = meshData.Material,
            VertexLayout = meshData.VertexLayout,
            ModelMatrix = Matrix4x4.CreateTranslation(meshData.Translation),
            Modulation = meshData.Modulation,
        };
        _totalVertices += mesh.VertexCount;
        _meshes[rid.Value] = mesh;
    }

    /// <summary>移除指定 RID 的 GPU 网格。</summary>
    public void RemoveMesh(RID rid)
    {
        if (rid.IsValid && _meshes.Remove(rid.Value, out var mesh))
        {
            _totalVertices -= mesh.VertexCount;
            RetireMesh(mesh);
        }
    }

    public void UpdateMeshTransform(RID rid, in Matrix4x4 transform)
    {
        if (rid.IsValid && _meshes.TryGetValue(rid.Value, out var mesh))
        {
            mesh.ModelMatrix = transform;
            TransformBounds(ref mesh, in transform);
            _meshes[rid.Value] = mesh;
        }
    }

    public void UpdateMeshModulation(RID rid, in Vector4 modulation)
    {
        MeshData.ValidateModulation(modulation);
        if (rid.IsValid && _meshes.TryGetValue(rid.Value, out var mesh))
        {
            mesh.Modulation = modulation;
            _meshes[rid.Value] = mesh;
        }
    }

    public void SetEnvironmentModulation(in Vector4 modulation)
    {
        MeshData.ValidateModulation(modulation);
        _environmentModulation = modulation;
    }

    private MaterialBlendMode GetBlendMode(in GpuMesh mesh) =>
        mesh.Material.IsValid && _materials.TryGetValue(mesh.Material.Value, out var material)
            ? material.BlendMode
            : MaterialBlendMode.Opaque;

    private Pipeline GetPipeline(in GpuMesh mesh) => mesh.VertexLayout switch
    {
        MeshVertexLayout.PositionUvShade => GetPipeline(mesh.Material),
        MeshVertexLayout.PositionUvShadeColor => GetVertexColorPipeline(mesh.Material),
        MeshVertexLayout.PositionUvShadeTile => GetTiledPipeline(mesh.Material),
        _ => throw new InvalidOperationException($"不支持的 GPU 网格顶点布局: {mesh.VertexLayout}"),
    };

    private Pipeline GetPipeline(RID materialRid) =>
        GetPipeline(materialRid.IsValid && _materials.TryGetValue(materialRid.Value, out var material)
            ? material.BlendMode
            : MaterialBlendMode.Opaque);

    private Pipeline GetPipeline(MaterialBlendMode blendMode) => blendMode switch
    {
        MaterialBlendMode.Background => _backgroundPipeline,
        MaterialBlendMode.BackgroundAlphaBlend => _backgroundAlphaBlendPipeline,
        MaterialBlendMode.Opaque => _opaquePipeline,
        MaterialBlendMode.Overlay => _overlayPipeline,
        MaterialBlendMode.AlphaBlend => _alphaBlendPipeline,
        _ => throw new ArgumentOutOfRangeException(nameof(blendMode)),
    };

    private Pipeline GetVertexColorPipeline(RID materialRid) =>
        GetVertexColorPipeline(materialRid.IsValid && _materials.TryGetValue(materialRid.Value, out var material)
            ? material.BlendMode
            : MaterialBlendMode.Opaque);

    private Pipeline GetVertexColorPipeline(MaterialBlendMode blendMode) => blendMode switch
    {
        MaterialBlendMode.Background => _backgroundVertexColorPipeline,
        MaterialBlendMode.BackgroundAlphaBlend => _backgroundAlphaBlendVertexColorPipeline,
        MaterialBlendMode.Opaque => _opaqueVertexColorPipeline,
        MaterialBlendMode.Overlay => _overlayVertexColorPipeline,
        MaterialBlendMode.AlphaBlend => _alphaBlendVertexColorPipeline,
        _ => throw new ArgumentOutOfRangeException(nameof(blendMode)),
    };

    private Pipeline GetTiledPipeline(RID materialRid) =>
        GetTiledPipeline(materialRid.IsValid && _materials.TryGetValue(materialRid.Value, out var material)
            ? material.BlendMode
            : MaterialBlendMode.Opaque);

    private Pipeline GetTiledPipeline(MaterialBlendMode blendMode) => blendMode switch
    {
        MaterialBlendMode.Background => _backgroundTiledPipeline,
        MaterialBlendMode.BackgroundAlphaBlend => _backgroundAlphaBlendTiledPipeline,
        MaterialBlendMode.Opaque => _opaqueTiledPipeline,
        MaterialBlendMode.Overlay => _overlayTiledPipeline,
        MaterialBlendMode.AlphaBlend => _alphaBlendTiledPipeline,
        _ => throw new ArgumentOutOfRangeException(nameof(blendMode)),
    };

    private static void TransformBounds(ref GpuMesh mesh, in Matrix4x4 transform)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var x = 0; x < 2; x++)
        {
            for (var y = 0; y < 2; y++)
            {
                for (var z = 0; z < 2; z++)
                {
                    var point = Vector3.Transform(
                        new Vector3(
                            x == 0 ? mesh.LocalBoundsMin.X : mesh.LocalBoundsMax.X,
                            y == 0 ? mesh.LocalBoundsMin.Y : mesh.LocalBoundsMax.Y,
                            z == 0 ? mesh.LocalBoundsMin.Z : mesh.LocalBoundsMax.Z),
                        transform);
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
            }
        }

        mesh.BoundsMin = min;
        mesh.BoundsMax = max;
    }

    /// <summary>渲染一帧：清屏 + 绘制所有已上传的通用网格。</summary>
    public void RenderFrame()
    {
        // 表面丢失后限速重试（Godot 式静默跳过）：避免驱动未就绪时紧循环重建。
        if (_surfaceLost && Environment.TickCount64 - _lastSurfaceRecreateTicks < 250)
        {
            return;
        }

        // 提交/取图失败后的冷却期：跳帧等待驱动恢复（不重建交换链）。
        if (Environment.TickCount64 < _retryAfterTicks)
        {
            return;
        }
        if (_resizeRequested)
        {
            RecreateSwapchain();
            return;
        }

        var fence = _inFlightFences[_currentFrame];
        // 仅在确有在途提交时才等待栅栏：驱动失败后栅栏可能永不发信号，无限等待会卡死。
        if (_fencePending[_currentFrame])
        {
            var fenceWait = _vk.WaitForFences(_device, 1, &fence, true, 1_000_000_000);
            if (!VulkanRenderPassPolicy.CanReuseFrameSlot(fenceWait))
            {
                if (fenceWait == Result.Timeout)
                {
                    // 超时不能证明 GPU 已停止使用该帧槽；保留 pending，下一帧继续等待并禁止复用资源。
                    _retryAfterTicks = Environment.TickCount64 + 200;
                    return;
                }

                ThrowOnError(fenceWait, "WaitForFences");
            }
            ThrowOnError(fenceWait, "WaitForFences");
            _fencePending[_currentFrame] = false;
            ReleaseAcquireSemaphoreForFrame(_currentFrame);
        }
        try
        {
            RenderOneFrame(fence);
        }
        catch
        {
            // 帧执行失败：若尚未提交，栅栏不会被驱动发信号，必须复位，否则下一帧 WaitForFences 永久阻塞。
            if (!_frameSubmitted)
            {
                _vk.ResetFences(_device, 1, &fence);
                _fencePending[_currentFrame] = false;
                // QueueSubmit 未成功消费 acquire semaphore；按 Godot 的失败路径销毁并重建，
                // 否则下一个 acquire 可能拿到仍处于 signal 状态的 semaphore。
                RecreateAcquireSemaphoreForFrame(_currentFrame);
            }
            throw;
        }
    }

    /// <summary>执行单帧渲染（RenderFrame 保证：提交失败时栅栏可复位，不会永久阻塞）。</summary>
    private void RenderOneFrame(Fence fence)
    {
        _frameSubmitted = false;

        // 安全期已过，销毁延迟队列中到期的旧网格
        DestroyRetired(_frameNumber);

        var acquireSemaphoreIndex = AcquireImageAvailableSemaphore();
        _frameAcquireSemaphore[_currentFrame] = acquireSemaphoreIndex;
        var acquireSemaphore = _imageAvailable[acquireSemaphoreIndex];
        uint imageIndex;
        var acquire = _khrSwapchain.AcquireNextImage(
            _device, _swapchain, 300_000_000, acquireSemaphore, default, &imageIndex);
        if (acquire == Result.ErrorOutOfDateKhr || acquire == Result.ErrorSurfaceLostKhr)
        {
            LogFirstFrameDiagnostic(acquire, null, null, null);
            RecreateAcquireSemaphoreForFrame(_currentFrame);
            if (acquire == Result.ErrorSurfaceLostKhr)
            {
                _surfaceLost = true;
            }
            RecreateSwapchain();
            return;
        }

        if (acquire == Result.Timeout)
        {
            LogFirstFrameDiagnostic(acquire, null, null, null);
            RecreateAcquireSemaphoreForFrame(_currentFrame);
            // 驱动在提交失败后可能短暂无法取图：只跳帧不重建（重建会触发 Adreno 段错误），稍后重试。
            _retryAfterTicks = Environment.TickCount64 + 200;
            return;
        }

        if (acquire == Result.NotReady)
        {
            LogFirstFrameDiagnostic(acquire, null, null, null);
            RecreateAcquireSemaphoreForFrame(_currentFrame);
            // 交换链图像暂未就绪（如后台化/最小化）：跳过本帧，不抛异常
            return;
        }

        // Suboptimal 仅代表性能非最优，仍可正常继续展出（如 IDENTITY 预变换时）
        if (acquire == Result.SuboptimalKhr)
        {
            WarnSuboptimalOnce();
        }
        else
        {
            ThrowOnError(acquire, "AcquireNextImageKHR");
        }

        if (!WaitForAcquiredImageFence(imageIndex))
        {
            LogFirstFrameDiagnostic(acquire, imageIndex, null, null);
            RecreateAcquireSemaphoreForFrame(_currentFrame);
            return;
        }

        // 先等待当前 acquired image 的旧提交，再复位本帧 fence；否则该图像恰好关联当前帧槽时会等待一个刚复位的 fence。
        ThrowOnError(_vk.ResetFences(_device, 1, &fence), "ResetFences");

        var commandBuffer = _commandBuffers[_currentFrame];
        // 对应帧槽的 fence 已完成，命令缓冲不再被 GPU 使用；必须回到 initial 状态后才能重新录制。
        ThrowOnError(_vk.ResetCommandBuffer(commandBuffer, 0), "ResetCommandBuffer");
        var captureThisFrame = _captureRequested;
        if (captureThisFrame)
        {
            PrepareCaptureBuffer();
        }

        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        ThrowOnError(_vk.BeginCommandBuffer(commandBuffer, &beginInfo), "BeginCommandBuffer");

        WriteCameraUbo();

        var clears = stackalloc ClearValue[2];
        clears[0] = new ClearValue
        {
            Color = new ClearColorValue
            {
                Float32_0 = _sceneClearColor.X,
                Float32_1 = _sceneClearColor.Y,
                Float32_2 = _sceneClearColor.Z,
                Float32_3 = _sceneClearColor.W,
            },
        };
        clears[1] = new ClearValue
        {
            DepthStencil = new ClearDepthStencilValue { Depth = 1f, Stencil = 0 },
        };

        var offscreenScene = RenderSceneOffscreen;
        OffscreenTarget? offscreenTarget = null;
        if (offscreenScene)
        {
            EnsureOffscreenTarget(_currentFrame);
            offscreenTarget = _offscreenTargets[_currentFrame];
        }

        var renderArea = new Rect2D { Offset = default, Extent = _extent };
        var renderPassBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = VulkanRenderPassPolicy.SelectSceneRenderPass(offscreenScene, _renderPass, _offscreenRenderPass),
            Framebuffer = offscreenTarget?.Framebuffer ?? _framebuffers[imageIndex],
            RenderArea = renderArea,
            ClearValueCount = 2,
            PClearValues = clears,
        };
        _vk.CmdBeginRenderPass(commandBuffer, &renderPassBegin, SubpassContents.Inline);

        var rect = _display.ComputeRenderRect((int)_extent.Width, (int)_extent.Height);
        var renderRect = new Rect2D
        {
            Offset = new Offset2D { X = rect.X, Y = rect.Y },
            Extent = new Extent2D { Width = (uint)rect.Width, Height = (uint)rect.Height },
        };
        // Vulkan NDC Y 向下：用负高度视口翻转栅格化，使世界“上”显示在屏幕上方（天空朝上）。
        // 负高度视口为 Vulkan 1.1+ 核心特性（原 VK_KHR_maintenance1），不影响投影/视锥数学。
        var viewport = new Viewport
        {
            X = renderRect.Offset.X,
            Y = renderRect.Offset.Y + (int)renderRect.Extent.Height,
            Width = renderRect.Extent.Width,
            Height = -(int)renderRect.Extent.Height,
            MinDepth = 0f, MaxDepth = 1f,
        };
        _vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        var scissor = new Rect2D { Offset = renderRect.Offset, Extent = renderRect.Extent };
        _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);

        ulong zeroOffset = 0;
        var frustum = new Frustum(_cameraView * _cameraProjection);
        var cameraPosition = Matrix4x4.Invert(_cameraView, out var cameraWorld)
            ? cameraWorld.Translation
            : Vector3.Zero;
        foreach (var blendMode in new[]
                 {
                     MaterialBlendMode.Background,
                     MaterialBlendMode.BackgroundAlphaBlend,
                     MaterialBlendMode.Opaque,
                     MaterialBlendMode.Overlay,
                     MaterialBlendMode.AlphaBlend,
                 })
        {
            IEnumerable<GpuMesh> visibleMeshes = _meshes.Values.Where(mesh =>
                GetBlendMode(mesh) == blendMode && frustum.Intersects(mesh.BoundsMin, mesh.BoundsMax));
            if (blendMode == MaterialBlendMode.AlphaBlend)
            {
                visibleMeshes = visibleMeshes.OrderByDescending(mesh =>
                    Vector3.DistanceSquared(cameraPosition, (mesh.BoundsMin + mesh.BoundsMax) * 0.5f));
            }

            foreach (var mesh in visibleMeshes)
            {
                _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, GetPipeline(mesh));
                var descriptorSet = mesh.Material.IsValid && _materials.TryGetValue(mesh.Material.Value, out var material)
                    ? material.DescriptorSets[_currentFrame]
                    : _descriptorSets[_currentFrame];
                _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &descriptorSet, 0, null);

                var environment = blendMode is MaterialBlendMode.Background or MaterialBlendMode.BackgroundAlphaBlend or MaterialBlendMode.Overlay
                    ? Vector4.One
                    : _environmentModulation;
                var push = new PushConstants
                {
                    Model = mesh.ModelMatrix,
                    Modulation = mesh.Modulation * environment,
                };
                _vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(PushConstants), &push);
                var vertexBuffer = mesh.VertexBuffer;
                _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &zeroOffset);
                _vk.CmdBindIndexBuffer(commandBuffer, mesh.IndexBuffer, 0, IndexType.Uint32);
                _vk.CmdDrawIndexed(commandBuffer, mesh.IndexCount, 1, 0, 0, 0);
            }
        }

        _vk.CmdEndRenderPass(commandBuffer);

        // UI（ImGui）渲染到交换链
        if (Overlay is not null)
        {
            Overlay(commandBuffer, _framebuffers[imageIndex], _extent, _swapchainFormat, (uint)_currentFrame);
        }

        if (captureThisFrame)
        {
            RecordCaptureCopy(commandBuffer, _swapchainImages[imageIndex]);
        }

        ThrowOnError(_vk.EndCommandBuffer(commandBuffer), "EndCommandBuffer");

        var waitSemaphore = _imageAvailable[acquireSemaphoreIndex];
        // 呈现引擎消费信号量的时点不受图形 frame fence 约束；按 acquired image 绑定，
        // 直到同一 image 再次 acquire 才可能复用，匹配 Godot 的 swapchain semaphore 所有权。
        var signalSemaphore = _renderFinished[imageIndex];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };
        // Adreno 等移动驱动偶发前几次 QueueSubmit 返回 ErrorInitializationFailed：小间隔重试，成功即继续。
        Result submitResult;
        var submitAttempt = 0;
        while (true)
        {
            submitResult = _vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, fence);
            if (submitResult == Result.Success || submitResult != Result.ErrorInitializationFailed || ++submitAttempt >= 10)
            {
                break;
            }
            System.Threading.Thread.Sleep(50);
        }
        if (submitResult != Result.Success)
        {
            LogFirstFrameDiagnostic(acquire, imageIndex, submitResult, null);
            var fenceStatus = _vk.GetFenceStatus(_device, fence);
            ThrowOnError(
                submitResult,
                $"QueueSubmit(frame={_frameNumber}, slot={_currentFrame}, image={imageIndex}, " +
                $"acquireSemaphore=0x{waitSemaphore.Handle:X}, presentSemaphore=0x{signalSemaphore.Handle:X}, " +
                $"fence=0x{fence.Handle:X}, fenceStatus={fenceStatus})");
        }
        _frameSubmitted = true;
        _retryAfterTicks = 0;
        _fencePending[_currentFrame] = true;
        _imageInFlightFences[imageIndex] = fence;

        if (captureThisFrame)
        {
            _vk.DeviceWaitIdle(_device);
            SaveCapture();
            _captureRequested = false;
            _screenshotPath = null;
        }

        var swapchain = _swapchain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };
        var present = _khrSwapchain.QueuePresent(_presentQueue, &presentInfo);
        if (present == Result.ErrorOutOfDateKhr || present == Result.ErrorSurfaceLostKhr)
        {
            if (present == Result.ErrorSurfaceLostKhr)
            {
                _surfaceLost = true;
            }
            RecreateSwapchain();
        }
        else if (present == Result.SuboptimalKhr)
        {
            // Suboptimal：无需重建，继续发送即可（避免在驱动上每帧重建交换链导致崩溃）
            WarnSuboptimalOnce();
        }
        else
        {
            LogFirstFrameDiagnostic(acquire, imageIndex, submitResult, present);
            ThrowOnError(present, "QueuePresentKHR");
        }

        LogFirstFrameDiagnostic(acquire, imageIndex, submitResult, present);

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
        _frameNumber++;

    }

    /// <summary>请求在下一渲染帧结束后保存截图。</summary>
    public void CaptureScreenshot(string path)
    {
        _screenshotPath = path ?? throw new ArgumentNullException(nameof(path));
        _captureRequested = true;
    }

    private void WriteCameraUbo()
    {
        var data = stackalloc byte[(int)UniformBufferSize];
        *(Matrix4x4*)data = _cameraView;
        *(Matrix4x4*)(data + 64) = _cameraProjection;
        System.Buffer.MemoryCopy(data, _uboMapped[_currentFrame], UniformBufferSize, UniformBufferSize);
    }

    // ---------- 创建 ----------

    private void CreateInstance()
    {
        var extensions = _window.RequiredVulkanExtensions;
        var extensionPtrs = stackalloc byte*[extensions.Length];
        for (var i = 0; i < extensions.Length; i++)
        {
            extensionPtrs[i] = (byte*)SilkMarshal.StringToPtr(extensions[i]);
        }

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("Cubit"),
            ApplicationVersion = Vk.MakeVersion(0, 1, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("Cubit"),
            EngineVersion = Vk.MakeVersion(0, 1, 0),
            ApiVersion = Vk.Version11,
        };

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Length,
            PpEnabledExtensionNames = extensionPtrs,
        };

        ThrowOnError(_vk.CreateInstance(&createInfo, null, out _instance), "CreateInstance");
    }

    private void PickPhysicalDevice()
    {
        uint deviceCount = 0;
        _vk.EnumeratePhysicalDevices(_instance, &deviceCount, null);
        if (deviceCount == 0)
        {
            throw new InvalidOperationException("没有找到支持 Vulkan 的物理设备");
        }

        var devices = stackalloc PhysicalDevice[(int)deviceCount];
        ThrowOnError(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, devices), "EnumeratePhysicalDevices");

        for (var d = 0; d < deviceCount; d++)
        {
            var device = devices[d];
            uint familyCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* familiesPtr = families)
            {
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, familiesPtr);
            }

            uint? graphics = null;
            uint? present = null;
            for (uint i = 0; i < familyCount; i++)
            {
                if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0 && graphics is null)
                {
                    graphics = i;
                }

                _khrSurface.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out var supported);
                if (supported && present is null)
                {
                    present = i;
                }

                if (graphics is not null && present is not null)
                {
                    break;
                }
            }

            if (graphics is not null && present is not null)
            {
                _physicalDevice = device;
                _graphicsFamily = graphics.Value;
                _presentFamily = present.Value;
                ReadPhysicalDeviceProperties();
                return;
            }
        }

        throw new InvalidOperationException("没有找到支持图形与呈现队列的 Vulkan 设备");
    }

    private void ReadPhysicalDeviceProperties()
    {
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
        _physicalDeviceVendorId = properties.VendorID;
        _physicalDeviceApiVersion = properties.ApiVersion;
        _physicalDeviceDriverVersion = properties.DriverVersion;
        _physicalDeviceName = Marshal.PtrToStringAnsi((nint)properties.DeviceName) ?? "Unknown Vulkan device";
    }

    private void CreateDevice()
    {
        var uniqueFamilies = _graphicsFamily == _presentFamily
            ? new[] { _graphicsFamily }
            : new[] { _graphicsFamily, _presentFamily };

        var queueInfos = stackalloc DeviceQueueCreateInfo[uniqueFamilies.Length];
        for (var i = 0; i < uniqueFamilies.Length; i++)
        {
            float priority = 1f;
            queueInfos[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueFamilies[i],
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        var deviceExtension = (byte*)SilkMarshal.StringToPtr("VK_KHR_swapchain");
        var features = new PhysicalDeviceFeatures();
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueFamilies.Length,
            PQueueCreateInfos = queueInfos,
            PEnabledFeatures = &features,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = &deviceExtension,
        };

        ThrowOnError(_vk.CreateDevice(_physicalDevice, &createInfo, null, out _device), "CreateDevice");
        _vk.GetDeviceQueue(_device, _graphicsFamily, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, _presentFamily, 0, out _presentQueue);
    }

    private void CreateSwapchain()
    {
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities);

        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null);
        var formats = stackalloc SurfaceFormatKHR[(int)formatCount];
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formats);
        var surfaceFormat = formats[0];
        for (var i = 0; i < formatCount; i++)
        {
            if (formats[i].Format == Format.B8G8R8A8Srgb)
            {
                surfaceFormat = formats[i];
                break;
            }
        }

        uint presentModeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModeCount, null);
        var presentModes = stackalloc PresentModeKHR[(int)presentModeCount];
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModeCount, presentModes);
        // Godot Android 默认使用 FIFO；Mailbox 会让 BufferQueue 在无 Swappy pacing 时耗尽可出队缓冲。
        var presentMode = PresentModeKHR.FifoKhr;

        var imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
        {
            imageCount = capabilities.MaxImageCount;
        }

        _extent = ChooseExtent(capabilities);
        _surfaceRotationDegrees = SurfaceTransformDegrees(capabilities.CurrentTransform);
        EmitDiagnostic($"[Cubit] surface transform: {capabilities.CurrentTransform} -> pre-rotation {_surfaceRotationDegrees} deg（currentTransform，Godot 4.7.1 生产路径）");

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            // Godot 4.7.1 生产路径：交换链跟随表面 currentTransform（Android 横屏=ROTATE_90），
            // present 保持最优（不出现 Suboptimal）；画面方向由投影旋转补偿（见 ApplySurfaceRotation）。
            PreTransform = capabilities.CurrentTransform,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
        };

        var createSwapchainResult = _khrSwapchain.CreateSwapchain(_device, &createInfo, null, out _swapchain);
        if (createSwapchainResult == Result.ErrorSurfaceLostKhr)
        {
            _surfaceLost = true;
        }
        ThrowOnError(createSwapchainResult, "CreateSwapchainKHR");
        _swapchainFormat = surfaceFormat.Format;

        uint count = 0;
        _khrSwapchain.GetSwapchainImages(_device, _swapchain, &count, null);
        _swapchainImages = new Image[count];
        _imageInFlightFences = new Fence[count];
        _firstFrameDiagnosticLogged = false;
        EmitDiagnostic(VulkanFrameDiagnostics.FormatStartup(
            _graphicsFamily,
            _presentFamily,
            _extent.Width,
            _extent.Height,
            count,
            presentMode,
            createInfo.CompositeAlpha));
        fixed (Image* images = _swapchainImages)
        {
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, &count, images);
        }

        _imageViews = new ImageView[count];
        for (var i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainFormat,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            ThrowOnError(_vk.CreateImageView(_device, &viewInfo, null, out _imageViews[i]), "CreateImageView");
        }
    }

    /// <summary>在复用交换链图像前等待其最后一次提交；超时仅跳帧，避免移动驱动上不安全地重建表面。</summary>
    private bool WaitForAcquiredImageFence(uint imageIndex)
    {
        var imageFence = _imageInFlightFences[imageIndex];
        if (imageFence.Handle == 0)
        {
            return true;
        }

        var waitResult = _vk.WaitForFences(_device, 1, &imageFence, true, 1_000_000_000);
        if (waitResult == Result.Timeout)
        {
            _retryAfterTicks = Environment.TickCount64 + 200;
            return false;
        }

        ThrowOnError(waitResult, "WaitForAcquiredImageFence");
        return true;
    }

    private void LogFirstFrameDiagnostic(Result acquire, uint? imageIndex, Result? submit, Result? present)
    {
        if (_firstFrameDiagnosticLogged)
        {
            return;
        }

        _firstFrameDiagnosticLogged = true;
        EmitDiagnostic(VulkanFrameDiagnostics.FormatFrame(acquire, imageIndex, submit, present));
    }

    private void EmitDiagnostic(string message)
    {
        Console.WriteLine(message);
        Diagnostic?.Invoke(message);
    }

    private void CreateDepthResources()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.D32Sfloat,
            Extent = new Extent3D { Width = _extent.Width, Height = _extent.Height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
        };
        ThrowOnError(_vk.CreateImage(_device, &imageInfo, null, out _depthImage), "CreateImage(depth)");

        _vk.GetImageMemoryRequirements(_device, _depthImage, out var requirements);
        var memoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = memoryTypeIndex,
        };
        ThrowOnError(_vk.AllocateMemory(_device, &allocInfo, null, out _depthImageMemory), "AllocateMemory(depth)");
        ThrowOnError(_vk.BindImageMemory(_device, _depthImage, _depthImageMemory, 0), "BindImageMemory(depth)");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _depthImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.D32Sfloat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        ThrowOnError(_vk.CreateImageView(_device, &viewInfo, null, out _depthImageView), "CreateImageView(depth)");
    }

    private void EnsureOffscreenTarget(int frameSlot)
    {
        var target = _offscreenTargets[frameSlot];
        var width = (int)_extent.Width;
        var height = (int)_extent.Height;
        if (target.Framebuffer.Handle != 0
            && target.Width == width
            && target.Height == height
            && target.Format == _swapchainFormat)
        {
            return;
        }

        DestroyOffscreenTarget(target);
        CreateOffscreenResources(target, width, height);
        target.Width = width;
        target.Height = height;
        target.Format = _swapchainFormat;
        target.Version = ++_offscreenVersion;
    }

    private void CreateOffscreenResources(OffscreenTarget target, int width, int height)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _swapchainFormat,
            Extent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        ThrowOnError(_vk.CreateImage(_device, &imageInfo, null, out target.Image), "CreateImage(offscreen)");

        _vk.GetImageMemoryRequirements(_device, target.Image, out var requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        ThrowOnError(_vk.AllocateMemory(_device, &allocInfo, null, out target.Memory), "AllocateMemory(offscreen)");
        ThrowOnError(_vk.BindImageMemory(_device, target.Image, target.Memory, 0), "BindImageMemory(offscreen)");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = target.Image,
            ViewType = ImageViewType.Type2D,
            Format = _swapchainFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        ThrowOnError(_vk.CreateImageView(_device, &viewInfo, null, out target.View), "CreateImageView(offscreen)");

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 1f,
        };
        ThrowOnError(_vk.CreateSampler(_device, &samplerInfo, null, out target.Sampler), "CreateSampler(offscreen)");

        var depthImageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.D32Sfloat,
            Extent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        ThrowOnError(_vk.CreateImage(_device, &depthImageInfo, null, out target.DepthImage), "CreateImage(offscreen depth)");
        _vk.GetImageMemoryRequirements(_device, target.DepthImage, out var depthRequirements);
        var depthAllocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = depthRequirements.Size,
            MemoryTypeIndex = FindMemoryType(depthRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        ThrowOnError(_vk.AllocateMemory(_device, &depthAllocInfo, null, out target.DepthMemory), "AllocateMemory(offscreen depth)");
        ThrowOnError(_vk.BindImageMemory(_device, target.DepthImage, target.DepthMemory, 0), "BindImageMemory(offscreen depth)");

        var depthViewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = target.DepthImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.D32Sfloat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        ThrowOnError(_vk.CreateImageView(_device, &depthViewInfo, null, out target.DepthView), "CreateImageView(offscreen depth)");

        var attachments = stackalloc ImageView[2] { target.View, target.DepthView };
        var framebufferInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _offscreenRenderPass,
            AttachmentCount = 2,
            PAttachments = attachments,
            Width = (uint)width,
            Height = (uint)height,
            Layers = 1,
        };
        ThrowOnError(_vk.CreateFramebuffer(_device, &framebufferInfo, null, out target.Framebuffer), "CreateFramebuffer(offscreen)");
    }

    private void DestroyOffscreenResources()
    {
        foreach (var target in _offscreenTargets)
        {
            DestroyOffscreenTarget(target);
        }
    }

    private void DestroyOffscreenTarget(OffscreenTarget target)
    {
        if (target.Framebuffer.Handle != 0)
        {
            _vk.DestroyFramebuffer(_device, target.Framebuffer, null);
            target.Framebuffer = default;
        }

        if (target.Sampler.Handle != 0)
        {
            _vk.DestroySampler(_device, target.Sampler, null);
            target.Sampler = default;
        }

        if (target.DepthView.Handle != 0)
        {
            _vk.DestroyImageView(_device, target.DepthView, null);
            target.DepthView = default;
        }

        if (target.DepthImage.Handle != 0)
        {
            _vk.DestroyImage(_device, target.DepthImage, null);
            target.DepthImage = default;
        }

        if (target.DepthMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, target.DepthMemory, null);
            target.DepthMemory = default;
        }

        if (target.View.Handle != 0)
        {
            _vk.DestroyImageView(_device, target.View, null);
            target.View = default;
        }

        if (target.Image.Handle != 0)
        {
            _vk.DestroyImage(_device, target.Image, null);
            target.Image = default;
        }

        if (target.Memory.Handle != 0)
        {
            _vk.FreeMemory(_device, target.Memory, null);
            target.Memory = default;
        }

        target.Width = 0;
        target.Height = 0;
        target.Format = default;
        target.Version = 0;
    }

    private void CreateFramebuffers()
    {
        _framebuffers = new Framebuffer[_imageViews.Length];
        var attachments = stackalloc ImageView[2];
        for (var i = 0; i < _imageViews.Length; i++)
        {
            attachments[0] = _imageViews[i];
            attachments[1] = _depthImageView;

            var createInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = _extent.Width,
                Height = _extent.Height,
                Layers = 1,
            };
            ThrowOnError(_vk.CreateFramebuffer(_device, &createInfo, null, out _framebuffers[i]), "CreateFramebuffer");
        }
    }

    private void CreateRenderPass()
    {
        CreateSceneRenderPass(offscreen: false, out _renderPass);
        CreateSceneRenderPass(offscreen: true, out _offscreenRenderPass);
    }

    private void CreateSceneRenderPass(bool offscreen, out RenderPass renderPass)
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = VulkanRenderPassPolicy.CreateSceneColorAttachment(_swapchainFormat, offscreen);
        attachments[1] = new AttachmentDescription
        {
            Format = Format.D32Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };

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

        var offscreenReadDependency = default(SubpassDependency);
        if (offscreen)
        {
            offscreenReadDependency = VulkanRenderPassPolicy.CreateOffscreenReadDependency();
            createInfo.DependencyCount = 1;
            createInfo.PDependencies = &offscreenReadDependency;
        }

        ThrowOnError(
            _vk.CreateRenderPass(_device, &createInfo, null, out renderPass),
            offscreen ? "CreateRenderPass(offscreen)" : "CreateRenderPass");
    }

    private void CreateFallbackTextureResources()
    {
        var data = new byte[] { 255, 255, 255, 255 };
        const int width = 1;
        const int height = 1;
        var size = (nuint)data.Length;

        // 暂存缓冲（宿主可见）
        var stagingInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };
        ThrowOnError(_vk.CreateBuffer(_device, &stagingInfo, null, out var stagingBuffer), "CreateBuffer(fallback staging)");
        _vk.GetBufferMemoryRequirements(_device, stagingBuffer, out var stagingReq);
        var stagingType = FindMemoryType(stagingReq.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        var stagingAlloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = stagingReq.Size, MemoryTypeIndex = stagingType };
        ThrowOnError(_vk.AllocateMemory(_device, &stagingAlloc, null, out var stagingMemory), "AllocateMemory(fallback staging)");
        ThrowOnError(_vk.BindBufferMemory(_device, stagingBuffer, stagingMemory, 0), "BindBufferMemory(fallback staging)");
        void* mapped;
        ThrowOnError(_vk.MapMemory(_device, stagingMemory, 0, size, 0, &mapped), "MapMemory(fallback staging)");
        fixed (byte* dataPtr = data)
        {
            System.Buffer.MemoryCopy(dataPtr, mapped, (long)size, (long)size);
        }
        _vk.UnmapMemory(_device, stagingMemory);

        // 图集图像（设备本地）
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
        };
        ThrowOnError(_vk.CreateImage(_device, &imageInfo, null, out _fallbackImage), "CreateImage(fallback)");
        _vk.GetImageMemoryRequirements(_device, _fallbackImage, out var imageReq);
        var imageType = FindMemoryType(imageReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        var imageAlloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = imageReq.Size, MemoryTypeIndex = imageType };
        ThrowOnError(_vk.AllocateMemory(_device, &imageAlloc, null, out _fallbackMemory), "AllocateMemory(fallback)");
        ThrowOnError(_vk.BindImageMemory(_device, _fallbackImage, _fallbackMemory, 0), "BindImageMemory(fallback)");

        // 单次命令：布局转换 -> 拷贝 -> 转换到只读
        var alloc = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer cmd;
        ThrowOnError(_vk.AllocateCommandBuffers(_device, &alloc, &cmd), "AllocateCommandBuffers(fallback)");
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(cmd, &begin);

        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = uint.MaxValue,
            DstQueueFamilyIndex = uint.MaxValue,
            Image = _fallbackImage,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 },
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransfer);

        var copy = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
            ImageExtent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
        };
        _vk.CmdCopyBufferToImage(cmd, stagingBuffer, _fallbackImage, ImageLayout.TransferDstOptimal, 1, &copy);

        var toRead = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = uint.MaxValue,
            DstQueueFamilyIndex = uint.MaxValue,
            Image = _fallbackImage,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 },
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &toRead);
        _vk.EndCommandBuffer(cmd);

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        ThrowOnError(_vk.CreateFence(_device, &fenceInfo, null, out var fence), "CreateFence(fallback)");
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        ThrowOnError(_vk.QueueSubmit(_graphicsQueue, 1, &submit, fence), "QueueSubmit(fallback)");
        _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue);
        _vk.DestroyFence(_device, fence, null);
        _vk.FreeCommandBuffers(_device, _commandPool, 1, &cmd);
        _vk.DestroyBuffer(_device, stagingBuffer, null);
        _vk.FreeMemory(_device, stagingMemory, null);

        // 图像视图
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _fallbackImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 },
        };
        ThrowOnError(_vk.CreateImageView(_device, &viewInfo, null, out _fallbackView), "CreateImageView(fallback)");

        // 采样器
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0f,
            MaxLod = 1f,
            MaxAnisotropy = 1f,
        };
        ThrowOnError(_vk.CreateSampler(_device, &samplerInfo, null, out _fallbackSampler), "CreateSampler(fallback)");
    }

    private static Format SelectTextureFormat(TextureColorSpace colorSpace) => colorSpace switch
    {
        TextureColorSpace.Srgb => Format.R8G8B8A8Srgb,
        TextureColorSpace.Linear => Format.R8G8B8A8Unorm,
        _ => throw new ArgumentOutOfRangeException(nameof(colorSpace), "未知纹理颜色空间"),
    };

    private GpuTexture CreateTextureResources(TextureData texture)
    {
        var data = texture.Rgba8.ToArray();
        var size = (nuint)data.Length;
        var stagingInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };
        ThrowOnError(_vk.CreateBuffer(_device, &stagingInfo, null, out var stagingBuffer), "CreateBuffer(texture staging)");
        _vk.GetBufferMemoryRequirements(_device, stagingBuffer, out var stagingReq);
        var stagingType = FindMemoryType(stagingReq.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        var stagingAlloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = stagingReq.Size, MemoryTypeIndex = stagingType };
        ThrowOnError(_vk.AllocateMemory(_device, &stagingAlloc, null, out var stagingMemory), "AllocateMemory(texture staging)");
        ThrowOnError(_vk.BindBufferMemory(_device, stagingBuffer, stagingMemory, 0), "BindBufferMemory(texture staging)");
        void* mapped;
        ThrowOnError(_vk.MapMemory(_device, stagingMemory, 0, size, 0, &mapped), "MapMemory(texture staging)");
        fixed (byte* dataPtr = data)
        {
            System.Buffer.MemoryCopy(dataPtr, mapped, (long)size, (long)size);
        }
        _vk.UnmapMemory(_device, stagingMemory);

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = SelectTextureFormat(texture.ColorSpace),
            Extent = new Extent3D { Width = (uint)texture.Width, Height = (uint)texture.Height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
        };
        ThrowOnError(_vk.CreateImage(_device, &imageInfo, null, out var image), "CreateImage(texture)");
        _vk.GetImageMemoryRequirements(_device, image, out var imageReq);
        var imageType = FindMemoryType(imageReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        var imageAlloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = imageReq.Size, MemoryTypeIndex = imageType };
        ThrowOnError(_vk.AllocateMemory(_device, &imageAlloc, null, out var memory), "AllocateMemory(texture)");
        ThrowOnError(_vk.BindImageMemory(_device, image, memory, 0), "BindImageMemory(texture)");

        var alloc = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer cmd;
        ThrowOnError(_vk.AllocateCommandBuffers(_device, &alloc, &cmd), "AllocateCommandBuffers(texture)");
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        ThrowOnError(_vk.BeginCommandBuffer(cmd, &begin), "BeginCommandBuffer(texture)");
        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = uint.MaxValue,
            DstQueueFamilyIndex = uint.MaxValue,
            Image = image,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 },
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransfer);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            ImageExtent = new Extent3D { Width = (uint)texture.Width, Height = (uint)texture.Height, Depth = 1 },
        };
        _vk.CmdCopyBufferToImage(cmd, stagingBuffer, image, ImageLayout.TransferDstOptimal, 1, &copy);
        var toRead = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = uint.MaxValue,
            DstQueueFamilyIndex = uint.MaxValue,
            Image = image,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 },
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &toRead);
        ThrowOnError(_vk.EndCommandBuffer(cmd), "EndCommandBuffer(texture)");
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        ThrowOnError(_vk.CreateFence(_device, &fenceInfo, null, out var fence), "CreateFence(texture)");
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        ThrowOnError(_vk.QueueSubmit(_graphicsQueue, 1, &submit, fence), "QueueSubmit(texture)");
        ThrowOnError(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "WaitForFences(texture)");
        _vk.DestroyFence(_device, fence, null);
        _vk.FreeCommandBuffers(_device, _commandPool, 1, &cmd);
        _vk.DestroyBuffer(_device, stagingBuffer, null);
        _vk.FreeMemory(_device, stagingMemory, null);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = SelectTextureFormat(texture.ColorSpace),
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 },
        };
        ThrowOnError(_vk.CreateImageView(_device, &viewInfo, null, out var view), "CreateImageView(texture)");
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = texture.Filter == TextureFilter.Nearest ? Filter.Nearest : Filter.Linear,
            MinFilter = texture.Filter == TextureFilter.Nearest ? Filter.Nearest : Filter.Linear,
            MipmapMode = texture.Filter == TextureFilter.Nearest ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0f,
            MaxLod = 1f,
            MaxAnisotropy = 1f,
        };
        ThrowOnError(_vk.CreateSampler(_device, &samplerInfo, null, out var sampler), "CreateSampler(texture)");
        return new GpuTexture { Image = image, Memory = memory, View = view, Sampler = sampler };
    }

    private void DestroyTextureResources(GpuTexture texture)
    {
        _vk.DestroySampler(_device, texture.Sampler, null);
        _vk.DestroyImageView(_device, texture.View, null);
        _vk.DestroyImage(_device, texture.Image, null);
        _vk.FreeMemory(_device, texture.Memory, null);
    }

    private DescriptorSet[] AllocateMaterialDescriptorSets(GpuTexture texture)
    {
        var descriptorSets = new DescriptorSet[MaxFramesInFlight];
        var layouts = stackalloc DescriptorSetLayout[MaxFramesInFlight];
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            layouts[i] = _descriptorSetLayout;
        }

        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = MaxFramesInFlight,
            PSetLayouts = layouts,
        };
        fixed (DescriptorSet* sets = descriptorSets)
        {
            ThrowOnError(_vk.AllocateDescriptorSets(_device, &allocInfo, sets), "AllocateDescriptorSets(material)");
        }

        var writes = stackalloc WriteDescriptorSet[2];
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            var bufferInfo = new DescriptorBufferInfo { Buffer = _uboBuffers[i], Range = UniformBufferSize };
            var imageInfo = new DescriptorImageInfo
            {
                Sampler = texture.Sampler,
                ImageView = texture.View,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSets[i],
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &bufferInfo,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSets[i],
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &imageInfo,
            };
            _vk.UpdateDescriptorSets(_device, 2, writes, 0, null);
        }

        return descriptorSets;
    }

    private void FreeMaterialDescriptorSets(DescriptorSet[] descriptorSets)
    {
        if (descriptorSets.Length == 0)
        {
            return;
        }

        fixed (DescriptorSet* sets = descriptorSets)
        {
            ThrowOnError(_vk.FreeDescriptorSets(_device, _descriptorPool, (uint)descriptorSets.Length, sets), "FreeDescriptorSets(material)");
        }
    }

    private void CreateUniformBuffers()
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = UniformBufferSize,
            Usage = BufferUsageFlags.UniformBufferBit,
            SharingMode = SharingMode.Exclusive,
        };

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            ThrowOnError(_vk.CreateBuffer(_device, &bufferInfo, null, out _uboBuffers[i]), "CreateBuffer(ubo)");
            _vk.GetBufferMemoryRequirements(_device, _uboBuffers[i], out var requirements);
            var memoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
            };
            ThrowOnError(_vk.AllocateMemory(_device, &allocInfo, null, out _uboMemories[i]), "AllocateMemory(ubo)");
            ThrowOnError(_vk.BindBufferMemory(_device, _uboBuffers[i], _uboMemories[i], 0), "BindBufferMemory(ubo)");
            void* mapped;
            ThrowOnError(_vk.MapMemory(_device, _uboMemories[i], 0, UniformBufferSize, 0, &mapped), "MapMemory(ubo)");
            _uboMapped[i] = mapped;
        }
    }

    private void CreateDescriptorObjects()
    {
        var layoutBindings = stackalloc DescriptorSetLayoutBinding[2];
        layoutBindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit,
        };
        layoutBindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = layoutBindings,
        };
        ThrowOnError(_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _descriptorSetLayout), "CreateDescriptorSetLayout");

        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = MaxFramesInFlight * 1025,
        };
        poolSizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = MaxFramesInFlight * 1025,
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
            MaxSets = MaxFramesInFlight * 1025,
        };
        ThrowOnError(_vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool), "CreateDescriptorPool");

        var layouts = stackalloc DescriptorSetLayout[MaxFramesInFlight];
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            layouts[i] = _descriptorSetLayout;
        }

        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = MaxFramesInFlight,
            PSetLayouts = layouts,
        };
        fixed (DescriptorSet* sets = _descriptorSets)
        {
            ThrowOnError(_vk.AllocateDescriptorSets(_device, &allocInfo, sets), "AllocateDescriptorSets");
        }

        var writes = stackalloc WriteDescriptorSet[2];
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            var bufferInfo = new DescriptorBufferInfo
            {
                Buffer = _uboBuffers[i],
                Offset = 0,
                Range = UniformBufferSize,
            };
            var imageInfo = new DescriptorImageInfo
            {
                Sampler = _fallbackSampler,
                ImageView = _fallbackView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSets[i],
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &bufferInfo,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSets[i],
                DstBinding = 1,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &imageInfo,
            };
            _vk.UpdateDescriptorSets(_device, 2, writes, 0, null);
        }
    }

    private void CreatePipeline()
    {
        var vertexCode = EmbeddedShaders.WorldVertex();
        var vertexColorCode = EmbeddedShaders.WorldVertexColor();
        var fragmentCode = EmbeddedShaders.WorldFragment();
        var tiledVertexCode = EmbeddedShaders.WorldTiledVertex();
        var tiledFragmentCode = EmbeddedShaders.WorldTiledFragment();
        _vertShader = CreateShaderModule(vertexCode);
        _vertexColorShader = CreateShaderModule(vertexColorCode);
        _fragShader = CreateShaderModule(fragmentCode);
        _tiledVertexShader = CreateShaderModule(tiledVertexCode);
        _tiledFragShader = CreateShaderModule(tiledFragmentCode);

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _vertShader,
            PName = (byte*)SilkMarshal.StringToPtr("main"),
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _fragShader,
            PName = (byte*)SilkMarshal.StringToPtr("main"),
        };

        var bindingDescription = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = 24,
            InputRate = VertexInputRate.Vertex,
        };
        var attributes = stackalloc VertexInputAttributeDescription[4];
        attributes[0] = new VertexInputAttributeDescription
        {
            Location = 0,
            Binding = 0,
            Format = Format.R32G32B32Sfloat,
            Offset = 0,
        };
        attributes[1] = new VertexInputAttributeDescription
        {
            Location = 1,
            Binding = 0,
            Format = Format.R32G32Sfloat,
            Offset = 12,
        };
        attributes[2] = new VertexInputAttributeDescription
        {
            Location = 2,
            Binding = 0,
            Format = Format.R32Sfloat,
            Offset = 20,
        };
        attributes[3] = new VertexInputAttributeDescription
        {
            Location = 3,
            Binding = 0,
            Format = Format.R32G32B32A32Sfloat,
            Offset = 24,
        };

        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &bindingDescription,
            VertexAttributeDescriptionCount = 3,
            PVertexAttributeDescriptions = attributes,
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };

        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };

        var rasterization = new PipelineRasterizationStateCreateInfo
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

        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            SampleShadingEnable = false,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };

        var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)sizeof(PushConstants),
        };

        var descriptorSetLayout = _descriptorSetLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorSetLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange,
        };
        ThrowOnError(_vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelineLayout), "CreatePipelineLayout");

        var blendModes = new[]
        {
            MaterialBlendMode.Background,
            MaterialBlendMode.BackgroundAlphaBlend,
            MaterialBlendMode.Opaque,
            MaterialBlendMode.Overlay,
            MaterialBlendMode.AlphaBlend,
        };
        var depthStates = stackalloc PipelineDepthStencilStateCreateInfo[blendModes.Length];
        var blendAttachments = stackalloc PipelineColorBlendAttachmentState[blendModes.Length];
        var blendStates = stackalloc PipelineColorBlendStateCreateInfo[blendModes.Length];
        var pipelineInfos = stackalloc GraphicsPipelineCreateInfo[blendModes.Length];
        var pipelines = stackalloc Pipeline[blendModes.Length];
        for (var index = 0; index < blendModes.Length; index++)
        {
            depthStates[index] = CreateDepthStencilState(blendModes[index]);
            blendAttachments[index] = CreateBlendAttachment(blendModes[index]);
            blendStates[index] = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachments[index],
            };
            pipelineInfos[index] = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStates[index],
                PColorBlendState = &blendStates[index],
                PDynamicState = &dynamicState,
                Layout = _pipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
        }

        ThrowOnError(
            _vk.CreateGraphicsPipelines(_device, default, (uint)blendModes.Length, pipelineInfos, null, pipelines),
            "CreateGraphicsPipelines");
        _backgroundPipeline = pipelines[0];
        _backgroundAlphaBlendPipeline = pipelines[1];
        _opaquePipeline = pipelines[2];
        _overlayPipeline = pipelines[3];
        _alphaBlendPipeline = pipelines[4];

        // 图集瓦片布局保留普通 UV 契约，并由额外的每顶点瓦片 ID 在片元阶段重复采样。
        stages[0].Module = _tiledVertexShader;
        stages[1].Module = _tiledFragShader;
        bindingDescription.Stride = (uint)(MeshData.PositionUvShadeTileVertexStride * sizeof(float));
        attributes[3].Format = Format.R32Sfloat;
        attributes[3].Offset = 24;
        vertexInput.VertexAttributeDescriptionCount = 4;
        var tiledPipelines = stackalloc Pipeline[blendModes.Length];
        ThrowOnError(
            _vk.CreateGraphicsPipelines(_device, default, (uint)blendModes.Length, pipelineInfos, null, tiledPipelines),
            "CreateGraphicsPipelines(tiled atlas)");
        _backgroundTiledPipeline = tiledPipelines[0];
        _backgroundAlphaBlendTiledPipeline = tiledPipelines[1];
        _opaqueTiledPipeline = tiledPipelines[2];
        _overlayTiledPipeline = tiledPipelines[3];
        _alphaBlendTiledPipeline = tiledPipelines[4];

        // 顶点颜色仅改变顶点输入和顶点着色器，材质混合与资源生命周期仍复用通用路径。
        stages[0].Module = _vertexColorShader;
        stages[1].Module = _fragShader;
        bindingDescription.Stride = (uint)(MeshData.PositionUvShadeColorVertexStride * sizeof(float));
        attributes[3].Format = Format.R32G32B32A32Sfloat;
        vertexInput.VertexAttributeDescriptionCount = 4;
        var vertexColorPipelines = stackalloc Pipeline[blendModes.Length];
        ThrowOnError(
            _vk.CreateGraphicsPipelines(_device, default, (uint)blendModes.Length, pipelineInfos, null, vertexColorPipelines),
            "CreateGraphicsPipelines(vertex color)");
        _backgroundVertexColorPipeline = vertexColorPipelines[0];
        _backgroundAlphaBlendVertexColorPipeline = vertexColorPipelines[1];
        _opaqueVertexColorPipeline = vertexColorPipelines[2];
        _overlayVertexColorPipeline = vertexColorPipelines[3];
        _alphaBlendVertexColorPipeline = vertexColorPipelines[4];
    }

    private static PipelineDepthStencilStateCreateInfo CreateDepthStencilState(MaterialBlendMode blendMode) => new()
    {
        SType = StructureType.PipelineDepthStencilStateCreateInfo,
        DepthTestEnable = blendMode is not (MaterialBlendMode.Background or MaterialBlendMode.BackgroundAlphaBlend),
        DepthWriteEnable = blendMode == MaterialBlendMode.Opaque,
        DepthCompareOp = CompareOp.Less,
        DepthBoundsTestEnable = false,
        StencilTestEnable = false,
    };

    private static PipelineColorBlendAttachmentState CreateBlendAttachment(MaterialBlendMode blendMode) => blendMode switch
    {
        MaterialBlendMode.Opaque => new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = false,
        },
        MaterialBlendMode.Background => new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.One,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.Zero,
            AlphaBlendOp = BlendOp.Add,
        },
        MaterialBlendMode.BackgroundAlphaBlend => new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add,
        },
        MaterialBlendMode.Overlay => new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.One,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.Zero,
            AlphaBlendOp = BlendOp.Add,
        },
        MaterialBlendMode.AlphaBlend => new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(blendMode)),
    };

    private ShaderModule CreateShaderModule(byte[] code)
    {
        var createInfo = new ShaderModuleCreateInfo
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)code.Length,
        };
        fixed (byte* codePtr = code)
        {
            createInfo.PCode = (uint*)codePtr;
            ThrowOnError(_vk.CreateShaderModule(_device, &createInfo, null, out var module), "CreateShaderModule");
            return module;
        }
    }

    private unsafe Buffer CreateHostBuffer<T>(T[] data, BufferUsageFlags usage, out DeviceMemory memory) where T : unmanaged
    {
        var size = (nuint)(data.Length * sizeof(T));

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        ThrowOnError(_vk.CreateBuffer(_device, &bufferInfo, null, out var buffer), "CreateBuffer(host)");

        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var memoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = memoryTypeIndex,
        };
        ThrowOnError(_vk.AllocateMemory(_device, &allocInfo, null, out memory), "AllocateMemory(host)");
        ThrowOnError(_vk.BindBufferMemory(_device, buffer, memory, 0), "BindBufferMemory(host)");

        void* ptr;
        ThrowOnError(_vk.MapMemory(_device, memory, 0, size, 0, &ptr), "MapMemory(host)");
        fixed (T* dataPtr = data)
        {
            System.Buffer.MemoryCopy(dataPtr, ptr, (long)size, (long)size);
        }
        _vk.UnmapMemory(_device, memory);

        return buffer;
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var memoryProperties);
        for (var i = 0; i < memoryProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 &&
                (memoryProperties.MemoryTypes[i].PropertyFlags & properties) == properties)
            {
                return (uint)i;
            }
        }

        throw new InvalidOperationException("没有找到满足要求的内存类型");
    }

    private void CreateCommandPoolAndBuffers()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _graphicsFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        ThrowOnError(_vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool), "CreateCommandPool");

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = MaxFramesInFlight,
        };
        fixed (CommandBuffer* buffers = _commandBuffers)
        {
            ThrowOnError(_vk.AllocateCommandBuffers(_device, &allocInfo, buffers), "AllocateCommandBuffers");
        }
    }

    /// <summary>图形队列预热：空提交并等待，规避 Adreno 等移动驱动首帧 QueueSubmit 偶发 ErrorInitializationFailed。</summary>
    private void PrimeGraphicsQueue()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer warmup;
        ThrowOnError(_vk.AllocateCommandBuffers(_device, &allocInfo, &warmup), "AllocateCommandBuffers(warmup)");

        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        _vk.BeginCommandBuffer(warmup, &begin);
        _vk.EndCommandBuffer(warmup);

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        ThrowOnError(_vk.CreateFence(_device, &fenceInfo, null, out var warmupFence), "CreateFence(warmup)");

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &warmup,
        };
        var result = _vk.QueueSubmit(_graphicsQueue, 1, &submit, warmupFence);
        if (result == Result.Success)
        {
            _vk.WaitForFences(_device, 1, &warmupFence, true, ulong.MaxValue);
            Console.WriteLine("[Cubit] 图形队列预热完成（首帧防抖）");
        }
        else
        {
            Console.WriteLine($"[Cubit] 图形队列预热提交未成功: {result}（不影响运行）");
        }

        _vk.DestroyFence(_device, warmupFence, null);
        _vk.FreeCommandBuffers(_device, _commandPool, 1, &warmup);
    }

    private void CreateSyncObjects()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit,
            };
            ThrowOnError(_vk.CreateFence(_device, &fenceInfo, null, out _inFlightFences[i]), "CreateFence");
        }

        CreatePresentSemaphores();
    }

    /// <summary>从 Godot 式 acquire semaphore 池取一个未使用的信号量。</summary>
    private int AcquireImageAvailableSemaphore()
    {
        if (_imageAvailableFree.Count > 0)
        {
            var freeIndex = _imageAvailableFree.Count - 1;
            var index = _imageAvailableFree[freeIndex];
            _imageAvailableFree.RemoveAt(freeIndex);
            return index;
        }

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        ThrowOnError(_vk.CreateSemaphore(_device, &semaphoreInfo, null, out var semaphore), "CreateSemaphore(imageAvailable)");
        _imageAvailable.Add(semaphore);
        return _imageAvailable.Count - 1;
    }

    /// <summary>提交 fence 完成后归还 acquire semaphore；未完成前禁止复用。</summary>
    private void ReleaseAcquireSemaphoreForFrame(int frameSlot)
    {
        var semaphoreIndex = _frameAcquireSemaphore[frameSlot];
        if (semaphoreIndex < 0)
        {
            return;
        }

        _frameAcquireSemaphore[frameSlot] = -1;
        if (!_imageAvailableFree.Contains(semaphoreIndex))
        {
            _imageAvailableFree.Add(semaphoreIndex);
        }
    }

    /// <summary>
    /// 丢弃未消费的 acquire semaphore 并创建替代品。
    /// Vulkan 规定 OUT_OF_DATE acquire semaphore 可能永久保持 signal，不能放回池中。
    /// </summary>
    private void RecreateAcquireSemaphore(int semaphoreIndex)
    {
        if (semaphoreIndex < 0 || semaphoreIndex >= _imageAvailable.Count)
        {
            return;
        }

        var oldSemaphore = _imageAvailable[semaphoreIndex];
        if (oldSemaphore.Handle != 0)
        {
            _vk.DestroySemaphore(_device, oldSemaphore, null);
        }

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        ThrowOnError(_vk.CreateSemaphore(_device, &semaphoreInfo, null, out var replacement), "CreateSemaphore(imageAvailable.recreate)");
        _imageAvailable[semaphoreIndex] = replacement;
        if (!_imageAvailableFree.Contains(semaphoreIndex))
        {
            _imageAvailableFree.Add(semaphoreIndex);
        }
    }

    /// <summary>当前帧尚未提交时，回收并重建它持有的 acquire semaphore。</summary>
    private void RecreateAcquireSemaphoreForFrame(int frameSlot)
    {
        var semaphoreIndex = _frameAcquireSemaphore[frameSlot];
        if (semaphoreIndex < 0)
        {
            return;
        }

        _frameAcquireSemaphore[frameSlot] = -1;
        RecreateAcquireSemaphore(semaphoreIndex);
    }

    /// <summary>为每个交换链图像创建独立的呈现完成信号量。</summary>
    private void CreatePresentSemaphores()
    {
        var semaphores = new Semaphore[_swapchainImages.Length];
        try
        {
            var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            for (var i = 0; i < semaphores.Length; i++)
            {
                ThrowOnError(_vk.CreateSemaphore(_device, &semaphoreInfo, null, out semaphores[i]), "CreateSemaphore(renderFinished)");
            }
        }
        catch
        {
            foreach (var semaphore in semaphores)
            {
                if (semaphore.Handle != 0)
                {
                    _vk.DestroySemaphore(_device, semaphore, null);
                }
            }

            throw;
        }

        if (_renderFinished.Length > 0)
        {
            // 交换链重建时，Android 呈现端可能仍在等待旧 semaphore；只能在 DeviceWaitIdle 后销毁。
            _retiredRenderFinished.Add(_renderFinished);
        }

        _renderFinished = semaphores;
    }

    private void DestroyPresentSemaphores(IEnumerable<Semaphore> semaphores)
    {
        foreach (var semaphore in semaphores)
        {
            if (semaphore.Handle != 0)
            {
                _vk.DestroySemaphore(_device, semaphore, null);
            }
        }
    }

    // ---------- 销毁 / 重建 ----------

    /// <summary>
    /// 视锥剔除：区块用包围球近似（中心 + 半对角线半径），与 6 个视锥平面做保守测试。
    /// 保守意味着不会误删可见区块（可能多画，但绝不少画）。
    /// </summary>
    private void RecreateSwapchain()
    {
        _lastSurfaceRecreateTicks = Environment.TickCount64;
        // 不能用 vkDeviceWaitIdle：Android 表面失效时队列可能永久挂起。
        // 改为有界等待在途帧栅栏；任何超时都保留旧资源并稍后重试，禁止销毁 GPU 仍可能引用的对象。
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (!_fencePending[i])
            {
                continue;
            }

            var inflight = _inFlightFences[i];
            var waitResult = _vk.WaitForFences(_device, 1, &inflight, true, 1_000_000_000);
            if (!VulkanRenderPassPolicy.CanDestroySwapchainResources(waitResult))
            {
                if (waitResult != Result.Timeout)
                {
                    ThrowOnError(waitResult, "WaitForFences(recreate)");
                }

                _retryAfterTicks = Environment.TickCount64 + 200;
                return;
            }

            _fencePending[i] = false;
            ReleaseAcquireSemaphoreForFrame(i);
        }
        DestroyOffscreenResources();
        DestroyFramebuffers();
        DestroyImageViews();
        DestroyDepthResources();
        if (_swapchain.Handle != 0)
        {
            _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
            _swapchain = default;
        }

        // Android 上 SurfaceView 重建会让旧 VkSurfaceKHR 失效（ErrorSurfaceLostKhr）；
        // 必须先销毁旧表面、再经窗口后端按当前 ANativeWindow 重新创建，否则交换链重建会永久失败。
        if (_surfaceLost)
        {
            if (_surface.Handle != 0)
            {
                _khrSurface.DestroySurface(_instance, _surface, null);
                _surface = default;
            }
            _surface = _window.CreateVulkanSurface(_vk, _instance);
        }
        var previousFormat = _swapchainFormat;
        CreateSwapchain();
        CreatePresentSemaphores();
        if (VulkanRenderPassPolicy.RequiresFormatRebuild(previousFormat, _swapchainFormat))
        {
            DestroySceneRenderResources();
            CreateRenderPass();
            CreatePipeline();
            SwapchainFormatChanged?.Invoke(_swapchainFormat);
        }
        CreateDepthResources();
        CreateFramebuffers();
        _resizeRequested = false;
        _surfaceLost = false;
    }

    private void RetireMesh(GpuMesh mesh) => _retiredMeshes.Retire(mesh, _frameNumber);

    private void RetireMaterial(GpuMaterial material) => _retiredMaterials.Retire(material, _frameNumber);

    private void RetireTexture(GpuTexture texture) => _retiredTextures.Retire(texture, _frameNumber);

    private void DestroySceneRenderResources()
    {
        DestroyPipeline(ref _backgroundPipeline);
        DestroyPipeline(ref _backgroundAlphaBlendPipeline);
        DestroyPipeline(ref _opaquePipeline);
        DestroyPipeline(ref _overlayPipeline);
        DestroyPipeline(ref _alphaBlendPipeline);
        DestroyPipeline(ref _backgroundVertexColorPipeline);
        DestroyPipeline(ref _backgroundAlphaBlendVertexColorPipeline);
        DestroyPipeline(ref _opaqueVertexColorPipeline);
        DestroyPipeline(ref _overlayVertexColorPipeline);
        DestroyPipeline(ref _alphaBlendVertexColorPipeline);
        DestroyPipeline(ref _backgroundTiledPipeline);
        DestroyPipeline(ref _backgroundAlphaBlendTiledPipeline);
        DestroyPipeline(ref _opaqueTiledPipeline);
        DestroyPipeline(ref _overlayTiledPipeline);
        DestroyPipeline(ref _alphaBlendTiledPipeline);

        if (_pipelineLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
            _pipelineLayout = default;
        }

        if (_vertShader.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _vertShader, null);
            _vertShader = default;
        }

        if (_vertexColorShader.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _vertexColorShader, null);
            _vertexColorShader = default;
        }

        if (_fragShader.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _fragShader, null);
            _fragShader = default;
        }

        if (_tiledVertexShader.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _tiledVertexShader, null);
            _tiledVertexShader = default;
        }

        if (_tiledFragShader.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _tiledFragShader, null);
            _tiledFragShader = default;
        }

        if (_offscreenRenderPass.Handle != 0)
        {
            _vk.DestroyRenderPass(_device, _offscreenRenderPass, null);
            _offscreenRenderPass = default;
        }

        if (_renderPass.Handle != 0)
        {
            _vk.DestroyRenderPass(_device, _renderPass, null);
            _renderPass = default;
        }
    }

    private void DestroyPipeline(ref Pipeline pipeline)
    {
        if (pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_device, pipeline, null);
            pipeline = default;
        }
    }

    private void DestroyRetired(long completedFrame)
    {
        _retiredMeshes.DrainCompleted(completedFrame, DestroyMeshResources);
        _retiredMaterials.DrainCompleted(completedFrame, material =>
            FreeMaterialDescriptorSets(material.DescriptorSets));
        _retiredTextures.DrainCompleted(completedFrame, DestroyTextureResources);
    }

    private void DestroyMeshResources(GpuMesh mesh)
    {
        _vk.DestroyBuffer(_device, mesh.VertexBuffer, null);
        _vk.FreeMemory(_device, mesh.VertexMemory, null);
        _vk.DestroyBuffer(_device, mesh.IndexBuffer, null);
        _vk.FreeMemory(_device, mesh.IndexMemory, null);
    }

    private void DestroyFramebuffers()
    {
        foreach (var framebuffer in _framebuffers)
        {
            _vk.DestroyFramebuffer(_device, framebuffer, null);
        }

        _framebuffers = [];
    }

    private void DestroyImageViews()
    {
        foreach (var imageView in _imageViews)
        {
            _vk.DestroyImageView(_device, imageView, null);
        }

        _imageViews = [];
    }

    private void DestroyDepthResources()
    {
        if (_depthImageView.Handle != 0)
        {
            _vk.DestroyImageView(_device, _depthImageView, null);
            _depthImageView = default;
        }

        if (_depthImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _depthImage, null);
            _depthImage = default;
        }

        if (_depthImageMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, _depthImageMemory, null);
            _depthImageMemory = default;
        }
    }

    private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
        }

        var (width, height) = _window.FramebufferSize;
        return new Extent2D
        {
            Width = Math.Clamp((uint)width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp((uint)height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height),
        };
    }


    /// <summary>表面预旋转补偿：按设备旋转角在裁剪空间旋转投影，保证输出始终正向（安卓自动旋转支持）。</summary>

    private void WarnSuboptimalOnce()
    {
        if (_warnedSuboptimal)
        {
            return;
        }

        _warnedSuboptimal = true;
        Console.WriteLine("[Cubit] 交换链 Suboptimal：继续渲染（性能非最优）");
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
        _vk.DeviceWaitIdle(_device);

        foreach (var mesh in _meshes.Values)
        {
            DestroyMeshResources(mesh);
        }
        _meshes.Clear();

        _retiredMeshes.DrainAll(DestroyMeshResources);

        DestroyOffscreenResources();
        DestroyFramebuffers();
        DestroyImageViews();
        DestroyDepthResources();

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            _vk.UnmapMemory(_device, _uboMemories[i]);
            _vk.DestroyBuffer(_device, _uboBuffers[i], null);
            _vk.FreeMemory(_device, _uboMemories[i], null);
        }

        foreach (var material in _materials.Values)
        {
            FreeMaterialDescriptorSets(material.DescriptorSets);
        }
        _materials.Clear();
        _retiredMaterials.DrainAll(material => FreeMaterialDescriptorSets(material.DescriptorSets));
        foreach (var texture in _textures.Values)
        {
            DestroyTextureResources(texture);
        }
        _textures.Clear();
        _retiredTextures.DrainAll(DestroyTextureResources);

        _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null);

        _vk.DestroySampler(_device, _fallbackSampler, null);
        _vk.DestroyImageView(_device, _fallbackView, null);
        _vk.DestroyImage(_device, _fallbackImage, null);
        _vk.FreeMemory(_device, _fallbackMemory, null);

        DestroySceneRenderResources();

        foreach (var semaphore in _imageAvailable)
        {
            if (semaphore.Handle != 0)
            {
                _vk.DestroySemaphore(_device, semaphore, null);
            }
        }
        _imageAvailable.Clear();
        _imageAvailableFree.Clear();

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            _vk.DestroyFence(_device, _inFlightFences[i], null);
        }
        DestroyPresentSemaphores(_renderFinished);
        foreach (var semaphores in _retiredRenderFinished)
        {
            DestroyPresentSemaphores(semaphores);
        }
        _retiredRenderFinished.Clear();

        _vk.DestroyCommandPool(_device, _commandPool, null);
        _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
        _vk.DestroyDevice(_device, null);
        _khrSurface.DestroySurface(_instance, _surface, null);
        _vk.DestroyInstance(_instance, null);
        _vk.Dispose();
    }



    private void PrepareCaptureBuffer()
    {
        var size = (nuint)(_extent.Width * _extent.Height * 4);
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };
        ThrowOnError(_vk.CreateBuffer(_device, &bufferInfo, null, out _captureBuffer), "CreateBuffer(capture)");
        _vk.GetBufferMemoryRequirements(_device, _captureBuffer, out var requirements);
        var memoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = memoryTypeIndex,
        };
        ThrowOnError(_vk.AllocateMemory(_device, &allocInfo, null, out _captureMemory), "AllocateMemory(capture)");
        ThrowOnError(_vk.BindBufferMemory(_device, _captureBuffer, _captureMemory, 0), "BindBufferMemory(capture)");
    }

    private void RecordCaptureCopy(CommandBuffer commandBuffer, Image image)
    {
        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            OldLayout = ImageLayout.PresentSrcKhr,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = uint.MaxValue,
            DstQueueFamilyIndex = uint.MaxValue,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        _vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransfer);

        var copy = new BufferImageCopy
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
            ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
            ImageExtent = new Extent3D { Width = _extent.Width, Height = _extent.Height, Depth = 1 },
        };
        _vk.CmdCopyImageToBuffer(commandBuffer, image, ImageLayout.TransferSrcOptimal, _captureBuffer, 1, &copy);

        var toPresent = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = 0,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.PresentSrcKhr,
            SrcQueueFamilyIndex = uint.MaxValue,
            DstQueueFamilyIndex = uint.MaxValue,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        _vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit, 0, 0, null, 0, null, 1, &toPresent);
    }

    private void SaveCapture()
    {
        var width = (int)_extent.Width;
        var height = (int)_extent.Height;
        var size = (nuint)(width * height * 4);

        void* mapped;
        _vk.MapMemory(_device, _captureMemory, 0, size, 0, &mapped);
        var pixels = new byte[width * height * 4];
        Marshal.Copy((nint)mapped, pixels, 0, pixels.Length);
        _vk.UnmapMemory(_device, _captureMemory);

        // 交换链格式 B8G8R8A8 → PNG 需要 R/B 交换
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }

        SimplePng.Save(_screenshotPath!, width, height, pixels);
        Console.WriteLine($"[Cubit] 截图已保存: {_screenshotPath} ({width}x{height})");

        _vk.DestroyBuffer(_device, _captureBuffer, null);
        _vk.FreeMemory(_device, _captureMemory, null);
    }
}
