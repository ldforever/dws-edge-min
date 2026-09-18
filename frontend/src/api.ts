/**
 * 接口层：所有 /api/* 调用都从这里走。
 *
 * 约定：
 *   * 永远返回 { status, data }，不抛"HTTP 错误"——业务代码只需要判断 status；
 *   * 只有"网络层失败"（服务没起来 / 断网）才抛异常，由调用方决定怎么提示；
 *   * 请求体统一 JSON，错误响应体里的 { error } 会放进 message 里方便直接显示。
 */
import type {
  ApplyRequest,
  ApplyResult,
  AuthConfigResponse,
  AuthEventRecord,
  AuthOptions,
  AuthStatus,
  AuthUsersResponse,
  BackupItem,
  BarcodeRuleSet,
  CameraCounter,
  CameraRecord,
  ConfigSummary,
  DedupStats,
  DeviceView,
  DownstreamLogItem,
  DownstreamOptions,
  DownstreamPreview,
  DownstreamResponse,
  FilteredCodeRecord,
  HistoryFilter,
  HistoryResult,
  ImageInfo,
  LoginResponse,
  MonitorAlertsResponse,
  MonitorCameraStatus,
  MonitorConfigResponse,
  MonitorEvent,
  MonitorOptions,
  MonitorSummary,
  ParcelRecord,
  PositionsResponse,
  RuleTestResponse,
  RulesResponse,
  SaveMonitorConfigResponse,
  SavePositionsResponse,
  SaveRulesResponse,
  SaveStorageResponse,
  Stats,
  StorageConfigResponse,
  StorageOptions as StorageOptionsType
} from "./types.js";

export interface ApiResult<T> {
  status: number;
  data: T | null;
  /** 后端返回的 { error: "..." }，或网络错误信息 */
  message?: string;
}

interface RequestOptions {
  method?: string;
  /** 有值就按 JSON 提交 */
  json?: unknown;
}

async function request<T>(url: string, options?: RequestOptions): Promise<ApiResult<T>> {
  // 同源 Cookie（登录会话）要带上：不写 credentials 时 fetch 对同源默认就会带，
  // 但这里显式声明，避免以后换成跨源部署时悄悄丢会话。
  const init: RequestInit = { method: options?.method ?? "GET", credentials: "same-origin" };
  if (options?.json !== undefined) {
    init.headers = { "Content-Type": "application/json" };
    init.body = JSON.stringify(options.json);
  }

  let res: Response;
  try {
    res = await fetch(url, init);
  } catch (e) {
    // 网络层失败：服务没起来、页面被关闭等
    return { status: 0, data: null, message: describe(e) };
  }

  const text = await res.text();
  let parsed: unknown = null;
  if (text) {
    try {
      parsed = JSON.parse(text);
    } catch {
      parsed = { raw: text };
    }
  }

  const result: ApiResult<T> = { status: res.status, data: (parsed as T) ?? null };
  // B9：会话过期/未登录 —— 交给 auth 模块弹登录框（登录接口自己的 401 不算，那是密码错）
  if (res.status === 401 && url.indexOf("/api/auth/login") !== 0) {
    if (unauthorizedHandler) unauthorizedHandler();
  }
  if (!res.ok) {
    const body = parsed as { error?: string; raw?: string } | null;
    result.message = body?.error ?? body?.raw ?? `HTTP ${res.status}`;
  }
  return result;
}

/** B9：收到 401 时通知界面（由 auth.ts 注册，用来弹出登录框） */
let unauthorizedHandler: (() => void) | null = null;
export function onUnauthorized(handler: () => void): void {
  unauthorizedHandler = handler;
}

function describe(e: unknown): string {
  if (e instanceof Error) return e.message;
  return String(e);
}

