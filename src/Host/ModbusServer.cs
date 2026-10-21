using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AxisLab.Core;

namespace AxisLab.Host;

public sealed class ModbusServer(CommunicationService bridge, ILogger<ModbusServer> logger) : BackgroundService
{
    private readonly Dictionary<TcpClient, Task> sessions = [];
    public static IPAddress ListenAddress => IPAddress.TryParse(Environment.GetEnvironmentVariable("AXISLAB_LISTEN_ADDRESS") ?? "127.0.0.1", out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork ? ip : throw new InvalidOperationException("AXISLAB_LISTEN_ADDRESS 需要 IPv4 地址。");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var address = ListenAddress;
        var portText = Environment.GetEnvironmentVariable("AXISLAB_MODBUS_PORT") ?? "1502";
        if (!int.TryParse(portText, out var port) || port is < 0 or > 65535) throw new InvalidOperationException("AXISLAB_MODBUS_PORT 需要 0—65535。");
        var listener = new TcpListener(address, port);
        try { listener.Start(32); }
        catch (SocketException ex)
        {
            bridge.SetListener(address.ToString(), port, false, $"Modbus 无法监听 {address}:{port}：{ex.SocketErrorCode}。可修改 AXISLAB_MODBUS_PORT 后重启。");
            logger.LogWarning("Modbus listener unavailable on {Address}:{Port}: {Error}", address, port, ex.SocketErrorCode); return;
        }
        bridge.SetListener(address.ToString(), ((IPEndPoint)listener.LocalEndpoint).Port, true);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(stoppingToken); tcp.NoDelay = true;
                CommunicationService.Client client;
                try { client = bridge.Connect(); }
                catch (DomainException) { tcp.Dispose(); continue; }
                var task = RunClient(tcp, client, stoppingToken);
                lock (sessions) sessions[tcp] = task;
                _ = task.ContinueWith(_ => { lock (sessions) sessions.Remove(tcp); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            listener.Stop(); bridge.SetListener(address.ToString(), port, false);
            Task[] pending;
            lock (sessions) { foreach (var tcp in sessions.Keys) tcp.Dispose(); pending = sessions.Values.ToArray(); }
            await Task.WhenAll(pending);
        }
    }
    private async Task RunClient(TcpClient tcp, CommunicationService.Client client, CancellationToken stoppingToken)
    {
        try
        {
            using var stream = tcp.GetStream();
            while (!stoppingToken.IsCancellationRequested)
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var ct = requestTimeout.Token;
                var header = new byte[7]; await stream.ReadExactlyAsync(header, ct);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (header[2] != 0 || header[3] != 0 || length is < 2 or > 254) break;
                var pdu = new byte[length - 1]; await stream.ReadExactlyAsync(pdu, ct);
                var response = header[6] == 1 ? bridge.Process(client, pdu) : [(byte)(pdu[0] | 128), (byte)11];
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)(response.Length + 1));
                await stream.WriteAsync(header, ct); await stream.WriteAsync(response, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { logger.LogError(ex, "Modbus connection failed"); }
        finally { bridge.Disconnect(client); tcp.Dispose(); }
    }
}
