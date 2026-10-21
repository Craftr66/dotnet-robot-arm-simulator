# 基于.net开发的仿真机械臂平台

AxisLab：三轴/六轴机械臂、虚拟相机、VisionMaster 视觉标定与 WPF 上位机实训。

**当前版本：AxisLab 0.7.0 / WPF 1.2.0。** 阶段 7B 已实现可识别标定板、TCP 圆点与九点采样向导；包含 9 个标定点＋3 个独立验证点、曝光配对导出、VM 结果误差报告入口。用户视觉算法与完整定位精度尚待实际 .sol 验收。

- [从零采样、VM 九点标定及 WPF 集成教程](docs/VM九点标定与WPF集成教程.md)
- [阶段 7B 实际验收与本机限制](docs/阶段7B验收记录.md)
- [第三方软件与厂商 SDK 说明](THIRD-PARTY-NOTICES.md)

源码版快速开始：Windows 安装 .NET 10 SDK、Node.js 24，运行 `npm ci --ignore-scripts`、`npm run build`，再运行 `start.ps1`。独立 WPF 使用 `src/WpfDemo/AxisLab.WpfDemo.csproj`；VM 桥接构建需本机 VM 4.3.0 SDK 和 .NET Framework 4.8 开发引用。仅使用场景、手动控制和取图无需 VM。

Git 仓库保存源码、文档与合成示例；便携 EXE、依赖缓存、用户方案、工程和采样数据不进入源码提交。

本项目是可搭建不同场景的机械臂与相机仿真平台。用户可添加、配置设备，操作机械臂，使用虚拟相机拍照、预览、保存和向外部 C# 程序传输图像，并逐步练习拖拽编程、示教及标定。

2026-10-21 开发方向调整：后续以 **AxisLab 虚拟设备 → VisionMaster 用户视觉流程 → 独立 WPF 上位机 → AxisLab 机器人** 为主线。现有积木功能保留，停止作为主要教学方案。新设计见[VisionMaster 与 WPF 集成设计](docs/VisionMaster与WPF集成设计.md)。

当前 WPF 主线为 [AxisLab.WpfDemo 1.2.0](dist/AxisLab.WpfDemo-1.2.0-win-x64/AxisLab.WpfDemo.exe)，基于用户提供的 WpfDemo 源码修改，保留原白色三栏界面、机械臂控制与拍照功能，新增 VisionMaster 和标定定位页。主程序沿用 .NET 9，通过独立 .NET Framework 4.8 进程调用本机 VM 4.3.0。见[使用说明](docs/原WPF框架集成说明.md)和[验收记录](docs/原WPF框架集成验收.md)。AxisLab 本体为 0.7.0；用户自己的视觉标定 .sol 和定位精度需后续接入验证。

此前独立的 WPF Vision Demo 0.1 保留为接入原型，记录见[原型验收](docs/VisionMaster接入验收记录.md)。后续开发以用户原框架为准。

## 桌面应用

当前 Windows x64 桌面版 **AxisLab 0.7.0**。先保存并退出旧版，再双击 [AxisLab.exe](dist/AxisLab-0.7.0/AxisLab.exe)；分享或换电脑时，解压 [发行 ZIP](dist/AxisLab-0.7.0-win-x64.zip) 并保留完整文件夹。

桌面版有独立窗口、原生菜单和自动管理的内部服务，无需打开终端、启动浏览器、安装 .NET SDK 或 Node.js。需要已安装 Microsoft Edge WebView2 Runtime，本机已具备。工程保存在 `%LOCALAPPDATA%\AxisLab\Projects`，旧网页版本的工程可通过 JSON 导入。

[桌面版使用说明](docs/桌面版使用说明.md) · [桌面版验收记录](docs/桌面版验收记录.md)。当前发布包为便携目录版，尚未制作安装向导或代码签名。

## 项目文档

