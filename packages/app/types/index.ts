/**
 * KiriyamaShare —— 前后端共用的领域类型（前端侧）
 *
 * 这个扩展做的事：
 *   1. content script 用 MutationObserver 监听宿主页面 DOM 变化；
 *   2. 变化经 background 推给服务端；
 *   3. 服务端把事件广播给订阅了同一房间的其它客户端；
 *   4. 其它客户端据此复现 / 共享页面内容。
 *
 * 这里的类型描述 UI 层需要的状态形状，服务端接好后直接复用即可。
 */

/** 卡片的主题策略 */
export type ThemePreference = 'auto' | 'light' | 'dark';

/** 当前所在页面的元信息（共享的目标） */
export interface PageInfo {
  title: string;
  url: string;
  host: string;
  /** 页面 favicon 地址，取不到时为 null */
  favicon: string | null;
}/** 用户可配置项，持久化在 browser.storage.local */
export interface WidgetSettings {
  /** 服务端地址，填**完整 Hub 地址**，如 http://localhost:5000/share（不用手写 ws://） */
  serverUrl: string;
  /** 断线后自动重连 */
  autoReconnect: boolean;
  /** 是否同步鼠标光标位置 */
  syncCursor: boolean;
  /** 主题策略 */
  theme: ThemePreference;
}

/** 与服务器的连接状态 */
export type ConnectionState = 'offline' | 'connecting' | 'online' | 'error';

/**
 * 服务端给的房间成员。
 *
 * **故意不含 `isSelf`**：`PeersChanged` 推给所有人的是同一份列表，
 * 服务端没法替某个客户端判断"哪个是你"。客户端拿 `WhoAmI` 的返回值比对得出。
 */
export interface SharePeerDto {
  id: string;
  name: string;
  /** 光标颜色，由服务端从调色板里分配，保证同一房间内不重样 */
  color: string;
}

/** 同一房间里的其它客户端（UI 侧，比对出 isSelf 之后） */
export interface Peer extends SharePeerDto {
  isSelf: boolean;
}

/** 服务端推来的房间生命周期事件 */
export interface ShareEventDto {
  /** 与 ActivityKind 对齐，客户端拿到 kind 直接决定图标与文案 */
  kind: 'share' | 'navigate' | 'join' | 'leave' | 'system';
  roomCode: string;
  /** 加入 / 离开的那个人；房间关闭、跳转时为 null */
  peer: SharePeerDto | null;
  /**
   * 跳转事件（kind='navigate'）专用的新地址；其它事件为 null。
   *
   * 只给地址不给内容：页面内容走整页快照那条链路（快照可以到几百 KB，
   * 塞进事件里会把活动流这种小消息撑爆）。
   */
  url: string | null;
  at: number;
}

/** 服务端上可订阅的共享房间 */
export interface SharedRoom {
  id: string;
  title: string;
  url: string;
  /** 发起方标识 */
  host: string;
  peerCount: number;
  /** 往返延迟（毫秒），未知为 null */
  latencyMs: number | null;
  /** 最后一次更新时间的毫秒时间戳 */
  updatedAt: number;
  /** 当前客户端是否已订阅 */
  subscribed: boolean;
}

/** 事件流条目的种类 */
export type ActivityKind = 'share' | 'mutation' | 'navigate' | 'join' | 'leave' | 'system';

export interface ActivityEntry {
  id: string;
  kind: ActivityKind;
  text: string;
  /** 毫秒时间戳 */
  at: number;
}

/** Tab 组件的数据描述 */
export interface TabItem {
  value: string;
  label: string;
  /** assets/icons 下的图标名，不带后缀 */
  icon: string;
}

/* ---------------------------------------------------------------------------
 * DOM 同步：共同基线 + 版本化 patch
 *
 * 这条链路是本项目的核心。它跟上一版的根本区别是**两端共用一份带 id 的权威树**：
 *   1. 房主用 rrweb 的 record() 采集，它内部维护一个 Mirror（节点 ↔ id）；
 *   2. 建立基线时把整棵**带 id 的节点树**交给订阅方（不是 HTML 字符串）；
 *   3. 之后只发"基于某个已知版本"的操作，每条操作都带节点 id；
 *   4. 服务端校验 base 版本，对不上就**拒收**，房主重交基线。
 *
 * 为什么不用选择器路径：`tag > tag:nth-of-type(n)` 是"按位置猜"，两端结构差一丝
 * 就指向错的节点，而且**猜错了不报错** —— 页面从此静默跑偏。id 是两端共用的身份，
 * 对不上只能说明链路丢了东西，那时候该重新同步，而不是继续在错的树上叠增量。
 *
 * 字段名与后端 Contracts/Sharing 下的 DTO 一一对应（SignalR 走 camelCase）。
 * ------------------------------------------------------------------------- */

