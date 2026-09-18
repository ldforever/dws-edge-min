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

/** 安装方位：顶/底/左/右/前/后/线体/备用 */
export type Position = "top" | "bottom" | "left" | "right" | "front" | "rear" | "line" | "spare";

/** GET /api/stats */
export interface Stats {
  events: number;
  parcels: number;
  noread: number;
  images: number;
  /** 0-1，界面乘 100 显示 */
  readRate: number;
  camerasTotal: number;
  camerasOnline: number;
  parseErrors: number;
  imageFileCount: number;
  imageDiskBytes: number;
  diskTotalBytes: number;
  diskFreeBytes: number;
  diskUsedPercent: number;
  pendingParcels: number;
  missingTraceId: number;
  traceIdConflicts: number;
  /** B1：被判定为重复、直接丢弃的事件数 */
  duplicateEvents: number;
  /** B1：发生过合并（收到 ≥2 次有效回调）的包裹数 */
  mergedParcels: number;
  /** B1：推送出去的包裹事件数（重复事件不推） */
  publishedParcels: number;
  /** B1：幂等下发状态统计 */
  dispatchPending: number;
  dispatchSent: number;
  dispatchFailed: number;
  serverTime: string;
}

/** 一个条码的完整信息 */
export interface CodeDetail {
  value: string;
  /** 1d / 2d / unknown */
  kind?: string;
  /** 方位（可能为空，采集宿主用 camera-positions.ini 兜底） */
  position?: string;
}

/** GET /api/parcels、GET /api/history */
export interface ParcelRecord {
  traceId?: string;
  deviceId?: string;
  stage?: string;
  capturedAtMs?: number;
  time?: string;
  codes?: string[];
  codeDetails?: CodeDetail[];

  /** B2：这个包裹被规则丢弃掉的条码（含命中的规则名） */
  filteredCodes?: FilteredCodeRecord[];

  codeCount?: number;
  weightGrams?: number;
  volumeMm3?: number;
  lengthMm?: number;
  widthMm?: number;
  heightMm?: number;
  imageCount?: number;
  firstImagePath?: string;
  /** 同一包裹被合并了几次回调 */
  updates?: number;
  /** provider 是否分阶段上报（true 时"看到 enriched 才算完整"） */
  staged?: boolean;
  /** 是否已完整 */
  complete?: boolean;

  // ---- B1 幂等下发 ----
  /** pending / sent / failed */
  dispatchState?: string;
  dispatchAttempts?: number;
  dispatchedAt?: string;
  dispatchError?: string;
}

/** 一台相机的状态与设备信息（GET /api/cameras、GET /api/devices 的行） */
export interface CameraRecord {
  deviceId: string;
  userId?: string;
  online: boolean;
  atMs?: number;
  lastChangeTime?: string;
  statusChanges?: number;
  codeCount?: number;
  model?: string;
  serialNumber?: string;
  vendor?: string;
  firmware?: string;

  // ---- A9：清单声明 / 方位 / 是否被 SDK 发现 ----
  /** cfg 里声明的接入方式：ip / key / id */
  declaredKind?: string;
  /** cfg 里声明的值（IP / 序列号 / 完整 id） */
  declaredValue?: string;
  /** "ip=172.20.10.11" 这样的显示文本 */
  declaredLabel?: string;
  position?: string;
  positionLabel?: string;
  positionOrder?: number;
  /** 方位刚在界面上改过、采集宿主还没重启 */
  positionPending?: boolean;
  /** false = 清单里声明了但 SDK 没发现（离线或没接入） */
  discovered?: boolean;
  fromSnapshot?: boolean;

  // ---- A2：掉线统计 ----
  offlineCount?: number;
  reconnectCount?: number;
  lastOfflineAtMs?: number;
  lastOfflineDurationMs?: number;
  lastOfflineTime?: string;
  lastOfflineDurationText?: string;
  lastCodeTime?: string;
}

/** 按方位聚合的一行（六面概览） */
export interface FaceSummary {
  position: string;
  label: string;
  total: number;
  online: number;
  offline: number;
  cameras: string[];
}

