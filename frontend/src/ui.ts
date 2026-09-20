/**
 * C7：自绘界面零件 —— 右下角提示（toast）与确认对话框（confirmBox）。
 *
 * 为什么单独一个文件，不塞进 dom.ts：
 *   * dom.ts 是"最底层工具"，被所有页面模块 import；这两块各自上百行，混进去不好读；
 *   * 这里刻意 **不 import dom.ts**：dom.ts 的 notify() 要反过来调本文件的 toast()，
 *     两边互相 import 就成了循环依赖（运行时活绑定能跑，但 tsc isolatedModules 下很脆），
 *     所以本文件自带一个三行的 mk() 造元素 helper，保持零依赖、单向引用：dom.ts → ui.ts。
 *
 * 安全约定（跟全项目一致）：一律 createElement + textContent，禁止 innerHTML ——
 * 条码、文件名、后端报错文案都会进界面。
 *
 * 容器都是 JS 懒创建并挂到 document.body 的，index.html 一行都不用改。
 */
export type ToastKind = "ok" | "warn" | "err";

/** 每种态的存活时长（毫秒）。0 = 常驻，必须手动点关闭（报错不让自动消失，怕看漏） */
const TOAST_TTL: Record<ToastKind, number> = { ok: 3000, warn: 6000, err: 0 };

/** 左侧的小图标，跟配置页现有的 "✓ / ✗" 提示保持一致 */
const TOAST_ICON: Record<ToastKind, string> = { ok: "✓", warn: "!", err: "✗" };

/** 同屏最多几条提示，超了挤掉最旧的 */
const TOAST_MAX = 4;

/**
 * ok 的说法：都是"动作已完成"。注意全部是"已完成体"的完整词组，
 * 避免把「重置失败」误判成 ok（它里面也有"重置"两个字）。
 */
const OK_PATTERNS: RegExp[] = [
  /成功/,
  /已保存/,
  /已回滚/,
  /已重置/,
  /已删除/,
  /已生效/,
  /已新增/,
  /已写入/,
  /已启用/,
  /已禁用/,
  /已轮换/,
  /已套用/,
  /已应用/,
  /已完成/
];

/**
 * err 的说法：一律"结尾锚定或后面紧跟标点/收尾"，也就是"这句话在说某个动作失败了"。
 *
 * 这么写是为了躲开 informational 文案：
 *   "失败的下发会自动重试（默认每 5 秒一次）…" ← "失败"后面是"的"，不是报错，是说明 → warn
 */
const ERR_TAIL = /(?:失败|错误|错误码|异常|故障|中止|中断|超时|拒绝)(?:[：:，,。；;！!？?、）)\]]|$)/;

/** 这几个词本身就是"做不了"的结论，整句命中即报错 */
const ERR_WORDS = /无法|不能|未能|不可/;

/** 三行的 createElement wrapper（本地版 el()，为了不 import dom.ts 造成循环依赖） */
function mk<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  text?: string,
  className?: string
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}

/**
 * 根据文案猜这条提示该用什么颜色。
 *
 * 优先级：ok → err → warn（兜底）。
 * 现有的 22 处 notify 都不带类型参数，全靠这里猜；真猜不准的地方由调用方显式传 kind。
 */
export function inferKind(message: string): ToastKind {
  const text = message.trim();
  if (OK_PATTERNS.some((pattern) => pattern.test(text))) return "ok";
  if (ERR_TAIL.test(text) || ERR_WORDS.test(text)) return "err";
  return "warn";
}

// ================================================================
// toast
// ================================================================

/** 一条正在显示的提示的运行时状态 */
interface LiveToast {
  root: HTMLElement;
  timer: number | null;
  /** 还剩多少毫秒（鼠标悬停暂停时用来接着算） */
  remaining: number;
  /** 本次计时开始的时刻 */
  startedAt: number;
}

let layer: HTMLElement | null = null;
const living: LiveToast[] = [];

/** 拿到（必要时创建）右下角的提示容器 */
function ensureLayer(): HTMLElement {
  if (layer && layer.isConnected) return layer;
  layer = mk("div", undefined, "tlayer");
  document.body.appendChild(layer);
  return layer;
}

/** 把某条提示从界面和队列里摘掉（幂等） */
function dropToast(item: LiveToast): void {
  if (item.timer !== null) {
    window.clearTimeout(item.timer);
    item.timer = null;
  }
  const index = living.indexOf(item);
  if (index >= 0) living.splice(index, 1);
  item.root.remove();
}

/** 开始（或恢复）自动消失倒计时；err 的 remaining 是 0，永远不会自动消失 */
function armTimer(item: LiveToast): void {
  if (item.timer !== null || item.remaining <= 0) return;
  item.startedAt = Date.now();
  item.timer = window.setTimeout(() => dropToast(item), item.remaining);
}

/** 鼠标移到提示上时暂停倒计时，移开接着走（给长文案留够阅读时间） */
function pauseTimer(item: LiveToast): void {
  if (item.timer === null) return;
  window.clearTimeout(item.timer);
  item.timer = null;
  item.remaining = Math.max(500, item.remaining - (Date.now() - item.startedAt));
}

/**
 * 右下角显示一条提示。
 *
 * @param message 正文，支持 \n 换行（用 textContent + CSS pre-wrap 渲染，不走 innerHTML）
 * @param kind    ok（3 秒消失）/ warn（6 秒消失）/ err（常驻，必须手动关）
 */
