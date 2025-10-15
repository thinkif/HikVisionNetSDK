using HikVisionNetSDK.Codec;
using Microsoft.AspNetCore.Http;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace YuanFxCore.Service.Middlewares
{
    /// <summary>
    /// WebSocket 视频流中间件 - 广播模式（修复版）
    /// 使用新的广播API，支持多用户同时观看，性能更好
    /// </summary>
    public class RTSPProxyMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly WebSocketTranscoder _transcoder;

        public RTSPProxyMiddleware(RequestDelegate next, WebSocketTranscoder transcoder)
        {
            _next = next;
            _transcoder = transcoder ?? throw new ArgumentNullException(nameof(transcoder));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // 只处理 WebSocket 请求
            var fullPath = (context.Request.PathBase + context.Request.Path).ToString();
            if (context.WebSockets.IsWebSocketRequest &&
                fullPath.StartsWith("/rtspproxy", StringComparison.OrdinalIgnoreCase))
            {
                // 从路径中提取 channelKey
                // 路径格式: /rtspproxy/{channelKey}
                var pathSegments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (pathSegments == null || pathSegments.Length < 2)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Invalid path format. Expected: /rtspproxy/{channelKey}");
                    return;
                }

                var channelKey = pathSegments[1];
                await HandleWebSocketAsync(context, channelKey);
            }
            else
            {
                await _next(context);
            }
        }

        /// <summary>
        /// 处理 WebSocket 连接 - 广播模式（修复版）
        /// </summary>
        private async Task HandleWebSocketAsync(HttpContext context, string channelKey)
        {
            WebSocket webSocket = null;
            string connectionId = null;

            try
            {
                // 1. 检查通道是否存在
                var channel = _transcoder.GetChannelInfo(channelKey);
                if (channel == null)
                {
                    Console.WriteLine($"[Middleware] 通道不存在 - ChannelKey: {channelKey}");
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync($"Channel not found: {channelKey}");
                    return;
                }

                Console.WriteLine($"[Middleware] 找到通道 - ChannelKey: {channelKey}, FFmpeg状态: {channel.Status}, 活动连接: {channel.ActiveConnections}");

                // 2. 接受 WebSocket 连接
                webSocket = await context.WebSockets.AcceptWebSocketAsync();
                Console.WriteLine($"[Middleware] WebSocket 连接已建立 - ChannelKey: {channelKey}");

                // 3. ⭐ 注册到广播列表（这会自动启动广播任务）
                try
                {
                    connectionId = await _transcoder.RegisterWebSocketAsync(channelKey, webSocket);
                    Console.WriteLine($"[Middleware] WebSocket 已注册到广播 - ChannelKey: {channelKey}, ConnectionId: {connectionId}");
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine($"[Middleware] 注册失败 - ChannelKey: {channelKey}, Error: {ex.Message}");
                    await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Failed to register", CancellationToken.None);
                    return;
                }

                Console.WriteLine($"[Middleware] 广播任务已启动，开始保持连接 - ChannelKey: {channelKey}, ConnectionId: {connectionId}");

                // 4. 保持连接（等待客户端关闭或取消）
                // 注意：在广播模式下，SDK 会自动将数据推送到 WebSocket
                // 这里只需要保持连接并等待客户端发送关闭消息
                var buffer = new byte[1024];
                while (webSocket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
                {
                    try
                    {
                        var result = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            context.RequestAborted);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Console.WriteLine($"[Middleware] 收到客户端关闭请求 - ConnectionId: {connectionId}");
                            break;
                        }

                        // 客户端可能发送心跳包，这里可以处理
                        // 当前实现忽略客户端发送的数据
                        if (result.MessageType == WebSocketMessageType.Text || result.MessageType == WebSocketMessageType.Binary)
                        {
                            Console.WriteLine($"[Middleware] 收到客户端数据 - ConnectionId: {connectionId}, Type: {result.MessageType}, Size: {result.Count}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"[Middleware] 请求被取消 - ConnectionId: {connectionId}");
                        break;
                    }
                    catch (WebSocketException ex)
                    {
                        Console.WriteLine($"[Middleware] WebSocket 异常 - ConnectionId: {connectionId}, Error: {ex.Message}");
                        break;
                    }
                }

                Console.WriteLine($"[Middleware] WebSocket 连接循环结束 - ConnectionId: {connectionId}, State: {webSocket.State}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Middleware] 处理 WebSocket 时出错 - ChannelKey: {channelKey}, Error: {ex.Message}");
                Console.WriteLine($"[Middleware] 异常堆栈: {ex.StackTrace}");
            }
            finally
            {
                // 5. ⭐ 注销 WebSocket
                if (connectionId != null)
                {
                    try
                    {
                        await _transcoder.UnregisterWebSocketAsync(channelKey, connectionId);
                        Console.WriteLine($"[Middleware] WebSocket 已注销 - ChannelKey: {channelKey}, ConnectionId: {connectionId}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Middleware] 注销 WebSocket 时出错 - ConnectionId: {connectionId}, Error: {ex.Message}");
                    }
                }

                // 6. 清理 WebSocket
                if (webSocket != null)
                {
                    if (webSocket.State == WebSocketState.Open)
                    {
                        try
                        {
                            await webSocket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Connection closed",
                                CancellationToken.None);
                            Console.WriteLine($"[Middleware] WebSocket 已主动关闭 - ConnectionId: {connectionId}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Middleware] 关闭 WebSocket 时出错 - ConnectionId: {connectionId}, Error: {ex.Message}");
                        }
                    }

                    try
                    {
                        webSocket.Dispose();
                    }
                    catch
                    {
                        // Ignore dispose errors
                    }
                }

                Console.WriteLine($"[Middleware] WebSocket 连接已清理完成 - ChannelKey: {channelKey}, ConnectionId: {connectionId}");
            }
        }
    }
}
