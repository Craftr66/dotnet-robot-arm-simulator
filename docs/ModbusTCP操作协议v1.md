# AxisLab Modbus TCP 操作协议 v1（0.6.0，扩展级别 1）

冻结日期：2026-10-21。此协议为可添加设备的通用平台协议，与旧《通信协议草案》的固定工作单元地址不同。后者从未实现，不可混用。

## 1. 传输和数据

服务端默认 IPv4 `127.0.0.1:1502`，可配置监听地址/端口；Unit ID=1。支持 FC03、FC06、FC16。MBAP 协议号必须 0，Length 为 Unit ID 加 PDU 的字节数，范围 2—254；支持 TCP 拆包、粘包，事务号原样回传。TCP 无串口 CRC。FC03 最多 125 个寄存器，FC16 最多 123 个，并受本业务可写范围限制。

地址均为十进制零基 PDU 偏移。本文地址 0 对应部分软件的 40001。UInt16 高字节先；UInt32、UInt64 先高字；Float32 IEEE 754、ABCD 字序。例：Float32 20.0 = `41 A0 00 00`，两个寄存器为 `0x41A0,0x0000`；UInt32 序号 1 为 `0x0000,0x0001`。长度 mm、角度 deg，姿态顺序 X/Y/Z/Rx/Ry/Rz，旋转 `Rz·Ry·Rx`。

最多 16 个活动 TCP 连接，15 秒无请求会关闭空闲连接。一个连接串行处理请求，可合并多个 ADU 发送。客户端建议请求超时 3 s、状态轮询 100—200 ms、控制心跳 1 s。心跳是业务寄存器，TCP keepalive 或单纯读状态不能代替。