/**
 * 序列化节点种类。**数值必须与 rrweb 的 `NodeType` 完全一致** ——
 * 接收方会把这些节点原样喂给 rrweb 的 `rebuild()`。
 */
export const NodeKinds = {
  /** 整个文档（基线的根就是它）。 */
  Document: 0,
  /** `<!DOCTYPE>`：内容在 name / publicId / systemId 上。 */
  DocumentType: 1,
  /** 元素。 */
  Element: 2,
  /** 文本。 */
  Text: 3,
  /** CDATA（只在 XML/SVG 文档里出现）。 */
  CData: 4,
  /** 注释。 */
  Comment: 5,
} as const;

/**
 * 一个序列化后的 DOM 节点，**带上传送方分配的 id**。
 *
 * 形状直接对齐 rrweb 的 `serializedNodeWithId`：接收方要把它原样交给 `rebuild()`。
 *
 * 对齐这件事**不是可选的美化**，而且失败方式有两种，都不是"少一点东西"那么温和：
 *   - 大多数字段少一个，重建出来的页面就少一点东西，**而且不报错**（最难查的那种）；
 *   - 少数几个字段（见下面 `childNodes`）少一个，`rebuild()` 直接抛，整份基线全废。
 * 所以每个字段的注释里都写清楚"缺了会怎样"。
 *
 * `type` 是必需字段，不是可省略的装饰：文本节点与注释节点**只靠 type 区分**
 * （两者的 textContent 都是字符串），只凭"有没有 tagName"判断会把注释当成文本。
 */
export interface SerializedNode {
  /** 节点 id。元素 / 文本 / 注释共用一套编号空间；由采集端的镜像分配、单调递增、永不复用。 */
  id: number;
  /** 节点种类，见 `NodeKinds`。**判定类型以它为准**。 */
  type: number;
  /** 元素标签名。非元素节点没有这个字段。 */
  tagName?: string;
  /** 文本 / 注释 / CDATA 的内容。元素与文档节点没有这个字段。 */
  textContent?: string;
  /** 元素属性。值一律是字符串（rrweb 落属性时本来就会 String() 一次）。 */
  attributes?: Record<string, string>;
  /**
   * 子节点。
   *
   * **`type` 是 0（文档）或 2（元素）时，这个字段必须存在 —— 没有子节点也要给空数组。**
   *
   * 这不是"少一点内容"，是硬失败：rrweb 的 `buildNodeWithSN()` 对这两类节点是无条件
   * `for (const childN of n.childNodes)` 的（源码里没有 `if` 兜底），字段缺了就是
   * `TypeError: n.childNodes is not iterable`，**整份基线一个字都建不出来**。
   * 页面上随便一个 `<meta>` / `<br>` / `<img>` 就够触发。rrweb 自己的序列化器恒给 `[]`。
   */
  childNodes?: SerializedNode[];
  /** 文档节点：`BackCompat` 表示怪异模式。 */
  compatMode?: string;
  /** DOCTYPE 的名字（通常是 `html`）。 */
  name?: string;
  /** DOCTYPE 的 publicId。 */
  publicId?: string;
  /** DOCTYPE 的 systemId。 */
  systemId?: string;
  /** 元素属于 SVG 命名空间。 */
  isSVG?: boolean;
  /** 元素带 `rr-block`（被采集方显式排除）。 */
  needBlock?: boolean;
  /** 自定义元素（带 `-` 的标签名）。 */
  isCustom?: boolean;
  /** 这个节点属于哪个根（主文档为 0）。 */
  rootId?: number;
  /** 这个元素挂着 shadow root。 */
  isShadowHost?: boolean;
  /** 这个节点本身在 shadow root 里。 */
  isShadow?: boolean;
}

