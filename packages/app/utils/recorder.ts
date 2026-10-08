import { record } from 'rrweb';
import { EventType, IncrementalSource, NodeType } from '@rrweb/types';
import type {
  attributeMutation,
  listenerHandler,
  mutationCallbackParam,
  serializedNodeWithId,
  styleOMValue,
} from '@rrweb/types';

import { DomOpKinds, NodeKinds } from '@/types';
import type { BaselinePayload, DomOp, ObserverStats, SerializedNode } from '@/types';

/**
 * 宿主页面的采集器 —— 这一版建立在 **rrweb 的镜像之上**。
 *
 * 它替掉了原来两个文件（`domObserver.ts` 发选择器路径、`snapshot.ts` 抓 outerHTML），
 * 因为那套做法有两个绕不开的毛病：
 *   1. 选择器 `tag > tag:nth-of-type(n)` 是**按位置猜**，两端结构差一丝就指向错的节点，
 *      而且猜错了**不报错** —— 页面从此静默跑偏；
 *   2. `childList` 只带"加了几个 / 减了几个"，移除无法复现、插入只能追加到末尾。
 *
 * 现在每个节点都有 rrweb 分配的 id（元素 / 文本 / 注释共用一套编号空间），
 * 操作全部按 id 寻址：
 *   - `insert` 落在一棵**带 id 的序列化子树**上，不存在"转义 / 截断把标签弄坏"；
 *   - `remove` 带的是节点 id，不是"删了几个"；
 *   - 属性删除与"设成空串"分得开（`set` / `remove` 两个字段）。
 *
 * ## 为什么用 `record()` 而不是 `snapshot()`
 *
 * `record()` 内部自己建一个 Mirror 并 `mirror.reset()`，**不接受外部传入的镜像**；
 * 而 `snapshot()` 接受。两个都调 = 两套独立的 id 计数器 → id 撞车。
 * 所以基线不自己抓：`record()` 启动时**同步**发出的第一个 `FullSnapshot` 就是基线，
 * 重新建立基线 = 重启 `record()`（镜像重置、id 重新编号，正好对应"基线即重置"的语义）。
 *
 * ## 采集不到的东西
 *
 * 只认 `IncrementalSource.Mutation`。鼠标 / 滚动 / 视口 / canvas 那些事件照常产生
 * （rrweb 的内部钩子管着），但我们一律丢掉 —— 共享的是 DOM，不是操作录像。
 */

/** 默认一批操作的聚合窗口（毫秒）。攒一下能显著减少消息条数。 */
const DEFAULT_FLUSH_DELAY = 120;

/**
 * 单批操作上限。到顶就**立刻冲出去**，而不是丢掉多余的。
 *
 * 这条线必须与服务端的 `MaxOpsPerPatch` 对齐（服务端默认 500）。丢一条操作就等于
 * 两头状态分叉，而且**看不出来** —— 所以这里的策略永远是"分多次发"，不是"超出就扔"。
 */
const DEFAULT_MAX_OPS_PER_PATCH = 400;

export interface RecorderOptions {
  /** 建立基线时回调（初次启动、以及每次重新对齐）。 */
  onBaseline: (baseline: BaselinePayload) => void;
  /** 攒好一批操作时回调。 */
  onOps: (ops: DomOp[]) => void;
  /** 统计变化时回调（UI 展示用）。 */
  onStats?: (stats: ObserverStats) => void;
  /** 聚合窗口（毫秒）。 */
  flushDelay?: number;
  /** 单批操作上限；到顶立刻冲出去。 */
  maxOpsPerPatch?: number;
}

