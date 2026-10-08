<script lang="ts" setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import SvgIcon from '@/components/SvgIcon.vue';
import { createReplica } from '@/utils/replay';
import type { ApplyOutcome, Replica } from '@/utils/replay';
import { onBridgeEvent, request } from '@/utils/shareBridge';
import { loadSettings } from '@/utils/settings';
import type { DomPatch, DomSnapshot, ThemePreference } from '@/types';

/**
 * 观看页 —— 扩展自己的一个普通 HTML 页面：`chrome-extension://<id>/viewer.html?room=…`。
 *
 * 为什么不打开被共享的那个 URL：观众要看到的是"共享方此刻的 DOM"，
 * 而不是"自己访问那个站点拿到的 DOM"（后者会因为登录态、AB、个性化而和基线对不上），
 * 而且很多页面在跨域新标签页里根本复现不出来。扩展自己的页面一定能打开，
 * 内容由**带 id 的基线树 + 增量**重建：
 *
 *   1. 进页面先向 background 要一份基线（background 先去取服务端存的那份当过渡，
 *      同时请房主现交一份新的）；
 *   2. 基线交给 rrweb 的 `rebuild()` 重建进一个 sandbox iframe；
 *   3. 之后的每条 patch 按**节点 id** 打到那棵树上。
 *
 * ## 版本闸门才是这个页面的重点
 *
 * 每条 patch 都声明它基于哪个版本。**不等于本地版本就整条丢掉**，绝不硬应用 ——
 * 在错的基线上落带 id 的操作，改坏的是别的位置，而且不会报错。丢掉之后去要一份新基线，
 * 把"静默错位"换成"一次明确的重建"。
 *
 * 基线在本地一律是**整个重置**（换镜像、换构建缓存、重画），不是往上叠增量：
 * 旧基线 + 增量 ≠ 新页面。
 *
 * iframe 由 rrweb 的 `createSandboxedIframe()` 建（它把 sandbox **强制**设成恰好
 * `allow-same-origin`）。没有 `allow-scripts` ⇒ 基线里的脚本与内联事件处理器一律不执行；
 * 有 `allow-same-origin` ⇒ 本页拿得到 contentDocument，才谈得上往上叠增量。
 */

const roomCode = new URLSearchParams(location.search).get('room')?.trim().toUpperCase() ?? '';

/** rrweb 建出来的 iframe 会被塞进这里。 */
const stage = ref<HTMLDivElement | null>(null);
const title = ref('观看共享');
const url = ref('');
const error = ref<string | null>(null);
/** 有没有拿到过基线。没有就只能等 —— 增量无处可叠。 */
const hasBaseline = ref(false);
const truncated = ref(false);
/** 本地已经对齐到的版本。新基线把它直接推过去，patch 只有 base 相等才认。 */
const appliedVersion = ref(0);
const receivedOps = ref(0);
const missedOps = ref(0);
/** 正在重新对齐：这期间收到的 patch 一律不应用 */
const resyncing = ref(false);

let replica: Replica | null = null;
let stopBridge: (() => void) | null = null;
let resyncTimer: number | null = null;

const statusText = computed(() => {
  if (error.value) return '出错';
  if (resyncing.value) return '重新同步中';
  if (!hasBaseline.value) return '等待共享方';
  if (truncated.value) return '快照不完整';
  if (missedOps.value) return `跳过 ${missedOps.value} 处`;
  return receivedOps.value ? `已同步 ${receivedOps.value} 处` : '已同步';
});

// ------------------------------------------------------------------ 渲染

/** 把任意异常转成一句能显示的短话。 */
function describe(cause: unknown): string {
  if (cause instanceof Error) return cause.message;
  return typeof cause === 'string' ? cause : String(cause);
}

/**
 * 换基线：**整个重置**，不是往上叠。
 *
 * 版本更旧的基线要丢掉：房主连着交两份时它们可能乱序到达，用旧的那份会把已经应用过的
 * 增量一起抹掉，页面看起来就"退回去了"。
 */
