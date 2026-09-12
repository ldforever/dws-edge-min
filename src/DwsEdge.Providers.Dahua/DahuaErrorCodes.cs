using System;
using LogisticsBaseCSharp;

namespace DwsEdge.Providers.Dahua
{
    /// <summary>
    /// 大华 SDK 返回码翻译。
    ///
    /// 注意：官方《C# 接口文档》里写的是 -1 ~ -10，但实际运行时返回的是
    /// LogisticsAPIStruct.ERunStatus 枚举值（0 / 1000 / 2200 / 3000 / 5000 ...）。
    /// 这里直接按真实枚举翻译，最常用的两个：
    ///   2200 = 没有加密狗；3000 = 相机数与配置不符（现场多半是相机没连上）。
    /// </summary>
    internal static class DahuaErrorCodes
    {
        public static string Describe(int code)
        {
            LogisticsAPIStruct.ERunStatus status = (LogisticsAPIStruct.ERunStatus)code;

            switch (status)
            {
                case LogisticsAPIStruct.ERunStatus.eAppStatusInitOK:
                    return "成功";

                case LogisticsAPIStruct.ERunStatus.eRunStatusIvalidHandlerror:
                    return "句柄无效（BaseApp 未创建）";
                case LogisticsAPIStruct.ERunStatus.eRunStatusAlreadyRunError:
                    return "已经在运行，请先停止再启动";
                case LogisticsAPIStruct.ERunStatus.eRunStatusInitialAgainError:
                    return "重复初始化：同一进程只能初始化一次";

                case LogisticsAPIStruct.ERunStatus.eRunStatusNoCfg:
                    return "找不到配置文件：检查 cfgPath 与文件后缀";
                case LogisticsAPIStruct.ERunStatus.eRunStatusCfgError:
                    return "配置项解析失败：检查 cfg 内容与 SDK 版本是否匹配（注意 GB2312 编码）";

                case LogisticsAPIStruct.ERunStatus.eRunStatusNoEncryptedDog:
                    return "未检测到加密狗：插入 USB 加密狗并确认驱动正常";
                case LogisticsAPIStruct.ERunStatus.eRunStatusAlgorithmError:
                    return "算法初始化失败：检查内存是否足够";

                case LogisticsAPIStruct.ERunStatus.eRunStatusBarcodeAlgError:
                case LogisticsAPIStruct.ERunStatus.eRunStatusBarcodeAlgfinError:
                    return "一维码算法初始化失败：检查 <BarCode> 参数";
                case LogisticsAPIStruct.ERunStatus.eRunStatusDMcodeAlgError:
                case LogisticsAPIStruct.ERunStatus.eRunStatusDMcodeAlgfinError:
                    return "二维码算法初始化失败：检查 <DMCode> 参数";
                case LogisticsAPIStruct.ERunStatus.eRunStatusMattingAlgError:
                case LogisticsAPIStruct.ERunStatus.eRunStatusMattingAlgfinError:
                    return "抠图算法初始化失败：检查 <ClarityMatting> 参数";

                case LogisticsAPIStruct.ERunStatus.eRunStatusCameraNumError:
                    return "相机数与配置不符 / 没有相机连上：确认相机 IP 与 Cfg 中 <Camera ... enable=\"1\"> 一致，"
                         + "<ImageAcq num> 与实际在线数一致，本机与相机同网段";
                case LogisticsAPIStruct.ERunStatus.eRunStatusCameraOpendError:
                    return "相机被其他程序占用：关闭 MVViewer / EasyID 等工具后重试";
                case LogisticsAPIStruct.ERunStatus.eRunStatusCameraListNumError:
                    return "指定相机列表数量与 num 不一致";
                case LogisticsAPIStruct.ERunStatus.eRunStatusCameraListUnmatch:
                    return "指定的相机不在线：核对 IP / Key / Id";
                case LogisticsAPIStruct.ERunStatus.eRunStatusSoftEncryptionError:
                    return "相机未授权：检查相机授权状态";
                case LogisticsAPIStruct.ERunStatus.eRunStatusIpcCameraError:
                    return "全景相机初始化失败：检查 <CombIPCAndBarcodes> 的 ip 是否在线";
                case LogisticsAPIStruct.ERunStatus.eRunStatusNtpServerError:
                    return "NTP 对时失败：检查本机 NTP 服务/端口";

                case LogisticsAPIStruct.ERunStatus.eRunStatus3DCameraError:
                    return "3D 相机初始化失败：检查 3D 相机与 <Volume> 配置";
                case LogisticsAPIStruct.ERunStatus.eRunStatusAICameraError:
                    return "AI 相机初始化失败";

                case LogisticsAPIStruct.ERunStatus.eRunStatusWeightError:
                    return "称重模块初始化失败：检查 <WeightCfg> 串口/IP 与协议";
                case LogisticsAPIStruct.ERunStatus.eRunStatusCodeRuleFilterError:
                    return "条码过滤模块初始化失败：检查 <CodeFilterCfg>";
                case LogisticsAPIStruct.ERunStatus.eRunStatusModuleOutputError:
                    return "输出模块初始化失败：检查 <OutputCfg1>";

                case LogisticsAPIStruct.ERunStatus.eRunStatusLocalImagePathError:
                case LogisticsAPIStruct.ERunStatus.eRunStatusLocalImageNumError:
                case LogisticsAPIStruct.ERunStatus.eRunStatusLocalImageInitError:
                    return "本地图片路径/数量初始化失败：检查存图路径是否存在、是否有写权限";

                default:
                    return "未知返回码；可在 runtime\\Log\\default.log 中搜索 ret val:[" + code + "] 查看底层原因";
            }
        }
    }
}
