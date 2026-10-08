import { buildNodeWithSN, createCache, createMirror, createSandboxedIframe, rebuild } from 'rrweb-snapshot';
import type { Mirror, BuildCache } from 'rrweb-snapshot';
import type { serializedNodeWithId } from '@rrweb/types';

import { DomOpKinds, NodeKinds } from '@/types';
import type { DomOp, DomPatch, DomSnapshot, SerializedNode } from '@/types';

/**
 * 复现层 —— 按**节点 id** 把基线与增量重建到观众页的 iframe 里。
 *
 * 它替掉了原来那个按 CSS 选择器路径 `querySelector` 的版本。那一版有三个
 * 结构性问题，都不是"再小心一点"能解决的：
 *   1. 路径是**按位置**拼的，两端结构差一丝就指到别的节点上，而且指错了不报错；
 *   2. 移除只带了"删了几个"，根本没法复现；
 *   3. 新增只能追加到末尾（没有兄弟位置信息）。
 * 现在每条操作都带发送方分配的 id，落不下去就是**指不到**（不是指错），
 * 于是"丢失"变成一个可以被发现的信号，而不是静默的错位。
 *
 * ## 两个必须守住的点
 *
 * **一、iframe 由 rrweb 创建，不要自己写。** `createSandboxedIframe()` 会把 iframe 的
 * sandbox **强制**设成恰好 `allow-same-origin`，并把它的 document 登记成"允许重建的目标"。
 * `rebuild()` 内部有一道 `assertRebuildTargetAllowed()`：目标文档没登记过就直接抛。
 * 这是 rrweb 的刻意设计 —— 重建的是**不可信**的数据，不允许倒进一个正常文档里。
 *
 * `allow-same-origin` 且**不带 `allow-scripts`** 正是我们要的：基线里带的脚本与内联
 * 事件处理器一律不执行，父页面又拿得到 contentDocument。**不要**为了省事改用 rrweb 的
 * `Replayer` —— 它的 iframe 是 `sandbox="allow-same-origin allow-scripts"`，
 * 那等于把"页面里的脚本会在观众端跑起来"这件事重新引进来。
 *
 * **二、镜像必须跨 patch 一直活着。** rrweb 处理节点移动时会把配对的 remove 消掉、
 * 只发一条 insert（源码里 `movedMap` 那段）。接收方拿到这条 insert 时，只要 id 已经在
 * 镜像里，`buildNodeWithSN()` 会**直接返回那个已有的 DOM 节点**而不是新建一个 ——
 * 于是移动在接收端就是一次真正的 DOM 移动。镜像要是每次重建，移动就会变成**内容翻倍**。
 */

/**
 * 复现结果的判定。
 *
 * 刻意不是 bool：调用方要区分"版本对不上（该重新要基线）"和"操作落空了
 * （也该重新要基线）"，以及"页面本来就是残缺的（落空是预期内的，别再要了）"。
 */
export interface ApplyOutcome {
  /** 有没有通过版本闸门 */
  accepted: boolean;
  /** 本地版本对不上的操作条数（没通过闸门时不用看别的） */
  stale: boolean;
  /** 版本对上了、但落不下去的操作条数 */
  missed: number;
  /** 应用之后本地的版本 */
  version: number;
}

export interface Replica {
  /** 用一份基线**重置**整个复现状态。换基线 = 整个重画，不是往上叠增量。 */
  reset: (snapshot: DomSnapshot) => void;
  /** 应用一条 patch。 */
  apply: (patch: DomPatch) => ApplyOutcome;
  /** 本地已经应用到的版本。 */
  version: () => number;
  /** 当前基线是不是残缺的（被服务端裁剪过）。 */
  isTruncated: () => boolean;
  /** rrweb 建出来的那个 iframe。 */
  element: () => HTMLIFrameElement | null;
  /** 拆掉 iframe 与镜像。 */
  destroy: () => void;
}

export interface ReplicaOptions {
  /** iframe 挂到哪个元素下面。必须**已经连在文档上**，否则 rrweb 会拒绝。 */
  root: HTMLElement;
}

/** 节点类型用数字判断，**不要用 `instanceof`** —— 见 `applyOp` 里的说明。 */
const ELEMENT_NODE = 1;
const TEXT_NODE = 3;