function reset(dom: DomSnapshot) {
  if (!replica) {
    error.value = '观看页没有拿到承载重建结果的容器';
    return;
  }

  if (hasBaseline.value && dom.version < appliedVersion.value) return;

  try {
    replica.reset(dom);
  } catch (cause) {
    // 建不出来**必须说出来**。
    //
    // 以前这里是静默的：下面那几行赋值根本没机会执行，于是 `hasBaseline` 一直是 false，
    // 页面永远停在"正在等待共享方交出页面…"，真正的异常只在控制台里闪一下。
    // 跟之前服务端把断开原因丢掉是同一类毛病 —— 失败可以，但必须看得见。
    error.value = `页面重建失败：${describe(cause)}`;
    if (import.meta.env.DEV) console.error('[kiriyama-share] 重建基线失败', cause);
    return;
  }

  appliedVersion.value = dom.version;
  truncated.value = dom.truncated;
  url.value = dom.url;
  title.value = dom.title || '观看共享';
  hasBaseline.value = true;
  error.value = null;
  missedOps.value = 0;
  resyncing.value = false;

  if (resyncTimer !== null) {
    window.clearTimeout(resyncTimer);
    resyncTimer = null;
  }
}

/**
 * 应用一条增量。
 *
 * 三种结果，处理方式完全不同 —— 这正是"回执 / 判定"不能用 bool 的原因：
 *   - 版本对不上：中间缺了东西，去要新基线；
 *   - 有操作落空：两端已经分叉，同样去要新基线；
 *   - 都对上：版本推进，收工。
 */
function applyPatch(patch: DomPatch) {
  if (!replica || !hasBaseline.value || resyncing.value) return;

  let outcome: ApplyOutcome;
  try {
    outcome = replica.apply(patch);
  } catch (cause) {
    // 落操作也可能炸（比如服务端给来一个非法属性名）。同 `reset`：炸了要说出来，
    // 不能让一条坏操作把整个观看页变成一句无声的等待。
    error.value = `应用增量失败：${describe(cause)}`;
    if (import.meta.env.DEV) console.error('[kiriyama-share] 应用增量失败', cause, patch);
    return;
  }

  receivedOps.value += patch.ops.length;

  if (outcome.stale) {
    requestResync(`版本对不上（本地 ${outcome.version}，收到 base ${patch.baseVersion}）`);
    return;
  }

  if (outcome.missed > 0) {
    missedOps.value += outcome.missed;

    // 基线本来就是残缺的（服务端按节点数 / 字符预算裁过），落空是预期内的：
    // 再要一份还是同样残缺，只会来回打转。如实提示，不重试。
    if (!replica.isTruncated()) requestResync(`${outcome.missed} 条操作落空`);
    return;
  }

  appliedVersion.value = outcome.version;
}

/** 请房主重新交一份基线。同一时间只发一次，避免一次抖动打出一串请求。 */
function requestResync(reason: string) {
  if (resyncing.value || !roomCode) return;
  resyncing.value = true;

  if (import.meta.env.DEV) {
    console.debug(`[kiriyama-share] 观看页重新对齐：${reason}`);
  }

  void request({ name: 'request-baseline', roomCode });

  // 兜底：房主可能已经不在了（房间实际已死），别让"重新同步中"永远转下去
  if (resyncTimer !== null) window.clearTimeout(resyncTimer);
  resyncTimer = window.setTimeout(() => {
    resyncing.value = false;
    resyncTimer = null;
  }, 8000);
}

// ------------------------------------------------------------------ 载入

async function load() {
  if (!roomCode) {
    error.value = '地址里没有房间号';
    return;
  }

  error.value = null;
  const result = await request({ name: 'viewer-load', roomCode });

  // 有现成的基线就先画上（首屏不用等一个来回）；background 同时已经去请房主交新的了，
  // 到了会以 dom-snapshot 事件推过来再重置一次。
  if (result.dom) {
    reset(result.dom);
    return;
  }

  if (!result.ok && result.error) error.value = result.error;
}

async function reload() {
  resyncing.value = false;
  await load();
}

// ------------------------------------------------------------------ 主题

const darkQuery = window.matchMedia('(prefers-color-scheme: dark)');
let currentTheme: ThemePreference = 'auto';

function applyThemeClass() {
  const dark = currentTheme === 'dark' || (currentTheme === 'auto' && darkQuery.matches);
  document.documentElement.classList.toggle('dark', dark);
}

// ------------------------------------------------------------------ 生命周期