/** 操作种类。与后端 `DomOpKinds` 对应。 */
export const DomOpKinds = {
  /** 在父节点（`id`）的锚点（`next`）之前插入一棵子树。 */
  Insert: 'insert',
  /** 摘掉一个节点（连同它的子树）。 */
  Remove: 'remove',
  /** 增删属性。 */
  Attrs: 'attrs',
  /** 改文本节点的内容。 */
  Text: 'text',
} as const;

/**
 * 一条作用在**共同基线**上的 DOM 操作。
 *
 * 操作按数组顺序逐条应用，顺序不能重排。对 `insert` 尤其严格：rrweb 产出的 adds 里
 * **父节点一定排在子节点前面、锚点节点一定排在被插入的节点前面**，接收方必须照这个顺序落，
 * 否则会去引用一个还没建出来的 id。
 *
 * 没有 `move` 这个种类，是因为**线上根本不存在**：rrweb 处理节点移动时会把配对的
 * remove 消掉、只发一条 add。接收方拿到一条 add 时，如果 id 已经在自己的镜像里，
 * rrweb 的 `buildNodeWithSN()` 会直接返回那个**已有的 DOM 节点**而不是新建一个 ——
 * 前提是接收方把自己的镜像传了进去。所以移动在接收端就是一次真正的 DOM 移动。
 */
export interface DomOp {
  op: (typeof DomOpKinds)[keyof typeof DomOpKinds];
  /**
   * 目标节点 id。
   * `insert` 时是**父节点**（插到它下面）；`remove` / `attrs` / `text` 都是**被操作的那个节点本人**。
   */
  id: number;
  /** `remove`：发送方认为这个节点的父节点是谁（接收方可用来发现两端分叉）。 */
  parent?: number | null;
  /**
   * `insert`：插入位置的**锚点节点 id** —— 新节点插到它前面。为 null / 缺省表示追加到父节点末尾。
   *
   * 用 id 而不是下标：两端的兄弟列表只要有一点不同步，下标就指到别的缝里去；
   * id 指错了会**指不到**（接收方立刻能发现），而不是指到隔壁那个无辜的节点上。
   */
  next?: number | null;
  /** `insert`：要插入的子树。 */
  nodes?: SerializedNode[];
  /** `attrs`：要设置的属性。 */
  set?: Record<string, string>;
  /** `attrs`：要删除的属性名。属性删除与"设成空串"是两件事，这里分得开。 */
  remove?: string[];
  /** `text`：文本节点的新内容。 */
  text?: string;
}

/**
 * 一个 patch：作用在房间内某个**已知版本**上的一组 DOM 操作。
 *
 * 形式上跟 Git 的 patch 一样：不发全量（全量只在建立基线时发一次），
 * 也不发"你改了什么键"的原始事件，而是发"基于同一 base 的增量"。
 */
export interface DomPatch {
  /** patch id，采集侧生成，仅用于日志排查。 */
  id: string;
  /** 变更发生的页面地址。 */
  url: string;
  /** 所属房间号。**由服务端盖章**，发送方填什么都会被覆盖。 */
  roomCode: string;
  /**
   * **发送方认为**这个 patch 基于哪个版本。必须等于房间当前版本才会被接受。
   * 这是"共同基线"的检查点：不等就说明中间丢了 patch，该重新建立基线而不是硬叠。
   */
  baseVersion: number;
  /** 接受之后房间的新版本，**由服务端分配**。接收方应用成功后把本地版本推进到它。 */
  version: number;
  /** 本次操作，按顺序应用。 */
  ops: DomOp[];
  /** 打包时间，epoch 毫秒。 */
  at: number;
}

/**
 * 上行时用的 patch —— 还没有房间号与版本，两者都由服务端在接收时盖章。
 *
 * 采集端（content script）连版本号都不知道：只有 background 看得见服务端的回执，
 * 所以"基于哪个版本"是 background publish 那一刻贴上去的。
 */
export interface DomPatchDraft {
  id: string;
  url: string;
  ops: DomOp[];
  at: number;
}

/**
 * 房主推 patch 之后收到的回执。
 *
 * 为什么不是简单的 bool：房主要靠它区分两件完全不同的事 ——
 * "base 过期了，重交一份基线"和"我现在根本不是房主了，别再推了"。
 */
