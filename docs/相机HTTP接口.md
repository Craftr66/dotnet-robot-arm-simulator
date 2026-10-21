# 相机 HTTP v1（AxisLab 0.6.0 兼容）

服务仅供本机仿真练习，桌面原生菜单“工具 → 连接信息”提供 baseUrl。保持软件渲染窗口打开；开发模式使用 `http://127.0.0.1:5080`。路径均相对 baseUrl，UUID 使用标准字符串。

## 外部客户端

| 方法与路径 | 返回 |
| --- | --- |
| GET /api/health | status、version、stage |
| GET /api/v1/devices | 设备数组；按 typeId=`camera.pinhole` 过滤 |
| GET /api/v1/runtime | 当前 sessionId、projectRevision、机器人和设备世界位姿 |
| POST /api/v1/cameras/{cameraId}/captures | 202，CaptureState；Location 指向捕获状态 |
| GET /api/v1/captures/{captureId} | 200，CaptureState |
| GET /api/v1/cameras/{cameraId}/frames/{frameId} | 200，FrameMetadata |
| GET /api/v1/cameras/{cameraId}/frames/{frameId}/image | 200，image/png 原始字节；ETag 为带引号的 SHA-256 |

触发无需请求体。依次轮询 `Queued → Rendering → Completed/Failed`。只有 Completed 带 frameId；Failed 带 errorCode/message。客户端遇到轮询网络超时时继续查询已获得的 CaptureId，避免误发重复拍照；HTTP 触发本身不提供跨重试去重，需要命令序号去重时可使用 Modbus 操作码5，详见《ModbusTCP操作协议v1.md》。

```json
{
  "captureId": "捕获UUID",
  "cameraId": "相机UUID",
  "status": "Completed",
  "frameId": "图像UUID",
  "errorCode": null,
  "message": null
}
```

FrameMetadata 包含：frameId、captureId、cameraId、cameraName、sessionId、sceneRevision、cameraConfigurationRevision、exposureAt、completedAt、width、height、encoding=`png`、sha256、cameraWorld、intrinsics、robots 和 imageUrl。时间为 ISO 8601 UTC，sha256 为大写十六进制。sceneRevision 是触发时工程版本，机器人 PoseId 在各机器人自己的运行会话内递增。

cameraWorld 包括 pose 和 16 元素列主序 matrix；以 matrix 为权威值。intrinsics 的 width/height/fx/fy/cx/cy 用于针孔投影，其 sizeX/Y/Z 为通用设备参数，在相机投影中不使用。robots 为曝光时全体机器人状态（deviceId、poseId、joints、status、commandId、progress、jointMatrices、flangeBase/tcpBase/flangeWorld/tcpWorld），可自行选择标定对象。

imageUrl 为相对此服务的 `/api/v1/.../image`，返回稳定帧而非最新图。预览和下载读取同一 PNG 缓存；校验 SHA-256 后保存。坐标和容量限制见《相机与尺寸使用说明.md》。独立运行的 C# 示例位于 `samples/CSharpClient`，发布包位于 `Samples/CSharpClient`。

## 错误

HTTP 非成功响应结构为 `{ "code": "...", "message": "..." }`。异步渲染错误在 CaptureState 中返回 Failed，状态查询本身仍为 200。

| HTTP / 状态 | 典型 code / errorCode | 含义 |
| --- | --- | --- |
| 404 | DeviceNotFound / CaptureNotFound / FrameNotFound | 对象或历史记录不存在，或相机与帧不匹配 |
| 409 | CameraBusy / CaptureQueueFull | 相机正忙或队列已满 |
| 410 | FrameGone | 帧淘汰或工程会话切换 |
| 422 | NotCamera / CaptureTooLarge | 设备类型错误或超过像素上限 |
| 503 | RendererUnavailable | 没有有效渲染窗口 |
| Failed | SceneChanged | 触发后结构、位姿、可见性、尺寸或内参改变 |
| Failed | RendererDisconnected / CaptureTimeout / RenderFailed / InvalidPng | 断连、超时、GPU/编码失败或无效内容 |

修改工程名称或执行关节运动不会使曝光快照失效；场景配置变更与工程切换会。工程重新打开即产生新 sessionId，即使工程 ID 和相机 ID 相同，旧帧也不可继续读取。

## 内部渲染协议

这部分由软件窗口使用，外部 C# 取图客户端不需要注册 renderer。单个有效租约消费队列，令牌随机生成，不写入工程。

| 方法与路径 | 用途 |
| --- | --- |
| POST /api/v1/renderers | 获取 rendererId/token/leaseSeconds，已有租约返回 409 |
| POST /api/v1/renderers/{id}/heartbeat | 刷新租约 |
| GET /api/v1/renderers/{id}/captures | 冻结 CaptureWork，空队列 204 |
| PUT /api/v1/renderers/{id}/captures/{captureId}/image | Content-Type=image/png，上传原始 PNG |
| POST /api/v1/renderers/{id}/captures/{captureId}/fail | JSON `{ "message": "..." }` 报告失败 |

除注册外，内部接口需 `X-AxisLab-Renderer-Token`，失效返回 403。PNG 检查签名、尺寸、位深、关键块、CRC、有限解压扫描行和过滤类型；支持 8-bit RGB/RGBA、非交错格式。像素需与触发相机分辨率完全相符，上传结果只能绑定当次捕获。

默认本机监听；局域网监听设置见《外部连接快速开始.md》。当前 Modbus 已提供控制权、命令去重和会话令牌；图像数据仍通过本文 HTTP 接口获取。