onMounted(async () => {
  // 整块包起来：这里面任何一步抛出去，下面那个 `onBridgeEvent` 就不会注册，
  // 于是"基线到了也没人接" —— 页面永远停在等待态，而且完全看不出是为什么。
  try {
    const settings = await loadSettings();
    currentTheme = settings.theme;
    applyThemeClass();
    darkQuery.addEventListener('change', applyThemeClass);

    // iframe 由 rrweb 建，所以要在容器已经在文档里之后再建 ——
    // createSandboxedIframe 会检查 root.isConnected，没连上直接抛。
    if (!stage.value) throw new Error('页面上没有承载重建结果的容器');
    replica = createReplica({ root: stage.value });

    stopBridge = onBridgeEvent((event) => {
      if (event.name === 'patch') {
        if (event.roomCode !== roomCode) return;
        applyPatch(event.patch);
        return;
      }

      // 房主换了页面（或这是我们第一份基线到货）：整个重画
      if (event.name === 'dom-snapshot' && event.roomCode === roomCode) {
        reset(event.dom);
      }
    });

    await load();
  } catch (cause) {
    error.value = `观看页初始化失败：${describe(cause)}`;
    if (import.meta.env.DEV) console.error('[kiriyama-share] 观看页初始化失败', cause);
  }
});

onBeforeUnmount(() => {
  stopBridge?.();
  replica?.destroy();
  if (resyncTimer !== null) window.clearTimeout(resyncTimer);
  darkQuery.removeEventListener('change', applyThemeClass);
});
</script>

<template>
  <div class="ksw-root ksw-root--page flex h-screen flex-col">
    <header
      class="flex h-[46px] shrink-0 items-center gap-2 border-b border-[var(--ui-line)] px-3"
    >
      <span
        class="grid h-[26px] w-[26px] shrink-0 place-items-center rounded-[8px] bg-[var(--ksw-accent-soft)] text-[var(--ui-text-muted)]"
      >
        <SvgIcon name="globe" :size="14" />
      </span>

      <span class="min-w-0 flex-1">
        <span class="block truncate text-[12px] font-semibold text-[var(--ui-text-strong)]">
          {{ title }}
        </span>
        <span class="block truncate text-[10.5px] text-[var(--ui-text-muted)]">
          {{ url || roomCode || '没有房间号' }}
        </span>
      </span>

      <span class="ksw-chip shrink-0">{{ statusText }}</span>

      <a
        v-if="url"
        class="ksw-btn h-[26px] shrink-0 px-2 text-[11px] no-underline"
        :href="url"
        target="_blank"
        rel="noreferrer"
      >
        原站
      </a>

      <button
        type="button"
        class="ksw-btn ksw-btn--ghost h-[26px] shrink-0 px-2 text-[11px]"
        @click="reload"
      >
        <SvgIcon name="refresh-cw" :size="12" />
        重新同步
      </button>
    </header>

    <p
      v-if="error"
      class="shrink-0 bg-[var(--ui-alert-bg)] px-3 py-2 text-[11.5px] text-[var(--ui-wrong)]"
    >
      {{ error }}
    </p>
    <p
      v-else-if="truncated"
      class="shrink-0 bg-[var(--ui-alert-bg)] px-3 py-2 text-[11px] text-[var(--ui-text-muted)]"
    >
      页面超过基线上限，只复现了前面一部分。
    </p>

    <!-- 容器常驻（不能 v-if）：rrweb 建的 iframe 要挂在它下面，
         而且重建目标必须一直是"连在文档上"的元素。等待态是压在上面的一个浮层。 -->
    <div ref="stage" class="ksw-viewer-stage relative min-h-0 flex-1">
      <div v-if="!hasBaseline" class="absolute inset-0 grid place-items-center text-center">
        <span class="flex flex-col items-center gap-2">
          <SvgIcon
            name="loader-circle"
            :size="22"
            :spin="!error"
            class="text-[var(--ui-text-muted)]"
          />
          <span class="text-[11.5px] text-[var(--ui-text-muted)]">
            {{ error ? '拿不到页面内容' : '正在等待共享方交出页面…' }}
          </span>
        </span>
      </div>
    </div>
  </div>
</template>

<!-- 不是 scoped：iframe 是 rrweb 在运行时建出来的，Vue 的 scoped 属性打不到它身上 -->
<style>
.ksw-viewer-stage > iframe {
  display: block;
  width: 100%;
  height: 100%;
  border: 0;
  background: #fff;
}
</style>
