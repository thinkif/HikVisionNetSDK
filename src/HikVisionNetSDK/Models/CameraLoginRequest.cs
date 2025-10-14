using System;

namespace HikVisionNetSDK.Models
{
    /// <summary>
    /// 摄像头登录请求
    /// 设备登录模式有两种：SDK私有协议和ISAPI协议。
    //1) SDK私有协议是我司私有的TCP/IP协议，登录使用的是设备服务端口（默认为8000），我司网络设备除特殊产品外基本都支持该协议方式登录，因此一般建议使用SDK私有协议模式登录。
    //2) ISAPI协议是基于标准HTTP REST架构，HTTP协议或者HTTPS协议访问设备，登录使用的是设备HTTP端口（默认为80）或者HTTPS端口（默认为443）。不支持SDK私有协议的设备如猎鹰、刀锋等采用ISAPI协议的方式登录。
    //使用ISAPI协议方式登录时bySupportLock、byRetryLoginTime、byPasswordLevel、byProxyType、dwSurplusLockTime、byCharEncodeType、bySupportDev5这些参数都不支持。
    /// </summary>
    public class CameraLoginRequest
    {
        /// <summary>
        /// 摄像头/NVR的IP地址
        /// </summary>
        public String IP { get; set; }
        /// <summary>
        /// 登录端口号 SDK私有协议设备服务端口（默认为8000），如果是ISAPI模式下，则使用设备后台设置的HTTP端口（默认为80）或者HTTPS端口（默认为443）
        /// </summary>
        public Int32 LoginPort { get; set; }
        /// <summary>
        /// 通道号
        /// </summary>
        public Int32 ChannelNo { get; set; } = 33;
        /// <summary>
        /// 登录用户名
        /// </summary>
        public String UserName { get; set; }
        /// <summary>
        /// 登录密码
        /// </summary>
        public String Password { get; set; }

        /// <summary>
        /// 登录模式：0- SDK私有协议，1- ISAPI协议, 默认是SDK模式
        /// </summary>
        public byte LoginMode { get; set; }
    }
}
