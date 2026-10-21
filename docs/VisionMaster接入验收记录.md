# VisionMaster / WPF 接入验收 · Demo 0.1

日期：2026-10-21。AxisLab 本体仍为0.6.0。交付范围为新阶段7A的外部接入底座，不代表阶段7全部标定能力完成。

## 构建与环境

- Demo：.NET Framework 4.8、Windows x64；本机运行时4.8.09037。
- VM：本机 VisionMaster4.3.0，官方 SDK 指南V4.3.1，使用安装目录的真实程序集和算法运行环境。
- WPF 与检查项目均构建成功，0警告、0错误。未复制厂商 SDK、修改 VM 安装或覆盖既有 .sol。
- 用独立 Host、动态 HTTP/Modbus 端口、无头 Edge 渲染器及隔离数据目录执行；用户正在编辑的工程未被测试修改。

## 本轮结果

| 检查组 | 结果 | 内容 |
| --- | --- | --- |
| 工作流/运动传输 | 24项通过 | 帧与请求身份、NG、标定失效、配置/会话/方案变化、旧帧、非有限数、样本配对和真值隔离、真实 Modbus MoveJ、取消后关节停止 |
| 原生 WPF / 真实 VM SDK | 8项通过 | 实际 WPF 窗口、设备发现、PNG预览、样本、官方 .sol 加载、RGB24/MONO8逐像素哈希一致、缺少约定接口或标定时不能产生动作 |

合计32项。实际 VM 测试采用安装包官方 CircleFind.sol，用于证明图像源送图和错误拒绝，不将该示例当作用户的机器人标定流程。

最终发布 EXE 证据：`artifacts/vision-demo-checks/1792568243164`，含 results.json、model-checks/model-results.json、配对PNG/observation.json、wpf-demo.png及隔离Host日志。测试还要求WPF正常退出，非零退出不会判为成功。

复现：

```powershell
dotnet build src/VisionDemo/AxisLab.VisionDemo.csproj -c Release -m:1 -nr:false
dotnet build tests/VisionDemoChecks/AxisLab.VisionDemoChecks.csproj -c Release -m:1 -nr:false
node scripts/vision-demo-checks.mjs
```

默认使用本机 VM4.3.0 官方示例路径。可用 AXISLAB_VM_TEST_SOLUTION 指定兼容的诊断方案；流程/图像源按测试脚本名称配置。AXISLAB_VISION_DEMO_EXE 可指定最终发布EXE。

## 待用户流程及后续阶段验证

用户尚未提供实际标定/识别 .sol，因此没有宣称完成用户算法的端到端定位精度验收。WPF 已实现约定的输入、输出和机器人调用；需接入用户流程后验证坐标方向、角度、标定有效性和实际误差。

标定板/标志设备、九点采样表、完整手眼、双相机、多工位及传送带抓放继续按新设计分阶段开发。首个运动闭环限定固定相机平面、基坐标、单目标、已知Z及MoveJ。

当前为依赖本机 VM 和 .NET Framework 4.8 的便携 Demo，无安装向导/签名。AxisLab原有功能的历史验收见阶段6记录；本轮没有重复执行所有历史测试。
