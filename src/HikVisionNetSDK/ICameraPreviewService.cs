using System;
using System.Collections.Generic;
using System.Text;

namespace HikVisionNetSDK
{
    public interface ICameraPreviewService : IDisposable
    {
        /// <summary>
        /// 打开实时预览。
        /// </summary>
        OResult<Boolean> Open();
        /// <summary>
        /// 开始录像。
        /// </summary>
        /// <param name="fileName">录像文件名</param>
        OResult<String> StartRecord(String fileName);
        /// <summary>
        /// 停止录像。
        /// </summary>
        OResult<Boolean> StopRecord();
        /// <summary>
        /// 关闭实时预览。
        /// </summary>
        OResult<Boolean> Close();
        /// <summary>
        /// 实时预览抓图。
        /// </summary>
        /// <param name="filePath">图片保存路径</param>
        OResult<Boolean> CapturePicture(String filePath);
    }
}