- [总体设计文档](docs/总体设计文档.md)：当前开发主依据，定义通用平台、模块、数据、图像与通信接口。
- [需求规格](docs/需求规格.md)：功能范围、坐标约定和验收场景。
- [分阶段实施计划](docs/分阶段实施计划.md)：开发顺序、交付物和阶段验收门槛。
- [通信协议草案](docs/通信协议草案.md)：旧工作单元的业务映射参考，尚未冻结；通用设备寻址以总体设计为准。
- [双相机传送带工作单元](docs/双相机传送带工作单元.md)：示例场景，可在平台基础能力完成后搭建。

## 当前进度

阶段 0：已整理通用平台总体设计与分步实施计划。

阶段 1：已实现本地场景编辑器 v0.1，提供空白三维场景、设备添加/复制/删除、属性与位姿编辑、父对象挂载、工程保存/重开及 JSON 导入导出。详见[阶段 1 验收记录](docs/阶段1验收记录.md)。

阶段 2：已实现六轴关节状态、有限步长点动、速度限幅、回零/停止、FK、法兰/TCP 位姿、末端相机挂载、当前关节位置保存与 v1 工程迁移。详见[阶段 2 验收记录](docs/阶段2验收记录.md)。

阶段 3：已实现机械臂尺寸编辑、独立/末端相机按曝光快照成像、PNG 预览/保存、帧信息、HTTP 二进制取图和独立 C# 示例。详见[阶段 3 验收记录](docs/阶段3验收记录.md)和[相机与尺寸使用说明](docs/相机与尺寸使用说明.md)。

阶段 4：已实现通用 Modbus TCP、设备发现、连接私有窗口、控制权/心跳、受限关节动作、停止、命令去重及相机帧映射，新增通用三轴 3R。连接步骤见[外部连接快速开始](docs/外部连接快速开始.md)，地址与操作定义见[Modbus 操作协议](docs/ModbusTCP操作协议v1.md)。

阶段 5：已实现三轴/六轴 IK、位姿 MoveJ、直线 MoveL、速度与加速度限制、基础碰撞预检、TCP/工件坐标、示教点及逐点回放。见[运动与示教使用说明](docs/运动与示教使用说明.md)、[HTTP/Modbus 扩展](docs/运动与示教接口.md)及[阶段 5 验收记录](docs/阶段5验收记录.md)。

阶段 6：已接入离线 Blockly 积木编辑器，支持运动/拍照流程、条件循环、变量、单步、断点、停止及 C# 程序控制。见[积木编程使用说明](docs/积木编程使用说明.md)、[程序接口](docs/积木程序接口.md)和[阶段 6 验收记录](docs/阶段6验收记录.md)。

阶段 7B：已实现可识别标定对象、六轴固定相机九点采样与独立验证入口。下一步完成用户 VM 方案、SDK 授权识别、平面定位精度及完整手眼。辅助设备及完整案例继续按阶段 8—9 推进。

需求基线 v0.3：平台不限定固定工位数量、双相机或传送带布局；设备数量受当前版本能力及测试机资源约束。传送带抓放是示例，不能成为核心模块的硬编码依赖。

## 开发模式启动

需要 Windows、.NET 10 SDK、Node.js 22+，浏览器使用支持 WebGL 2 的 Edge/Chrome。前端依赖当前已安装在本机工作区；源代码交付时包含 pnpm 锁文件，不包含 node_modules。

在项目根目录运行：

```powershell
# 新下载源代码时先安装前端依赖（任选本机已有的包管理器）
pnpm install --frozen-lockfile
# 或 npm install

.\start.ps1
```