/**
 * 交给 rrweb 之前，把"缺了就会炸"的字段补齐。
 *
 * 这三处都不是"少一点内容"，而是**硬失败**，且失败方式非常隐蔽 —— 观看页会永远停在
 * "正在等待共享方交出页面…"，因为异常是从 `rebuild()` 里抛出来的，而调用方那几行
 * 赋值（`hasBaseline = true`）根本没机会执行：
 *
 *   1. 文档 / 元素的 `childNodes`：`buildNodeWithSN()` 无条件 `for...of`，缺了就是
 *      `TypeError: n.childNodes is not iterable`。页面上一个 <br> 就够触发。
 *   2. `getTagName()` 对 `<link>` 会**无条件**读 `n.attributes._cssText`（没有空值兜底），
 *      所以没有属性对象的 link 也会炸。（其余标签走 `for (const name in n.attributes)`，
 *      那对 undefined 是安全的，不用补。）
 *   3. `createDocumentType(n.name, n.publicId, n.systemId)`：WebIDL 把 `undefined` 转成
 *      字符串 `"undefined"`，省掉就得到 `<!DOCTYPE html PUBLIC "undefined" ...>`。
 *
 * 采集端已经保证这几点（见 `sanitize()`），但**线上格式的不变量不能只靠发送方守**：
 * 服务端那道裁剪（`CopyWithin`）就会把空的 `childNodes` 吞掉，旧版本的对端也可能不守。
 * 一旦破了，用户看到的只是一个转不完的圈 —— 没有任何可诊断的信息。
 *
 * 就地补写：对象是结构化克隆过来的，本来就是我们自己的。
 */
function normalizeForRebuild(node: SerializedNode): void {
  if (node.type === NodeKinds.DocumentType) {
    node.name ??= 'html';
    node.publicId ??= '';
    node.systemId ??= '';
    return;
  }

  if (node.type === NodeKinds.Element) {
    if (!node.attributes && node.tagName === 'link') node.attributes = {};
  }

  // 只有文档与元素会被 rrweb 迭代子节点；文本 / 注释 / CDATA 到这儿就到底了
  if (node.type !== NodeKinds.Document && node.type !== NodeKinds.Element) return;

  if (!node.childNodes) node.childNodes = [];
  for (const child of node.childNodes) normalizeForRebuild(child);
}

