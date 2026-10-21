# AxisLab WpfDemo 1.2 · 基于用户原项目

本版基于用户提供的 WpfDemo/WpfDemo 源码修改，保留原 .NET 9 WPF 项目、白色三栏界面、工程/设备列表、关节控制、相机拍照和日志。AxisLab 本体使用0.7.0，不需要使用内置积木。

新增“九点采样”页、TCP 圆点与独立误差报告，参见[完整教程](VM九点标定与WPF集成教程.md)。本轮实际验证及 SDK 授权限制见[阶段 7B 验收](阶段7B验收记录.md)。

## 启动

1. 打开 AxisLab 0.7.0，在软件中导入本发布包 Samples/vm-bridge.scene.json。该场景避免三轴与六轴初始相交，用于接入练习，不是已标定场景。
2. 运行 AxisLab.WpfDemo.exe，沿用原来的“自动检测/连接”，或者填写 AxisLab 显示的实际 HTTP 地址。
3. 左侧选择机器人，再选择相机。两种选择分别保留，点击相机页“拍照并取图”。AxisLab 渲染窗口须保持打开。

主程序自带.NET 9.0.20运行时。使用 VisionMaster 功能还需本机 VM4.3.0/.NET Framework4.8及实际运行授权；手动控制和取图不会预先启动VM。厂商SDK和算法包未附带发布。

## 原框架上的新增入口

右侧增加三个页签：

| 页签 | 功能 |
| --- | --- |
| 相机取图 | 原来的拍照、PNG/JSON保存、自动保存和帧信息 |
| VisionMaster | 选.sol、流程/图像源、输入输出字段、加载、图像源自检和运行视觉流程 |
| 标定定位 | 配对样本、用户标定方案绑定、Z/Rx/Ry、预检及单次定位 |

配对样本保存 PNG 与 observation.json，含曝光时机器人关节、法兰/TCP位姿和列主序矩阵。样本不含相机安装真值和内参真值。原“保存PNG/JSON”保留完整帧元数据，不能把其中相机真值当作标定算法输入。

## VisionMaster 流程接口

用户先在VM中用导出的图片建立标定、识别及坐标映射流程，再保存.sol。在Demo的VisionMaster页填写准确的流程名和图像源模块名，点击加载。加载只改变内存中的SDK图像源模式，不覆盖.sol。

默认接口如下，均可在页面更改名称：

| 方向 | 名称 | VM类型及意义 |
| --- | --- | --- |
| 输入 | AxisLabRequestId | 整数，本次请求编号 |
| 输出 | ResultRequestId | 整数，本帧处理完毕后回传同一个编号 |
| 输出 | CalibrationOk | 整数0/1，用户流程确认当前标定是否可用 |
| 输出 | TargetOk | 整数0/1，本帧是否恰有一个有效目标 |
| 输出 | RobotX、RobotY | 各一个浮点数，机器人基坐标下目标TCP的X/Y，mm |
| 输出 | RobotRz | 一个浮点数，基坐标下目标TCP的Rz，deg |

失败分支必须输出NG，每次运行重置有效标志，本帧计算结束后发布请求编号与结果。不要用永久常量1冒充标定有效。坐标必须由你的标定/映射流程算出，像素不能直接作为毫米坐标。

“图像源自检”只验证同一帧像素确实进入真实VM SDK，不生成运动目标。“送图并运行”才读取约定的流程输出。

## 定位

1. 取得有效的本帧VM结果，进入“标定定位”。
2. 填写你已完成的VM标定方案标识并绑定。绑定记录关联设备、配置、会话和方案，不执行标定求解。
3. 设置目标TCP的Z、Rx、Ry，或读取当前TCP的对应值。速度沿用中间机器人面板。
4. 预检后执行一次定位。使用Modbus操作码7；三轴按仅位置求解，六轴使用完整目标姿态。
5. 若原先没有手动持权，定位会自动申请并在完成后释放；若已经手动接管，完成后保留原控制方式。

结果需在取图后30秒内使用，拍照后场景/机器人修订变化、设备安装/几何/TCP变化、VM方案/映射变化均需重新采集或绑定。首版只允许固定相机、单目标、已知平面和基坐标MoveJ。末端相机可取图和送VM，定位需后续手眼变换。

中间原“停止”按钮同时取消VM任务，并用独立Stop连接停止机器人；不会等待当前运动命令的轮询结束。断开和关闭窗口会停止已接管的机器人并退出本Demo启动的VM服务。

## 项目结构与构建

原文件AxisLabConnection、AxisLabHttpClient、ExternalControlSession和MainWindow继续承担现有业务。新增MainWindow.Vision.cs、VisionContext.cs、VmBridgeClient.cs；VmBridge为.NET Framework4.8配套进程，VmSdk为SDK适配源码。原.NET9主进程不直接加载厂商.NET4.8程序集。

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release
```

在源码目录运行此脚本，会构建两个进程，并把配套Worker复制到主程序输出目录的VmBridge子目录。随后运行bin/Release/net9.0-windows/AxisLab.WpfDemo.exe。首次在Visual Studio调试前也先执行脚本（Debug配置），之后可正常调试原主项目。需要.NET9或更新SDK、.NET Framework4.8开发引用程序集及本机VM SDK；发布版无需SDK。

默认VM路径 C:\Program Files\VisionMaster4.3.0。其他目录构建Worker时用 `-p:VmRoot="D:\VisionMaster4.3.0"`，运行时设置环境变量 AXISLAB_VM_ROOT。

使用完整发布目录，保留VmBridge子目录。Source目录提供可单独打开的源码副本。工程版本、运行方法和验收范围以本文为准，原项目0.4时代的限制不再作为当前版本说明。

当前没有替用户制作标定/识别.sol，未验证用户算法定位精度。标定板和九点采样已在 7B 实现；完整手眼、双相机和传送带抓放继续按后续阶段完成。