export interface Recorder {
  /** 开始采集（幂等）。启动时会立刻产出一份基线。 */
  start: () => void;
  /** 停止采集，并把窗口里剩下的操作冲出去。 */
  stop: () => void;
  /**
   * 重新建立基线。
   *
   * 先把攒着的操作冲出去（它们是**旧基线**上的增量，必须按原顺序先发），
   * 再重启 `record()`：镜像重置、id 从 1 重新编号。
   *
   * 服务端拒收（base 对不上）之后就靠它对齐 —— 这正是把"静默跑偏"换成
   * "一次明确的重建"的那个动作。
   */
  restart: () => void;
  /** 取当前基线。还没建立（或页面根节点不存在）时为 null。 */
  capture: () => BaselinePayload | null;
  /** 当前统计。 */
  getStats: () => ObserverStats;
}

/** 内联事件处理器一律不传：观众页那个 iframe 没有 allow-scripts 本来就跑不起来，这里是第二道闸。 */
function isEventHandler(name: string): boolean {
  return name.length > 2 && name[0] === 'o' && name[1] === 'n';
}

/**
 * 把 rrweb 的 `attributes`（值可能是 string / number / true / null）归一成"全字符串"。
 *
 * 归一是**无损**的：rrweb 重建时本来就会 `String(value)` 一次，所以两端看到的结果一样。
 * 换来的是我们这边的类型不用处理 JSON 的标量种类。
 */
function normalizeAttrs(
  raw: Record<string, string | number | true | null>,
): Record<string, string> | undefined {
  const out: Record<string, string> = {};

  for (const [name, value] of Object.entries(raw)) {
    if (isEventHandler(name)) continue;
    // rrweb 内联样式表时用的私有键，不是真属性
    if (name === '_cssText') continue;
    if (value === null) continue;
    if (value === true) {
      // 布尔属性：rrweb 落属性时就是空串
      out[name] = '';
      continue;
    }
    out[name] = String(value);
  }

  return Object.keys(out).length ? out : undefined;
}

/* --------------------------------------------------------------------------
 * style 属性：rrweb 有时发的是"紧凑 diff"而不是完整字符串
 *
 * `record/mutation.ts` 里会把 `style` 压成 `{ 属性名: 新值 }`（比整串短才压），
 * 删除的记 `false`，带 `!important` 的记 `[值, 'important']`。而我们的协议里
 * `attrs` 落的是**完整属性值**，所以必须自己把它拼回整串。
 *
 * 拼的前提是有上一次的完整状态 —— 而 `record()` 的镜像不对外，拿不到节点本身。
 * 所以这里自己维护一份 `id → 属性表`，从基线树和每个新增子树播种。
 * 只要两头都按同一份重建，后续的 diff 就能一直咬合。
 * ------------------------------------------------------------------------ */

function parseStyle(text: string): Map<string, string> {
  const props = new Map<string, string>();

  for (const part of text.split(';')) {
    const trimmed = part.trim();
    if (!trimmed) continue;
    const colon = trimmed.indexOf(':');
    if (colon <= 0) continue;
    props.set(trimmed.slice(0, colon).trim(), trimmed.slice(colon + 1).trim());
  }

  return props;
}

function stringifyStyle(props: Map<string, string>): string {
  return [...props].map(([name, value]) => `${name}: ${value}`).join('; ');
}

class StyleTracker {
  private readonly state = new Map<number, Map<string, string>>();

  /**
   * 播种**单个**节点。
   *
   * 刻意不递归：调用方（`sanitize`）本来就在逐节点遍历，这里再递归一次会让
   * 整棵树变成 O(n²) —— 基线动辄上万个节点，那一下就是几千万次操作。
   */
  seedSelf(node: SerializedNode): void {
    if (node.type !== NodeKinds.Element) return;
    const style = node.attributes?.style;
    this.state.set(node.id, style ? parseStyle(style) : new Map());
  }

