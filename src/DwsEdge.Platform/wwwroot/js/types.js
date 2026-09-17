/**
 * 平台 API 的数据类型。
 *
 * 这些 interface 与后端 DwsEdge.Platform 的 DTO 一一对应：
 *   SpoolModels.cs  → Stats / ParcelRecord / CameraRecord / DeviceView / PositionsRequest
 *   ConfigStore.cs  → ConfigSummary / ApplyResult / PositionsResponse
 *
 * 后端改字段时，这里要跟着改；改错了 tsc 会在编译期报错（这就是上 TypeScript 的目的）。
 * 字段名用小写驼峰，与 ASP.NET Core 的 JSON 序列化保持一致。
 */
export {};