打开 [AxisLab 本地界面](http://127.0.0.1:5080)。首次启动脚本会检查 TypeScript 并构建界面与 C# 服务；以后未修改代码可用 `.\start.ps1 -NoBuild`。若端口占用，可用 `-Port 5081`。Ctrl+C 停止服务；重新编译 C# 前请先停止已有服务。

本机 Codex 环境未提供全局 pnpm/npm 命令时，可用以下已有 pnpm 启动器安装依赖：

```powershell
& "$env:USERPROFILE\.cache\codex-runtimes\codex-primary-runtime\dependencies\bin\fallback\pnpm.cmd" install --frozen-lockfile
```

启动后所有界面资源来自本地，使用软件无需访问 CDN。若 PowerShell 的执行策略阻止脚本，可分别运行下面的构建与启动命令，无需更改系统策略。

```powershell
node scripts/build.mjs
dotnet run --project src/Host/AxisLab.Host.csproj --no-launch-profile
```

## 使用与工程文件

1. 从设备库添加机器人、相机或长方体，在场景树中选择对象。
2. 编辑右侧属性后点击“应用属性”；也可拖动三维坐标轴。W 平移、E 旋转、F 聚焦。
3. 点击“保存工程”，服务将工程写入根目录 `data/projects/<工程ID>.scene.json`。重启服务后先显示空白工作区，点击“打开”恢复已保存工程。
4. “导出 JSON”可将工程另存到下载目录；“导入”可加载 JSON。可导入 [基础设备示例](samples/basic-layout.scene.json) 体验一台机械臂、两台相机与一个工件的布局。

复制只复制当前设备，不复制子对象；删除挂载父对象时会列出子对象数量，确认后级联删除。修改父对象保留局部位姿数值，可能改变世界位置，界面有明确提示。

阶段 2 可导入 [六轴与末端相机示例](samples/joint-motion.scene.json)。选择机器人，设置下方六关节目标后执行，或用增减按钮点动。运动中锁定场景结构；先停止才能保存当前姿态。相机父坐标可选择基座、法兰或 TCP，F/T 坐标轴和位姿由核心统一计算。

阶段 3 可导入 [双相机拍照与尺寸示例](samples/camera-capture.scene.json)。选择机器人，在右侧修改 7 段长度和 6 个关节直径，再应用属性；选择相机，在下方“相机画面”拍照、保存 PNG 或导出帧信息。长度参与 FK，直径同时参与基础碰撞代理。v1/v2/v3/v4 工程自动补齐默认参数，保存为 v5；旧软件不支持 v5，建议保留旧工程副本。

HTTP 抓图需要保持软件渲染界面打开。桌面菜单“工具 → 连接信息/复制 HTTP 连接地址”给出本次动态端口；运行 [C# 取图示例](samples/CSharpClient/README.md) 可完成独立程序触发、下载和 SHA-256 校验。相机为无畸变针孔模型，当前单帧最多 4194304 像素；无连续预览与视觉识别。

## 开发与检查

```powershell
node node_modules/typescript/bin/tsc --noEmit -p src/Frontend/tsconfig.json
node scripts/build.mjs
dotnet build AxisLab.slnx
dotnet run --project tests/CoreChecks/AxisLab.CoreChecks.csproj
dotnet build samples/CSharpClient/AxisLab.CaptureClient.csproj -c Release
```

浏览器验收使用隔离服务和独立数据目录，避免修改用户工程。另开终端启动：

```powershell
$env:AXISLAB_URL = 'http://127.0.0.1:5091'
$env:AXISLAB_DATA = Join-Path $PWD 'artifacts/ui-projects'
dotnet run --project src/Host/AxisLab.Host.csproj --no-build --no-launch-profile
```

再运行 `node scripts/ui-checks.mjs`，使用本机 Edge，无须另外安装浏览器。测试应从空白工作区开始；重复运行前重启测试服务。验收输出与截图位于 `artifacts/`。UI 测试强制使用软件 WebGL 以保持测试环境一致，其帧率不代表实际显卡性能。

随后运行 `node scripts/camera-checks.mjs`，在同一隔离服务上验证尺寸、像素投影、双相机、真实 C# 取图与示例。该脚本会替换测试服务的工作区，不能指向正在编辑的用户工程。发布后 `node scripts/desktop-checks.mjs` 运行独立数据目录的桌面验收；测试连接设置 `noDefaults` 保留 WebView 原生下载，真实点击后操作系统对话框保存文件。

项目结构：`src/Core` 为设备/工程与校验，`src/Host` 为 C# HTTP 和静态界面服务，`src/Frontend` 为 TypeScript/Three.js，`tests/CoreChecks` 为无第三方测试框架的核心检查。


新增三轴使用说明：[通用 3R 操作](docs/三轴机械臂操作说明.md)。外部连接场景：[三轴/六轴与双相机](samples/external-control.scene.json)。阶段交付结果：[阶段 4 验收](docs/阶段4验收记录.md)。

