/**
 * T0：外壳级显示设置 —— 主题（跟随系统 / 深色 / 浅色）与信息密度（舒适 / 紧凑）。
 *
 * 为什么放在浏览器里、不放到后端配置：
 *   主题和密度是"看的人"的偏好，不是"这台设备"的配置。现场大屏要紧凑（一屏多塞几行），
 *   办公室笔记本要舒适；做成后端配置就会互相覆盖，还会平白多一份"要重启才生效"的设置。
 *   所以只写本机 localStorage：换浏览器、换机器就是各自的默认值。
 *
 * index.html 的 <head> 里有一段同样 key 的小脚本，负责在样式表加载**之前**先把
 * data-theme / data-density 贴上 —— 否则选了浅色的人每次刷新都会先闪一帧深色。
 * 两处逻辑要一起改（key 一共就两个，写在这里做记录）。
 */
import { $ } from "./dom.js";

type ThemeChoice = "auto" | "dark" | "light";
type DensityChoice = "comfortable" | "compact";

const THEME_KEY = "dws.ui.theme";
const DENSITY_KEY = "dws.ui.density";
const LIGHT_QUERY = "(prefers-color-scheme: light)";

function readChoice(key: string, fallback: string): string {
  try {
    return window.localStorage.getItem(key) ?? fallback;
  } catch {
    // 隐私模式 / 某些内嵌浏览器禁用存储：当作没设置过
    return fallback;
  }
}

function writeChoice(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // 存不下就只对本次会话生效，不影响使用
  }
}

/**
 * 默认深色：这是原来的观感，也是现场大屏在用的。
 * 默认给"跟随系统"的话，Windows 处于浅色模式的机器一升级就整个界面变白 —— 那是惊吓不是升级。
 */
function normalizeTheme(value: string): ThemeChoice {
  return value === "light" || value === "auto" ? value : "dark";
}

function normalizeDensity(value: string): DensityChoice {
  return value === "compact" ? "compact" : "comfortable";
}

/** "跟随系统"在这里落成具体的 dark / light —— CSS 只认这两个值 */
function resolveTheme(choice: ThemeChoice): "dark" | "light" {
  if (choice !== "auto") return choice;
  return window.matchMedia(LIGHT_QUERY).matches ? "light" : "dark";
}

export function applyTheme(choice: ThemeChoice): void {
  document.documentElement.dataset.theme = resolveTheme(choice);
}

export function applyDensity(choice: DensityChoice): void {
  document.documentElement.dataset.density = choice === "compact" ? "compact" : "comfortable";
}

export function initShell(): void {
  const themeSelect = $<HTMLSelectElement>("uiTheme");
  const densitySelect = $<HTMLSelectElement>("uiDensity");

  const theme = normalizeTheme(readChoice(THEME_KEY, "dark"));
  const density = normalizeDensity(readChoice(DENSITY_KEY, "comfortable"));

  themeSelect.value = theme;
  densitySelect.value = density;
  applyTheme(theme);
  applyDensity(density);

  themeSelect.addEventListener("change", () => {
    const choice = normalizeTheme(themeSelect.value);
    writeChoice(THEME_KEY, choice);
    applyTheme(choice);
  });

  densitySelect.addEventListener("change", () => {
    const choice = normalizeDensity(densitySelect.value);
    writeChoice(DENSITY_KEY, choice);
    applyDensity(choice);
  });

  // 选"跟随系统"时，系统在浅/深之间切换（比如 Windows 夜间模式）要实时跟上
  window.matchMedia(LIGHT_QUERY).addEventListener("change", () => {
    if (themeSelect.value === "auto") applyTheme("auto");
  });
}
