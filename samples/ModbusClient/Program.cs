using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length < 2)
{
    Console.WriteLine("用法：dotnet run -- <HTTP地址> list|state|move|movepose|movel|teach|stop|capture [设备编号] [目标或示教点编号] [速度%或保存目录] [base|world|work] [position]\n例：dotnet run -- http://127.0.0.1:实际端口 move 1 20,0,0 25\n位姿目标为 X,Y,Z,Rx,Ry,Rz（mm/deg）；三轴自动仅约束位置。保持软件打开以便相机成像。"); return 1;
}
try
{
    var baseUri = new Uri(args[0].TrimEnd('/') + '/');
    using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180)); var ct = timeout.Token;
    var status = await http.GetFromJsonAsync<JsonElement>("api/v1/communication", ct);
    if (!status.GetProperty("listening").GetBoolean()) throw new InvalidOperationException(status.GetProperty("error").GetString() ?? "Modbus 尚未启动。");
    using var modbus = new ModbusTcpClient(); await modbus.Connect(baseUri.Host, status.GetProperty("modbusPort").GetInt32(), ct);
    Console.WriteLine($"Connected Modbus TCP {baseUri.Host}:{status.GetProperty("modbusPort")} Unit ID 1");
    var head = await modbus.Read(0, 32, ct);
    if (head[0] != 0x4158 || head[1] != 1) throw new InvalidOperationException("不是受支持的 AxisLab Modbus v1 服务。");
    if (args[1] == "list")
    {
        var names = await http.GetFromJsonAsync<JsonElement[]>("api/v1/devices", ct) ?? [];
        for (var page = 0; page * 16 < head[10]; page++)
        {
            await modbus.Write(201, [(ushort)page], ct); var rows = await modbus.Read(128, 64, ct);
            for (var i = 0; i < 16 && rows[i * 4] != 0; i++)
            {
                var number = rows[i * 4]; var d = names.FirstOrDefault(d => d.GetProperty("deviceNumber").GetInt32() == number);
                Console.WriteLine($"#{number} type={rows[i * 4 + 1]} axes={rows[i * 4 + 2]} capabilities=0x{rows[i * 4 + 3]:X} {d.GetProperty("name").GetString()}");
            }
        }
        return 0;
    }
    if (args.Length < 3) throw new ArgumentException("需要稳定设备编号，可先执行 list。");
    var deviceNumber = ushort.Parse(args[2], CultureInfo.InvariantCulture);
    await modbus.Write(200, [deviceNumber], ct); head = await modbus.Read(0, 32, ct);
    uint sequence = 0;
    async Task<ushort[]> Command(ushort opcode, float[]? joints = null, float speed = 25, ushort flags = 0, ushort pointNumber = 0)
    {
        var latest = await modbus.Read(0, 32, ct); var revision = await modbus.Read(98, 2, ct);
        var bank = new ushort[28]; bank[0] = opcode; bank[1] = flags; bank[2] = opcode is 7 or 8 ? head[13] : (ushort)(joints?.Length ?? 0); bank[24] = pointNumber;
        if (joints is not null) for (var i = 0; i < joints.Length; i++) ModbusTcpClient.PutFloat(bank, 4 + i * 2, joints[i]);
        ModbusTcpClient.PutFloat(bank, 16, speed); Array.Copy(latest, 4, bank, 18, 4); revision.CopyTo(bank, 22);
        await modbus.Write(202, bank, ct); var submit = ++sequence; await modbus.Write(232, ModbusTcpClient.UInt32Words(submit), ct);
        while (true)
        {
            var result = await modbus.Read(0, 32, ct);
            if (ModbusTcpClient.U32(result, 18) != submit) throw new InvalidDataException("命令序号不匹配。");
            if (result[15] >= 3)
            {
                if (result[15] != 3)
                {
                    var info = await http.GetFromJsonAsync<JsonElement>($"api/v1/communication/clients/{ModbusTcpClient.U32(result, 26)}/commands/{submit}", ct);
                    throw new InvalidOperationException($"状态={result[15]} 错误={result[16]} {info}");
                }
                return result;
            }
            await Task.Delay(100, ct);
        }
    }
    switch (args[1])
    {
        case "state":
            var state = await modbus.Read(0, 120, ct);
            Console.WriteLine($"Device #{state[11]}, type={state[12]}, axes={state[13]}, controller={state[14]}, session={ModbusTcpClient.U64(state,4):X16}");
            Console.WriteLine("Joints: " + string.Join(", ", Enumerable.Range(0, state[13]).Select(i => ModbusTcpClient.Float(state, 32 + i * 2).ToString("F3", CultureInfo.InvariantCulture))));
            Console.WriteLine("TCP(base): " + string.Join(", ", Enumerable.Range(0, 6).Select(i => ModbusTcpClient.Float(state, 44 + i * 2).ToString("F3", CultureInfo.InvariantCulture))));
            break;
        case "move": case "movepose": case "movel": case "teach":
            if (args.Length < 4) throw new ArgumentException("需要关节/位姿目标或示教点编号。");
            var isTeach = args[1] == "teach"; var cartesian = args[1] is "movepose" or "movel";
            var target = isTeach ? null : args[3].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            if (target is not null && target.Length != (cartesian ? 6 : head[13])) throw new ArgumentException($"需要 {(cartesian ? 6 : head[13])} 个目标数值。");
            var speed = args.Length > 4 ? float.Parse(args[4], CultureInfo.InvariantCulture) : 25;
            var frame = args.Length > 5 ? args[5] : "base";
            ushort frameFlags = frame switch { "base" => 0, "world" => 2, "work" => 4, _ => throw new ArgumentException("坐标系须为 base/world/work。") };
            ushort flags = cartesian ? (ushort)(frameFlags | (head[13] == 3 || args.Length > 6 && args[6] == "position" ? 1 : 0)) : (ushort)0;
            await Command(1);
            using (var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var heartbeat = Task.Run(async () =>
                {
                    uint counter = 0;
                    while (!heartbeatStop.IsCancellationRequested) { await modbus.Write(230, ModbusTcpClient.UInt32Words(++counter), ct); await Task.Delay(1000, heartbeatStop.Token); }
                }, heartbeatStop.Token);
                try { await Command(isTeach ? (ushort)9 : args[1] == "movel" ? (ushort)8 : cartesian ? (ushort)7 : (ushort)3, target, speed, flags, isTeach ? ushort.Parse(args[3], CultureInfo.InvariantCulture) : (ushort)0); Console.WriteLine((args[1] == "move" ? "MoveJ" : args[1])+" completed"); }
                finally
                {
                    await heartbeatStop.CancelAsync();
                    try { await heartbeat; } catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
                    var ownership = await modbus.Read(14, 1, ct);
                    if (ownership[0] == 1) await Command(2);
                }
            }
            break;
        case "stop": await Command(4); Console.WriteLine("Robot stopped; external control revoked"); break;
        case "capture":
            var capture = await Command(5);
            var session = ModbusTcpClient.U64(capture, 4).ToString("X16"); var frameToken = ModbusTcpClient.U32(capture, 24);
            var meta = await http.GetFromJsonAsync<JsonElement>($"api/v1/communication/frames/{session}/{frameToken}", ct);
            var png = await http.GetByteArrayAsync(meta.GetProperty("imageUrl").GetString(), ct);
            if (Convert.ToHexString(SHA256.HashData(png)) != meta.GetProperty("sha256").GetString()) throw new InvalidDataException("PNG SHA256 不匹配。");
            var directory = Path.GetFullPath(args.Length > 3 ? args[3] : "captures"); Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, meta.GetProperty("frameId").GetString() + ".png");
            await File.WriteAllBytesAsync(file, png, ct); await File.WriteAllTextAsync(Path.ChangeExtension(file, ".frame.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }), ct);
            Console.WriteLine($"Capture completed, token={frameToken}, SHA256={meta.GetProperty("sha256")}\nSaved {file}");
            break;
        default: throw new ArgumentException("操作应为 list/state/move/movepose/movel/teach/stop/capture。");
    }
    return 0;
}
catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException or FormatException or System.Net.Sockets.SocketException or OperationCanceledException)
{ Console.Error.WriteLine(ex.Message); return 1; }