export const api = {
  stats: (): Promise<ApiResult<Stats>> => request<Stats>("/api/stats"),

  parcels: (limit: number): Promise<ApiResult<ParcelRecord[]>> =>
    request<ParcelRecord[]>(`/api/parcels?limit=${limit}`),

  cameras: (): Promise<ApiResult<CameraRecord[]>> => request<CameraRecord[]>("/api/cameras"),

  /** C2：相机计数（出码数/掉线次数/方位），相机状态墙首屏用 */
  cameraCounters: (): Promise<ApiResult<CameraCounter[]>> => request<CameraCounter[]>("/api/cameras/counters"),

  devices: (): Promise<ApiResult<DeviceView>> => request<DeviceView>("/api/devices"),

  positions: (): Promise<ApiResult<PositionsResponse>> =>
    request<PositionsResponse>("/api/camera-positions"),

  savePositions: (positions: Record<string, string>): Promise<ApiResult<SavePositionsResponse>> =>
    request<SavePositionsResponse>("/api/camera-positions", { method: "POST", json: { positions } }),

  config: (): Promise<ApiResult<ConfigSummary>> => request<ConfigSummary>("/api/config"),

  applyConfig: (body: ApplyRequest): Promise<ApiResult<ApplyResult>> =>
    request<ApplyResult>("/api/config/apply", { method: "POST", json: body }),

  // ---- B2 条码过滤规则 ----
  rules: (): Promise<ApiResult<RulesResponse>> => request<RulesResponse>("/api/rules"),

  saveRules: (ruleSet: BarcodeRuleSet): Promise<ApiResult<SaveRulesResponse>> =>
    request<SaveRulesResponse>("/api/rules", { method: "POST", json: ruleSet }),

  testRules: (codes: string[], ruleSet: BarcodeRuleSet | null): Promise<ApiResult<RuleTestResponse>> =>
    request<RuleTestResponse>("/api/rules/test", {
      method: "POST",
      json: { codes, ruleset: ruleSet }
    }),

  filteredCodes: (limit: number): Promise<ApiResult<FilteredCodeRecord[]>> =>
    request<FilteredCodeRecord[]>(`/api/rules/filtered?limit=${limit}`),

  // ---- B1 去重指纹归档 ----
  dedup: (): Promise<ApiResult<DedupStats>> => request<DedupStats>("/api/dedup"),

  compactDedup: (): Promise<ApiResult<DedupStats>> =>
    request<DedupStats>("/api/dedup/compact", { method: "POST" }),

  // ---- B3 历史查询与导出 ----
  history: (filter: HistoryFilter): Promise<ApiResult<HistoryResult>> =>
    request<HistoryResult>("/api/history?" + historyQueryString(filter, true)),

  /** 导出用的 URL（点击后由浏览器直接下载 CSV） */
  historyExportUrl: (filter: HistoryFilter): string =>
    "/api/history/export?" + historyQueryString(filter, false),

  // ---- B4 下游 TCP 输出 ----
  downstream: (): Promise<ApiResult<DownstreamResponse>> =>
    request<DownstreamResponse>("/api/downstream"),

  saveDownstream: (options: DownstreamOptions): Promise<ApiResult<{ ok: boolean; note?: string }>> =>
    request<{ ok: boolean; note?: string }>("/api/downstream", { method: "POST", json: options }),

  testDownstream: (options: DownstreamOptions): Promise<ApiResult<{ ok: boolean; target?: string; error?: string; note?: string }>> =>
    request<{ ok: boolean; target?: string; error?: string; note?: string }>("/api/downstream/test", {
      method: "POST",
      json: options
    }),

  previewDownstream: (template: string): Promise<ApiResult<DownstreamPreview>> =>
    request<DownstreamPreview>("/api/downstream/preview", { method: "POST", json: { template } }),

  downstreamLog: (limit: number): Promise<ApiResult<DownstreamLogItem[]>> =>
    request<DownstreamLogItem[]>(`/api/downstream/log?limit=${limit}`),

  /** 图片按需读取接口（只允许图片根目录内的文件） */
  imageUrl: (path: string): string => "/api/images?path=" + encodeURIComponent(path),

  /** B7：缩略图（BMP 真缩小并缓存，JPEG 回退原图） */
  imageThumbUrl: (path: string, width: number): string =>
    "/api/images/thumb?w=" + width + "&path=" + encodeURIComponent(path),

  imageInfo: (path: string): Promise<ApiResult<ImageInfo>> =>
    request<ImageInfo>("/api/images/info?path=" + encodeURIComponent(path)),

  // ---- B8 相机状态监控与告警 ----
  monitorSummary: (): Promise<ApiResult<MonitorSummary>> => request<MonitorSummary>("/api/monitor/summary"),

  monitorCameras: (): Promise<ApiResult<MonitorCameraStatus[]>> =>
    request<MonitorCameraStatus[]>("/api/monitor/cameras"),

  monitorEvents: (limit: number, camera?: string): Promise<ApiResult<MonitorEvent[]>> =>
    request<MonitorEvent[]>(
      "/api/monitor/events?limit=" + limit + (camera ? "&camera=" + encodeURIComponent(camera) : "")
    ),

  monitorAlerts: (limit: number, activeOnly: boolean): Promise<ApiResult<MonitorAlertsResponse>> =>
    request<MonitorAlertsResponse>("/api/monitor/alerts?limit=" + limit + "&activeOnly=" + activeOnly),

  monitorConfig: (): Promise<ApiResult<MonitorConfigResponse>> =>
    request<MonitorConfigResponse>("/api/monitor/config"),

  saveMonitorConfig: (options: MonitorOptions): Promise<ApiResult<SaveMonitorConfigResponse>> =>
    request<SaveMonitorConfigResponse>("/api/monitor/config", { method: "POST", json: options }),

  // ---- B9 账号与鉴权 ----
  authStatus: (): Promise<ApiResult<AuthStatus>> => request<AuthStatus>("/api/auth/status"),

  login: (username: string, password: string): Promise<ApiResult<LoginResponse>> =>
    request<LoginResponse>("/api/auth/login", { method: "POST", json: { username, password } }),

  logout: (): Promise<ApiResult<{ ok: boolean; note?: string }>> =>
    request<{ ok: boolean; note?: string }>("/api/auth/logout", { method: "POST" }),

  me: (): Promise<ApiResult<{ username: string; role: string; viaServiceKey: boolean; expiresAtMs: number }>> =>
    request<{ username: string; role: string; viaServiceKey: boolean; expiresAtMs: number }>("/api/auth/me"),

  changePassword: (
    username: string | null,
    oldPassword: string | null,
    newPassword: string
  ): Promise<ApiResult<{ ok: boolean; username: string; byAdmin: boolean; note?: string }>> =>
    request<{ ok: boolean; username: string; byAdmin: boolean; note?: string }>("/api/auth/password", {
      method: "POST",
      json: { username, oldPassword, newPassword }
    }),

  authUsers: (): Promise<ApiResult<AuthUsersResponse>> => request<AuthUsersResponse>("/api/auth/users"),

  addUser: (
    username: string,
    password: string,
    role: string,
    note?: string
  ): Promise<ApiResult<{ ok: boolean; user: unknown }>> =>
    request<{ ok: boolean; user: unknown }>("/api/auth/users", {
      method: "POST",
      json: { username, password, role, note }
    }),

  updateUser: (
    username: string,
    role: string | null,
    enabled: boolean | null,
    note?: string | null
  ): Promise<ApiResult<{ ok: boolean; user: unknown }>> =>
    request<{ ok: boolean; user: unknown }>("/api/auth/users/update", {
      method: "POST",
      json: { username, role, enabled, note }
    }),

  deleteUser: (username: string): Promise<ApiResult<{ ok: boolean; note?: string }>> =>
    request<{ ok: boolean; note?: string }>("/api/auth/users/delete", { method: "POST", json: { username } }),

  resetPassword: (username: string, password: string): Promise<ApiResult<{ ok: boolean; note?: string }>> =>
    request<{ ok: boolean; note?: string }>("/api/auth/users/reset-password", {
      method: "POST",
      json: { username, password }
    }),

  authConfig: (): Promise<ApiResult<AuthConfigResponse>> => request<AuthConfigResponse>("/api/auth/config"),

  saveAuthConfig: (options: AuthOptions): Promise<ApiResult<{ ok: boolean; backup?: string | null }>> =>
    request<{ ok: boolean; backup?: string | null }>("/api/auth/config", { method: "POST", json: options }),

  rotateServiceKey: (): Promise<ApiResult<{ ok: boolean; serviceKey: string; note?: string }>> =>
    request<{ ok: boolean; serviceKey: string; note?: string }>("/api/auth/service-key", { method: "POST" }),

  authEvents: (limit: number): Promise<ApiResult<AuthEventRecord[]>> =>
    request<AuthEventRecord[]>("/api/auth/events?limit=" + limit),

  // ---- C5 配置页（简版）----
  storageConfig: (): Promise<ApiResult<StorageConfigResponse>> =>
    request<StorageConfigResponse>("/api/config/storage"),

  saveStorageConfig: (options: StorageOptionsType): Promise<ApiResult<SaveStorageResponse>> =>
    request<SaveStorageResponse>("/api/config/storage", { method: "POST", json: options }),

  /** 备份列表（rules / downstream / monitor / auth / gateway.ini / Cfg 的 .bak-* 都在这） */
  backups: (): Promise<ApiResult<BackupItem[]>> => request<BackupItem[]>("/api/config/backups"),

  rollback: (file: string): Promise<ApiResult<{ ok: boolean; target: string; restoredFrom: string; backupOfCurrent?: string | null; effect?: string; note?: string }>> =>
    request<{ ok: boolean; target: string; restoredFrom: string; backupOfCurrent?: string | null; effect?: string; note?: string }>(
      "/api/config/rollback",
      { method: "POST", json: { file } }
    )
};

export const STREAM_URL = "/api/stream";

/** 历史查询串：导出时不要 limit/offset（要全量） */
function historyQueryString(filter: HistoryFilter, paged: boolean): string {
  const params = new URLSearchParams();
  params.set("from", filter.from);
  params.set("to", filter.to);
  if (filter.code) params.set("code", filter.code);
  if (filter.deviceId) params.set("deviceId", filter.deviceId);
  if (filter.noread !== "") params.set("noread", filter.noread);
  if (filter.dispatchState !== "") params.set("dispatchState", filter.dispatchState);
  if (filter.hasImage !== "") params.set("hasImage", filter.hasImage);
  if (paged) {
    params.set("limit", String(filter.limit));
    params.set("offset", String(filter.offset));
  }
  return params.toString();
}