  /** 应用一份紧凑 diff，返回拼好的完整 style 字符串。 */
  apply(id: number, diff: styleOMValue): string {
    let props = this.state.get(id);
    if (!props) {
      // 没见过这个节点（基线里被裁掉了，或者两端已经分叉）。从空开始比乱猜安全：
      // 拼出来的值可能不全，但下一个完整字符串形态的 diff 会自动把它纠正回来。
      props = new Map();
      this.state.set(id, props);
    }

    for (const [name, value] of Object.entries(diff)) {
      if (value === false) {
        props.delete(name);
      } else if (typeof value === 'string') {
        props.set(name, value);
      } else if (Array.isArray(value)) {
        // [值, 优先级]
        const [text, priority] = value as [string, string];
        props.set(name, priority === 'important' ? `${text} !important` : text);
      }
    }

    return stringifyStyle(props);
  }

  /** 节点被摘掉了，状态跟着丢（id 永不复用，留着只是占内存）。 */
  forget(id: number): void {
    this.state.delete(id);
  }
}

/** 把 rrweb 交出来的节点树转成我们的线格式：属性归一 + 去掉内联事件处理器 + 播种 style。 */
function sanitize(node: serializedNodeWithId, styles: StyleTracker): SerializedNode {
  const out: SerializedNode = { id: node.id, type: node.type };

  // 按 type 收窄，不能按"有没有 tagName / textContent"去猜：
  // 文本与注释的唯一区别就是 type，猜错会让观众端把注释重建成一段文字。
  switch (node.type) {
    case NodeType.Document:
      if (node.compatMode) out.compatMode = node.compatMode;
      out.childNodes = node.childNodes.map((child) => sanitize(child, styles));
      break;

    case NodeType.DocumentType:
      // 三个字段**恒给**，空串也给。rrweb 的 buildNode 是
      // `createDocumentType(n.name || "html", n.publicId, n.systemId)`，而 WebIDL 会把
      // 缺省的 `undefined` 转成字符串 `"undefined"` —— 省掉这两个字段，观众端拿到的就是
      // `<!DOCTYPE html PUBLIC "undefined" "undefined">`。rrweb 自己的序列化器同样恒给。
      out.name = node.name;
      out.publicId = node.publicId ?? '';
      out.systemId = node.systemId ?? '';
      break;

    case NodeType.Element: {
      out.tagName = node.tagName;

      const attributes = normalizeAttrs(
        node.attributes as Record<string, string | number | true | null>,
      );
      if (attributes) out.attributes = attributes;

      if (node.isSVG) out.isSVG = true;
      if (node.needBlock) out.needBlock = true;
      if (node.isCustom) out.isCustom = true;

      // **空数组也必须写出来。**
      //
      // rrweb 的 `buildNodeWithSN()` 对文档与元素是无条件
      // `for (const childN of n.childNodes)` 的（源码里没有 `if (n.childNodes)` 兜底），
      // 所以"没有子节点就不写这个字段"会让观众端直接
      // `TypeError: n.childNodes is not iterable`，**整份基线一个字都建不出来**。
      // 页面上随便一个 <meta> / <br> / <img> / 空 <div> 就够触发 —— 是必现，不是边缘情况。
      // （rrweb 自己也靠这条不变量活着：它落到 <textarea value> 时甚至会主动 `n.childNodes = []`。）
      out.childNodes = node.childNodes.map((child) => sanitize(child, styles));
      break;
    }

    case NodeType.Text:
    // `isStyle` **故意不带上**：rrweb 见到它就会拿 `adaptCssForReplay()` 改写 <style> 的
    // 文本。那是给它自己的回放引擎用的（改写 hover 规则），我们不用那个引擎，
    // 反而要宿主页原本的 CSS 一字不动地过去。
    case NodeType.CDATA:
    case NodeType.Comment:
      out.textContent = node.textContent;
      break;

    default:
      break;
  }

  // shadow DOM 相关的附加字段，所有种类都可能带
  if (node.rootId) out.rootId = node.rootId;
  if (node.isShadowHost) out.isShadowHost = true;
  if (node.isShadow) out.isShadow = true;

  styles.seedSelf(out);
  return out;
}