/** GET /api/devices */
export interface DeviceView {
  total: number;
  online: number;
  offline: number;
  discovered: number;
  /** 清单里声明了 enable="1" 但 SDK 没发现的台数 */
  declaredMissing: number;
  /** 还没标方位的台数 */
  positionMissing: number;
  positionsFile: string;
  positionsFileExists: boolean;
  positionOptions: string[];
  faces: FaceSummary[];
  cameras: CameraRecord[];
}

/** GET /api/camera-positions */
export interface PositionsResponse {
  file: string;
  exists: boolean;
  count: number;
  options: Position[];
  entries: { key: string; position: Position; label: string }[];
}

/** POST /api/camera-positions 的返回 */
export interface SavePositionsResponse {
  ok: boolean;
  changed: number;
  count: number;
  file: string;
  backup?: string;
  note?: string;
}

/** 配置页里的相机清单行 */
export interface ConfigCamera {
  index: number;
  kind: string;
  value: string;
  /** "ip=172.20.10.11,pos=top" */
  line: string;
  position?: string;
  positionLabel?: string;
}

/** GET /api/config */
export interface ConfigSummary {
  runtimeRoot: string;
  cfgPath: string;
  cfgExists: boolean;
  gatewayPath: string;
  provider?: string;
  mode: string;
  num: number;
  randWorkMode: string;
  triggerMode: string;
  triggerName: string;
  enabledCameras: number;
  declaredCameras: number;
  cameras: ConfigCamera[];
  positionsFile: string;
  positionsCount: number;
  toolsReady: boolean;
  applyScript: string;
  cameraListFile: string;
  lastApply?: ApplyResult | null;
  problems: string[];
  warnings: string[];
}

/** POST /api/config/apply 的返回（也用于 /api/config 里的 lastApply） */
export interface ApplyResult {
  ok: boolean;
  exitCode: number;
  /** 结论文案：配置已生效 / 已回滚 / 需人工检查 / 应用失败 */
  conclusion: string;
  output: string;
  durationMs: number;
  at: string;
  commandLine?: string;
  error?: string;
}

/** POST /api/config/apply 的请求体 */
export interface ApplyRequest {
  /** hard / soft / free；空串 = 不改 */
  triggerMode: string;
  /** 相机清单；null = 不改 */
  cameras: string[] | null;
  skipVerify: boolean;
  stopHost: boolean;
  restartHost: boolean;
}

/** SSE /api/stream 推送的消息 */
export type StreamMessage =
  | { type: "parcel"; data: ParcelRecord }
  | { type: "camera"; data: CameraRecord }
  | { type: "stats"; data: Stats }
  /** B8：监控快照（在线率/心跳/活动告警），平台按检查间隔周期推送 */
  | { type: "monitor"; data: MonitorSnapshot }
  /** B8：单条告警产生/恢复 */
  | { type: "alert"; data: MonitorAlert }
  /** C2：相机计数（出码数等）—— 出一包就变，单独推，节流 700ms */
  | { type: "camera-count"; data: CameraCounter[] };

/** C2：一台相机的计数类信息（相机状态墙用） */
export interface CameraCounter {
  camera: string;
  /** 累计出码包裹数（按 traceId 去重后） */
  codeCount: number;
  lastCodeTime?: string | null;
  offlineCount: number;
  online: boolean;
  discovered: boolean;
  position?: string | null;
  positionLabel?: string | null;
  model?: string | null;
  serialNumber?: string | null;
}

// ---------------------------------------------------------------- B2 条码过滤规则

/** 一条条码过滤规则（字段名与后端 Core\Rules\BarcodeRule.cs 一致） */
export interface BarcodeRule {
  name: string;
  /** 越小越先判断；第一条命中的规则决定结果 */
  priority: number;
  enabled: boolean;
  /** keep / drop */
  action: string;
  remark?: string | null;
  minLength?: number | null;
  maxLength?: number | null;
  prefix?: string | null;
  suffix?: string | null;
  regex?: string | null;
  /** 支持 * 通配：SF* / *0001 / *JD* */
  whitelist?: string[] | null;
  blacklist?: string[] | null;
}

export interface BarcodeRuleSet {
  /** 没有规则命中时的默认动作：keep / drop */
  defaultAction: string;
  ignoreCase: boolean;
  rules: BarcodeRule[];
}

