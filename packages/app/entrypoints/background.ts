import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';

import { BRIDGE_PORT, EMPTY_SNAPSHOT, isRuntimeMessage, PatchRejectReasons } from '@/types';
import type {
  BaselinePayload,
  BridgeCommand,
  BridgeEvent,
  BridgeResult,
  BridgeWireMessage,
  DomOp,
  DomPatch,
  DomSnapshot,
  PageInfo,
  PatchAck,
  Peer,
  RuntimeMessage,
  ShareEventDto,
  SharePeerDto,
  ShareServerSnapshot,
  SharedRoom,
} from '@/types';
import { loadSettings } from '@/utils/settings';

/**
 * background —— 唯一持有 SignalR 连接的地方。
 *
 * 为什么连接必须放在这里（而不是 content script 或 popup）：
 *   - content script 发出的请求走的是**宿主页面的 origin**，拿不到扩展的身份，
 *     SignalR 的 /share/negotiate 会被目标站点自己的 CORS 挡掉；
 *   - popup 一关就没了，连接活不过一次点击。
 * 所以只有常驻的 background 能当这个连接的家。UI 侧（页面里的卡片、工具栏弹窗）
 * 通过长连接 Port 接进来下发命令、接收推送（见 utils/shareBridge.ts）。
 *
 * 两条入站通道，各管一摊：
 *   1. Port：低频、有状态、要回执的操作（建房 / 订阅 / 拉列表 / 连接）。
 *   2. runtime.sendMessage：高频、无回执的**单向上报** —— DOM 批次，以及页面自报的
 *      `ksw:page-changed`、`ksw:tab-foreground`。走这里是为了不让每条批次
 *      都占一个 Port 往返，也让端口偶尔断开时批次不会把队列堵住。
 *
 * 浏览器级事件决定"共享目标是谁"：`tabs.onActivated`（同窗口换标签页）与页面自报的
 * 前台状态（换窗口）把目标切到用户正在看的标签页；`tabs.onRemoved` 只在目标标签页
 * 被关掉时把那条记录清空 —— **房间不关**，交给下一个标签页接手。
 *
 * 还有一件只有这里做得了的事：**房间版本**。客户端推的每条 patch 都要声明"基于哪个版本"，
 * 而版本号是服务端在回执里给的 —— 采集端根本看不见回执。所以 base 由这里贴，
 * 并且所有上行都排进同一条**串行队列**：patch 必须按采集顺序到达服务端，
 * 基线必须排在它前面那些 patch 之后（否则服务端给它盖的版本号是旧的）。
 */
