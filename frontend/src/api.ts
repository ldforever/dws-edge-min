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
  BarcodeRuleSet,
  CameraRecord,
  ConfigSummary,
  DeviceView,
  FilteredCodeRecord,
  ParcelRecord,
  PositionsResponse,
  RuleTestResponse,
  RulesResponse,
  SavePositionsResponse,
  SaveRulesResponse,
  Stats
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
  const init: RequestInit = { method: options?.method ?? "GET" };
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
  if (!res.ok) {
    const body = parsed as { error?: string; raw?: string } | null;
    result.message = body?.error ?? body?.raw ?? `HTTP ${res.status}`;
  }
  return result;
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

  /** 图片按需读取接口（只允许图片根目录内的文件） */
  imageUrl: (path: string): string => "/api/images?path=" + encodeURIComponent(path)
};

export const STREAM_URL = "/api/stream";
