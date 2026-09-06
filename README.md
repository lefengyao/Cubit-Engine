# Cubit —— .NET + Vulkan 通用游戏引擎

> 定位：通用游戏引擎核心 + 官方体素/像素插件生态。体素与像素是重点优化方向，但不是引擎核心的边界。
> 示例项目已迁移到工作区同级的 `../我的第一个cubit项目/`；`Cubit-Engine` 仓库只保留引擎本体、运行宿主与工具链。
> 技术基线：.NET 9 / C# 13 / Silk.NET Vulkan / SPIR-V（预编译内嵌）/ 数据驱动。
> 纹理：取自本地 Minecraft 26.1.2 实例资源（`26.1.2.jar.src\assets\minecraft\textures\block`），仅用于学习用途。
> 架构定位：见 `../docs/13-通用引擎与官方插件定位.md`；历史恢复点与实验记录见 `../.codex/sessions/`。

## 项目结构

```
Cubit-Engine/
├─ src/
│  ├─ Cubit.Core/            # 通用引擎核心（场景/对象/渲染/输入/资源）
│  │  ├─ Rendering/          #   通用 Vulkan 渲染服务（IRenderBackend、MeshData、RID）
│  │  ├─ Plugins/            #   插件契约、注册表、确定性生命周期与诊断
│  │  ├─ Ecs/                #   generation 实体、稀疏集、查询、CommandBuffer、System 调度
│  │  ├─ Jobs/               #   多核 worker 池、工作窃取、取消、异常与排空关闭
│  │  ├─ Scene/              #   场景树（Node/SceneTree/Resource/SceneIO/Camera3D）
│  │  ├─ Input/              #   输入快照（GameInput）
│  │  ├─ Camera/             #   飞行相机
│  │  ├─ Platform/           #   平台抽象（IGameWindow）
│  │  ├─ Shaders/            #   通用着色器资源
│  │  └─ Imaging/ Engine/    #   图像工具与宿主
│  ├─ Cubit.Voxel/           # 官方体素插件（World/Textures/Input，cubit.voxel@1.0.0）
│  ├─ Cubit.Animation/       # 官方通用动画插件（时间轴/Tween，cubit.animation@1.0.0）
│  ├─ Cubit.Audio/           # 官方音频核心插件（48 kHz PCM/显式后端，cubit.audio@1.0.0）
│  ├─ Cubit.Mcp/              # 内置 MCP Server（JSON-RPC，AI/编辑器驱动引擎）
│  └─ Cubit.Editor/           # 内置 ImGui 编辑器
├─ assets/shaders/            # 引擎公共着色器
├─ hosts/                     # 桌面与 Android 平台运行宿主（不包含项目玩法源码）
│  ├─ Cubit.Sample/
│  └─ Cubit.Sample.Android/
├─ tools/Cubit.Tool/         # CLI 工具（shader/worldcheck/simcheck/scenecheck/streamcheck/atlascheck/scene）
└─ Cubit.sln

../我的第一个cubit项目/
├─ scenes/gameplay.cscene     # 作者场景与项目玩法源码
└─ assets/                    # 项目拥有的方块、实体、UI 等游戏资产

../通用3D-ECS示例项目/
├─ src/Cubit.Ecs.Sample/      # 只依赖 Cubit.Core 的高数量 ECS 场景
├─ scenes/main.cscene         # 真实 EcsNode 场景文件
└─ project.cubit.json         # 不声明体素或其他官方插件
```

官方插件状态：`Cubit.Voxel` 已独立，面向区块世界、体素网格、光照和流式；`Cubit.Pixel` 仍在规划中，面向精灵、图集、瓦片地图、整数缩放和帧动画。插件依赖 Core，Core 不反向引用插件。

## 项目插件声明

项目通过 `project.cubit.json` 显式声明插件及精确版本：

```json
{
  "formatVersion": 1,
  "name": "示例项目",
  "mainScene": "scenes/main.cscene",
  "plugins": [
    { "id": "cubit.voxel", "version": "1.0.0" }
  ]
}
```

1.0 使用显式程序集引用，不进行目录扫描或热加载。`PluginManager` 在启动前校验缺失插件、重复 ID、精确版本和类型名冲突；插件按项目声明顺序注册/启动，按逆序停止，启动失败会回滚已启动插件。

## 依赖清单（NuGet，已获取）