export default defineBackground(() => {
  /** 服务端状态快照。UI 拿到的是整份，UI 侧不做增量合并。 */
  const snapshot: ShareServerSnapshot = { ...EMPTY_SNAPSHOT, peers: [], rooms: [] };

  let hub: HubConnection | null = null;
  /** 当前这条连接连的是哪个地址，用来判断"设置改了要不要重连" */
  let hubUrl = '';
  let connectPromise: Promise<BridgeResult> | null = null;

  /** 正在共享的那个标签页。DOM 批次只认它，避免多标签页把变更串到一个房间里。 */
  let sharingTabId: number | null = null;

  /**
   * 当前房间已经对齐到的版本。
   *
   * 这是"共同基线"在本地的落脚点：每条 patch 都声明它基于这个版本，服务端认可之后
   * 把它推进到回执给的新版本。**它不推进，后续 patch 就会被服务端当成过期拒收** ——
   * 那正是我们要的：宁可暴露出来重新对齐，也不在一棵已经分叉的树上继续叠。
   */
  let roomVersion = 0;

  /** 房间号 → 我在这个房间里的成员 id。不认领自己就没法把"我"从观看人数里排掉。 */
  const selfIds = new Map<string, string>();

  /** 本次会话已转发的批次数，开发期观察链路用 */
  let published = 0;

  /** 一条已接上来的 UI 通道 */
  interface BridgeChannel {
    port: Browser.runtime.Port;
    /** 发起这条通道的标签页；popup 没有标签页时为 null */
    tabId: number | null;
    /** 它订阅过的房间。只有这些房间的变更会推给它 —— 扩展是全站注入的，不筛会向所有标签页扇出。 */
    rooms: Set<string>;
    /**
     * 它是不是某个房间的"观看页"（viewer.html）。
     *
     * 记这个只为一件事：观看页关掉时要退订那个房间。房间的"几人观看"是按订阅数算的，
     * 不退订就会留下一个永远算在人数里的幽灵观众。
     */
    viewingRoom: string | null;
  }
  const channels = new Set<BridgeChannel>();

  // ---------------------------------------------------------------- 输出

  function safePost(port: Browser.runtime.Port, message: BridgeWireMessage) {
    try {
      port.postMessage(message);
    } catch {
      /* 端口正在关闭，忽略 */
    }
  }

  function broadcast(event: BridgeEvent) {
    for (const channel of channels) {
      safePost(channel.port, { kind: 'event', event });
    }
  }

  function broadcastSnapshot() {
    // postMessage 会做结构化克隆，所以直接给引用是安全的
    broadcast({ name: 'snapshot', snapshot });
  }

  function reply(ok: boolean, error: string | null = null): BridgeResult {
    return { ok, error, snapshot };
  }

  /**
   * 一次操作失败（比如订阅的房间号不存在）。
   *
   * 刻意**不**写进 snapshot.error：那是"连接坏了"的专用位置，
   * 拿来装这种业务失败会让设置页一直红着，而连接其实是好的。
   * 失败原因跟着 BridgeResult 回给发起方，由它决定怎么展示。
   */
  function fail(error: string): BridgeResult {
    return reply(false, error);
  }

  // ---------------------------------------------------------------- 小工具

  function readableError(error: unknown): string {
    const raw =
      error instanceof Error ? error.message : typeof error === 'string' ? error : '';
    return raw.trim() || '连接失败：服务端不可达，或地址不是 Hub 地址';
  }

  const isOnline = () => hub?.state === HubConnectionState.Connected;

  /** 请求方与已建立连接的地址不一致时也要重连，所以拿当前设置比一次 */
  async function currentUrl(): Promise<string> {
    const settings = await loadSettings();
    return settings.serverUrl?.trim() ?? '';
  }

  /** 房间号统一大写后比较：服务端大小写不敏感，客户端这边别自己造出两份 */
  const normalizeCode = (code: string) => code.trim().toUpperCase();

  const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

  // ---------------------------------------------------------------- 连接

  async function connectHub(): Promise<BridgeResult> {
    const settings = await loadSettings();
    const url = settings.serverUrl?.trim() ?? '';

    if (!url) {
      snapshot.connection = 'error';
      snapshot.error = '还没有填写服务器地址';
      broadcastSnapshot();
      return reply(false, snapshot.error);
    }

    if (isOnline() && hubUrl === url) {
      return reply(true);
    }

    await disposeHub();

    snapshot.connection = 'connecting';
    snapshot.error = null;
    broadcastSnapshot();

    const builder = new HubConnectionBuilder()
      .withUrl(url)
      .configureLogging(import.meta.env.DEV ? LogLevel.Information : LogLevel.Warning);

    // 自动重连按设置走：关掉的话断线就停在 offline，交给用户手动重连。
    // 注意这是建连时读的，改了设置要重新连接才生效。
    if (settings.autoReconnect) {
      builder.withAutomaticReconnect();
    }

    const connection = builder.build();
    registerServerHandlers(connection);

    hub = connection;
    hubUrl = url;

    try {
      await connection.start();
      snapshot.connection = 'online';
      snapshot.error = null;
      broadcastSnapshot();

      // 连上后服务端会推一条 RoomsChanged，列表由那次推送驱动；
      // 但万一那条丢了，这里补一次，让 UI 立刻有内容。
      await refreshRooms();
      return reply(true);
    } catch (error) {
      hub = null;
      hubUrl = '';
      snapshot.connection = 'error';
      snapshot.error = readableError(error);
      broadcastSnapshot();

      // 没连上的连接对象不能复用，直接丢掉
      try {
        await connection.stop();
      } catch {
        /* 本来就起不来 */
      }

      return reply(false, snapshot.error);
    }
  }

  async function disposeHub(): Promise<void> {
    const current = hub;
    hub = null;
    hubUrl = '';
    if (!current) return;

    try {
      await current.stop();
    } catch {
      /* 已经断了 */
    }
  }

  /** 幂等连接：已经有连接、或正在连，就不重复发起 */
  function ensureConnected(): Promise<BridgeResult> {
    if (isOnline() || hub?.state === HubConnectionState.Connecting) {
      return Promise.resolve(reply(true));
    }

    if (!connectPromise) {
      connectPromise = connectHub().finally(() => {
        connectPromise = null;
      });
    }

    return connectPromise;
  }

  function resetRoomState() {
    snapshot.shareCode = '';
    snapshot.peers = [];
    selfIds.clear();
    sharingTabId = null;
    // 版本与队列一起清：留着旧队列只会在新房间里用错的 base 推 patch
    roomVersion = 0;
    outbox = [];
  }

  // ---------------------------------------------------------------- 服务端推送

  function registerServerHandlers(connection: HubConnection) {
    // 房间列表变了。服务端只报版本号和数量 —— 列表本身要自己再拉一次，
    // 因为 subscribed 是按客户端各算各的，逐个推完整列表既浪费又不准。
    connection.on('RoomsChanged', () => {
      void refreshRooms();
    });

    // 房间成员变化。只关心自己开的那个房间：UI 的"几人观看"就是它。
    connection.on('PeersChanged', (roomCode: string, members: SharePeerDto[]) => {
      void handlePeersChanged(roomCode, members);
    });

    // 订阅的房间里来了一个新的 patch（基于共同基线的增量）。发起方不会收到这条。
    // 房间号由服务端盖章，所以这里不需要再补 —— 一个连接可能同时订着好几个房间。
    connection.on('ChangesReceived', (patch: DomPatch) => {
      broadcastPatch(patch);
    });

    // 房间生命周期事件：有人进 / 有人出 / 房间被关 / 房主换了页面。文案由 UI 组织，这里只转发结构。
    connection.on('ShareEventReceived', (event: ShareEventDto) => {
      broadcast({ name: 'share-event', event });
    });

    // 服务端替某个观众来要一份整页快照。这条只会发给房主，交不交得出来看我们这边的 DOM。
    connection.on('SnapshotRequested', (roomCode: string) => {
      void serveSnapshot(roomCode);
    });

    // 房主交上来新基线了：取回来推给正在看这个房间的观看页，让它整个重画。
    // 第二个参数是这份基线代表的版本，取回的那份自己带着它，这里只用来触发。
    connection.on('SnapshotReady', (roomCode: string, _version: number) => {
      void handleSnapshotReady(roomCode);
    });

    connection.onreconnecting(() => {
      snapshot.connection = 'connecting';
      broadcastSnapshot();
    });

    connection.onreconnected(async () => {
      // 注意：服务端的房间寿命等于房主的连接。断线重连后连接 id 变了，
      // 之前开的房间和订阅都不复存在，所以这里必须把房间状态清干净，
      // 不能留着上一个房间的共享码骗用户。
      snapshot.connection = 'online';
      snapshot.error = null;
      resetRoomState();
      broadcastSnapshot();
      await refreshRooms();
    });

    connection.onclose((error) => {
      // 只有"当前这条连接"的关闭才代表状态变化 ——
      // 主动 dispose 时 hub 已经换成别的（或 null）了，那次关闭不算。
      if (hub !== connection) return;

      hub = null;
      hubUrl = '';
      snapshot.connection = error ? 'error' : 'offline';
      snapshot.error = error ? readableError(error) : null;
      resetRoomState();
      broadcastSnapshot();
    });
  }

  async function handlePeersChanged(roomCode: string, members: SharePeerDto[]) {
    if (!roomCode || roomCode !== snapshot.shareCode) return;

    let selfId = selfIds.get(roomCode) ?? null;

    // 认领自己：首次拿到成员列表、或者重连后 id 变了，都去问一次。
    // 成员 id 是服务端随机的，客户端没有别的办法知道哪个是自己。
    if (!selfId || !members.some((member) => member.id === selfId)) {
      selfId = await whoAmI(roomCode);
      if (selfId) selfIds.set(roomCode, selfId);
    }

    snapshot.peers = members.map<Peer>((member) => ({
      ...member,
      isSelf: selfId !== null && member.id === selfId,
    }));

    broadcastSnapshot();
  }

  async function whoAmI(roomCode: string): Promise<string | null> {
    if (!isOnline()) return null;

    try {
      const self = await hub!.invoke<SharePeerDto | null>('WhoAmI', roomCode);
      return self?.id ?? null;
    } catch {
      return null;
    }
  }

  function broadcastPatch(patch: DomPatch) {
    if (!patch?.roomCode) return;

    const roomCode = normalizeCode(patch.roomCode);
    const event: BridgeEvent = { name: 'patch', roomCode, patch };

    for (const channel of channels) {
      if (!channel.rooms.has(roomCode)) continue;
      safePost(channel.port, { kind: 'event', event });
    }
  }

  // ---------------------------------------------------------------- 上行队列

  /** 一件要送到服务端的事。只有两种，而且**必须按发生顺序送**。 */
  type OutboxTask = { kind: 'ops'; ops: DomOp[]; url: string } | { kind: 'baseline' };

  interface TaskResult {
    ok: boolean;
    /** 失败之后要不要立刻重新建立基线对齐 */
    rebaseline?: boolean;
  }

  /**
   * 上行队列。**串行，不并发、不重排。**
   *
   * 为什么必须是队列而不是直接 invoke：每条 patch 都声明"基于哪个版本"，
   * 而新版本要等回执才知道。两条 patch 并发发出去，第二条带的就是过期的 base，
   * 会被服务端拒收 —— 明明只是想做两个动作，却被当成链路丢了东西。
   *
   * 而且基线要排在它**前面**那些 patch 之后：服务端给基线盖的是"此刻"的房间版本，
   * 它要抢在前面到，观众就会先拿到新基线、再收到落在旧基线之后的增量，白重画一次。
   */
  /**
   * 单条上行消息的字节预算，**必须**低于服务端的 `maximumReceiveMessageSize`。
   *
   * 超了不是"被拒绝"这么轻：SignalR 会直接 abort 掉这条连接，房主那边只拿到一条
   * HubException，而房间的寿命等于房主的连接 —— 表现出来就是"房间刚建好就没了"，
   * 而且客户端无从知道原因。
   *
   * E2E 实测（对 4 MiB 上限）：3.93 MB 收得下，4.42 MB 当场打死连接；
   * 服务端现在调到 8 MiB，这里跟着留出约 0.5 MB 余量。
   */
  const MAX_UPLINK_BYTES = 7_500_000;

  /**
   * 一条消息在线上占多少**字节**。
   *
   * 必须真的编码成 UTF-8 来量，不能用 `JSON.stringify(x).length` ——
   * 那是 UTF-16 码元数，中文一个字算 1，而线上跑的是 UTF-8 的 3 字节。
   * 服务端的上限是按字节算的，量错了就等于没量。
   */
  function uplinkBytes(value: unknown): number {
    try {
      return new TextEncoder().encode(JSON.stringify(value)).length;
    } catch {
      // 循环引用之类根本序列化不出来：量不出来就别放它过去
      return Number.POSITIVE_INFINITY;
    }
  }

  /**
   * 单条上行消息允许的最大 JSON 嵌套层数，**必须**不高于服务端的 `maxJsonDepth`。
   *
   * 和字节超限一样，这也不是"被拒绝"那么轻：服务端解析失败时 SignalR 直接 abort 连接，
   * 房主只拿到一条 "Connection closed with an error"，房间跟着连接一起消失。
   *
   * 节点树**每层 DOM 花两层 JSON**（`childNodes` 数组 + 节点对象），加上信封
   * （消息对象 → arguments 数组 → DTO → root，共 4 层），服务端旧配置的 64 层
   * 只够约 30 层 DOM 深 —— 真实页面随便就超。服务端已调到 512，这里留几层余量。
   */
  const MAX_UPLINK_DEPTH = 500;

  /**
   * 一条消息在线上有多少层 JSON 嵌套。
   *
   * 用显式栈迭代而不是递归：这棵树本来就深，递归法会在"量深度"这一步先把自己的
   * 调用栈打爆，把一个本来能明确报出来的失败变成更难查的错误。
   */
  function uplinkDepth(value: unknown): number {
    let max = 0;
    const nodes: unknown[] = [value];
    const depths: number[] = [1];

    while (nodes.length > 0) {
      const node = nodes.pop();
      const depth = depths.pop() ?? 1;

      if (depth > max) max = depth;
      // 已经确定超标，没必要把整棵树量完
      if (max > MAX_UPLINK_DEPTH) return max;

      if (node === null || typeof node !== 'object') continue;

      const next = depth + 1;
      if (Array.isArray(node)) {
        for (const item of node) {
          nodes.push(item);
          depths.push(next);
        }
      } else {
        for (const key of Object.keys(node)) {
          nodes.push((node as Record<string, unknown>)[key]);
          depths.push(next);
        }
      }
    }

    return max;
  }

  /**
   * 量一条消息会不会踩服务端的两个硬上限（字节 / JSON 深度）。
   * 返回 `null` 表示可以发；否则返回一句能直接进日志的原因。
   *
   * 两件事都得做，因为后果完全一样（连接被 abort、房间消失），
   * 而踩中哪一个取决于页面：长文页面撞字节上限，深嵌套页面撞深度上限。
   */
  function uplinkRejection(value: unknown): string | null {
    const bytes = uplinkBytes(value);
    if (bytes > MAX_UPLINK_BYTES) {
      return `${bytes} 字节，超过上限 ${MAX_UPLINK_BYTES}`;
    }

    const depth = uplinkDepth(value);
    if (depth > MAX_UPLINK_DEPTH) {
      return `JSON 嵌套 ${depth} 层，超过上限 ${MAX_UPLINK_DEPTH}`;
    }

    return null;
  }

  let outbox: OutboxTask[] = [];
  let draining = false;

  function enqueue(task: OutboxTask) {
    if (!snapshot.shareCode) return;
    outbox.push(task);
    void drainOutbox();
  }

  async function drainOutbox() {
    if (draining) return;
    draining = true;

    try {
      while (outbox.length) {
        const task = outbox[0];
        if (!task) break;

        const result = await runTask(task);

        if (result.ok) {
          outbox.shift();
          continue;
        }

        // 没成功：队列里剩下的一律作废 —— 它们全都基于同一个已经不作数的 base，
        // 继续发只会把别人的树也带偏。清空之后**不 break**，
        // 让下面按需塞进来的"重新对齐"在同一条循环里跑掉。
        outbox = [];
        if (result.rebaseline) outbox.push({ kind: 'baseline' });
      }
    } finally {
      draining = false;
    }
  }

  async function runTask(task: OutboxTask): Promise<TaskResult> {
    if (!isOnline() || !snapshot.shareCode) return { ok: false };
    return task.kind === 'ops' ? sendOps(task.ops, task.url) : sendBaseline();
  }

  /**
   * 推一批操作。
   *
   * `baseVersion` 在这里贴 —— 不是在采集端。采集端根本看不见服务端的回执，
   * 它只知道"我刚改了什么"，不知道房间现在到哪个版本了。
   */
  async function sendOps(ops: DomOp[], url: string): Promise<TaskResult> {
    const patch: DomPatch = {
      id: `p${Date.now().toString(36)}`,
      url,
      roomCode: snapshot.shareCode,
      baseVersion: roomVersion,
      // 服务端会重新盖章，这里给个占位
      version: 0,
      ops,
      at: Date.now(),
    };

    // 太大就**不发**，改走"重建基线"。不能像服务端拒收那样只清队列：
    // 这条 patch 里的操作一旦不发，两端的树就分叉了，而基线的语义正是"此刻整棵树"，
    // 新基线一盖就把这些变化补齐 —— 这条路本来就是协议里为分叉准备的出口。
    const tooBig = uplinkRejection(patch);
    if (tooBig) {
      if (import.meta.env.DEV) {
        console.warn(`[kiriyama-share] patch 超出上行上限（${tooBig}），改为重建基线`);
      }
      return { ok: false, rebaseline: true };
    }

    let ack: PatchAck;
    try {
      ack = await hub!.invoke<PatchAck>('PublishChanges', patch);
    } catch (error) {
      // 根本没送到（连接抖了）。base 其实没变，但顺序已经断了 —— 后面的别再发了。
      if (import.meta.env.DEV) console.warn('[kiriyama-share] patch 推送失败', error);
      return { ok: false };
    }

    if (ack.accepted) {
      roomVersion = ack.version;
      return { ok: true };
    }

    if (ack.reason === PatchRejectReasons.NotHosting) {
      // 房间已经没了（服务端按房主连接的生命周期清房）。清掉本地房间状态，
      // 别再往一个不存在的房间推东西。
      if (import.meta.env.DEV) console.debug('[kiriyama-share] 已不是房主，房间状态清空');
      resetRoomState();
      broadcastSnapshot();
      return { ok: false };
    }

    // stale-base / too-large：本地状态和服务端已经分叉。
    // 唯一的正确动作是**重新建立基线** —— 这正是把"静默跑偏"换成"一次明确重建"的地方。
    if (import.meta.env.DEV) {
      console.debug(
        `[kiriyama-share] patch 被拒（${ack.reason}），本地 base=${roomVersion}，服务端=${ack.version}，重新建立基线`,
      );
    }
    roomVersion = ack.version;
    return { ok: false, rebaseline: true };
  }

  /**
   * 向正在共享的那个标签页要一份**当前**的基线。
   *
   * 基线只能由页面里的 content script 拿 —— 它是唯一看得到宿主 DOM、
   * 也是唯一拿着 rrweb 镜像的地方。它会重新起一遍采集再交出来，
   * 所以拿到的永远是"此刻"的树（SPA 跳转之后旧的基线描述的是上一页）。
   */
  async function captureBaseline(): Promise<BaselinePayload | null> {
    if (sharingTabId === null) return null;

    try {
      return (await browser.tabs.sendMessage(sharingTabId, {
        type: 'ksw:capture-snapshot',
      } satisfies RuntimeMessage)) as BaselinePayload | null;
    } catch {
      // 标签页已经关了 / 跳到了没有 content script 的页面（chrome:// 之类）
      return null;
    }
  }

  /**
   * 交一份整页基线给服务端。这是"观众端第一次连上就能看到完整页面"的唯一来源。
   *
   * 房间号、地址、版本、时间全部由服务端盖章，这里只负责把树送上去。
   */
  async function sendBaseline(): Promise<TaskResult> {
    const code = snapshot.shareCode;
    const captured = await captureBaseline();
    if (!captured) return { ok: false };

    const payload = {
      roomCode: code,
      url: captured.url,
      title: captured.title,
      doctype: captured.doctype,
      root: captured.root,
      // 服务端会盖成它此刻的房间版本
      version: 0,
      truncated: captured.truncated,
      at: Date.now(),
    };

    // 超量就**不发**，并明确失败。
    //
    // 这里刻意不裁这棵树：`id` 是采集器镜像里的身份，裁掉一个节点，后面引用它的
    // mutation 就永远落不下去了 —— 那是比"发不出去"更糟的静默分叉。
    const tooBig = uplinkRejection(payload);
    if (tooBig) {
      console.warn(
        `[kiriyama-share] 基线超出上行上限（${tooBig}），未发送。` +
          '直接发出去会把连接打死，房间会跟着消失，所以宁可在这里失败。',
      );
      return { ok: false };
    }

    try {
      await hub!.invoke<boolean>('PublishSnapshot', payload);
    } catch (error) {
      if (import.meta.env.DEV) console.warn('[kiriyama-share] 基线推送失败', error);
      return { ok: false };
    }

    // 取回来对齐本地版本。
    //
    // 基线是"此刻整棵树"，它代表的版本就是房间当前版本 —— 而且只有 patch 会让版本前进，
    // 我们的队列是串行的，此刻没有别的上行在飞，所以这一次读回来的值就是准的。
    // 有了它，紧随其后的 patch 贴的 base 才能对上（否则第一条必被拒收，白跑一个来回）。
    const stored = await fetchSnapshot(code);
    if (stored) roomVersion = stored.version;

    return { ok: true };
  }

  /** 取服务端存着的那份基线（不是现抓的）。 */
  async function fetchSnapshot(roomCode: string): Promise<DomSnapshot | null> {
    if (!isOnline()) return null;

    try {
      return await hub!.invoke<DomSnapshot | null>('GetSnapshot', roomCode);
    } catch {
      return null;
    }
  }

  /**
   * 服务端在替观众要基线（或观众自己发现分叉了）：排一份新基线上去。
   *
   * 只有"这个房间确实是我在共享"时才回应 —— 别的房间的基线我交不出来，
   * 而且服务端本身也只把这条消息发给房主。
   *
   * 走队列而不是立刻发：此刻可能还有没送出去的 patch，那些 patch 描述的变化
   * 必须排在基线之前（基线已经包含了它们，反过来的话观众会先重置再被旧增量改一遍）。
   */
  function serveSnapshot(roomCode: string) {
    if (!roomCode || normalizeCode(roomCode) !== normalizeCode(snapshot.shareCode)) return;
    enqueue({ kind: 'baseline' });
  }

  /**
   * 房主交上来新基线了：取回来推给正在看这个房间的观看页，让它整个重画。
   *
   * 为什么观众不自己轮询：这次刷新可能是房主换了页面引起的，
   * 观看页那边只知道自己该重画，不知道什么时候该重画 —— 由这里推最省事也最及时。
   */
  async function handleSnapshotReady(roomCode: string) {
    const code = normalizeCode(roomCode);
    const dom = await fetchSnapshot(code);
    if (!dom) return;

    for (const channel of channels) {
      if (channel.viewingRoom !== code) continue;
      safePost(channel.port, { kind: 'event', event: { name: 'dom-snapshot', roomCode: code, dom } });
    }
  }

  /** 请房主现抓一份新基线。异步：房主交上来之后会推 SnapshotReady，我们再通知观看页。 */
  function requestFreshSnapshot(roomCode: string) {
    if (!isOnline()) return;

    void hub!.invoke<boolean>('RequestSnapshot', roomCode).catch(() => {
      /* 房主没回应（它可能已经走了）—— 观看页会停在现有那份基线上，等下一次机会 */
    });
  }

  // ---------------------------------------------------------------- 房间操作

  async function refreshRooms(): Promise<void> {
    if (!isOnline()) return;

    try {
      const rooms = await hub!.invoke<SharedRoom[]>('ListRooms');

      // 服务端测不出客户端的 RTT，latencyMs 恒为 null —— 借一次 Ping 自己填。
      const latency = rooms.length ? await measureLatency() : null;

      snapshot.rooms = rooms.map((room) => ({ ...room, latencyMs: latency }));
      // 拉得动就说明连接是好的，把上一次的错误清掉
      snapshot.error = null;
      broadcastSnapshot();
    } catch (error) {
      snapshot.error = readableError(error);
      broadcastSnapshot();
    }
  }

  async function measureLatency(): Promise<number | null> {
    if (!isOnline()) return null;

    try {
      const started = Date.now();
      await hub!.invoke<number>('Ping');
      return Date.now() - started;
    } catch {
      return null;
    }
  }

  async function startSharing(page: PageInfo): Promise<BridgeResult> {
    if (!isOnline()) {
      return fail('还没连上服务器，请先在「设置」里检查地址');
    }

    try {
      const room = await hub!.invoke<SharedRoom>('StartSharing', {
        title: page.title,
        url: page.url,
        host: page.host,
        // 显示名留给服务端分配（没有设置项，不凭空编一个出来）
        name: null,
      } satisfies Record<string, unknown>);

      snapshot.shareCode = room.id;
      // 刚建好的房间里只有自己，服务端不会推 PeersChanged；
      // 等第一个订阅者进来才会有成员列表，所以这里先空着 —— 观看人数 0 是对的。
      snapshot.peers = [];
      selfIds.delete(room.id);
      broadcastSnapshot();

      // 版本从 0 起：新房间里什么都没发生过。
      // （StartSharing 是幂等的 —— 同一个连接重复调用会拿回原来那个房间，而它的版本
      //  早就涨上去了。那种情况下第一条 patch 会被当成过期拒收，然后自动重新对齐，
      //  不必在这里猜服务端的版本。）
      roomVersion = 0;
      outbox = [];

      // 顺手交一份基线。不做也不影响正确性（观众连上来会现要一份），
      // 但有了它，第一个观众能立刻看到画面而不用等"请房主抓 DOM"那一个来回。
      enqueue({ kind: 'baseline' });

      await refreshRooms();
      return reply(true);
    } catch (error) {
      return fail(readableError(error));
    }
  }

  async function stopSharing(): Promise<BridgeResult> {
    const code = snapshot.shareCode;

    if (isOnline() && code) {
      try {
        await hub!.invoke<boolean>('StopSharing');
      } catch (error) {
        return fail(readableError(error));
      }
    }

    resetRoomState();
    broadcastSnapshot();
    await refreshRooms();
    return reply(true);
  }

  async function subscribe(roomCode: string, displayName?: string): Promise<BridgeResult> {
    if (!isOnline()) return fail('还没连上服务器');

    try {
      await hub!.invoke<string>('Subscribe', roomCode, displayName ?? null);
    } catch (error) {
      // 服务端把领域错误原样带过来了（房间号不存在之类），直接用
      return fail(readableError(error));
    }

    await refreshRooms();
    return reply(true);
  }

  async function unsubscribe(roomCode: string): Promise<BridgeResult> {
    if (!isOnline()) return fail('还没连上服务器');

    try {
      await hub!.invoke<boolean>('Unsubscribe', roomCode);
    } catch (error) {
      return fail(readableError(error));
    }

    await refreshRooms();
    return reply(true);
  }

  /**
   * 把一批操作排进上行队列。
   *
   * 不在这里 await：采集端是高频回调，堵在那儿会拖慢页面。进队列即可，
   * 由 `drainOutbox` 串行送出去 —— 顺序与 base 都由那边保证。
   */
  function queueOps(ops: DomOp[], url: string) {
    enqueue({ kind: 'ops', ops, url });
  }

  /**
   * 开始观看一个房间：订阅它，然后把**扩展自己的观看页**在新标签页里打开。
   *
   * 不打开被共享的那个 URL：观众要看到的是"共享方此刻的 DOM"，而不是"自己访问那个站点
   * 拿到的 DOM"（后者会因为登录态 / AB / 个性化而和基线对不上），而且不少页面在跨域
   * 新标签页里根本复现不出来。观看页是扩展自己的页面，一定能打开，
   * 内容由整页快照 + 增量重建（见 entrypoints/viewer）。
   */
  async function watchRoom(roomCode: string): Promise<BridgeResult> {
    if (!isOnline()) return fail('还没连上服务器');

    const code = normalizeCode(roomCode);

    // 服务端只给"订阅了这个房间"的连接推变更，所以先订阅（重复订阅是幂等的）
    try {
      await hub!.invoke<string>('Subscribe', code, null);
    } catch (error) {
      return fail(readableError(error));
    }

    try {
      await browser.tabs.create({
        url: browser.runtime.getURL(`/viewer.html?room=${encodeURIComponent(code)}`),
      });
    } catch (error) {
      return fail(readableError(error));
    }

    await refreshRooms();
    return reply(true);
  }

  /**
   * 观看页启动：订阅（幂等）+ 拿一份基线。
   *
   * 先看有没有现成的，有就直接给（首屏不用等一个来回），同时**再去要一份新的**：
   * 增量只推给"已经订阅"的连接，旧快照 + 订阅之后的增量会缺掉中间那段
   * （页面缺一块，而且从画面上看不出来），所以那份旧的只配当首屏的过渡。
   * 新的一到，`handleSnapshotReady` 会把观看页整个重画一遍。
   */
  async function loadViewer(roomCode: string): Promise<BridgeResult> {
    // 观看页可能是被**直接打开**的（地址栏、历史记录、收藏），这时连接还没建起来。
    // 先自己连一次，别让用户对着"还没连上服务器"不知所措 —— 顺带也把被回收的 SW 唤醒。
    const connected = await ensureConnected();
    if (!connected.ok) return fail(connected.error ?? '还没连上服务器');

    const code = normalizeCode(roomCode);

    try {
      await hub!.invoke<string>('Subscribe', code, null);
    } catch (error) {
      // 服务端把领域错误原样带过来了（房间号不存在 / 房间已关），直接用
      return fail(readableError(error));
    }

    const cached = await fetchSnapshot(code);
    requestFreshSnapshot(code);

    await refreshRooms();
    return { ...reply(true), dom: cached };
  }

  /**
   * 共享的页面换了地址：**房间不关**，只换共享目标，并让订阅者拿到新基线。
   *
   * StartSharing 是幂等的（同一个连接重复调用房间号不变），服务端自己会从
   * "地址变了"这件事识别出跳转并给订阅者推一条 navigate 事件 —— 客户端既不需要
   * 也无法代它广播，所以这里只管把新目标报上去、再交一份新快照。
   */
  async function retargetSharedPage(page: PageInfo): Promise<void> {
    if (!isOnline() || !snapshot.shareCode) return;

    try {
      const room = await hub!.invoke<SharedRoom>('StartSharing', {
        title: page.title,
        url: page.url,
        host: page.host,
        name: null,
      } satisfies Record<string, unknown>);

      snapshot.shareCode = room.id;
      snapshot.error = null;
      broadcastSnapshot();
    } catch (error) {
      snapshot.error = readableError(error);
      broadcastSnapshot();
      return;
    }

    // 等一小会儿再抓：SPA 跳转刚发生时新页面往往还没渲染完，抓早了拿到的是"旧内容 + 新地址"。
    // 之后新增出来的内容会以增量形式补上（采集器一直在跑），所以宁可稍微等一下。
    await delay(350);
    // 走队列：跳转前可能还有没送出去的 patch，它们要排在基线之前
    enqueue({ kind: 'baseline' });
    await refreshRooms();
  }

  /**
   * 问某个标签页"你现在是哪个页面"。
   *
   * 没有 content script 的页面（chrome://、扩展页、应用商店、PDF 阅读器）会直接 reject；
   * 还在加载中（或刚被浏览器丢弃、正在重新加载）的标签页也一样 —— 两种都返回 null，
   * 由调用方决定怎么办。
   */
  async function askPageInfo(tabId: number): Promise<PageInfo | null> {
    try {
      return (await browser.tabs.sendMessage(tabId, {
        type: 'ksw:get-page-info',
      } satisfies RuntimeMessage)) as PageInfo | null;
    } catch {
      return null;
    }
  }

  /**
   * 把共享目标换成"刚刚被激活的那个标签页"。
   *
   * 用户在标签页之间切换时，被共享的应该是**他此刻正在看的页面**，而不是最初点过
   * 「开始共享」的那一个。切换**不影响房间**：走的还是 `retargetSharedPage` 里那条
   * 幂等的 `StartSharing`，共享码不变，订阅者收到一条 `navigate` 与一份新的整页快照。
   *
   * 两个入口都会调到这里：background 的 `tabs.onActivated`，以及页面自己报的
   * `ksw:tab-foreground`（后者能覆盖换窗口，前者能覆盖页面不上报 focus 的情形）。
   *
   * 换不动的两种情况都**还原成原目标**，不把房间推向"目标标签页上没有 content script"
   * 那种哑状态：① 这页永远不会有 content script；② 它还没来得及跑起来。
   * 第 ② 种会自愈：那页的 content script 起来时会补一次 `ksw:tab-foreground`。
   */
  async function switchShareTarget(tabId: number, knownPage?: PageInfo): Promise<void> {
    // 没在共享就没有"目标"可换。用户到新标签页里按「开始共享」即可。
    if (!isOnline() || !snapshot.shareCode) return;
    if (tabId === sharingTabId) return;

    // 先把目标占住、再去问页面信息。`tabs.onActivated` 和 `ksw:tab-foreground`
    // 在切一次标签页时会**各报一次**，两条并发调用都会走到这里 —— 占位让第二条
    // 立刻返回，不会白抓两份整页快照。
    const previous = sharingTabId;
    sharingTabId = tabId;

    const page = knownPage ?? (await askPageInfo(tabId));
    if (!page) {
      if (sharingTabId === tabId) sharingTabId = previous;
      return;
    }

    // 问的过程中用户又切走了，那以新的为准，这次不算
    if (sharingTabId !== tabId) return;

    if (import.meta.env.DEV) {
      console.debug(`[kiriyama-share] 激活标签页 ${tabId}，共享目标切到 ${page.url}`);
    }

    await retargetSharedPage(page);
  }

  // ---------------------------------------------------------------- 命令

  async function handleCommand(
    channel: BridgeChannel,
    command: BridgeCommand,
  ): Promise<BridgeResult> {
    switch (command.name) {
      case 'connect':
        return ensureConnected();

      case 'disconnect':
        await disposeHub();
        resetRoomState();
        snapshot.connection = 'offline';
        snapshot.error = null;
        snapshot.rooms = [];
        broadcastSnapshot();
        return reply(true);

      case 'get-snapshot':
        return reply(true);

      case 'list-rooms':
        await refreshRooms();
        return reply(true);

      case 'start-sharing':
        // DOM 批次要认出"是哪个标签页在共享"，所以记下来。
        // 从 popup 发起时它没有标签页，必须把目标标签页显式带过来；
        // 带不过来就会是 null，而 null 会让所有批次被挡掉（房间建好却同步不出东西）。
        sharingTabId = command.tabId ?? channel.tabId;
        return startSharing(command.request);

      case 'stop-sharing':
        return stopSharing();

      case 'subscribe':
        return subscribe(command.roomCode, command.displayName);

      case 'unsubscribe':
        return unsubscribe(command.roomCode);

      case 'watch-room':
        return watchRoom(command.roomCode);

      case 'viewer-load':
        return loadViewer(command.roomCode);

      case 'request-baseline':
        // 观众发现自己跟不上了（版本对不上，或者有操作落空）。
        // 走服务端那条链路请房主现交一份：服务端把 SnapshotRequested 转给房主，
        // 房主交上来之后推 SnapshotReady，我们再把它转给观看页整个重画。
        requestFreshSnapshot(normalizeCode(command.roomCode));
        return reply(true);

      default:
        return fail('无法识别的命令');
    }
  }

  async function onChannelMessage(channel: BridgeChannel, raw: unknown) {
    if (!raw || typeof raw !== 'object') return;

    const message = raw as BridgeWireMessage;
    if (message.kind !== 'command') return;

    // 观看页的登记要抢在命令执行**之前**：快照或变更可能在命令返回之前就推过来了，
    // 那时候如果通道还没认领这个房间，那份快照就会被丢掉。
    if (message.command.name === 'viewer-load') {
      const code = normalizeCode(message.command.roomCode);
      channel.viewingRoom = code;
      // 变更只推给"订阅过这个房间"的通道，观看页当然要收
      channel.rooms.add(code);
    }

    const result = await handleCommand(channel, message.command);

    // 订阅关系跟着命令走：谁要求订阅哪个房间，谁就收那个房间的变更推送。
    if (message.command.name === 'subscribe') {
      channel.rooms.add(normalizeCode(message.command.roomCode));
    }

    if (message.command.name === 'unsubscribe') {
      channel.rooms.delete(normalizeCode(message.command.roomCode));
    }

    safePost(channel.port, { kind: 'result', id: message.id, result });
  }

  // ---------------------------------------------------------------- 接入口

  /**
   * 共享标签页被关掉：**房间不关**，目标交给"下一个被激活的标签页"接手。
   *
   * 这里原来挂的是 `stopSharing()`，于是关掉正在共享的那个标签页会连房间一起收掉。
   * 但房间的寿命本来就不该由某一个标签页决定 —— 换页面不停、切标签页不停，
   * 那关掉标签页凭什么停。现在的语义：
   *   - 有别的前台网页可接手 → 目标切过去，**共享码不变**，订阅者收到一条 navigate
   *     与一份新基线（观看页跟着重画，不会掉线）；
   *   - 一时没有（剩下的是 chrome:// / 观看页这类装不了内容脚本的页）→ 目标先空着，
   *     房间留着，等用户切到下一个网页时由 `ksw:tab-foreground` 自动补上。
   *
   * 所以房间现在只有两种结束方式：用户点「停止共享」，或者连接断了
   * （服务端按房主连接的生命周期清房，这个不归客户端管）。
   *
   * 这个监听只做一件事：把**已经死掉的目标**从记录里清掉。
   *   - **必须清**：Chrome 会复用已关闭标签页的 id，留着旧 id 会让后来的新标签页
   *     伪装成原目标 —— `switchShareTarget` 见到 `tabId === sharingTabId` 会直接返回，
   *     目标就再也换不动了。
   *   - **只在"当前目标确实是它"时清**：`tabs.onActivated` 有可能比这个事件先到、
   *     已经把目标换成接手的那一个，这时清空等于把刚设好的目标抹掉。
   *     两个事件的先后顺序没有保证，这个条件让两种顺序都对。
   *
   * 接手那条路不在这里主动找：`onActivated`（同窗口换标签页）和内容脚本自报的
   * `ksw:tab-foreground`（换窗口、以及页面加载完时补报）本来就在跑，
   * `switchShareTarget` 会自己去重。
   *
   * `tabs.onRemoved` 是"标签页真的没了"的权威信号，事件驱动、没有竞态；
   * 而且它**不需要 tabs 权限**（要权限的只有读 url / title / favIconUrl 那几个字段，
   * 本扩展一个都没读，所以 manifest 里仍然只有 storage）。
   *
   * 它注册在顶层，service worker 被回收后再唤醒也会重新挂上。
   */
  browser.tabs.onRemoved.addListener((tabId) => {
    // 关掉的不是共享目标（别的标签页、观看页）—— 跟这个房间无关
    if (sharingTabId === null || tabId !== sharingTabId) return;

    if (import.meta.env.DEV) {
      console.debug(
        `[kiriyama-share] 共享标签页 ${tabId} 已关闭，房间 ${snapshot.shareCode} 保留，等下一个标签页接手`,
      );
    }

    sharingTabId = null;
  });

  /**
   * 共享目标跟着**当前激活的标签页**走：用户切到哪个标签页，就共享哪一个。
   *
   * 这里只解决"同一个窗口内点标签页 / Ctrl+Tab"。**换窗口不会触发 `onActivated`**，
   * 那半边由页面自己报的 `ksw:tab-foreground` 补上（`window` 的 focus 事件）。
   * 两条路都汇到 `switchShareTarget`，它自己会去重。
   *
   * 权限：**不需要 `tabs` 权限**。该权限只用来放行 Tab 上那四个敏感属性
   * （url / pendingUrl / title / favIconUrl），既不打开 `chrome.tabs` 命名空间，
   * 也不影响事件；`onActivated` 只给 tabId + windowId，我们也没读 tab.url
   * （页面信息一律问 content script），所以 manifest 仍然只有 storage。
   */
  browser.tabs.onActivated.addListener(({ tabId }) => {
    void switchShareTarget(tabId);
  });

  browser.runtime.onConnect.addListener((port) => {
    if (port.name !== BRIDGE_PORT) return;

    const channel: BridgeChannel = {
      port,
      tabId: port.sender?.tab?.id ?? null,
      rooms: new Set<string>(),
      viewingRoom: null,
    };

    channels.add(channel);

    // 新面板一连上先给一份全量，UI 不用自己拉
    safePost(port, {
      kind: 'event',
      event: { name: 'snapshot', snapshot },
    });

    // background 的 service worker 闲着会被回收，连着的 HubConnection 随之消失。
    // 任何一条 UI 通道重新接回来（正是那种时刻）就顺手把连接恢复，做到自愈。
    void (async () => {
      const settings = await loadSettings();
      if (settings.autoReconnect && !hub) {
        await ensureConnected();
      }
    })();

    port.onMessage.addListener((raw) => {
      void onChannelMessage(channel, raw);
    });

    port.onDisconnect.addListener(() => {
      channels.delete(channel);

      // 观看页关掉就不再需要这个房间的推送了。房间的"几人观看"是按订阅数算的，
      // 不退订会留下一个永远算在人数里的幽灵观众。
      // 只在"没有别的观看页还在看这个房间"时退订 —— 同一个房间可以开好几个观看页。
      const watching = channel.viewingRoom;
      if (watching && ![...channels].some((other) => other.viewingRoom === watching)) {
        void unsubscribe(watching);
      }

      // 注意：**这里绝对不能收房间**。Port 断开只说明"这一段 content script 没了"，
      // 而**整页跳转**（点链接 / 改地址栏 / 刷新）恰好会销毁旧的 content script ——
      // 那条路径必须让房间活着，只是共享目标换了地址（见 ksw:page-changed）。
      // 曾经这里挂着 stopSharing()，于是"共享方换个页面"被当成了"房主关了标签页"，
      // 房间连同共享码一起消失。标签页真的关掉由下面的 tabs.onRemoved 负责 ——
      // 而它现在也只是清掉目标，同样不收房间。
    });
  });

  browser.runtime.onMessage.addListener((message, sender) => {
    if (!isRuntimeMessage(message)) return;

    switch (message.type) {
      case 'ksw:dom-patch': {
        const tabId = sender.tab?.id ?? null;

        // 只有正在共享的那个标签页有权往外推
        if (sharingTabId === null || tabId !== sharingTabId) return;

        published += 1;
        if (import.meta.env.DEV && published % 20 === 1) {
          console.debug(
            `[kiriyama-share] 上报第 ${published} 批操作：${message.patch.ops.length} 条`,
            message.patch.url,
          );
        }

        queueOps(message.patch.ops, message.patch.url);
        break;
      }

      case 'ksw:share-started':
      case 'ksw:share-stopped':
        if (import.meta.env.DEV) console.debug(`[kiriyama-share] ${message.type}`, message.url);
        break;

      case 'ksw:page-changed': {
        const tabId = sender.tab?.id ?? null;

        // 只有**正在共享的那个标签页**的页面变化才算"共享目标跳转"。
        // 其它标签页（包括观看页自己）换个地址跟这个房间没有任何关系。
        if (tabId === null || sharingTabId === null || tabId !== sharingTabId) break;

        if (import.meta.env.DEV) {
          console.debug(`[kiriyama-share] 共享目标页面变化 -> ${message.page.url}`);
        }

        void retargetSharedPage(message.page);
        break;
      }

      case 'ksw:tab-foreground': {
        const tabId = sender.tab?.id ?? null;
        if (tabId === null) break;

        // 这条**故意不校验** `tabId === sharingTabId` —— 换目标正是它的目的。
        // 它也不会无条件生效：没在共享、或本来就是共享目标，`switchShareTarget` 直接返回。
        // 页面信息跟着消息一起来，省一次往返（`tabs.onActivated` 那条没有，得现问）。
        if (import.meta.env.DEV) {
          console.debug(`[kiriyama-share] 标签页 ${tabId} 进入前台 -> ${message.page.url}`);
        }

        void switchShareTarget(tabId, message.page);
        break;
      }

      default:
        break;
    }
  });
});