export function createReplica(options: ReplicaOptions): Replica {
  let iframe: HTMLIFrameElement | null = null;
  /** iframe 那一边的文档。**跨 realm**，不要拿本页的 Document 去比。 */
  let doc: Document | null = null;
  let mirror: Mirror = createMirror();
  let cache: BuildCache = createCache();
  let version = 0;
  let truncated = false;

  /** iframe 只建一次，之后换基线都复用它（避免整块闪一下）。 */
  function ensureFrame(): Document | null {
    if (!iframe) {
      if (!options.root.isConnected) return null;
      iframe = createSandboxedIframe({ root: options.root });
      doc = iframe.contentDocument;
      blockNavigation();
    }
    return doc;
  }

  /**
   * 观众页不该被导航走。
   *
   * iframe 带了 sandbox（没开 allow-top-navigation），所以点链接只会换掉 iframe 自己 ——
   * 但那也够糟了：一换走就没有基线，后面所有增量都打在错的文档上。
   */
  function blockNavigation() {
    const doc = iframe?.contentDocument;
    if (!doc) return;

    doc.addEventListener(
      'click',
      (event) => {
        const target = event.target;
        // 跨 realm：iframe 里的元素不是本页的 Element 实例，所以按 closest 的能力判断
        const anchor = (target as Element | null)?.closest?.('a[href]');
        if (anchor) event.preventDefault();
      },
      true,
    );
  }

  function reset(snapshot: DomSnapshot) {
    const doc = ensureFrame();
    if (!doc) return;

    // 基线即重置：镜像与构建缓存一起换新，id 从发送方那一份重新开始。
    // 旧镜像留着会让"两套 id 混在一棵树上"，那是比丢一点内容严重得多的错。
    mirror = createMirror();
    cache = createCache();

    version = snapshot.version;
    truncated = snapshot.truncated;

    // rrweb 对几个字段是**无兜底**读的（见 normalizeForRebuild），缺一个就整份建不出来
    normalizeForRebuild(snapshot.root);

    rebuild(snapshot.root as unknown as serializedNodeWithId, {
      doc,
      mirror,
      cache,
      hackCss: true,
    });
  }

  /**
   * 落一条操作。返回 false 表示**这条落不下去** —— 目标 id 不在镜像里，
   * 说明两端已经分叉（或者这个节点在基线里就被裁掉了）。
   */
  function applyOp(op: DomOp): boolean {
    switch (op.op) {
      case DomOpKinds.Text: {
        const node = mirror.getNode(op.id);
        if (!node || node.nodeType !== TEXT_NODE) return false;
        (node as CharacterData).data = op.text ?? '';
        return true;
      }

      case DomOpKinds.Attrs: {
        const node = mirror.getNode(op.id);
        // 用 nodeType 而不是 instanceof：iframe 里的元素属于它自己的 realm，
        // `node instanceof Element` 在这里**永远是 false** —— 一个不会报错的错。
        if (!node || node.nodeType !== ELEMENT_NODE) return false;
        const el = node as Element;

        for (const [name, value] of Object.entries(op.set ?? {})) {
          el.setAttribute(name, value);
        }
        for (const name of op.remove ?? []) {
          el.removeAttribute(name);
        }
        return true;
      }

      case DomOpKinds.Remove: {
        const node = mirror.getNode(op.id);
        if (!node) return false;

        node.parentNode?.removeChild(node);
        // 从镜像里摘掉：之后的 insert 若引用这个 id，才会**落空**而不是复活一个游离节点
        mirror.removeNodeFromMap(node);
        return true;
      }

      case DomOpKinds.Insert: {
        const parent = mirror.getNode(op.id);
        if (!parent) return false;
        // 'rebuild' 那次已经建出 iframe 了；走到这里 doc 一定有值
        if (!doc) return false;

        // next 是**锚点节点 id**（插到它前面）；为 null 表示追加到末尾。
        // 指错了会指不到（上一条就返回 false），而不是指到隔壁那个无辜的节点上。
        let anchor: Node | null = op.next == null ? null : mirror.getNode(op.next);

        for (const serialized of op.nodes ?? []) {
          // 新插入的子树走的是同一个 buildNodeWithSN，同样吃那几个无兜底的字段
          normalizeForRebuild(serialized);

          // 关键：镜像传进去。id 已在镜像里时，rrweb 返回的是**已有的那个 DOM 节点**，
          // 于是移动就是一次真正的移动 —— 这也是 insert 能表达 move 的全部原因。
          const built = buildNodeWithSN(serialized as unknown as serializedNodeWithId, {
            doc,
            mirror,
            cache,
            hackCss: true,
          });

          if (!built) return false;

          parent.insertBefore(built, anchor);
          anchor = built.nextSibling;
        }

        return true;
      }

      default:
        return false;
    }
  }

  function apply(patch: DomPatch): ApplyOutcome {
    // 版本闸门。对不上就**整条丢掉**，绝不硬应用 ——
    // 在错的基线上落带 id 的操作，改坏的是别的位置，而且不会报错。
    if (patch.baseVersion !== version) {
      return { accepted: false, stale: true, missed: 0, version };
    }

    let missed = 0;
    for (const op of patch.ops) {
      if (!applyOp(op)) missed += 1;
    }

    // 落空了就不推进版本：这样后续 patch 会一直被闸门挡住，
    // 调用方那边只会看到"过期"，直到重新对齐为止 —— 不会继续在分叉的树上叠。
    if (missed === 0) {
      version = patch.version;
    }

    return { accepted: true, stale: false, missed, version };
  }

  function destroy() {
    iframe?.parentNode?.removeChild(iframe);
    iframe = null;
    doc = null;
    mirror = createMirror();
    cache = createCache();
    version = 0;
    truncated = false;
  }

  return {
    reset,
    apply,
    version: () => version,
    isTruncated: () => truncated,
    element: () => iframe,
    destroy,
  };
}
