using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace HikVisionNetSDK.Codec
{
    /// <summary>
    /// 转码通道
    /// </summary>
    public class TranscodeChannel
    {
        /// <summary>
        /// 通道唯一键，用于识别相同的视频源
        /// </summary>
        public String ChannelKey { get; set; }

        /// <summary>
        /// 活动WebSocket连接数（通过广播连接列表计算）
        /// </summary>
        public Int32 ActiveConnections => _webSocketConnections?.Count(r => r.WebSocket?.State != WebSocketState.Closed && r.WebSocket?.State != WebSocketState.Aborted) ?? 0;

        /// <summary>
        /// TCP端口（FFmpeg输出端口）
        /// </summary>
        public Int32 TcpPort { get; set; }

        /// <summary>
        /// TCP监听器（用于FFmpeg连接）
        /// </summary>
        public TcpListener TcpListener { get; set; }

        /// <summary>
        /// FFmpeg进程
        /// </summary>
        public Process FFmpegProcess { get; set; }

        /// <summary>
        /// Node.js进程（如果使用Node.js模式）
        /// </summary>
        public Process NodeProcess { get; set; }

        /// <summary>
        /// RTSP源地址
        /// </summary>
        public String RTSPUri { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedTime { get; set; }

        /// <summary>
        /// 最后访问时间
        /// </summary>
        public DateTime LastAccessTime { get; set; }

        /// <summary>
        /// FFmpeg进程退出码（null表示进程还在运行）
        /// </summary>
        public Int32? FFmpegExitCode { get; set; }

        /// <summary>
        /// FFmpeg进程退出时间
        /// </summary>
        public DateTime? FFmpegExitTime { get; set; }

        /// <summary>
        /// FFmpeg最后的错误信息
        /// </summary>
        public String FFmpegLastError { get; set; }

        /// <summary>
        /// 进程状态
        /// </summary>
        public ProcessStatus Status { get; set; } = ProcessStatus.Starting;

        /// <summary>
        /// FFmpeg进程是否活动（正在运行）
        /// </summary>
        public Boolean IsActive => FFmpegProcess != null && !FFmpegProcess.HasExited;

        /// <summary>
        /// FFmpeg进程ID
        /// </summary>
        public Int32? FFmpegProcessId => FFmpegProcess?.Id;

        // ⭐ 广播模式相关字段
        private readonly List<WebSocketConnection> _webSocketConnections = new List<WebSocketConnection>();
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private Task _broadcastTask;
        private CancellationTokenSource _broadcastCts;
        private TcpClient _ffmpegTcpClient;

        /// <summary>
        /// WebSocket 连接信息
        /// </summary>
        private class WebSocketConnection
        {
            public WebSocket WebSocket { get; set; }
            public String ConnectionId { get; set; }
            public DateTime ConnectedTime { get; set; }
        }

        /// <summary>
        /// 启动广播任务
        /// </summary>
        public async Task StartBroadcastAsync()
        {
            if (_broadcastTask != null && !_broadcastTask.IsCompleted)
            {
                return; // 已经在运行
            }

            _broadcastCts = new CancellationTokenSource();

            _broadcastTask = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[Broadcast] 开始广播任务 - ChannelKey: {ChannelKey}");

                    // 接受 FFmpeg 的 TCP 连接（只接受一次）
                    _ffmpegTcpClient = await TcpListener.AcceptTcpClientAsync();
                    Console.WriteLine($"[Broadcast] 已接受 FFmpeg 连接 - ChannelKey: {ChannelKey}");

                    var stream = _ffmpegTcpClient.GetStream();
                    var buffer = new byte[8192];

                    while (!_broadcastCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            // 从 FFmpeg 读取数据
                            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _broadcastCts.Token);

                            if (bytesRead == 0)
                            {
                                Console.WriteLine($"[Broadcast] FFmpeg 连接已关闭 - ChannelKey: {ChannelKey}");
                                break;
                            }

                            // 广播给所有 WebSocket
                            await BroadcastToAllAsync(buffer, bytesRead);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Broadcast] 读取或广播数据时出错: {ex.Message}");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Broadcast] 广播任务异常: {ex.Message}");
                    Status = ProcessStatus.ExitedWithError;
                }
                finally
                {
                    Console.WriteLine($"[Broadcast] 广播任务结束 - ChannelKey: {ChannelKey}");
                    _ffmpegTcpClient?.Close();
                    _ffmpegTcpClient?.Dispose();
                }
            }, _broadcastCts.Token);

            // 等待 FFmpeg 连接建立
            await Task.Delay(100);
        }

        /// <summary>
        /// 广播数据给所有 WebSocket
        /// </summary>
        private async Task BroadcastToAllAsync(byte[] buffer, int length)
        {
            await _lock.WaitAsync();
            List<WebSocketConnection> deadConnections = new List<WebSocketConnection>();

            try
            {
                if (_webSocketConnections.Count == 0)
                {
                    return; // 没有连接，不需要广播
                }

                // 准备数据
                var segment = new ArraySegment<byte>(buffer, 0, length);

                // 并行发送给所有活动的 WebSocket
                var tasks = _webSocketConnections
                    .Where(conn => conn.WebSocket.State == WebSocketState.Open)
                    .Select(async conn =>
                    {
                        try
                        {
                            await conn.WebSocket.SendAsync(
                                segment,
                                WebSocketMessageType.Binary,
                                true,
                                _broadcastCts.Token);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Broadcast] 发送失败 ConnectionId: {conn.ConnectionId}, Error: {ex.Message}");
                            deadConnections.Add(conn);
                        }
                    })
                    .ToArray();

                if (tasks.Length > 0)
                {
                    await Task.WhenAll(tasks);
                }

                // 清理断开的连接
                foreach (var deadConn in deadConnections)
                {
                    _webSocketConnections.Remove(deadConn);
                    Console.WriteLine($"[Broadcast] 移除断开的连接 - ConnectionId: {deadConn.ConnectionId}");
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 添加 WebSocket 到广播列表
        /// </summary>
        public async Task<String> AddWebSocketAsync(WebSocket webSocket)
        {
            await _lock.WaitAsync();
            try
            {
                var connectionId = Guid.NewGuid().ToString("N")[..8];
                var connection = new WebSocketConnection
                {
                    WebSocket = webSocket,
                    ConnectionId = connectionId,
                    ConnectedTime = DateTime.Now
                };

                _webSocketConnections.Add(connection);
                Console.WriteLine($"[Broadcast] 添加 WebSocket - ConnectionId: {connectionId}, 总数: {_webSocketConnections.Count}");

                return connectionId;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 从广播列表移除 WebSocket
        /// </summary>
        public async Task RemoveWebSocketAsync(String connectionId)
        {
            await _lock.WaitAsync();
            try
            {
                var connection = _webSocketConnections.FirstOrDefault(c => c.ConnectionId == connectionId);
                if (connection != null)
                {
                    _webSocketConnections.Remove(connection);
                    Console.WriteLine($"[Broadcast] 移除 WebSocket - ConnectionId: {connectionId}, 剩余: {_webSocketConnections.Count}");
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 获取当前广播连接数
        /// </summary>
        public Int32 GetBroadcastConnectionCount()
        {
            return _webSocketConnections.Count;
        }

        /// <summary>
        /// 停止广播任务
        /// </summary>
        public void StopBroadcast()
        {
            Console.WriteLine($"[Broadcast] 停止广播任务 - ChannelKey: {ChannelKey}");

            _broadcastCts?.Cancel();
            _ffmpegTcpClient?.Close();

            // 等待任务完成
            if (_broadcastTask != null)
            {
                try
                {
                    _broadcastTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Ignore
                }
            }

            _broadcastCts?.Dispose();
        }
    }

    /// <summary>
    /// 进程状态枚举
    /// </summary>
    public enum ProcessStatus
    {
        /// <summary>
        /// 启动中
        /// </summary>
        Starting = 0,

        /// <summary>
        /// 运行中
        /// </summary>
        Running = 1,

        /// <summary>
        /// 正常退出
        /// </summary>
        ExitedNormally = 2,

        /// <summary>
        /// 异常退出
        /// </summary>
        ExitedWithError = 3,

        /// <summary>
        /// 被强制终止
        /// </summary>
        Killed = 4
    }

    /// <summary>
    /// WebSocket通道（Node.js模式使用）
    /// </summary>
    public class WebSocketChannel
    {
        public Int32 StreamPort { get; set; }
        public Int32 WebSocketPort { get; set; }
        public String Secret { get; set; }
        public Process Process { get; set; }
    }

    /// <summary>
    /// 流通道
    /// </summary>
    public class StreamChannel
    {
        public String WebSocketUrl { get; set; }
        public String RTSPUri { get; set; }
        public Process Process { get; set; }
    }

    /// <summary>
    /// 转码请求
    /// </summary>
    public class TranscodeRequest
    {
        /// <summary>
        /// 通道起始编号
        /// </summary>
        public const Int32 CHANNEL_START_NO = 33;

        /// <summary>
        /// 摄像头或网络录像机的IP地址
        /// </summary>
        public String IP { get; set; }

        /// <summary>
        /// 登录端口号
        /// </summary>
        public Int32 LoginPort { get; set; }

        /// <summary>
        /// 用户名
        /// </summary>
        public String UserName { get; set; }

        /// <summary>
        /// 密码
        /// </summary>
        public String Password { get; set; }

        /// <summary>
        /// 通道号
        /// </summary>
        public Int32 ChannelNo { get; set; }

        /// <summary>
        /// 码流类型：1-主码流，2-子码流，3-第三码流，默认是主码流
        /// </summary>
        public Int32 StreamType { get; set; } = 1;

        /// <summary>
        /// 画面宽度
        /// </summary>
        public Int32 Width { get; set; }

        /// <summary>
        /// 画面高度
        /// </summary>
        public Int32 Height { get; set; }

        public String WebSocketUrl { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }
}