| 包 | 版本 | 用途 | 用于 |
| --- | --- | --- | --- |
| Silk.NET.Vulkan | 2.23.0 | Vulkan API 绑定 | 引擎核心 |
| Silk.NET.Vulkan.Extensions.KHR | 2.23.0 | 交换链/表面（KhrSwapchain/KhrSurface） | 引擎核心 |
| Silk.NET.Vulkan.Extensions.ANDROID | 2.23.0 | Android 表面扩展 | Android |
| Silk.NET.Windowing | 2.23.0 | 桌面窗口（GLFW 后端） | 桌面 |
| Silk.NET.Windowing.Sdl | 2.23.1-cubit.3 | 项目本地修订包；SDL Java/native 均为 2.32.10 的移动端窗口后端 | Android |
| Silk.NET.Input | 2.23.0 | 键鼠/触摸输入 | 桌面 + Android |
| Silk.NET.Maths | 2.23.0 | 向量/矩阵数学 | 引擎核心 |
| Silk.NET.Shaderc | 2.23.0 | GLSL → SPIR-V（仅桌面工具用；Android 端不依赖原生 shaderc） | 工具 |
| StbImageSharp | 2.30.15 | PNG 解码（体素图集） | Cubit.Voxel |

> 传递依赖：Silk.NET.Core 2.23.0、Silk.NET.Shaderc.Native 2.23.0（win/linux/osx）、Ultz.Native.SDL 2.32.10 等。

## 环境要求

- .NET SDK 9.0+（已确认 9.0.315）
- Vulkan 运行时 1.4+（桌面 NVIDIA）；Android 要求 Vulkan 1.1 可用
- Android 构建：`.NET Android 工作负载` + Android SDK（API 34/35）+ JDK 17+（本机已装齐）
- Android 发行：安装下限 Android 7.0 / API 24，正式 ABI 为 `arm64-v8a`；`x86_64` 留作后续模拟器测试，32 位 ABI 不在 1.0 承诺范围
- 首次还原需要联网（NuGet），之后可离线构建

## 构建与运行

### 桌面
```powershell
dotnet build hosts\Cubit.Sample\Cubit.Sample.csproj
$env:CUBIT_PROJECT_PATH = (Resolve-Path ..\我的第一个cubit项目)
dotnet run --project hosts\Cubit.Sample
```

### Android（真机 USB 调试）
```powershell
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:JAVA_HOME = "<JDK17+ 路径>"
dotnet build hosts\Cubit.Sample.Android\Cubit.Sample.Android.csproj -c Debug `
  -p:EmbedAssembliesIntoApk=true -p:AndroidUseSharedRuntime=false