export interface PatchAck {
  /** 这条 patch 有没有被接受并转发出去。 */
  accepted: boolean;
  /** 被接受时是刚分配的新版本；被拒时是需要对齐到的房间当前版本。 */
  version: number;
  /** 没被接受的原因，见 `PatchRejectReasons`。被接受时是空串。 */
  reason: string;
}

/** patch 被拒的原因。与后端 `PatchRejectReasons` 对应。 */
export const PatchRejectReasons = {
  /** base 版本对不上：中间丢了 patch。处理方式：重新交一份整页基线对齐。 */
  StaleBase: 'stale-base',
  /** 发送方现在不是任何房间的房主。处理方式：清掉本地的房间状态。 */
  NotHosting: 'not-hosting',
  /** patch 超出服务端能承载的规模（操作条数 / 单棵子树大小）。处理方式：重新交基线。 */
  TooLarge: 'too-large',
} as const;

/**
 * 被共享页面的**整页基线**：一棵带 id 的节点树，加上它代表哪个版本。
 *
 * 旧版本这里是 `<html>` 的 outerHTML 字符串，配合选择器路径当增量。那种做法有两个
 * 绕不开的问题，都已经被实际踩到：① 字符串里的节点没有身份，接收方只能靠重新解析出来的
 * **位置**去对，而位置会随渲染漂移；② 字符串一旦截断就可能断在标签中间，解析器会"修补"出
 * 一棵和发送方不一样的树，而修补是静默的。
 *
 * 换成结构化节点树之后，整棵树的身份由发送方一次给定，接收方不解析、不推断，只重建。
 */
export interface DomSnapshot {
  /** 所属房间号。由服务端盖章。 */
  roomCode: string;
  /** 快照对应的页面地址。 */
  url: string;
  /** 页面标题。 */
  title: string;
  /**
   * 文档类型串（标准模式是 `html`，怪异模式为空串）。
   * 它决定接收方用哪种模式渲染，而怪异模式和标准模式的布局可以差出一整页。
   */
  doctype: string;
  /** 根节点（文档节点）。 */
  root: SerializedNode;
  /**
   * 这份快照**代表的房间版本**：应用完版本号小于等于它的 patch 之后，状态就等于这棵树。
   * 接收方拿到快照后本地版本直接推到它，只接受 `baseVersion` 正好等于这个值的 patch。
   */
  version: number;
  /** 是否因为超过上限被裁剪过。裁剪后的页面必然不完整，客户端据此给出提示。 */
  truncated: boolean;
  /** 快照生成时间，epoch 毫秒。 */
  at: number;
}

/**
 * 采集端交出的基线内容 —— 还没有房间号与版本（那两个由服务端盖章）。
 *
 * background 向 content script 要基线时拿到的就是它；同一份东西被包成 DomSnapshot 后上行。
 */
export interface BaselinePayload {
  root: SerializedNode;
  url: string;
  title: string;
  doctype: string;
  /** 采集端自己就知道这份基线不完整（比如超长被拒） */
  truncated: boolean;
}

/** 采集器运行统计，给 UI 显示用 */
export interface ObserverStats {
  running: boolean;
  /** 已采集的操作总数 */
  captured: number;
  /** 已产出的 patch 数 */
  batches: number;
  /**
   * 被**丢掉**的操作数。
   *
   * 正常情况恒为 0：操作从来不会因为"这一批攒太多了"被丢 —— 攒够上限就立刻冲出去
   * （丢一条就等于两头状态分叉，而且看不出来）。这里只统计真正无法表达的东西
   * （比如 rrweb 认不出的属性形态）。
   */
  dropped: number;
  /** 已建立过的基线次数（初次 + 每次重新对齐） */
  baselines: number;
  /** 最近一批的产出时间，未产出为 null */
  lastAt: number | null;
}

/**
 * content script → background 的消息。
 * 用 type 前缀 ksw: 避免和页面上其它扩展的消息撞车。
 */
