import { reactive } from 'vue';
import { onBridgeEvent, request } from '@/utils/shareBridge';
import { DEFAULT_SETTINGS, loadSettings, saveSettings } from '@/utils/settings';
import type {
  ActivityEntry,
  BridgeResult,
  ConnectionState,
  DomOp,
  ObserverStats,
  PageInfo,
  Peer,
  RuntimeMessage,
  ShareEventDto,
  SharedRoom,
  WidgetSettings,
} from '@/types';

/**
 * 组件的状态层（单例，每个 JS 上下文各一份）。
 *
 * 不预置任何假数据：房间列表、协作者、事件流一律只由真实来源驱动。
 * 真正的连接由 background 持有（只有它常驻），这里通过桥下发命令、接收推送，
 * 把服务端的快照映射成组件直接能读的状态。
 *
 * 设置的默认值在 `utils/settings.ts`（background 也要读，不能从这个文件引）。
 */

/** 组件展示用的 3 个 Tab */
export const TABS = [
  { value: 'share', label: '共享', icon: 'share-2' },
  { value: 'subscribe', label: '订阅', icon: 'inbox' },
  { value: 'settings', label: '设置', icon: 'settings' },
] as const;

let activitySeq = 0;
let initialised = false;
/** 事件流里记录 DOM 变更的最小间隔，避免刷屏 */
let lastMutationNoteAt = 0;
/** 收到变更的事件流限频 */
let lastReceiveNoteAt = 0;
let receivedBatches = 0;
let stopBridgeListener: (() => void) | null = null;
/**
 * 真正要采集的那个标签页。
 *
 * 在页面里（浮层）发起共享时是 null —— background 用发起方 Port 自带的 tabId 就够了。
 * 从 popup 发起时才有值：popup 自己不是标签页，必须把目标标签页显式带过去。
 */
let targetTabId: number | null = null;

const state = reactive({
  connection: 'offline' as ConnectionState,
  /** 连接失败的原因；给设置页显示，不然"连接失败"三个字没法排查 */
  connectionError: null as string | null,
  page: null as PageInfo | null,
  /** 正在确定"要共享哪个页面"。初值为 true：resolveTargetPage 会落定它 */
  resolvingPage: true,
  sharing: false,
  shareCode: '',
  peers: [] as Peer[],
  rooms: [] as SharedRoom[],
  activity: [] as ActivityEntry[],
  settings: { ...DEFAULT_SETTINGS } as WidgetSettings,
  loadingRooms: false,
  /** 采集器统计，由 content script 回填 */
  observer: {
    running: false,
    captured: 0,
    batches: 0,
    dropped: 0,
    baselines: 0,
    lastAt: null,
  } as ObserverStats,
  /** 本次共享已同步的操作条数 */
  mutationCount: 0,
  /** 订阅侧已收到的操作条数 */
  receivedCount: 0,
});

function pushActivity(kind: ActivityEntry['kind'], text: string, at = Date.now()) {
  state.activity.unshift({ id: `a${(activitySeq += 1)}`, kind, text, at });
  state.activity.splice(12);
}

/** 当前上下文是不是一个真的网页（popup 之类是扩展自己的页面，不能当共享目标） */
function isPageContext(): boolean {
  return /^https?:$/.test(location.protocol);
}

/**
 * 读取一次页面元信息，共享的目标就是它。
 * 不是网页上下文（工具栏弹窗、预览页）时返回 null —— 宁可显示"没有页面"，
 * 也不能把扩展自己的地址当成用户的页面发出去。
 */
export function capturePage(): PageInfo | null {
  if (!isPageContext()) return null;

  const url = location.href;
  let favicon: string | null = null;
  const link = document.querySelector<HTMLLinkElement>(
    'link[rel~="icon"], link[rel="shortcut icon"]',
  );
  if (link?.href) favicon = link.href;

  return {
    title: document.title || location.hostname,
    url,
    host: location.hostname,
    favicon,
  };
}

/**
 * 确定"要共享哪个页面"，并把结果落到 state.page。
 *
 * 两种上下文差别很大，这也正是之前"检测不到页面"的原因：
 *   - 在页面里（浮层）：直接读自己的 location / DOM 就是答案；
 *   - 在 popup 里：`capturePage()` 那条 `location.protocol` 判断必然为假，
 *     而 popup 又没有任何办法看到宿主 DOM —— 只能去问那个标签页的 content script。
 *
 * 问 content script 是零权限的：popup 拿不到 tab.url（没声明 tabs 权限），
 * 但 content script 本来就有完整 DOM 访问权，`tabs.sendMessage` 也不需要额外权限。
 */