/** GET /api/rules */
export interface RulesResponse extends BarcodeRuleSet {
  file: string;
  exists: boolean;
  ruleCount: number;
  enabledCount: number;
}

/** POST /api/rules */
export interface SaveRulesResponse {
  ok: boolean;
  file: string;
  backup?: string | null;
  enabledCount: number;
  defaultAction: string;
  note?: string;
}

/** 单个条码的判定结果 */
export interface RuleDecision {
  code: string;
  kept: boolean;
  matchedRule?: string | null;
  reason: string;
  action?: string | null;
}

/** POST /api/rules/test */
export interface RuleTestResponse {
  /** true = 用的是界面上还没保存的规则 */
  usingDraft: boolean;
  defaultAction: string;
  enabledCount: number;
  kept: number;
  dropped: number;
  decisions: RuleDecision[];
  problems: string[];
}

/** 被规则丢弃的条码（GET /api/rules/filtered） */
export interface FilteredCodeRecord {
  time: string;
  traceId: string;
  deviceId: string;
  code: string;
  rule?: string | null;
  reason: string;
}

/** B1 去重指纹归档的状态（GET /api/dedup 与 POST /api/dedup/compact 返回） */
export interface DedupStats {
  directory: string;
  /** 索引里的 traceId 条数 */
  indexEntries: number;
  indexKeys: number;
  /** 还没合并进索引的 WAL 行数 */
  walLines: number;
  walBytes: number;
  indexBytes: number;
  retentionDays: number;
  compactWhenWalLines: number;
  compactEveryMinutes: number;
  lastCompactAt?: string | null;
  compactions: number;
  droppedExpired: number;
  importedLegacy: number;
}

// ---------------------------------------------------------------- B3 历史查询

/** 历史查询条件（对应 /api/history 的查询串） */
export interface HistoryFilter {
  from: string;
  to: string;
  code: string;
  deviceId: string;
  noread: "" | "true" | "false";
  dispatchState: "" | "pending" | "sent" | "failed";
  hasImage: "" | "true" | "false";
  limit: number;
  offset: number;
}

/** GET /api/history 的返回 */
export interface HistoryResult {
  /** 命中总数（分页前） */
  total: number;
  returned: number;
  /** 服务端查询耗时（毫秒）—— 用来验证"10 万条 2 秒内" */
  elapsedMs: number;
  /** true = 读的是按 traceId 收敛后的索引 */
  fromIndex: boolean;
  items: ParcelRecord[];
}

// ---------------------------------------------------------------- B4 下游输出

/** 下游 TCP 输出配置（对应 runtime\config\downstream.json） */
export interface DownstreamOptions {
  enabled: boolean;
  protocol: string;
  host: string;
  port: number;
  template: string;
  encoding: string;
  connectTimeoutMs: number;
  retryIntervalMs: number;
  /** 0 = 一直重试 */
  maxAttempts: number;
  /** 分阶段 provider：等重量体积到齐再发 */
  sendOnlyComplete: boolean;
  sendIntervalMs: number;
  /** B5 服务端模式：新客户端接入时补发最近 N 条（0 = 不补发） */
  replayRecentCount: number;

  // ---- B6 HTTP 推送 ----
  /** 下游接收地址，如 http://192.168.1.50:8080/dws */
  url: string;
  httpTimeoutMs: number;
  contentType: string;
  /** 幂等键用的请求头名，值为包裹 traceId */
  idempotencyHeader: string;
  /** 额外请求头，格式 "名称: 值" */
  headers: string[];
}

export interface DownstreamStats {
  enabled: boolean;
  target: string;
  connected: boolean;
  connectedSince?: string | null;
  sent: number;
  failed: number;
  retries: number;
  bytesSent: number;
  queueDepth: number;
  lastSentAt?: string | null;
  lastError?: string | null;
  templateProblems: string[];
  /** B5 服务端模式状态 */
  serverMode: boolean;
  listening: boolean;
  listenTarget?: string | null;
  clientCount: number;
  clients: DownstreamClient[];

  /** B6 HTTP 模式 */
  httpMode: boolean;
  httpTarget?: string | null;
  idempotencyHeader?: string | null;
}