export function toast(message: string, kind: ToastKind = "warn"): void {
  const root = mk("div", undefined, "toast " + kind);
  root.setAttribute("role", "status");

  const icon = mk("span", TOAST_ICON[kind], "ticon " + kind);
  root.appendChild(icon);
  root.appendChild(mk("div", message, "tmsg"));

  const item: LiveToast = { root, timer: null, remaining: TOAST_TTL[kind], startedAt: 0 };

  const close = mk("button", "✕", "tclose");
  close.type = "button";
  close.title = "关闭";
  close.setAttribute("aria-label", "关闭提示");
  close.addEventListener("click", () => dropToast(item));
  root.appendChild(close);

  root.addEventListener("mouseenter", () => pauseTimer(item));
  root.addEventListener("mouseleave", () => armTimer(item));

  ensureLayer().appendChild(root);
  living.push(item);
  while (living.length > TOAST_MAX) dropToast(living[0]);
  armTimer(item);
}

// ================================================================
// confirmBox —— window.confirm 的自绘替代（异步）
// ================================================================

export interface ConfirmOptions {
  /** 标题，默认「请确认」 */
  title?: string;
  /** 危险操作：确认按钮变红 */
  danger?: boolean;
  /** 确认按钮文案，默认「确定」 */
  okText?: string;
  /** 取消按钮文案，默认「取消」 */
  cancelText?: string;
}

/** 打开的对话框栈：键盘只响应最上面那层，免得按一次 Esc 连关两层 */
const dialogStack: Array<() => void> = [];

/**
 * 自绘确认框，替代 window.confirm。
 *
 * 用法（唯一的变化就是宿主函数要 async 并 await）：
 *   if (!await confirmBox("删除模板「x」？", { danger: true })) return;
 *
 * 行为：Esc / 点遮罩 / 点取消 = false；Enter / 点确定 = true；
 *      打开时焦点落在确定按钮，关闭后还给打开前的元素；Tab 在两个按钮之间循环（不跑到页面上）。
 */
export function confirmBox(message: string, options: ConfirmOptions = {}): Promise<boolean> {
  const title = options.title ?? "请确认";
  const okText = options.okText ?? "确定";
  const cancelText = options.cancelText ?? "取消";

  const mask = mk("div", undefined, "dlgmask");
  const box = mk("div", undefined, "dlg");
  box.setAttribute("role", "dialog");
  box.setAttribute("aria-modal", "true");
  box.appendChild(mk("div", title, "dlgtitle"));
  box.appendChild(mk("div", message, "dlgmsg"));

  const buttons = mk("div", undefined, "dlgbtns");
  const cancel = mk("button", cancelText, "cancel");
  cancel.type = "button";
  const ok = mk("button", okText, "ok" + (options.danger === true ? " danger" : ""));
  ok.type = "button";
  buttons.appendChild(cancel);
  buttons.appendChild(ok);
  box.appendChild(buttons);
  mask.appendChild(box);

  return new Promise<boolean>((resolve) => {
    let closed = false;
    const previous = document.activeElement;

    /** 收摊：解绑、出栈、摘 DOM、还原焦点，最后兑现 promise（幂等） */
    function finish(result: boolean): void {
      if (closed) return;
      closed = true;

      document.removeEventListener("keydown", onKeyDown, true);
      const index = dialogStack.indexOf(onCancel);
      if (index >= 0) dialogStack.splice(index, 1);
      mask.remove();

      // 焦点还原：打开前的元素还在就还回去，否则退回 body（至少不丢焦点）
      const target = previous instanceof HTMLElement && previous.isConnected ? previous : document.body;
      target.focus();

      resolve(result);
    }

    /** Esc 和遮罩点击都走这里 */
    function onCancel(): void {
      finish(false);
    }

    /** 全局键盘：Esc 取消、Enter 确认、Tab 在框内循环 */
    function onKeyDown(event: KeyboardEvent): void {
      if (dialogStack[dialogStack.length - 1] !== onCancel) return;

      if (event.key === "Escape") {
        event.preventDefault();
        onCancel();
        return;
      }

      if (event.key === "Enter") {
        // 焦点已经在某个按钮上：浏览器会自己补一个 click，别在这里重复触发
        if (event.target instanceof HTMLElement && event.target.tagName === "BUTTON") return;
        event.preventDefault();
        finish(true);
        return;
      }

      if (event.key === "Tab") {
        const nodes = [cancel, ok];   // 顺序跟 DOM 一致
        const current = nodes.indexOf(event.target as HTMLButtonElement);
        if (current < 0) {
          event.preventDefault();
          cancel.focus();
          return;
        }
        const next = (current + (event.shiftKey ? -1 : 1) + nodes.length) % nodes.length;
        event.preventDefault();
        nodes[next].focus();
      }
    }

    cancel.addEventListener("click", () => finish(false));
    ok.addEventListener("click", () => finish(true));
    mask.addEventListener("click", (event) => {
      if (event.target === mask) finish(false);   // 只有点在遮罩上才算取消，点对话框里不算
    });
    document.addEventListener("keydown", onKeyDown, true);

    document.body.appendChild(mask);
    dialogStack.push(onCancel);
    ok.focus();
  });
}