async function resolveTargetPage(): Promise<PageInfo | null> {
  state.resolvingPage = true;

  try {
    if (isPageContext()) {
      targetTabId = null;
      state.page = capturePage();
      return state.page;
    }

    const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
    if (!tab?.id) {
      targetTabId = null;
      state.page = null;
      return null;
    }

    const info = (await browser.tabs.sendMessage(tab.id, {
      type: 'ksw:get-page-info',
    } satisfies RuntimeMessage)) as PageInfo | null;

    targetTabId = info ? tab.id : null;
    state.page = info ?? null;
    return state.page;
  } catch {
    // chrome:// 、扩展商店、PDF 阅读器这类页面根本没有 content script，
    // sendMessage 会直接抛。这不是错误，就是"这个页面共享不了"。
    targetTabId = null;
    state.page = null;
    return null;
  } finally {
    state.resolvingPage = false;
  }
}

export function formatRelative(timestamp: number): string {
  const diff = Date.now() - timestamp;
  if (diff < 60_000) return '刚刚';
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)} 分钟前`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)} 小时前`;
  return `${Math.floor(diff / 86_400_000)} 天前`;
}

/** 服务端的结构化事件 → 文案。服务端只给 kind，文案由客户端组织。 */
function describeEvent(event: ShareEventDto): string {
  const who = event.peer?.name ?? '有人';
  switch (event.kind) {
    case 'share':
      return `${who} 开始共享`;
    case 'navigate':
      // 房间不关，只是共享目标换了页面
      return `共享的页面跳转到了 ${event.url ?? '新地址'}`;
    case 'join':
      return `${who} 订阅了房间 ${event.roomCode}`;
    case 'leave':
      return `${who} 离开了房间 ${event.roomCode}`;
    case 'system':
      return `房间 ${event.roomCode} 已关闭`;
    default:
      return `房间 ${event.roomCode} 状态变化`;
  }
}

/** 把服务端快照落到组件状态上 */
function applySnapshot(result: BridgeResult) {
  const { snapshot } = result;

  state.connection = snapshot.connection;
  state.connectionError = snapshot.error;
  state.peers = snapshot.peers;
  state.rooms = snapshot.rooms;
  state.shareCode = snapshot.shareCode;
  // "正在共享"由服务端给的房间号驱动：共享码没回来就不算在共享。
  state.sharing = Boolean(snapshot.shareCode);
}

/** 下发命令并把返回的快照铺开；失败原因写进 connectionError 之外的提示 */
async function send(command: Parameters<typeof request>[0]): Promise<BridgeResult> {
  const result = await request(command);
  applySnapshot(result);
  return result;
}

function onBridgeMessage(event: import('@/types').BridgeEvent) {
  switch (event.name) {
    case 'snapshot':
      applySnapshot({ ok: true, error: null, snapshot: event.snapshot });
      break;

    case 'share-event':
      pushActivity(event.event.kind as ActivityEntry['kind'], describeEvent(event.event), event.event.at);
      break;

    case 'patch': {
      state.receivedCount += event.patch.ops.length;
      receivedBatches += 1;

      // 限频：动画一跑就是每秒十几批，事件流扛不住
      const now = Date.now();
      if (now - lastReceiveNoteAt < 4000) return;
      lastReceiveNoteAt = now;
      pushActivity(
        'mutation',
        `收到 ${event.patch.ops.length} 条操作（第 ${receivedBatches} 批，累计 ${state.receivedCount}）`,
      );
      break;
    }

    default:
      break;
  }
}

async function loadSettingsIntoState() {
  Object.assign(state.settings, await loadSettings());
}

async function updateSettings(patch: Partial<WidgetSettings>) {
  Object.assign(state.settings, patch);
  await saveSettings({ ...state.settings });

  // 换了服务器地址就得重新连，否则会一直连着旧的
  if (patch.serverUrl !== undefined) {
    await send({ name: 'disconnect' });
    await connect();
  }
}

/** 拉取服务器上的房间列表 */
async function refreshRooms() {
  state.loadingRooms = true;
  try {
    await send({ name: 'list-rooms' });
  } finally {
    state.loadingRooms = false;
  }
}

/** 建立连接（幂等：已经连上就直接返回） */
async function connect(): Promise<boolean> {
  const result = await send({ name: 'connect' });
  return result.ok;
}

async function disconnect() {
  await send({ name: 'disconnect' });
  state.peers = [];
}

/**
 * 开始共享当前页面。
 * 共享码、观看者列表全部等服务端回执，客户端不自己编。
 */