/** B5：已接入的下游客户端 */
export interface DownstreamClient {
  id: string;
  remote: string;
  connectedAt: string;
  sent: number;
  bytes: number;
  lastError?: string | null;
}

/** B7：图片元信息（GET /api/images/info） */
export interface ImageInfo {
  ok: boolean;
  error?: string;
  path?: string;
  name?: string;
  bytes?: number;
  modified?: string;
  extension?: string;
  /** BMP 才能真的生成缩略图；JPEG 会回退原图 */
  thumbSupported?: boolean;
}

export interface DownstreamResponse {
  file: string;
  config: DownstreamOptions;
  stats: DownstreamStats;
  templateFields: string[];
}

export interface DownstreamLogItem {
  time: string;
  traceId?: string | null;
  success: boolean;
  attempt: number;
  bytes: number;
  message: string;
  payload?: string | null;
  error?: string | null;
}

export interface DownstreamPreview {
  usingSample: boolean;
  rendered: string;
  bytes: number;
  problems: string[];
}

// ---------------------------------------------------------------- B8 相机状态监控与告警

/** 监控阈值配置（runtime\config\monitor.json，与后端 MonitorOptions 一一对应） */
export interface MonitorOptions {
  enabled: boolean;
  /** 多少秒没收到任何数据（状态或出码）就算心跳超时 */
  heartbeatTimeoutSeconds: number;
  /** 离线持续多少秒才告警（避免闪断刷屏） */
  offlineAlertSeconds: number;
  frequentOfflineCount: number;
  frequentOfflineWindowMinutes: number;
  /** 在线率低于这个百分比就告警 */
  onlineRateAlertPercent: number;
  /** 在线率统计窗口（分钟） */
  onlineRateWindowMinutes: number;
  checkIntervalSeconds: number;
  eventRetentionDays: number;
}

/** 一条活动告警的简表（挂在相机状态里） */
export interface MonitorAlertBrief {
  code: string;
  severity: string;
  message: string;
}

/** GET /api/monitor/cameras：每台相机的监控指标 */
export interface MonitorCameraStatus {
  camera: string;
  online: boolean;
  /** 在线率；数据不足时为 -1 */
  onlineRatePercent: number;
  onlineSeconds: number;
  offlineSeconds: number;
  currentStateSeconds: number;
  lastHeartbeatAt?: string | null;
  /** 距上次心跳多少秒；没有心跳记录时为 -1 */
  lastHeartbeatAgeSeconds: number;
  lastCodeAt?: string | null;
  offlineCount: number;
  alertCount: number;
  alerts: MonitorAlertBrief[];

  // ---- C2：相机状态墙要的"计数类"字段（由 SpoolStore 提供权威值）----
  /** 累计出码包裹数（按 traceId 去重后） */
  codeCount?: number;
  lastCodeTime?: string | null;
  offlineCountTotal?: number;
  discovered?: boolean;
  position?: string | null;
  positionLabel?: string | null;
  model?: string | null;
  serialNumber?: string | null;
}

/** GET /api/monitor/summary */
export interface MonitorSummary {
  cameras: number;
  online: number;
  offline: number;
  averageOnlineRatePercent: number;
  activeAlerts: number;
  events: number;
  enabled: boolean;
  heartbeatTimeoutSeconds: number;
  offlineAlertSeconds: number;
  configFile: string;
}

/** 一条相机事件（掉线记录 / 上线 / 恢复 / 告警产生 / 告警恢复） */
export interface MonitorEvent {
  time: string;
  atMs: number;
  camera: string;
  /** offline / online / recovered / alert-raised / alert-cleared */
  event: string;
  detail?: string | null;
  /** 本次离线时长（毫秒），只有 recovered 才有 */
  offlineDurationMs: number;
  code?: string | null;
  severity?: string | null;
}

/** 一条告警 */
export interface MonitorAlert {
  id: string;
  camera: string;
  /** camera-offline / heartbeat-timeout / frequent-offline / low-online-rate / declared-missing */
  code: string;
  severity: string;
  message: string;
  active: boolean;
  raisedAt: string;
  clearedAt?: string | null;
}

/** GET /api/monitor/alerts */
export interface MonitorAlertsResponse {
  activeCount: number;
  items: MonitorAlert[];
}

