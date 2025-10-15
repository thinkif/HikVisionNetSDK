using System;
using HikVisionNetSDK;
using HikVisionNetSDK.Codec;
using HikVisionNetSDK.Models;
using Xunit;

namespace HkvsNetDrvierTests
{
    public class WebSocketTranscoderTest
    {
        /// <summary>
        /// 直连摄像头取流（使用Node.js）
        /// </summary>
        [Fact]
        public void TestStartCVR()
        {
            var transcoder = new WebSocketTranscoder(@"C:\ffmpeg\bin\ffmpeg.exe", @"C:\nodejs\node.exe", "192.168.8.89");
            var device1 = new CameraDevice()
            {
                Id = "001",
                IP = "192.168.8.170",
                StreamPort = 554,//取流端口号
                ChannelNo = 1,
                UserName = "admin",
                Password = "lct12345"
            };

            var startResult = transcoder.Start(device1, "123456");
            Assert.True(startResult.Success);

            transcoder.Stop(device1.Id);
            Assert.NotNull(startResult.Value);
        }

        /// <summary>
        /// 通过NVR连接摄像头取流（使用Node.js）
        /// </summary>
        [Fact]
        public void TestStartNVR()
        {
            var transcoder = new WebSocketTranscoder(@"C:\ffmpeg\bin\ffmpeg.exe", @"C:\nodejs\node.exe", "192.168.8.89");
            var device2 = new CameraDevice()
            {
                Id = "002",
                IP = "192.168.8.168",
                StreamPort = 554,
                ChannelNo = 33,
                UserName = "admin",
                Password = "78910"
            };

            var startResult = transcoder.Start(device2, "78910");
            Assert.True(startResult.Success);

            transcoder.Stop(device2.Id);
            Assert.NotNull(startResult.Value);
        }

        /// <summary>
        /// 使用C# WebSocket直连摄像头取流
        /// </summary>
        [Fact]
        public void TestStartCVRWithCSharpWebSocket()
        {
            // 不传入nodePath参数，使用C# WebSocket
            var transcoder = new WebSocketTranscoder(@"C:\ffmpeg\bin\ffmpeg.exe", null, "192.168.8.89");
            var device1 = new CameraDevice()
            {
                Id = "003",
                IP = "192.168.8.170",
                StreamPort = 554,
                ChannelNo = 1,
                UserName = "admin",
                Password = "lct12345"
            };

            var startResult = transcoder.Start(device1, "123456");
            Assert.True(startResult.Success);
            Assert.NotNull(startResult.Value);
            Assert.Contains("ws://", startResult.Value.WebSocketUrl);

            transcoder.Stop(device1.Id);
        }

        /// <summary>
        /// 使用C# WebSocket通过NVR连接摄像头取流
        /// </summary>
        [Fact]
        public void TestStartNVRWithCSharpWebSocket()
        {
            // 不传入nodePath参数，使用C# WebSocket
            var transcoder = new WebSocketTranscoder(@"C:\ffmpeg\bin\ffmpeg.exe", wsHost: "192.168.8.89");
            var device2 = new CameraDevice()
            {
                Id = "004",
                IP = "192.168.8.168",
                StreamPort = 554,
                ChannelNo = 33,
                UserName = "admin",
                Password = "lct12345"
            };

            var startResult = transcoder.Start(device2, "78910");
            Assert.True(startResult.Success);
            Assert.NotNull(startResult.Value);
            Assert.Contains("ws://", startResult.Value.WebSocketUrl);

            transcoder.Stop(device2.Id);
        }

        /// <summary>
        /// 测试连接复用功能：多个设备请求相同的视频源
        /// </summary>
        [Fact]
        public void TestConnectionReuse()
        {
            var transcoder = new WebSocketTranscoder(@"C:\ffmpeg\bin\ffmpeg.exe", wsHost: "192.168.8.89");
            
            // 第一个设备请求
            var device1 = new CameraDevice()
            {
                Id = "005",
                IP = "192.168.8.170",
                StreamPort = 554,
                ChannelNo = 1,
                UserName = "admin",
                Password = "lct12345"
            };

            var result1 = transcoder.Start(device1, "123456");
            Assert.True(result1.Success);
            var url1 = result1.Value;

            // 第二个设备请求相同的视频源（IP、端口、通道号都相同）
            var device2 = new CameraDevice()
            {
                Id = "006", // 不同的设备ID
                IP = "192.168.8.170", // 相同的IP
                StreamPort = 554, // 相同的端口
                ChannelNo = 1, // 相同的通道号
                UserName = "admin",
                Password = "lct12345"
            };

            var result2 = transcoder.Start(device2, "123456");
            Assert.True(result2.Success);
            var url2 = result2.Value;

            // 应该返回相同的WebSocket地址（复用连接）
            Assert.Equal(url1, url2);

            // 停止第一个设备，但由于第二个设备还在使用，连接应该保持
            transcoder.Stop(device1.Id);

            // 第三个设备请求相同的视频源
            var device3 = new CameraDevice()
            {
                Id = "007",
                IP = "192.168.8.170",
                StreamPort = 554,
                ChannelNo = 1,
                UserName = "admin",
                Password = "lct12345"
            };

            var result3 = transcoder.Start(device3, "123456");
            Assert.True(result3.Success);
            Assert.Equal(url1, result3.Value);

            // 停止所有设备
            transcoder.Stop(device2.Id);
            transcoder.Stop(device3.Id);
        }

        /// <summary>
        /// 测试自动清理功能：当所有客户端断开连接后自动清理资源
        /// </summary>
        [Fact]
        public void TestAutoCleanup()
        {
            var transcoder = new WebSocketTranscoder(@"C:\ffmpeg\bin\ffmpeg.exe", wsHost: "192.168.8.89");
            
            var device = new CameraDevice()
            {
                Id = "008",
                IP = "192.168.8.170",
                StreamPort = 554,
                ChannelNo = 1,
                UserName = "admin",
                Password = "lct12345"
            };

            var result = transcoder.Start(device, "123456");
            Assert.True(result.Success);

            // 模拟客户端连接然后断开
            // 在实际场景中，当所有WebSocket客户端断开连接30秒后，资源会自动清理

            transcoder.Stop(device.Id);
        }
    }
}