adb install -r hosts\Cubit.Sample.Android\bin\Debug\net9.0-android\android-arm64\com.cubit.sample-Signed.apk
adb shell monkey -p com.cubit.sample -c android.intent.category.LAUNCHER 1
```

### 着色器重新编译（改 GLSL 后）
```powershell
dotnet run --project tools/Cubit.Tool -- shader <Cubit根目录>
```

### 无界面世界自检（验证生成/射线/挖放/逐方块）
```powershell
dotnet run --project tools/Cubit.Tool -- worldcheck [种子]
dotnet run --project tools/Cubit.Tool -- simcheck [种子]
dotnet run --project tools/Cubit.Tool -- atlascheck
```

### Core-only ECS 正式项目自检
```powershell
dotnet build ..\通用3D-ECS示例项目\src\Cubit.Ecs.Sample\Cubit.Ecs.Sample.csproj -c Debug
dotnet run --project tools/Cubit.Tool -- ecsproductioncheck
```

### 场景文件（0.8）
```powershell
# 生成并回读示例项目场景（../我的第一个cubit项目/scenes/gameplay.cscene）
dotnet run --project tools/Cubit.Tool -- scene
```

### 世界类型切换
```powershell
# 环境变量：VOXEL_WORLDTYPE=flat 用平坦世界（原版超平坦思路），默认噪声地形
$env:VOXEL_WORLDTYPE = "flat"
$env:CUBIT_PROJECT_PATH = (Resolve-Path ..\我的第一个cubit项目)
dotnet run --project hosts\Cubit.Sample
```

## 进度

| 里程碑 | 状态 |
| --- | --- |
| M0 环境与骨架 | ✅ 完成（解决方案 + 依赖 + 版本锁定） |
| M1 Vulkan 渲染骨架（桌面） | 🟡 代码完成、编译通过；桌面运行验证待做 |
| M1-M Android 平台层 | ✅ 真机验证通过（vivo V2425A / Android 16 / arm64-v8a / Adreno）：体素 3D 场景、SDL `48 kHz / 双声道 / float32` AudioTrack、后台退出及冷启动均成功；双区触摸和 440 Hz 主观可听结果仍待验收 |
| M2 领域插件基础 | ✅ G1-G4 完成；`Cubit.Voxel` 独立，Core.World 已删除，正式项目/Editor/MCP 已迁移 |
| M3 方块逐个实现 | ✅ 数据驱动 BlockDef 逐个注册（草/泥土/石/沙/木/叶/基岩）；支持平坦世界（`VOXEL_WORLDTYPE=flat`） |
| 引擎加固 | ✅ 0 编译警告；GPU 资源延迟销毁（2 帧安全期）；acquire NotReady 优雅跳过；帧统计 API |
| 0.5 场景基础 | ✅ Godot 式场景架构：Node / SceneTree / NodePath / Resource / C# 事件信号 / 分组 / 固定物理步；scenecheck 12/12 |
| 0.7 游戏节点化 | ✅ Camera3D / VoxelWorldNode / PlayerController / GameScene；桌面与 simcheck 均节点驱动，渲染服务分离 |
| 0.8 资源化与场景文件 | ✅ BlockDef/WorldConfig : Resource；SceneIO JSON 场景（[Export] + NodePath 引用）；`scene` 命令保存/加载示例项目中的 demo.cscene |
| 0.9 异步区块流 | ✅ 生成+网格化在工作线程，主线程仅上传；streamcheck 全绿（视锥剔除因有误移除，见 docs/06 技术债） |
| 1.0 收口 | 🚧 G5：双端构建、全量检查、编辑器视口、迁移后基准、Android 16 KB 审计和真机启动/音频链路已有证据；缺迁移前 p95 对照、双区触摸和 440 Hz 主观可听结果 |

## 已知问题
- Silk.NET SDL/Android 表面生命周期在个别首帧会触发一次 `QueueSubmit: ErrorInitializationFailed`；已做容错（捕获 + 重建交换链 + 不崩溃），根治方向：正确处理 Activity 暂停/恢复与表面丢失。详见 `docs/02`。
- 相机垂直向下（pitch = ±90°）时视图矩阵会退化（万向锁）；游戏内俯仰已限幅 ±88.8° 规避。

## 引擎状态
- 渲染：Vulkan 1.1 实例/设备/交换链/深度/管线/UBO/描述符/推常量，双缓冲帧同步
- 资源管理：通用 Texture/Material/Mesh RID 延迟销毁（2 帧安全期），无 DeviceWaitIdle 卡顿
- 健壮性：acquire OutOfDate/SurfaceLost/NotReady 均可恢复；渲染异常容错
- 统计：`MeshCount` / `TotalVertices` / `FrameCount` / `RetiredMeshCount`（调试用）

## Core 1.0 当前状态（2026-08-11）

`Cubit.Physics` 已作为可选 `cubit.physics@1.0.0` 独立交付，提供 3D 角色、刚体、查询、接触、材质、休眠/唤醒和固定步确定性。插件入口只在主线程启动，禁止重复启动，`Stop` 幂等；它不创建全局物理世界、后台 worker、GPU 资源或 Core 服务。运行 `dotnet run --project tools/Cubit.Tool --no-build -- physicscheck` 验证完整物理契约。

当前已通过 `puritycheck`、`plugincheck`、`jobcheck`、`ecscheck`、`contentcheck`、`physicscheck`、`animationcheck`、`audiocheck`、`spatialcheck`、`partitioncheck`、`voxelstagecheck` 和 `ecsproductioncheck`。`Cubit.Animation` 已作为可选 `cubit.animation@1.0.0` 交付；`Cubit.Audio` 已完成 48 kHz 双声道 PCM 核心、受限 WAV 解析、显式后端提交、总线、场景播放器和缺失后端诊断 `audio.backend.missing`。Windows 已验证默认设备与队列排空；Android 已在真机建立 `48 kHz / 双声道 / float32` 系统输出链路，但 440 Hz 的主观可听结果仍待确认。五项核心模块验收前不开始新 demo，也不把物理或体素逻辑移回 `Cubit.Core`。