协议依据 Modbus Organization 的[应用协议 V1.1b3](https://www.modbus.org/file/secure/modbusprotocolspecification.pdf)和[TCP 实施指南 V1.0b](https://www.modbus.org/file/secure/messagingimplementationguide.pdf)。下面的设备窗口与操作码是 AxisLab 自定义业务层。

## 2. 会话、寻址与发现

设备编号随工程保存，删除后不复用；UUID 是永久设备身份。选择窗口是每 TCP 连接私有的：向 200 写设备编号，服务端绑定当前工程完整会话和设备 UUID，不按设备列表索引绑定。编号 0 表示清空选择。

项目令牌为运行工程 SessionId 的前 16 个十六进制字符转换成 UInt64，新建/导入/重开/服务重启都会变化。HTTP `GET /api/v1/communication` 返回 16 字符 sessionToken。令牌用于防止错会话，不是密码。即使重开同一文件、设备 UUID 和编号相同，也必须重新选择设备。

用 10 读设备数，201 写零基分页号，128—191 读 16 行设备目录，每行 4 个 UInt16：设备编号、类型、轴数、能力位。空行全部零。类型 1=六轴、2=三轴、10=相机、20=长方体。能力位：bit0 配置、bit1 关节运动、bit2 FK、bit3 法兰、bit4 工具、bit5 拍照、bit6 读图；0.5.0 增加 bit7 位姿 MoveJ、bit8 MoveL、bit9 示教点。机器人为 0x039F（原有能力位保持）、相机为 0x0061、几何体为 0x0001。名称、完整配置和 UUID 也可从 HTTP `/api/v1/devices` 读取。

## 3. 寄存器表

R 为可读，RW 为读写暂存。保留字段读取零，写入仅允许命令暂存段中的保留字段，提交时必须为零。各次 FC03 从一次核心状态快照填充寄存器；分多次读取可能跨越运动时刻，建议状态一次读取 0—119（120 个）。8—9 为每次读生成的 UInt32 快照序号，允许回绕，不作为命令序号使用。

### 公共头：0—31

| 地址 | 长度 | 类型 / 权限 | 含义 |
| --- | --- | --- | --- |
| 0 | 1 | UInt16 R | 魔数 0x4158 |
| 1 | 1 | UInt16 R | 协议版本 1 |
| 2 | 1 | UInt16 R | 软件版本标志 0x0006 |
| 3 | 1 | UInt16 R | Modbus 正在监听=1 |
| 4—7 | 4 | UInt64 R | 当前工程会话令牌 |
| 8—9 | 2 | UInt32 R | 读取快照序号 |
| 10 | 1 | UInt16 R | 设备数量 |
| 11 | 1 | UInt16 R | 当前连接选择的设备编号 |
| 12 | 1 | UInt16 R | 选择设备类型；失效为 0 |
| 13 | 1 | UInt16 R | 轴数：3/6，非机器人为 0 |
| 14 | 1 | UInt16 R | 控制权：0 空闲、1 当前连接、2 其他连接 |
| 15 | 1 | UInt16 R | 此连接最近提交命令状态 |
| 16 | 1 | UInt16 R | 最近提交业务错误码 |
| 17 | 1 | UInt16 R | 所选设备能力位 |
| 18—19 | 2 | UInt32 R | 最近提交/查询重放的命令序号 |
| 20—21 | 2 | UInt32 R | 最近接受的命令序号 |
| 22—23 | 2 | UInt32 R | 最近进入终态的最大序号 |
| 24—25 | 2 | UInt32 R | 最近命令成功拍照得到的 frameToken；无结果为 0 |
| 26—27 | 2 | UInt32 R | 当前 TCP 连接 clientId |
| 28 | 1 | UInt16 R | 控制心跳超时秒数，当前为 5 |
| 29 | 1 | UInt16 R | 选择的身份在本会话有效=1，否则 0 |
| 30 | 1 | UInt16 R | 实际 Modbus 端口 |
| 31 | 1 | UInt16 R | 实际 HTTP 端口 |

### 选择设备状态：32—127

| 地址 | 长度 | 类型 / 权限 | 含义 |
| --- | --- | --- | --- |
| 32—43 | 12 | Float32×6 R | J1—J6，三轴 J4—J6 为零 |
| 44—55 | 12 | Float32×6 R | TCP 在机器人基坐标中的位姿 |
| 56—67 | 12 | Float32×6 R | 法兰在机器人基坐标中的位姿 |
| 68—79 | 12 | Float32×6 R | TCP 世界位姿 |
| 80—91 | 12 | Float32×6 R | 设备原点世界位姿，适用于相机等 |
| 92—95 | 4 | UInt64 R | 机器人 PoseId |
| 96 | 1 | UInt16 R | 运动：0 Idle、1 Running、2 Completed、3 Cancelled、4 Failed |
| 97 | 1 | UInt16 R | 动作进度千分比，0—1000 |
| 98—99 | 2 | UInt32 R | 设备配置版本 |
| 100—107 | 8 | UUID R | UUID 去掉连字符后的 32 个 hex 字符，每 4 字符一寄存器，按原顺序 |
| 108—115 | 8 | Float32×4 R | 相机 fx、fy、cx、cy；非相机为零 |
| 116、117 | 各 1 | UInt16 R | 相机宽、高，px |
| 118 | 1 | UInt16 R | 控制权剩余心跳毫秒数 |
| 119、120 | 各 1 | UInt16 R | 当前步骤、总步骤 |
| 121 | 1 | UInt16 R | 路径类型：0 无、1 MoveJ、2 MoveL |
| 122 | 1 | UInt16 R | 当前运动错误 |
| 123 | 1 | UInt16 R | 扩展级别=1 |
| 124—127 | 4 | 保留 R | 零 |

UUID 例 `12345678-1234-5678-90ab-cdef12345678` → `1234 5678 1234 5678 90AB CDEF 1234 5678`。不能直接使用 .NET Guid.ToByteArray 的默认混合字节顺序解释。非机器人运动状态和 TCP 字段为零；失效选择的设备状态为零，11 仍保留原设备编号供诊断。

### 目录与暂存：128—233

| 地址 | 长度 | 类型 / 权限 | 含义 |
| --- | --- | --- | --- |
| 128—191 | 64 | UInt16 R | 当前目录页 16 行，每行编号/类型/轴数/能力 |
| 192—199 | 8 | 保留 R | 零 |
| 200 | 1 | UInt16 RW | 选择设备编号，FC06 或 FC16(count=1) |
| 201 | 1 | UInt16 RW | 目录分页号，FC06 或 FC16(count=1) |
| 202 | 1 | UInt16 RW | 暂存操作码 |
| 203 | 1 | UInt16 RW | 7/8 为坐标与仅位置标志（见扩展说明）；其他操作码零 |
| 204 | 1 | UInt16 RW | 3/7/8 的机器人轴数，必须与设备一致 |
| 205 | 1 | 保留 RW | 必须零 |
| 206—217 | 12 | Float32×6 RW | 操作码3：J1—J6，三轴补零；7/8：XYZRxRyRz |
| 218—219 | 2 | Float32 RW | 运动速度百分比，1—100 |
| 220—223 | 4 | UInt64 RW | 预期项目会话，复制 4—7 |
| 224—225 | 2 | UInt32 RW | 预期配置版本，复制 98—99 |
| 226 | 1 | UInt16 RW | 操作码9为点编号；其他操作码零 |
| 227—229 | 3 | 保留 RW | 必须零 |
| 230—231 | 2 | UInt32 RW | 心跳计数，仅一次 FC16(count=2)；必须改变才续期 |
| 232—233 | 2 | UInt32 提交 | 命令序号，仅一次 FC16(count=2)，最后写；读取为零 |

202—229 可用 FC06 或 FC16 暂存任意完整范围，但不能跨入心跳/提交区。暂存不会执行动作。不可把整个 200—233 区域一包写入。选择设备时清空命令暂存和最近命令显示；此连接的历史命令仍可通过 HTTP 查询。

## 4. 操作与状态

| 操作码 | 名称 | 前置条件与结果 |
| --- | --- | --- |
| 1 | Acquire | 所选机器人空闲且未被其他连接持有，申请本连接控制权；立即完成 |
| 2 | Release | 当前连接持有，停止尚在运动的该机器人并释放控制权；立即完成 |
| 3 | MoveJ | 当前连接持有，校验实际轴数/关节限位/有限数值/速度；运行至完成或取消 |
| 4 | Stop | 所选为机器人，不要求持有控制权；停止并撤销控制权 |
| 5 | Capture | 所选为相机，有效渲染窗口；不要求机器人控制权，完成返回 frameToken |
| 6 | CancelCapture | 所选为相机；仅取消本连接对该相机的活动捕获，取消操作本身立即完成 |

状态：0 Idle、1 Accepted（内部过渡）、2 Running、3 Completed、4 Failed、5 Cancelled、6 Rejected。控制参数/身份校验失败为 Rejected；异步成像失败为 Failed；机器人停止/断线/心跳超时为 Cancelled。机器人取消的 errorCode 可为 0，仍必须检查状态。不能仅用错误码 0 判断动作成功。

持有机器人控制权时，其他本地/HTTP/Modbus 运动被拒绝，场景结构编辑和保存也锁定。本地停止、任意有效 Stop、释放、TCP 断线、控制心跳到期会撤销控制权并停止。5 秒计时使用单调真实时钟；重复相同计数、读状态、重放 Acquire 都不刷新心跳。

当前连接持有任何机器人或存在活动命令时不能修改选择，返回 Modbus 异常 06。不同连接有独立选择、暂存和结果窗口；不同机器人可以各被不同连接持有。相机忙和总队列限制沿用相机服务。

## 5. 原子提交与去重

1. 选择设备，核对身份有效、轴数、完整 UUID、项目令牌及配置版本。
2. 暂存 202—229。预期会话和配置版本每条命令都必须正确，包括 Acquire/Release/Stop/Capture。
3. 最后 FC16 写 232—233。序号是连接内 UInt32，非零且严格递增，不回绕；到达最大值前应正常释放并新建连接。
4. 以 18—19 和 15/16 配对轮询；不要把其他连接或旧选择的结果当作本次结果。

网络等待超时但 TCP 仍连接时，可用**同一序号、同一设备绑定和完全相同的 28 个暂存寄存器**重试；服务端返回原任务，不重新运动或曝光。同序号不同参数返回 SequenceConflict。客户端改变目标或修正被拒绝的参数，应使用新序号。

每连接最多保留 128 项命令历史。已经清理的旧序号仍小于最高序号，返回 SequenceTooOld，绝不重新执行。去重作用域为当前 TCP 连接；重连后是新 clientId，必须重新选择、核对状态和申请控制权，不能承诺原连接命令能跨重连自动续跑。

工程切换使连接的原选择失效。即使把新项目令牌写入旧选择窗口，也拒绝，必须重新写 200。配置变化要求重新读取确认 98—99。不会把旧命令发给相同编号的另一个设备。

## 6. 拍照结果如何变成 PNG

成功 Capture 后读取 4—7 的项目令牌及 24—25 的 frameToken。项目令牌格式化为 16 字符 hex（高字在前，可大小写），frameToken 作为十进制整数：

```text
GET /api/v1/communication/frames/{sessionToken}/{frameToken}
```

返回原相机服务 FrameMetadata，包括 cameraId、frameId、captureId、sha256、imageUrl、曝光相机世界矩阵、内参和机器人姿态。然后按 imageUrl 获取 `image/png` 二进制。例：`/api/v1/cameras/{cameraId}/frames/{frameId}/image`。对收到的字节计算 SHA-256，与元数据比较后保存。

另可查 `GET /api/v1/communication/clients/{clientId}/commands/{sequence}` 得到 ExternalCommand：sequence、opcode、deviceId、state、error、errorCode、message、commandId、captureId、frameToken。此历史最多保留 64 个连接（最多 16 活动连接），旧的已断开连接优先清理。

帧令牌单进程内递增且非零，映射最多 512 项；令牌不回绕重用。项目会话失效返回 HTTP 410。映射或命令历史已清理返回 404。PNG 缓存仍为每相机 20 帧、全局 256 MiB，缓存被淘汰返回 410。新启动会话不同，不可只保存 frameToken 而丢失 sessionToken。

## 7. 错误码

Modbus 标准异常和业务错误不同。非法功能返回 01；越界/只读/不合法写范围返回 02；PDU 长度、数量或字节数错误返回 03；选择设备时忙返回 06；错误 Unit ID 返回 0B。非法 MBAP 协议号/长度直接关闭该 TCP 连接。提交寄存器写入合法不等于动作被接受，必须读取业务状态。

| 业务码 | errorCode |
| --- | --- |
| 0 | None |
| 1 | NoDevice |
| 2 | SessionChanged |
| 3 | ConfigurationChanged |
| 4 / 5 / 6 | InvalidSequence / SequenceConflict / SequenceTooOld |
| 7 / 8 | UnsupportedCommand / InvalidParameters |
| 9 / 10 | NotRobot / NotCamera |
| 11 / 12 / 13 | ControlBusy / NoControl / RobotBusy |
| 14 / 15 / 16 | JointLimit / InvalidJoints / InvalidSpeed |
| 17 / 18 / 19 | RendererUnavailable / CameraBusy / CaptureQueueFull |
| 20 / 21 / 22 | CaptureTimeout / RendererDisconnected / SceneChanged |
| 23 / 24 / 25 | CaptureCancelled / MotionTooLong / DeviceNotFound |
| 26 / 27 / 28 / 29 | CaptureTooLarge / RenderFailed / InvalidPng / CaptureNotFound |
| 255 | InternalError 或未映射错误 |

完整机器可读表：`GET /api/v1/communication/map`，发布包 `Modbus-v1-map.json`。连接地址、启动参数及可直接运行的 C# 命令见《外部连接快速开始.md》。

## 0.5.0 运动与示教扩展

新增操作码 7=位姿 MoveJ、8=MoveL、9=执行示教点。字段标志、错误码 30—39、HTTP 点位管理和完整示例见[运动与示教接口](运动与示教接口.md)。原操作码 1—6 保留。规划时会检查整条路径，运行中碰撞会报告 Failed。能力按位判断，不能把机器人能力值固定比较为旧版 0x001F。

## 0.6.0 程序执行互斥

积木程序运行或断点暂停期间，机器人控制状态14显示2，外部申请或运动冲突返回40 ProgramActive。本地 Stop 也会停止当前程序。程序启动/单步/继续/停止使用《积木程序接口.md》的 HTTP 接口；旧 Modbus 操作码1—9保留，直接动作的5秒心跳规则不变。