async function startSharing() {
  // 用已经确定好的目标；还没定过（popup 刚打开就点了）就现查一次
  const info = state.page ?? (await resolveTargetPage());
  if (!info) return;

  state.mutationCount = 0;
  lastMutationNoteAt = 0;

  const result = await send({
    name: 'start-sharing',
    request: info,
    // 浮层里发起时是 null —— background 用发起方 Port 自带的 tabId 就够了。
    // 从 popup 发起必须带上，否则 background 拿到的 tabId 是 null，DOM 批次会被全部挡掉。
    tabId: targetTabId ?? undefined,
  });
  if (result.ok) pushActivity('share', `开始共享「${info.title}」`);
}

async function stopSharing() {
  if (!state.sharing) return;
  await send({ name: 'stop-sharing' });
}

function setObserverStats(next: ObserverStats) {
  state.observer = next;
}

/** 采集端刚建立了一份基线（初次共享，或服务端拒收之后重新对齐）。 */
function noteBaseline() {
  if (!state.sharing) return;
  pushActivity('system', '已建立页面基线');
}

/**
 * 采集器每产出一批操作就会调这里。
 * 只有正在共享时才计入 —— 没共享的时候采集器照样跑（UI 要显示统计），
 * 但不该往事件流里灌数据。
 */
function noteOps(ops: DomOp[]) {
  if (!state.sharing) return;
  state.mutationCount += ops.length;

  // 事件流限频：最多每 4 秒记一条，否则动画一跑就刷屏
  const now = Date.now();
  if (now - lastMutationNoteAt < 4000) return;
  lastMutationNoteAt = now;
  pushActivity('mutation', `同步了 ${ops.length} 条操作（累计 ${state.mutationCount}）`);
}

/**
 * 开始观看一个房间：订阅它，并把**扩展自己的观看页**在新标签页里打开。
 *
 * 观看页的地址是 `chrome-extension://<id>/viewer.html?room=…`，它拿整页快照当基线、
 * 再往上叠增量地重建页面（见 entrypoints/viewer）。之所以不是"打开被共享的那个 URL"：
 * 真实站点的页面不一定能用新标签页复现，而且观众要看到的是共享方此刻的 DOM，
 * 不是自己访问那个站点拿到的 DOM。
 */
async function watchRoom(roomId: string) {
  const room = state.rooms.find((item) => item.id === roomId);
  if (!room) return;

  const result = await send({ name: 'watch-room', roomCode: room.id });
  if (!result.ok && result.error) pushActivity('system', result.error);
}

/**
 * 订阅 / 取消订阅。结果以服务端返回的 subscribed 为准，不做本地乐观翻转。
 *
 * 订阅成功会**顺带把观看页开出来**：只订阅是什么都看不到的，
 * 用户点开开关的意图就是"我要看这个房间"。
 */
async function toggleSubscription(roomId: string) {
  const room = state.rooms.find((item) => item.id === roomId);
  if (!room) return;

  if (room.subscribed) {
    await send({ name: 'unsubscribe', roomCode: roomId });
    return;
  }

  const result = await send({ name: 'subscribe', roomCode: roomId });
  if (!result.ok) {
    if (result.error) pushActivity('system', result.error);
    return;
  }

  await watchRoom(roomId);
}

async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

/**
 * 清空本地记录。
 *
 * 只清本地的：事件流与收到的变更计数。房间列表在服务端，清它没有意义
 * （下一次推送就回来了），所以不假装能清掉。
 */
function clearCache() {
  state.activity = [];
  state.receivedCount = 0;
  receivedBatches = 0;
}

/** 幂等初始化：读设置 → 连服务端 → 订上桥的推送 */
async function initSession() {
  if (initialised) return;
  initialised = true;

  await loadSettingsIntoState();

  if (!stopBridgeListener) {
    stopBridgeListener = onBridgeEvent(onBridgeMessage);
  }

  await connect();
  // 落在连接之后：这样"重新连接"也会顺带把页面信息刷新一次
  await resolveTargetPage();
  await refreshRooms();
}

export function useShareSession() {
  return {
    state,
    initSession,
    capturePage,
    /** 确定"要共享哪个页面"。popup 里靠它去问当前标签页的 content script */
    resolveTargetPage,
    refreshRooms,
    connect,
    disconnect,
    startSharing,
    stopSharing,
    toggleSubscription,
    /** 打开观看页（订阅成功会自动调它；标签页被关掉后也可以手动再开） */
    watchRoom,
    updateSettings,
    copyText,
    clearCache,
    setObserverStats,
    noteOps,
    noteBaseline,
    formatRelative,
    /** 当前上下文能不能作为共享目标（popup 不行） */
    isPageContext,
  };
}
