using System;
using DwsEdge.Core.Model;

namespace DwsEdge.Core.Abstractions
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    /// <summary>
    /// provider 的唯一出口。
    /// 现在由宿主实现成"控制台 + 日志 + JSONL spool"；
    /// 将来换成 gRPC 推给业务进程，provider 一行都不用改。
    /// </summary>
    public interface IEventSink
    {
        void OnParcel(ParcelEvent parcel);
        void OnCameraRead(CameraReadEvent cameraRead);
        void OnCameraStatus(CameraStatusEvent status);
        void OnImageSaved(ImageRef image);
        void Log(LogLevel level, string message);
        void LogError(string message, Exception ex);
    }
}