/** 一条 `attributes` 变更 → 一条 `attrs` 操作。没有可落的东西时返回 null。 */
function translateAttrs(mutation: attributeMutation, styles: StyleTracker): DomOp | null {
  const set: Record<string, string> = {};
  const remove: string[] = [];

  for (const [name, value] of Object.entries(mutation.attributes)) {
    // rrweb 内联样式表时用的私有键，不是真属性。我们没开 inlineStylesheet，正常不会出现；
    // 万一出现了也绝不能当普通属性落下去。
    if (name === '_cssText') continue;
    if (isEventHandler(name)) continue;

    // 声明上这里只会有 string | styleOMValue | null，但 rrweb 内部确实可能给出
    // number / true（布尔属性），所以按 unknown 收，别让类型挡住真实数据。
    const raw: unknown = value;

    if (raw === null || raw === undefined) {
      // 属性被删了。与"设成空串"是两件事，必须分开表达（旧协议里这两者分不出来）。
      remove.push(name);
      continue;
    }

    if (name === 'style' && typeof raw === 'object') {
      // 紧凑 diff 形态：得自己拼回完整字符串
      set.style = styles.apply(mutation.id, raw as styleOMValue);
      continue;
    }

    if (typeof raw === 'string') {
      set[name] = raw;
      continue;
    }

    if (typeof raw === 'number') {
      set[name] = String(raw);
      continue;
    }

    if (raw === true) {
      set[name] = '';
      continue;
    }

    // 认不出的形态：丢掉，并计入 dropped。猜一个值出来只会静默改坏页面。
  }

  if (!Object.keys(set).length && !remove.length) return null;

  const op: DomOp = { op: DomOpKinds.Attrs, id: mutation.id };
  if (Object.keys(set).length) op.set = set;
  if (remove.length) op.remove = remove;
  return op;
}

/**
 * 把 rrweb 的一批 mutation 翻成我们的操作序列。
 *
 * **顺序不能重排**，照抄 rrweb 自己的顺序（文本 → 属性 → 移除 → 新增）。
 * 新增那一段尤其严格：rrweb 已经保证"父节点排在子节点前面"（源码里的 parentId requirement）
 * 与"锚点排在新增节点前面"（nextId requirement），接收方必须照这个顺序落，
 * 否则会去引用一个还没建出来的 id。
 */
function translateMutation(
  mutation: mutationCallbackParam,
  styles: StyleTracker,
  stats: ObserverStats,
): DomOp[] {
  const ops: DomOp[] = [];

  for (const text of mutation.texts) {
    if (text.value === null) continue;
    ops.push({ op: DomOpKinds.Text, id: text.id, text: text.value });
  }

  for (const attribute of mutation.attributes) {
    const op = translateAttrs(attribute, styles);
    if (op) ops.push(op);
    else stats.dropped += 1;
  }

  for (const removed of mutation.removes) {
    ops.push({ op: DomOpKinds.Remove, id: removed.id, parent: removed.parentId });
    styles.forget(removed.id);
  }

  for (const added of mutation.adds) {
    ops.push({
      op: DomOpKinds.Insert,
      // insert 的目标是**父节点**，新节点插到锚点（next）前面
      id: added.parentId,
      next: added.nextId ?? null,
      nodes: [sanitize(added.node, styles)],
    });
  }

  return ops;
}