export type RuntimeMessage =
  | { type: 'ksw:dom-patch'; patch: DomPatchDraft }
  | { type: 'ksw:share-started'; url: string }
  | { type: 'ksw:share-stopped'; url: string }
  /**
   * popup → content script："你这个标签页是哪个页面？"
   *
   * 这条方向（tabs.sendMessage）必须存在，因为工具栏弹窗**看不到宿主页面的 DOM**：
   * 它自己的 location 是 chrome-extension://，又没声明 tabs/host 权限去读 tab.url。
   * 问 content script 是唯一零权限的通道 —— 它本来就有完整 DOM 访问权。
   */
  | { type: 'ksw:get-page-info' }
  /**
   * background → content script："把你的整页基线交出来"。
   *
   * 基线只能由**页面里的** content script 拿（它是唯一能看到宿主 DOM、
   * 也是唯一拿着 rrweb 镜像的地方），而房主在 background 里 —— 所以方向是 background 来问它。
   * 同步应答：采集器手里本来就常驻着最近一份基线，取出来即可，不需要重新遍历 DOM。
   */
  | { type: 'ksw:capture-snapshot' }
  /**
   * content script → background：本标签页的页面信息（初始化时一次，之后每次地址变化一次）。
   *
   * SPA 跳转不会触发任何原生事件，只能靠轮询 + popstate/hashchange 发现；发现之后要立刻
   * 告诉 background —— 它决定"这是不是正在共享的那个标签页"，是的话就更新共享目标、
   * 朝订阅者推一条跳转事件、并重新交一份整页快照（房间不关）。
   *
   * 整页跳转（真刷新）也走同一条消息：content script 是全新的 JS 上下文，
   * 初始化时发一次，background 就能把"房主换页了"补上。
   */
  | { type: 'ksw:page-changed'; page: PageInfo }
  /**
   * content script → background：**"现在被用户看着的是我这个标签页"**。
   *
   * 共享目标要跟着用户当前看的标签页走。而"我在被看着"这件事只有页面自己知道得最准：
   * `document.visibilityState` 管同一窗口内的标签切换，window 的 focus / blur 管窗口切换。
   * （background 那边另有 `tabs.onActivated` 兜底，两条路都汇到同一个切换逻辑上。）
   *
   * **为什么不复用 `ksw:page-changed`**：两者的语义不同，混起来会白抓快照。
   *   - `page-changed`＝"我的地址变了"→ 即使我本来就是共享目标，也要换一份新基线；
   *   - `tab-foreground`＝"该把共享目标换成我"→ 我本来就**是**目标时什么都不用做。
   * 合成一条之后，来回切标签页就会每次重抓一份整页快照（可能半兆）。
   */
  | { type: 'ksw:tab-foreground'; page: PageInfo };

/** 运行时判定一条消息是不是我们自己的 */
export function isRuntimeMessage(value: unknown): value is RuntimeMessage {
  if (!value || typeof value !== 'object') return false;
  const type = (value as { type?: unknown }).type;
  return typeof type === 'string' && type.startsWith('ksw:');
}

/* ---------------------------------------------------------------------------
 * UI ⇄ background 的桥
 *
 * 为什么连接必须由 background 持有：MV3 里 content script 发出的请求走的是**宿主页面的
 * origin**，拿不到扩展的身份，跨域协商会被目标站点自己的 CORS 挡掉（弹窗倒是有扩展 origin，
 * 但它一关就没了）。所以唯一常驻的 background 是唯一能放 HubConnection 的地方。
 *
 * UI 侧（content script / popup）通过一条长连接 Port 接进来：下发命令、接收推送。
 * 用 Port 而不是 sendMessage，是因为 content script 收不到 background 用
 * runtime.sendMessage 广播的消息（那个只能到达扩展页面），而 Port 两个方向都通，
 * 且端口断开本身就是"这个上下文没了"的可靠信号。
 * ------------------------------------------------------------------------- */

/** 桥的 Port 名，两端都要用 */
export const BRIDGE_PORT = 'ksw-bridge';

/**
 * 空快照：还没连上服务端时的初值。
 * 不是假数据，是真的"什么都没有"——UI 该显示空态就显示空态。
 */
export const EMPTY_SNAPSHOT: ShareServerSnapshot = {
  connection: 'offline',
  error: null,
  shareCode: '',
  peers: [],
  rooms: [],
};

/** background 维护的服务端状态快照。命令的返回和推送都直接给整份，UI 整体替换，不用自己合并 */
export interface ShareServerSnapshot {
  connection: ConnectionState;
  /** 连接失败的原因（给设置页显示），正常为 null */
  error: string | null;
  /** 正在共享时的房间号；未共享为空串 */
  shareCode: string;
  /** 自己所在房间的成员（含自己）。只有房主会用到它来显示"几人观看" */
  peers: Peer[];
  /** 房间列表快照 */
  rooms: SharedRoom[];
}