/** SSE type=monitor 的载荷 */
export interface MonitorSnapshot {
  summary: MonitorSummary;
  cameras: MonitorCameraStatus[];
  alerts: MonitorAlert[];
  serverTime: string;
}

/** GET /api/monitor/config */
export interface MonitorConfigResponse {
  file: string;
  dataDirectory: string;
  options: MonitorOptions;
  note?: string;
}

export interface SaveMonitorConfigResponse {
  ok: boolean;
  file: string;
  backup?: string | null;
  options: MonitorOptions;
  note?: string;
}

// ---------------------------------------------------------------- B9 账号与鉴权

/** GET /api/auth/status（公开：前端靠它决定要不要弹登录框） */
export interface AuthStatus {
  enabled: boolean;
  /** 读接口是否也要登录 */
  protectRead: boolean;
  allowServiceKey: boolean;
  authenticated: boolean;
  username?: string | null;
  role?: string | null;
  viaServiceKey?: boolean;
  activeSessions: number;
  /** 初始密码文件还在（还没改过密码） */
  initialPasswordPending: boolean;
  initialPasswordFile?: string | null;
}

/** POST /api/auth/login 成功返回 */
export interface LoginResponse {
  ok: boolean;
  token: string;
  username: string;
  role: string;
  mustChangePassword: boolean;
  expiresAtMs: number;
  sessionMinutes: number;
  note?: string;
}

/** 一个账号（永远不含密码哈希） */
export interface AuthUserView {
  username: string;
  role: string;
  enabled: boolean;
  createdAt?: string | null;
  updatedAt?: string | null;
  mustChangePassword: boolean;
  note?: string | null;
  locked: boolean;
  lockedSeconds: number;
  failedCount: number;
  lastLoginAt?: string | null;
  lastLoginIp?: string | null;
  lastFailureAt?: string | null;
}

export interface AuthUsersResponse {
  file: string;
  maxFailures: number;
  lockMinutes: number;
  activeSessions: number;
  users: AuthUserView[];
}

/** 鉴权策略 */
export interface AuthOptions {
  enabled: boolean;
  protectRead: boolean;
  allowServiceKey: boolean;
  serviceKey: string;
  serviceKeyRole: string;
  maxFailures: number;
  lockMinutes: number;
  failureWindowMinutes: number;
  sessionMinutes: number;
}

export interface AuthConfigResponse {
  file: string;
  usersFile: string;
  initialPasswordFile: string;
  dataDirectory: string;
  options: AuthOptions;
  activeSessions: number;
  note?: string;
}

/** 登录/鉴权审计事件 */
export interface AuthEventRecord {
  time: string;
  atMs: number;
  kind: string;
  username?: string | null;
  ip?: string | null;
  detail?: string | null;
}

// ---------------------------------------------------------------- C5 配置页（简版）

/** 存图策略（gateway.ini 的 provider 段 + [storage] 段） */
export interface StorageOptions {
  saveOriginal: boolean;
  saveWaybill: boolean;
  savePerCamera: boolean;
  attachAllCameraCodeInfo: boolean;
  providerImageDir: string;
  imageDir: string;
  retentionDays: number;
  maxDiskPercent: number;
  cleanupIntervalMinutes: number;
  cleanupOnStart: boolean;
  spoolRetentionDays: number;
}

/** GET /api/config/storage */
export interface StorageConfigResponse {
  file: string;
  exists: boolean;
  provider: string;
  providerSection: string;
  /** 存图开关写进哪个段（一般是相机 provider 段；测试环境 provider=simulator 时会落到 dahua-dws 段） */
  storageSection: string;
  imageRoot: string;
  options: StorageOptions;
  limits: Record<string, string>;
  note: string;
}

/** POST /api/config/storage */
export interface SaveStorageResponse {
  ok: boolean;
  file: string;
  backup?: string | null;
  changed: string[];
  changedCount: number;
  needRestartHost: boolean;
  note?: string;
}

/** 一条配置备份（GET /api/config/backups） */
export interface BackupItem {
  fileName: string;
  folder: string;
  target: string;
  targetExists: boolean;
  size: number;
  sizeText: string;
  modified: string;
  effect: string;
}
