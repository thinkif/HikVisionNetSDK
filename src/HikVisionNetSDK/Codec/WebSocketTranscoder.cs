using HikVisionNetSDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace HikVisionNetSDK.Codec
{
    /// <summary>
    /// 【全局单例模式，必须使用注入依赖】WebSocket转码器，提供将RTSP流转换成WebSocket流的方法。
    /// 本SDK负责管理FFmpeg进程和TCP流，Web项目负责WebSocket连接处理。
    /// </summary>
    public sealed class WebSocketTranscoder : IDisposable
    {
        private readonly ConcurrentDictionary<String, TranscodeChannel> _transcodeChannels = new ConcurrentDictionary<String, TranscodeChannel>();
        private readonly ConcurrentDictionary<String, String> _deviceIdToChannelKey = new ConcurrentDictionary<String, String>();
        private readonly ConcurrentDictionary<Int32, Boolean> _usedPorts = new ConcurrentDictionary<Int32, Boolean>();

        private readonly String _ffMpegPath;
        private readonly String _nodePath;
        private readonly String _wsHost;
        private readonly Int32 _wsPort;
        private readonly String _wsBasePath;
        private readonly Boolean _useNodeMode;

        private readonly System.Threading.Timer _cleanupTimer;
        private Boolean _disposed = false;

        /// <summary>
        /// 初始化一个转码器。
        /// </summary>
        /// <param name="ffmpegPath">ffmpeg.exe文件路径</param>
        /// <param name="nodePath">node.exe文件路径，不传则使用C#模式（推荐）</param>
        /// <param name="wsHost">【node.exe模式】Web服务器的主机地址，默认127.0.0.1</param>
        /// <param name="wsPort">【node.exe模式】Web服务器的端口号，默认50080</param>
        /// <param name="wsBasePath">【node.exe模式】WebSocket的基础路径，默认/rtspproxy</param>
        public WebSocketTranscoder(String ffmpegPath, String nodePath = null, String wsHost = "127.0.0.1", Int32 wsPort = 80, String wsBasePath = "/wsproxy")
        {
            if (String.IsNullOrWhiteSpace(ffmpegPath))
            {
                throw new ArgumentNullException(nameof(ffmpegPath));
            }

            _ffMpegPath = ffmpegPath;
            _useNodeMode = !String.IsNullOrWhiteSpace(nodePath);

            if (_useNodeMode)
            {
                _nodePath = nodePath;
                if (!File.Exists(nodePath))
                {
                    throw new FileNotFoundException($"文件\"{nodePath}\"不存在");
                }
            }

            _wsHost = wsHost;
            _wsPort = wsPort;
            _wsBasePath = wsBasePath?.TrimEnd('/') ?? "/wsproxy";

            // ⭐ 启动定时清理任务，每60秒检查一次僵尸连接
            _cleanupTimer = new System.Threading.Timer(CleanupZombieChannels, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            Console.WriteLine("[Cleanup Timer] 僵尸连接清理定时器已启动，检查间隔: 60秒");
        }

        /// <summary>
        /// 定期清理僵尸通道（FFmpeg已退出但未清理的通道）
        /// </summary>
        private void CleanupZombieChannels(Object state)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var now = DateTime.Now;
                var channelsToCleanup = new List<String>();

                foreach (var kvp in _transcodeChannels)
                {
                    var channel = kvp.Value;

                    // 30秒内新创建的通道不检查
                    if ((now - channel.CreatedTime).TotalSeconds < 30)
                    {
                        continue;
                    }

                    // ⭐ 检查条件1：FFmpeg进程已退出且无活动WebSocket连接
                    if ((channel.FFmpegProcess == null || channel.FFmpegProcess.HasExited))
                    {
                        Console.WriteLine($"[Cleanup Timer] 发现僵尸通道（FFmpeg已退出且无WebSocket连接） - ChannelKey: {kvp.Key}");
                        channelsToCleanup.Add(kvp.Key);
                        continue;
                    }

                    // ⭐ 检查条件2：长时间无WebSocket连接（超过5分钟）
                    var inactiveTime = (now - channel.LastAccessTime).TotalMinutes;
                    if (channel.ActiveConnections <= 0 && inactiveTime > 5)
                    {
                        Console.WriteLine($"[Cleanup Timer] 发现长时间无WebSocket连接的通道（{inactiveTime:F1}分钟） - ChannelKey: {kvp.Key}");
                        channelsToCleanup.Add(kvp.Key);
                        continue;
                    }

                    // ⭐ 检查条件3：FFmpeg进程存在但无WebSocket连接且超过10秒无访问
                    if (channel.FFmpegProcess != null &&
                        !channel.FFmpegProcess.HasExited &&
                        channel.ActiveConnections <= 0 &&
                        inactiveTime > 0.17) // 10秒
                    {
                        try
                        {
                            // 尝试检查进程是否真的在运行
                            var isResponding = !channel.FFmpegProcess.HasExited;
                            if (!isResponding)
                            {
                                Console.WriteLine($"[Cleanup Timer] 发现无响应进程 - ChannelKey: {kvp.Key}, PID: {channel.FFmpegProcess.Id}");
                                channelsToCleanup.Add(kvp.Key);
                            }
                        }
                        catch
                        {
                            // 进程已经不存在
                            Console.WriteLine($"[Cleanup Timer] 进程已不存在 - ChannelKey: {kvp.Key}");
                            channelsToCleanup.Add(kvp.Key);
                        }
                    }
                }

                // 执行清理
                if (channelsToCleanup.Count > 0)
                {
                    Console.WriteLine($"[Cleanup Timer] 准备清理 {channelsToCleanup.Count} 个僵尸通道");

                    foreach (var channelKey in channelsToCleanup)
                    {
                        CleanupChannel(channelKey);
                    }

                    Console.WriteLine($"[Cleanup Timer] 已清理 {channelsToCleanup.Count} 个僵尸通道");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cleanup Timer] 清理僵尸通道时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据设备信息生成唯一的通道键，用于识别相同的视频源
        /// </summary>
        private String GenerateChannelKey(CameraDevice device, Int32 width, Int32 height, DateTime? startTime, DateTime? endTime)
        {
            var timeKey = startTime.HasValue ? $"_{startTime:yyyyMMddHHmmss}_{endTime:yyyyMMddHHmmss}" : "";
            return $"{device.IP}_{device.StreamPort}_{device.ChannelNo}_{device.StreamType}_{width}_{height}{timeKey}";
        }

        /// <summary>
        /// 开启对指定设备的转码。
        /// </summary>
        /// <param name="device">要进行转码的设备</param>
        /// <param name="width">转码后的画面宽度</param>
        /// <param name="height">转码后的画面高度</param>
        /// <param name="startTime">转码截取的起始时间</param>
        /// <param name="endTime">转码截取的截止时间</param>
        /// <returns>包含ChannelKey和WebSocket地址的结果</returns>
        public OResult<TranscodeResult> Start(CameraDevice device, Int32 width = 1920, Int32 height = 1080, DateTime? startTime = null, DateTime? endTime = null)
        {
            return Start(device, "", width, height, startTime, endTime);
        }

        /// <summary>
        /// 开启对指定设备的转码。
        /// </summary>
        /// <param name="device">要进行转码的设备</param>
        /// <param name="secret">设置转码密钥（Node.js模式需要）</param>
        /// <param name="width">转码后的画面宽度</param>
        /// <param name="height">转码后的画面高度</param>
        /// <param name="startTime">转码截取的起始时间</param>
        /// <param name="endTime">转码截取的截止时间</param>
        /// <returns>包含ChannelKey和WebSocket地址的结果</returns>
        public OResult<TranscodeResult> Start(CameraDevice device, String secret = "", Int32 width = 1920, Int32 height = 1080, DateTime? startTime = null, DateTime? endTime = null)
        {
            if (!File.Exists(_ffMpegPath))
            {
                throw new FileNotFoundException($"文件\"{_ffMpegPath}\"不存在");
            }

            var channelKey = GenerateChannelKey(device, width, height, startTime, endTime);

            // ⭐ 检查是否已存在相同视频源的转码通道（通道复用）
            if (_transcodeChannels.TryGetValue(channelKey, out var existingChannel))
            {
                // 只更新设备ID映射，不增加引用计数
                _deviceIdToChannelKey.TryAdd(device.Id, channelKey);

                Console.WriteLine($"[Start] 复用现有通道 - ChannelKey: {channelKey}, DeviceId: {device.Id}");

                var result = new TranscodeResult
                {
                    ChannelKey = channelKey,
                    WebSocketUrl = BuildWebsocketUrl(channelKey),
                    TcpPort = existingChannel.TcpPort,
                    IsReused = true
                };

                return new OResult<TranscodeResult>(result);
            }

            Console.WriteLine($"[Start] 创建新通道 - ChannelKey: {channelKey}, DeviceId: {device.Id}");

            // 创建新的转码通道
            if (_useNodeMode)
            {
                return StartWithNodeMode(device, secret, channelKey, width, height, startTime, endTime);
            }
            else
            {
                return StartWithCSharpMode(device, channelKey, width, height, startTime, endTime);
            }
        }

        /// <summary>
        /// 使用C#模式启动转码（推荐）- Web项目自行处理WebSocket
        /// </summary>
        private OResult<TranscodeResult> StartWithCSharpMode(CameraDevice device, String channelKey, Int32 width, Int32 height, DateTime? startTime, DateTime? endTime)
        {
            TcpListener tcpListener = null;

            try
            {
                var tcpPort = GetValidPort();
                if (tcpPort < 0)
                {
                    return new OResult<TranscodeResult>(default, "无可用端口");
                }

                // ⭐ 预先启动 TCP 监听器，确保端口可用
                try
                {
                    tcpListener = new TcpListener(System.Net.IPAddress.Loopback, tcpPort);
                    tcpListener.Start(10); // 允许10个排队连接
                    Console.WriteLine($"[TCP Listener] 已启动监听 - Port: {tcpPort}");
                }
                catch (Exception ex)
                {
                    _usedPorts.TryRemove(tcpPort, out _);
                    return new OResult<TranscodeResult>(default, $"启动TCP监听失败: {ex.Message}");
                }

                var transcodeRequest = new TranscodeRequest()
                {
                    IP = device.IP,
                    LoginPort = device.StreamPort,
                    ChannelNo = device.ChannelNo,
                    StreamType = device.StreamType,
                    UserName = device.UserName,
                    Password = device.Password,
                    Width = width,
                    Height = height,
                    StartTime = startTime,
                    EndTime = endTime,
                    WebSocketUrl = $"tcp://127.0.0.1:{tcpPort}"
                };

                var startFFmpegResult = StartFFMpegServer(transcodeRequest);
                if (!startFFmpegResult.Success)
                {
                    tcpListener?.Stop();
                    _usedPorts.TryRemove(tcpPort, out _);
                    return new OResult<TranscodeResult>(default, startFFmpegResult.Message);
                }

                var channel = new TranscodeChannel()
                {
                    ChannelKey = channelKey,
                    TcpPort = tcpPort,
                    FFmpegProcess = startFFmpegResult.Value.Process,
                    RTSPUri = startFFmpegResult.Value.RTSPUri,
                    CreatedTime = DateTime.Now,
                    LastAccessTime = DateTime.Now,
                    Status = ProcessStatus.Starting,
                    TcpListener = tcpListener // ⭐ 保存监听器引用
                };

                _transcodeChannels.TryAdd(channelKey, channel);
                _deviceIdToChannelKey.TryAdd(device.Id, channelKey);

                // ⭐ 延迟100ms后检查进程是否正常启动
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    if (!channel.FFmpegProcess.HasExited)
                    {
                        await channel.StartBroadcastAsync();

                        channel.Status = ProcessStatus.Running;
                        Console.WriteLine($"[FFmpeg] 进程启动成功 - PID: {channel.FFmpegProcess.Id}");
                    }
                    else
                    {
                        Console.WriteLine($"[FFmpeg] 进程启动失败 - ExitCode: {channel.FFmpegProcess.ExitCode}");
                        channel.Status = ProcessStatus.ExitedWithError;
                    }
                });

                var result = new TranscodeResult
                {
                    ChannelKey = channelKey,
                    WebSocketUrl = BuildWebsocketUrl(channelKey),
                    TcpPort = tcpPort,
                    IsReused = false
                };

                return new OResult<TranscodeResult>(result);
            }
            catch (Exception ex)
            {
                tcpListener?.Stop();
                return new OResult<TranscodeResult>(default, ex);
            }
        }

        /// <summary>
        /// 使用Node.js模式启动转码（需要Node.js环境）
        /// </summary>
        private OResult<TranscodeResult> StartWithNodeMode(CameraDevice device, String secret, String channelKey, Int32 width, Int32 height, DateTime? startTime, DateTime? endTime)
        {
            if (String.IsNullOrWhiteSpace(secret))
            {
                return new OResult<TranscodeResult>(default, "Node.js模式需要提供转码密钥");
            }

            var startNodeResult = StartNodeServer(secret.Replace(" ", String.Empty));
            if (!startNodeResult.Success)
            {
                return new OResult<TranscodeResult>(default, startNodeResult.Message);
            }

            var transcodeRequest = new TranscodeRequest()
            {
                IP = device.IP,
                LoginPort = device.StreamPort,
                ChannelNo = device.ChannelNo,
                StreamType = device.StreamType,
                UserName = device.UserName,
                Password = device.Password,
                Width = width,
                Height = height,
                StartTime = startTime,
                EndTime = endTime,
                WebSocketUrl = $"http://127.0.0.1:{startNodeResult.Value.StreamPort}/{startNodeResult.Value.Secret}"
            };

            var startTranscodeResult = StartFFMpegServer(transcodeRequest);
            if (!startTranscodeResult.Success)
            {
                StopNodeServer(startNodeResult.Value.Process);
                return new OResult<TranscodeResult>(default, startTranscodeResult.Message);
            }

            var channel = new TranscodeChannel()
            {
                ChannelKey = channelKey,
                TcpPort = startNodeResult.Value.StreamPort,
                FFmpegProcess = startTranscodeResult.Value.Process,
                NodeProcess = startNodeResult.Value.Process,
                RTSPUri = startTranscodeResult.Value.RTSPUri,
                CreatedTime = DateTime.Now,
                LastAccessTime = DateTime.Now
            };

            _transcodeChannels.TryAdd(channelKey, channel);
            _deviceIdToChannelKey.TryAdd(device.Id, channelKey);

            var result = new TranscodeResult
            {
                ChannelKey = channelKey,
                WebSocketUrl = $"ws://{_wsHost}:{startNodeResult.Value.WebSocketPort}/",
                TcpPort = startNodeResult.Value.StreamPort,
                IsReused = false
            };

            return new OResult<TranscodeResult>(result);
        }

        /// <summary>
        /// 构造WebSocket流的访问地址
        /// </summary>
        private String BuildWebsocketUrl(String channelKey)
        {
            return $"ws://{_wsHost}:{_wsPort}{_wsBasePath}/{channelKey}";
        }

        /// <summary>
        /// Web项目调用此方法注册 WebSocket 到广播列表
        /// </summary>
        /// <param name="channelKey">通道键</param>
        /// <param name="webSocket">WebSocket 连接</param>
        /// <returns>连接ID</returns>
        public async Task<String> RegisterWebSocketAsync(String channelKey, System.Net.WebSockets.WebSocket webSocket)
        {
            if (!_transcodeChannels.TryGetValue(channelKey, out var channel))
            {
                throw new InvalidOperationException($"通道不存在: {channelKey}");
            }

            channel.LastAccessTime = DateTime.Now;

            // 如果广播任务未启动，先启动
            await channel.StartBroadcastAsync();

            // 添加 WebSocket 到广播列表
            var connectionId = await channel.AddWebSocketAsync(webSocket);

            return connectionId;
        }

        /// <summary>
        /// Web项目调用此方法注销 WebSocket
        /// </summary>
        /// <param name="channelKey">通道键</param>
        /// <param name="connectionId">连接ID</param>
        public async Task UnregisterWebSocketAsync(String channelKey, String connectionId)
        {
            if (!_transcodeChannels.TryGetValue(channelKey, out var channel))
            {
                return;
            }

            channel.LastAccessTime = DateTime.Now;

            await channel.RemoveWebSocketAsync(connectionId);
        }

        /// <summary>
        /// 获取通道信息
        /// </summary>
        public TranscodeChannel GetChannelInfo(String channelKey)
        {
            if (!_transcodeChannels.TryGetValue(channelKey, out var channel))
            {
                return null;
            }

            return channel;
        }

        /// <summary>
        /// 获取所有通道信息
        /// </summary>
        public TranscodeChannel[] GetAllChannels()
        {
            return _transcodeChannels.Values.ToArray();
        }

        /// <summary>
        /// 停止对指定设备的转码（移除设备ID映射，但不清理通道）
        /// 通道会在所有WebSocket连接关闭后自动清理
        /// </summary>
        public void Stop(String deviceId)
        {
            if (!_deviceIdToChannelKey.TryGetValue(deviceId, out var channelKey))
            {
                Console.WriteLine($"[Stop] 设备不存在 - DeviceId: {deviceId}");
                return;
            }

            // 只移除设备ID映射
            _deviceIdToChannelKey.TryRemove(deviceId, out _);
            Console.WriteLine($"[Stop] 已移除设备映射 - DeviceId: {deviceId}, ChannelKey: {channelKey}");

            // 不立即清理通道，等待WebSocket连接自然关闭或超时清理
            if (_transcodeChannels.TryGetValue(channelKey, out var channel))
            {
                Console.WriteLine($"[Stop] 通道仍有 {channel.ActiveConnections} 个WebSocket连接，等待自动清理");
            }
        }

        /// <summary>
        /// 清理指定的转码通道
        /// </summary>
        private void CleanupChannel(String channelKey)
        {
            if (!_transcodeChannels.TryRemove(channelKey, out var channel))
            {
                Console.WriteLine($"[Cleanup] 通道不存在或已被清理 - ChannelKey: {channelKey}");
                return;
            }

            Console.WriteLine($"[Cleanup] 开始清理通道 - ChannelKey: {channelKey}");

            try
            {
                // ⭐ 0. 停止广播任务
                channel.StopBroadcast();

                // ⭐ 1. 停止FFmpeg进程
                if (channel.FFmpegProcess != null && !channel.FFmpegProcess.HasExited)
                {
                    Console.WriteLine($"[Cleanup] 停止FFmpeg进程 - PID: {channel.FFmpegProcess.Id}");
                    channel.Status = ProcessStatus.Killed;
                    StopFFMpegServer(channel.FFmpegProcess);
                }
                else if (channel.FFmpegProcess != null)
                {
                    Console.WriteLine($"[Cleanup] FFmpeg进程已退出 - ExitCode: {channel.FFmpegProcess.ExitCode}");
                }

                // ⭐ 2. 停止Node.js进程（如果有）
                if (channel.NodeProcess != null && !channel.NodeProcess.HasExited)
                {
                    Console.WriteLine($"[Cleanup] 停止Node.js进程 - PID: {channel.NodeProcess.Id}");
                    StopNodeServer(channel.NodeProcess);
                }

                // ⭐ 3. 停止TCP监听器
                if (channel.TcpListener != null)
                {
                    try
                    {
                        channel.TcpListener.Stop();
                        Console.WriteLine($"[Cleanup] 已停止TCP监听器 - Port: {channel.TcpPort}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Cleanup] 停止TCP监听器失败 - Port: {channel.TcpPort}, Error: {ex.Message}");
                    }
                }

                // ⭐ 4. 释放端口
                if (_usedPorts.TryRemove(channel.TcpPort, out _))
                {
                    Console.WriteLine($"[Cleanup] 已释放端口 - Port: {channel.TcpPort}");
                }

                // ⭐ 5. 清理设备ID映射
                var deviceIdsToRemove = _deviceIdToChannelKey
                    .Where(kvp => kvp.Value == channelKey)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var deviceId in deviceIdsToRemove)
                {
                    if (_deviceIdToChannelKey.TryRemove(deviceId, out _))
                    {
                        Console.WriteLine($"[Cleanup] 已清理设备映射 - DeviceId: {deviceId}");
                    }
                }

                Console.WriteLine($"[Cleanup] 通道清理完成 - ChannelKey: {channelKey}, 总资源: FFmpeg进程, 广播任务, TCP监听器, {deviceIdsToRemove.Count}个设备映射");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cleanup] 清理通道时发生异常 - ChannelKey: {channelKey}, Error: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止所有设备的转码
        /// </summary>
        public void StopAll()
        {
            foreach (var channelKey in _transcodeChannels.Keys.ToList())
            {
                CleanupChannel(channelKey);
            }
        }

        private OResult<WebSocketChannel> StartNodeServer(String secret)
        {
            try
            {
                var wsLiveVideoPath = Path.Combine(AppContext.BaseDirectory, @"lib\wsLiveVideo\wsLiveVideo.js");
                if (!File.Exists(wsLiveVideoPath))
                {
                    return new OResult<WebSocketChannel>(null, new FileNotFoundException(wsLiveVideoPath));
                }

                var streamPort = GetValidPort();
                var websocketPort = GetValidPort();

                var startInfo = new ProcessStartInfo()
                {
                    FileName = _nodePath,
                    Arguments = $"\"{wsLiveVideoPath}\" {secret} {streamPort} {websocketPort}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var process = new Process() { StartInfo = startInfo };
                process.Start();
                process.BeginOutputReadLine();

                var channel = new WebSocketChannel()
                {
                    Process = process,
                    Secret = secret,
                    StreamPort = streamPort,
                    WebSocketPort = websocketPort
                };

                return new OResult<WebSocketChannel>(channel);
            }
            catch (Exception ex)
            {
                return new OResult<WebSocketChannel>(null, ex);
            }
        }

        private static String BuildRSTPUrl(TranscodeRequest request)
        {
            int streamType = request.StreamType;

            if (request.StartTime == null && request.EndTime == null)
            {
                if (request.ChannelNo >= TranscodeRequest.CHANNEL_START_NO)
                {
                    return $"rtsp://{request.UserName}:{request.Password}@{request.IP}:{request.LoginPort}/h265/ch{request.ChannelNo}/main/av_stream";
                }
                else
                {
                    return $"rtsp://{request.UserName}:{request.Password}@{request.IP}:{request.LoginPort}/Streaming/Channels/{request.ChannelNo}0{streamType}";
                }
            }
            else
            {
                var starttime = $"{request.StartTime:yyyyMMdd}t{request.StartTime:HHmmss}z";
                var endtime = request.EndTime != null ? $"{request.EndTime:yyyyMMdd}t{request.EndTime:HHmmss}z" : null;

                var chNo = request.ChannelNo >= TranscodeRequest.CHANNEL_START_NO ? (request.ChannelNo - TranscodeRequest.CHANNEL_START_NO + 1) : request.ChannelNo;
                var url = $"rtsp://{request.UserName}:{request.Password}@{request.IP}:{request.LoginPort}/Streaming/tracks/{chNo}0{streamType}?starttime={starttime}";
                if (!String.IsNullOrWhiteSpace(endtime))
                {
                    url += $"&endtime={endtime}";
                }

                return url;
            }
        }

        private OResult<StreamChannel> StartFFMpegServer(TranscodeRequest request)
        {
            try
            {
                var rtspUri = BuildRSTPUrl(request);
                var outputUrl = request.WebSocketUrl;

                var arguments = $"-rtsp_transport tcp -i \"{rtspUri}\" -buffer_size 1024000 -max_delay 500000 -timeout 20000000 -acodec copy -ar 44100 -b:a 128k -an -f mpegts -codec:v mpeg1video -vf scale={request.Width}:{request.Height} -s {request.Width}x{request.Height} {outputUrl}";

                var startInfo = new ProcessStartInfo()
                {
                    FileName = _ffMpegPath,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var process = new Process() { StartInfo = startInfo };
                var lastErrorMessage = "";

                // ⭐ 监听标准错误输出（FFmpeg的日志输出到stderr）
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        // 过滤掉进度信息，只记录重要日志
                        if (!e.Data.StartsWith("frame=", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"[FFmpeg] {e.Data}");

                            // 检查是否包含错误信息
                            if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                e.Data.Contains("failed", StringComparison.OrdinalIgnoreCase))
                            {
                                lastErrorMessage = e.Data;
                                Console.WriteLine($"[FFmpeg Error] {e.Data}");
                            }
                        }
                    }
                };

                // ⭐ 监听标准输出
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Console.WriteLine($"[FFmpeg Output] {e.Data}");
                    }
                };

                // ⭐ 监听进程退出事件
                process.EnableRaisingEvents = true;
                process.Exited += (sender, e) =>
                {
                    try
                    {
                        var exitCode = process.ExitCode;
                        var exitTime = process.ExitTime;

                        Console.WriteLine($"[FFmpeg] 进程已退出 - ExitCode: {exitCode}");

                        // 查找对应的通道并更新状态
                        var channel = _transcodeChannels.Values.FirstOrDefault(ch => ch.FFmpegProcess?.Id == process.Id);
                        if (channel != null)
                        {
                            channel.FFmpegExitCode = exitCode;
                            channel.FFmpegExitTime = exitTime;
                            channel.FFmpegLastError = lastErrorMessage;

                            // 根据退出码判断状态
                            if (exitCode == 0)
                            {
                                channel.Status = ProcessStatus.ExitedNormally;
                                Console.WriteLine("[FFmpeg] 正常退出");
                            }
                            else
                            {
                                channel.Status = ProcessStatus.ExitedWithError;
                                Console.WriteLine($"[FFmpeg] 异常退出 - ExitCode: {exitCode}");
                            }

                            // ⭐ FFmpeg进程退出时立即触发资源清理
                            _ = Task.Run(async () =>
                            {
                                Console.WriteLine($"[FFmpeg] 进程退出，准备清理资源 - ChannelKey: {channel.ChannelKey}");

                                // 等待3秒，给现有连接一点时间接收最后的数据
                                await Task.Delay(3000);

                                // 检查是否还有活动连接
                                if (channel.ActiveConnections > 0)
                                {
                                    Console.WriteLine($"[FFmpeg] 还有 {channel.ActiveConnections} 个活动连接，等待其关闭");

                                    // 等待最多30秒让连接自然关闭
                                    for (int i = 0; i < 30; i++)
                                    {
                                        await Task.Delay(1000);
                                        if (channel.ActiveConnections <= 0)
                                        {
                                            break;
                                        }
                                    }
                                }

                                // 立即清理资源（无需再等待30秒，因为FFmpeg已经停止）
                                Console.WriteLine($"[FFmpeg] 立即清理通道资源 - ChannelKey: {channel.ChannelKey}");
                                CleanupChannel(channel.ChannelKey);
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[FFmpeg] 处理退出事件时出错: {ex.Message}");
                    }
                };

                process.Start();

                // 开始异步读取输出
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                var channel = new StreamChannel()
                {
                    Process = process,
                    WebSocketUrl = request.WebSocketUrl,
                    RTSPUri = rtspUri
                };

                return new OResult<StreamChannel>(channel);
            }
            catch (Exception ex)
            {
                return new OResult<StreamChannel>(null, ex);
            }
        }

        private static OResult<Boolean> StopNodeServer(Process process)
        {
            try
            {
                if (process == null || process.HasExited)
                {
                    return new OResult<Boolean>(true);
                }

                KillProcess(process.Id);
                return new OResult<Boolean>(true);
            }
            catch (Exception ex)
            {
                return new OResult<Boolean>(default, ex);
            }
        }

        private static OResult<Boolean> StopFFMpegServer(Process process)
        {
            try
            {
                if (process == null || process.HasExited)
                {
                    return new OResult<Boolean>(true);
                }

                KillProcess(process.Id);
                return new OResult<Boolean>(true);
            }
            catch (Exception ex)
            {
                return new OResult<Boolean>(default, ex);
            }
        }

        private static void KillProcess(Int32 pid)
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo()
                {
                    FileName = "taskkill",
                    Arguments = $"/f /t /pid {pid}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                proc?.WaitForExit();
            }
            catch
            {
                // 忽略异常
            }
        }

        private Int32 GetValidPort()
        {
            for (var port = 10000; port < 50000; port++)
            {
                if (_usedPorts.ContainsKey(port))
                {
                    continue;
                }

                if (!PortInUse(port))
                {
                    _usedPorts.TryAdd(port, true);
                    return port;
                }
            }

            return -1;
        }

        private static Boolean PortInUse(Int32 port)
        {
            var ipProperties = IPGlobalProperties.GetIPGlobalProperties();
            var ipEndPoints = ipProperties.GetActiveTcpListeners();
            return ipEndPoints.Any(x => x.Port == port);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Console.WriteLine("[Dispose] 开始释放WebSocketTranscoder资源");

            // ⭐ 停止定时器
            try
            {
                _cleanupTimer?.Dispose();
                Console.WriteLine("[Dispose] 清理定时器已停止");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dispose] 停止定时器失败: {ex.Message}");
            }

            // 停止所有通道
            StopAll();

            Console.WriteLine("[Dispose] WebSocketTranscoder资源释放完成");
        }
    }

    /// <summary>
    /// 转码结果
    /// </summary>
    public class TranscodeResult
    {
        /// <summary>
        /// 通道键
        /// </summary>
        public String ChannelKey { get; set; }

        /// <summary>
        /// WebSocket访问地址
        /// </summary>
        public String WebSocketUrl { get; set; }

        /// <summary>
        /// TCP端口（内部使用）
        /// </summary>
        public Int32 TcpPort { get; set; }

        /// <summary>
        /// 是否复用了现有通道
        /// </summary>
        public Boolean IsReused { get; set; }
    }
}
