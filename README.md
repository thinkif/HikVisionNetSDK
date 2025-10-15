## 对设备网络SDK封装库的升级，完全自用，集成在项目中
- 主要升级了SDK版本
- C# 实现 RTSP 转 WebSocket，适用于浏览器播放，支持多用户广播模式
- 设备内置的WebSocket服务只能在WEB无插件开发包中使用，但是它不支持移动端设备特别是iOS设备，H5Player需要对接综合安防管理平台，且这2个前端库都已加密不方便二次开发
- 所以还是需要使用RTSP转WebSocket的方式来播放视频流，兼容性更好

## 快速开始

### 1. 安装

下载官方设备网络SDK_Win64 当前仓库内置版本是 2023年的版本

### 2. 配置依赖注入，全局单例模式，确保统一对ffmpeg和TCP连接池的管理

```csharp
// Startup.cs 或 Program.cs
services.AddSingleton<WebSocketTranscoder>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new WebSocketTranscoder(
        ffmpegPath: "path/to/ffmpeg.exe"
    );
});
```

### 3. 启用 WebSocket 中间件

```csharp
// Startup.cs - Configure 方法
app.UseWebSockets();
app.UseMiddleware<RTSPProxyMiddleware>();
```

### 4. 启动视频转码

```csharp
[ApiController]
[Route("api/video")]
public class VideoController : ControllerBase
{
    private readonly WebSocketTranscoder _transcoder;

    public VideoController(WebSocketTranscoder transcoder)
    {
        _transcoder = transcoder;
    }

    [HttpPost("start")]
    public IActionResult StartStream([FromBody] StreamRequest request)
    {
        var device = new CameraDevice
        {
            Id = request.DeviceId,
            IP = request.Ip,
            StreamPort = request.Port,
            ChannelNo = request.ChannelNo,
            UserName = request.Username,
            Password = request.Password,
            StreamType = 1 // 1-主码流，2-子码流
        };

        var result = _transcoder.Start(device, width: 1920, height: 1080);

        if (result.Success)
        {
            return Ok(new
            {
                channelKey = result.Value.ChannelKey,
                webSocketUrl = result.Value.WebSocketUrl,
                isReused = result.Value.IsReused
            });
        }

        return BadRequest(result.Message);
    }

    [HttpPost("stop/{deviceId}")]
    public IActionResult StopStream(string deviceId)
    {
        _transcoder.Stop(deviceId);
        return Ok();
    }
}
```

### 5. 客户端播放

先调用转码接口获取 WebSocket 地址，然后使用前端播放器播放，在我的项目中使用的是JSMpeg