/** UI → background 的命令 */
export type BridgeCommand =
  | { name: 'connect' }
  | { name: 'disconnect' }
  | { name: 'get-snapshot' }
  | { name: 'list-rooms' }
  | {
      name: 'start-sharing';
      request: PageInfo;
      /**
       * 真正要采集的标签页。
       *
       * 从页面里（浮层）发起时不用给 —— background 用发起方那条 Port 自带的 tabId。
       * 从 popup 发起时必须给：popup 没有标签页，`port.sender.tab` 是空的，
       * background 会拿到 null，于是所有 DOM 批次都会被"这个标签页没在共享"挡掉，
       * 房间建出来了却一个变更都同步不出去。
       */
      tabId?: number;
    }
  | { name: 'stop-sharing' }
  | { name: 'subscribe'; roomCode: string; displayName?: string }
  | { name: 'unsubscribe'; roomCode: string }
  /**
   * 开始观看：订阅这个房间，并把**扩展自己的观看页**在新标签页里打开
   * （`chrome-extension://<id>/viewer.html?room=…`）。
   *
   * 不打开被共享的那个 URL：真实的、跨域的页面地址不一定能用新标签页复现，
   * 而且观众要看到的是"共享方此刻的 DOM"，不是"自己去访问那个站点拿到的 DOM"
   * （后者会因为登录态/AB/动态内容而和基线对不上）。观看页是扩展自己的页面，
   * 一定能打开，内容由整页快照 + 增量重建。
   */
  | { name: 'watch-room'; roomCode: string }
  /**
   * 观看页启动：订阅（幂等）+ 取一份**新鲜**的整页快照。
   *
   * 必须现取而不是用存着的那份：增量只推给"已经订阅"的连接，所以旧快照 +
   * 订阅之后的增量，中间那段变更永远补不回来（页面会缺一块，而且看不出来）。
   * 返回的 `BridgeResult.dom` 就是基线；拿不到时为 null。
   */
  | { name: 'viewer-load'; roomCode: string }
  /**
   * 观看页发现自己**跟不上了**：请房主重新交一份基线。
   *
   * 两个触发条件，都是"静默错位"的可见化：
   *   - 收到的 patch 声明的 base 不等于本地版本（中间缺了东西）；
   *   - 版本对得上，但有操作落空了（目标 id 在镜像里找不到）。
   * 两种都不能硬着头皮继续叠 —— 继续叠改坏的是别的位置，而且不会报错。
   */
  | { name: 'request-baseline'; roomCode: string };

/** 命令的返回：回一份最新快照 + 是否成功 */
export interface BridgeResult {
  ok: boolean;
  /** 失败原因，成功为 null。服务端抛的领域错误（如"房间号不存在"）原样带过来 */
  error: string | null;
  snapshot: ShareServerSnapshot;
  /** 仅 `viewer-load` 会带：服务端存着的那份基线。其它命令为 undefined */
  dom?: DomSnapshot | null;
}

/**
 * background → UI 的推送。
 *
 * `patch` 只发给"订阅过这个房间的那条 Port" —— 扩展是全站注入的，
 * 若向所有 content script 广播，开 20 个标签页就要为每个批次扇出 20 次，纯浪费。
 */
export type BridgeEvent =
  | { name: 'snapshot'; snapshot: ShareServerSnapshot }
  | { name: 'share-event'; event: ShareEventDto }
  | { name: 'patch'; roomCode: string; patch: DomPatch }
  /**
   * 新的整页快照到了，**重新画一遍**（不是叠增量）。
   *
   * 两种来源：房主换了页面（跳转），或者我们这个连接是刚接上、第一份基线才取到。
   * 观众端收到它必须整个换掉 iframe 里的内容 —— 旧快照 + 增量 ≠ 新页面。
   */
  | { name: 'dom-snapshot'; roomCode: string; dom: DomSnapshot };

/** Port 上跑的报文：命令、返回、推送三种 */
export type BridgeWireMessage =
  | { kind: 'command'; id: number; command: BridgeCommand }
  | { kind: 'result'; id: number; result: BridgeResult }
  | { kind: 'event'; event: BridgeEvent };
