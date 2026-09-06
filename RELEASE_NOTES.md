# Cubit 1.0 预发布开发快照与产品定位更新

> 当前状态：2026-08-15，G5 发布证据收口中；**尚未发布 Cubit 1.0**。
> 本文件保留 2026-08-08 的历史开发快照，不是版本发布公告或发布验收记录。
> 当前可追溯验收结论见工作区 `../docs/27-Cubit1.0-G5验收证据矩阵-20260815.md`。
> 性质：学习用途项目，与微软、Mojang 无关；如产生任何责任由使用者本人承担。
> 纹理取自本地 Minecraft 26.1.2 实例资源（`26.1.2.jar.src\assets\minecraft\textures\block`），仅用于学习。

## 一、这是什么

Cubit 是一个 **通用游戏引擎**：.NET + Vulkan 渲染，Godot 式架构，脚本语言为原生 C#。
新的产品主张：**通用核心 + 官方领域插件** —— `Cubit.Core` 提供场景、对象、资源、渲染、输入和平台服务；`Cubit.Voxel` 与 `Cubit.Pixel` 为官方优化插件，项目通过插件组装具体游戏玩法。

当前开发快照：体素世界已迁入独立 `Cubit.Voxel`，`Cubit.Core.World` 与 Core 方块纹理已删除。项目、Editor 和 MCP 均已迁移到插件边界。

## 二、1.0 亮点

- **通用核心边界**：Cubit.Core 不依赖示例项目，新增体素/像素领域能力不得继续堆入 Core。
- **Godot 式架构**：Node / SceneTree / NodePath / Resource / C# 事件信号 / 分组 / 固定物理步 60Hz（防螺旋死亡）。
- **Vulkan 渲染**：实例/设备/交换链/深度/管线/UBO/描述符/推常量/纹理图集；双缓冲帧同步；GPU 资源延迟销毁（2 帧安全期，无 DeviceWaitIdle 卡顿）；acquire OutOfDate/SurfaceLost/NotReady 可恢复。
- **官方体素插件**：`Cubit.Voxel` 承载数据驱动方块、噪声/平坦世界、光照、异步区块流、体素网格、图集和存档。
- **插件路线**：官方 Pixel 插件后续承载精灵/图集/瓦片地图/整数缩放/帧动画。
- **场景系统**：JSON 场景文件（SceneIO、[Export] 属性、NodePath 引用）。
- **双平台**：桌面（GLFW）+ Android（SDL / Vulkan，包名 `com.cubit.sample`）。
- **自检工具**：`Cubit.Tool`（worldcheck / simcheck / scenecheck / streamcheck / atlascheck / scene / shader）。

## 三、核心 API 概览（Cubit.Core）

| 模块 | 类型 |
| --- | --- |
| Scene | Node、SceneTree、NodePath、Resource、SceneFile、SceneIO、ExportAttribute、Camera3D |
| Rendering | RenderingServer、IRenderBackend、TextureData、MaterialData、MeshData、VulkanContext |
| ECS / Jobs | EcsWorld、CommandBuffer、EcsScheduler、JobSystem、EcsNode |
| Camera | FlyCamera |
| Input | GameInput |
| Platform | IGameWindow |

## 四、运行方式

```powershell
# 桌面（0 警告 0 错误）
dotnet build hosts\Cubit.Sample\Cubit.Sample.csproj
$env:CUBIT_PROJECT_PATH = (Resolve-Path ..\我的第一个cubit项目)
dotnet run --project hosts\Cubit.Sample

# 平坦世界
$env:VOXEL_WORLDTYPE = "flat"
$env:CUBIT_PROJECT_PATH = (Resolve-Path ..\我的第一个cubit项目)
dotnet run --project hosts\Cubit.Sample

# 自检（全绿）
dotnet run --project tools/Cubit.Tool -- worldcheck 20260808
dotnet run --project tools/Cubit.Tool -- simcheck 20260808
dotnet run --project tools/Cubit.Tool -- scenecheck
dotnet run --project tools/Cubit.Tool -- streamcheck 20260808
dotnet run --project tools/Cubit.Tool -- atlascheck
dotnet run --project tools/Cubit.Tool -- scene ..\我的第一个cubit项目\scenes\gameplay.cscene
```

Android 构建（需要 .NET Android 工作负载 + Android SDK + JDK）：

```powershell
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:JAVA_HOME = "<JDK17+ 路径>"
dotnet build hosts\Cubit.Sample.Android\Cubit.Sample.Android.csproj -c Debug `
  -p:EmbedAssembliesIntoApk=true -p:AndroidUseSharedRuntime=false