export function createRecorder(options: RecorderOptions): Recorder {
  const flushDelay = options.flushDelay ?? DEFAULT_FLUSH_DELAY;
  const maxOpsPerPatch = options.maxOpsPerPatch ?? DEFAULT_MAX_OPS_PER_PATCH;

  const stats: ObserverStats = {
    running: false,
    captured: 0,
    batches: 0,
    dropped: 0,
    baselines: 0,
    lastAt: null,
  };

  let styles = new StyleTracker();
  let stopRecording: listenerHandler | null = null;
  let baseline: BaselinePayload | null = null;
  let queue: DomOp[] = [];
  let timer: number | null = null;

  function reportStats() {
    options.onStats?.({ ...stats });
  }

  function flush() {
    if (timer !== null) {
      window.clearTimeout(timer);
      timer = null;
    }
    if (!queue.length) return;

    const ops = queue;
    queue = [];

    stats.batches += 1;
    stats.lastAt = Date.now();

    try {
      options.onOps(ops);
    } finally {
      reportStats();
    }
  }

  function schedule() {
    if (timer !== null) return;
    timer = window.setTimeout(flush, flushDelay);
  }

  /**
   * rrweb 的 emit 回调。这里只做两件事：接住基线、把 mutation 翻成操作。
   *
   * 其余事件（鼠标 / 滚动 / 视口 / 输入 / canvas）一律丢掉 —— 共享的是 DOM 状态，
   * 不是操作录像。rrweb 的内部钩子照常产生它们，但不会出现在我们的链路上。
   */
  function emit(event: { type: EventType } & Record<string, unknown>) {
    if (event.type === EventType.FullSnapshot) {
      const data = event.data as { node: serializedNodeWithId } | undefined;
      if (!data?.node) return;

      styles = new StyleTracker();
      const root = sanitize(data.node, styles);

      baseline = {
        root,
        url: location.href,
        title: document.title || location.hostname,
        doctype: document.compatMode === 'BackCompat' ? '' : 'html',
        // 裁剪是服务端那层的事（它按节点数与字符预算决定），采集端不假装知道
        truncated: false,
      };

      stats.baselines += 1;
      options.onBaseline(baseline);
      reportStats();
      return;
    }

    if (event.type !== EventType.IncrementalSnapshot) return;

    const data = event.data as { source: IncrementalSource } | undefined;
    if (data?.source !== IncrementalSource.Mutation) return;

    const ops = translateMutation(data as unknown as mutationCallbackParam, styles, stats);
    if (!ops.length) return;

    stats.captured += ops.length;
    queue.push(...ops);

    // 攒够一批就立刻冲出去。**不是丢掉多余的** —— 丢一条就等于两头分叉。
    if (queue.length >= maxOpsPerPatch) flush();
    else schedule();
  }

  function start() {
    if (stopRecording) return;

    // 显式开 slimDOMOptions：它默认是 `{}`（什么都不剔），不显式打开的话每个 <script>
    // 和每条注释都会跟着基线传到观众端。
    //
    // inlineStylesheet 关掉：开着会把 CSS 文本整段内联进基线，体积涨得厉害，
    // 而 `transformAttribute` 已经把 src / href 这些相对地址绝对化了，
    // 观众页那个 iframe 直接按原地址加载样式表即可。
    //
    // blockClass 用 rrweb 自带的机制排除我们自己的控件宿主节点 ——
    // 比手工在 observer 里过滤干净，也从根上断掉"采集 → 渲染 → 再采集"的环。
    const handler = record({
      emit,
      slimDOMOptions: true,
      blockClass: 'rr-block',
      inlineStylesheet: false,
      // 打进输入框的内容不进基线。默认只遮 password，其余（邮箱、卡号…）明文过去，
      // 而共享是给**别人**看的页面，不该顺带把用户敲进去的东西也交出去。
      maskAllInputs: true,
      // 鼠标轨迹对复现 DOM 毫无用处，关掉能省掉一大截高频回调
      sampling: { mousemove: false },
    });

    if (!handler) return;
    stopRecording = handler;
    stats.running = true;
    reportStats();
  }

  function stop() {
    if (!stopRecording) return;
    flush();
    stopRecording();
    stopRecording = null;
    stats.running = false;
    reportStats();
  }

  function restart() {
    // 先把旧基线上的增量发出去：它们描述的是"到刚才为止"的变化，
    // 而基线反映的是"现在"，顺序反了就会把基线之后的状态又按旧增量改一遍。
    flush();
    stop();
    start();
  }

  function capture(): BaselinePayload | null {
    return baseline ? { ...baseline, root: baseline.root } : null;
  }

  return {
    start,
    stop,
    restart,
    capture,
    getStats: () => ({ ...stats }),
  };
}
