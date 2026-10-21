# C# 积木程序控制示例

适用 AxisLab 0.6.0。需要 .NET 10 SDK，无额外包。先在软件中保存积木程序，并从“外部连接”取得本次 HTTP 地址。

```powershell
dotnet run -- http://127.0.0.1:实际端口 validate
dotnet run -- http://127.0.0.1:实际端口 run
dotnet run -- http://127.0.0.1:实际端口 status
dotnet run -- http://127.0.0.1:实际端口 step
dotnet run -- http://127.0.0.1:实际端口 pause
dotnet run -- http://127.0.0.1:实际端口 continue
dotnet run -- http://127.0.0.1:实际端口 stop
```

run/continue/step 默认等待同一 sessionId/RunId 的完成或暂停；末尾加 nowait 只提交。退出码0为完成或操作被接受，2为校验未通过/已暂停，1为失败、取消或通信错误。只提交、暂停、停止的返回值需按 status 判断具体状态。

程序由核心持有，客户端退出后不自动停止；需要显式调用 stop。拍照仍需软件渲染界面打开。lastCapture.imageUrl 用于下载最近成功帧，缓存规则见相机接口。直接关节/位姿控制继续使用旁边的 ModbusClient，其所有权与心跳规则不变。

完整说明见随包《积木编程使用说明.md》《积木程序接口.md》。