```

## 五、1.0 验收状态

- 最新桌面和 Android Debug 构建：0 个 C# 错误；离线 NuGet 源会产生已知环境警告 `NU1900`，不代表代码告警。
- 最新 Android APK 静态审计：`androidpackagecheck`、`androidnativeaudit`、`androidreleasecheck` 通过；项目本地 SDL AAR 与 APK 一致，全部 ARM64 ELF `PT_LOAD=0x4000`。
- Core、插件、ECS/Jobs、Editor 预览和 Physics/Animation/Content/Audio/Scripting 均有独立 Tool 验证；完整证据和边界见当前验收矩阵。
- G3/G4 已完成；G5 尚未关闭。Android 真机触摸、音频、后台退出和冷启动没有设备证据；迁移前同口径 p95 不可追溯，不能宣称“性能回退不超过 10%”已经证明。

## 六、已知限制与技术债（不阻塞 1.0）

1. **视锥剔除**：上一版实现有误（平面提取与裁剪结果不一致导致误剔），已整体移除。重新引入前必须先写单元测试（固定相机下“可见区块必被绘制、不可见区块必被剔除”）。
2. **Android 真机回归**：引擎改动后需在真机复跑（上次通过：vivo V2425A / Android 16 / Adreno）。
3. **区块边界补网格**：已实现，缺专项断言（streamcheck 已覆盖基础路径）。
4. **SDL/Android 表面生命周期**：首帧偶发一次 `QueueSubmit: ErrorInitializationFailed`，已容错不崩溃；根治方向是正确处理 Activity 暂停/恢复与表面丢失。
5. **相机万向锁**：俯仰 ±90° 视图矩阵退化；游戏内已限幅 ±88.8° 规避。

## 七、后续开发方向（2026-08-09）

> 下一阶段 = 通用 Core 稳定化 + 官方 Voxel/Pixel 插件边界 + 原生 C# + 内置 MCP。体素机制继续借鉴 Minecraft，但实现归属插件，不再扩大 Core 的领域耦合。

| 里程碑 | 内容 | 验收 |
| --- | --- | --- |
| M1 骨架 | 20Hz 确定性 tick + 渲染插值 + RenderingServer(RID) + 暂停/ProcessPriority | simcheck/scenecheck 全绿，桌面 0 警告 |
| M2 世界存储 | BlockState + Section 自适应调色板 + 区块状态机/TicketLevel + 版本化存档往返 | worldcheck 27 项全绿 |
| M3 光照 | 天空光/方块光 + 网格明暗采样 + 存档 v2 光照（修复 int/byte 错位 bug） | lightcheck 全绿 |
| M4 渲染 | AO 角遮蔽 + 视锥剔除（frustumcheck 单测先行）+ 网格明暗 | frustumcheck 全绿 |
| M5 模拟 | 计划刻/随机刻/BlockEntity + DeterministicRandom | tickcheck 全绿 |
| M6 MCP | Cubit.Mcp 内置 JSON-RPC stdio（17 工具）+ GameSceneMcpHost | mcpcheck 内存管道 + 真实 stdio 全绿 |
| M7 内容包 | BlockRegistry 动态注册 + SurfaceRules 数据驱动 | packcheck 全绿 |
| M8 存档 | region 分片（32×32/文件）+ level.json 版本化 + v2→v3 DataFixer | regioncheck 全绿 |
| M9 平台与回归 | Android 真机渲染 GameScene 地形（修复纯蓝清屏）+ headless 确定性冒烟 | 真机 vivo V2425A/Adreno 地形正常渲染；12 项自检全绿 |
| M17 场景编辑 | NodeRegistry（ClassDB 式）+ MCP scene.add_node/remove_node/save/load（预览） | mcpcheck 20 请求全绿；18 项自检全绿 |
| M16 MCP prompts | prompts/list + prompts/get（4 提示词，模板嵌入参数）；MCP 协议面完整 | mcpcheck 15 请求全绿；18 项自检全绿 |
| M15 MCP resources | resources/list + resources/read（cubit://scene/tree、world/registry、run/status）；协议面补全 | mcpcheck 13 请求全绿；18 项自检全绿 |
| M14 MCP input | MCP input.* 工具（7 个）：AI 注入动作/摇杆/视角/挖放/选块/快照；与平台输入共享 InputServer | mcpcheck/mcphttptest 全绿；18 项自检全绿 |
| M13 MCP HTTP | MCP HTTP 传输（POST /mcp + GET /health，TcpListener 最小服务器）；McpServer 核心 stdio/HTTP 共用 | mcphttptest 全绿；18 项自检全绿 |
| M10 引擎显示服务 | 表面预旋转（Godot 方案：IDENTITY + 接受 SUBOPTIMAL）+ DisplaySettings/Engine.ConfigureDisplay 拉伸服务（letterbox）+ 修复渲染垂直翻转（负高度视口） | viewportcheck/displaycheck 锁定约定；14 项自检全绿 |
| P1 插件契约 | 定义领域插件生命周期、注册、资源包、版本兼容和编辑器扩展接口 | ✅ 已完成 |
| P2 Cubit.Voxel | 将 Core.World 破坏性迁移为官方体素插件，不保留兼容层，提供场景升级器 | ✅ 已完成；正式项目/Editor/MCP/Tool 全部迁移 |
| P3 Cubit.Pixel | 精灵批处理、图集、瓦片地图、整数缩放、帧动画和像素采样 | 像素示例项目可独立运行并通过像素渲染自检 |

**自检命令（25 项全绿基线）**：plugincheck / jobcheck / ecscheck / renderresourcecheck / voxelcheck / editorplugincheck / headless / puritycheck / worldcheck / worldgencheck / meshercheck / lightcheck / frustumcheck / viewportcheck / displaycheck / inputcheck / scenecheck / mcphttptest / simcheck / tickcheck / streamcheck / mcpcheck / packcheck / regioncheck / atlascheck。另用 `benchmarkcheck 20260808` 记录迁移后性能基线。
