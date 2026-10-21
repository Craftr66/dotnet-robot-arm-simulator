# 独立 C# Modbus TCP 客户端

需要 .NET 10 SDK，无第三方包。运行 AxisLab 0.5.0，导入 `external-control.scene.json`。从“外部连接”复制本次 HTTP 地址；程序会查询实际 Modbus 端口。

```powershell
dotnet run -- http://127.0.0.1:实际端口 list
dotnet run -- http://127.0.0.1:实际端口 state 1
dotnet run -- http://127.0.0.1:实际端口 move 1 20,-10,30 25
dotnet run -- http://127.0.0.1:实际端口 move 2 10,0,0,0,0,0 25
dotnet run -- http://127.0.0.1:实际端口 stop 1
dotnet run -- http://127.0.0.1:实际端口 capture 3 ./captures
```

以上编号对应随包示例；自建场景先用 list 查询编号。三轴和六轴必须分别填 3/6 个角度。移动时程序申请控制权、自动更新心跳、等待结果并释放；断线或 5 秒没有新心跳，服务端停止所持有的机器人。停止可由其他客户端执行。

0.5.0 新增 `movepose`（操作码7）、`movel`（8）和 `teach`（9）。位姿目标为六个逗号分隔值 X,Y,Z,Rx,Ry,Rz，后接速度和 base/world/work；可再加 position 仅约束位置，三轴自动启用。`teach` 的目标参数为已保存且有效的点编号。完整命令示例及失效规则见随包《运动与示教使用说明.md》及《运动与示教接口.md》。MoveJ/MoveL 都会检查限位与基础碰撞。

拍照经 Modbus 操作码 5 触发，使用返回的 sessionToken/frameToken 查询 HTTP 映射，再下载同一帧 PNG、帧 JSON并核对 SHA-256。需保持软件窗口打开以供真实渲染。图片不会通过 Modbus 发送。

`ModbusTcpClient.cs` 为可复用的最小 MBAP/FC03/FC16 客户端，单个连接串行读写，检查事务号、协议号、长度、Unit ID 和功能码；`Program.cs` 展示业务协议。若需要在重连后恢复，应重新发现和选择设备，重新申请控制权；不能把旧连接的命令序号当成全局永久命令 ID。

跨电脑时，用运行软件电脑的局域网 IP，并按发布包《外部连接快速开始.md》启用监听。完整寄存器/错误/去重说明见《ModbusTCP操作协议v1.md》，JSON 地址表可从 GUI 或 `GET /api/v1/communication/map` 获取。
