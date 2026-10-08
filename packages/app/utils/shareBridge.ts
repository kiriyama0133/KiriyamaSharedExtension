import { BRIDGE_PORT, EMPTY_SNAPSHOT } from '@/types';
import type {
  BridgeCommand,
  BridgeEvent,
  BridgeResult,
  BridgeWireMessage,
} from '@/types';

/**
 * UI 侧（content script / popup）与 background 之间的桥。
 *
 * 为什么要有这一层：连接只能由 background 持有（见 types/index.ts 里的说明），
 * 而 UI 需要下发命令、接收推送。这里把 Port 的报文往返包成 Promise，
 * 让上层的 `useShareSession` 看起来像在调本地函数。
 *
 * 用长连接 Port 而不是 `runtime.sendMessage`：后者从 background 发给 content script
 * 根本收不到（`tabs.sendMessage` 才行，还要额外的 host 权限），而 Port 两个方向都通。
 */

/** 单条命令的超时。background 被回收或服务端卡住时，别让 UI 一直转圈。 */
const REQUEST_TIMEOUT = 8000;

type EventListener = (event: BridgeEvent) => void;

let port: Browser.runtime.Port | null = null;
let commandSeq = 0;

const pending = new Map<number, (result: BridgeResult) => void>();
const listeners = new Set<EventListener>();

/** 桥不可用时的统一返回 */
function unavailable(reason: string): BridgeResult {
  return { ok: false, error: reason, snapshot: { ...EMPTY_SNAPSHOT } };
}

function settleAll(reason: string) {
  for (const [id, resolve] of [...pending]) {
    pending.delete(id);
    resolve(unavailable(reason));
  }
}

function dispatch(event: BridgeEvent) {
  // 复制一份再遍历：监听器里可能会取消订阅
  for (const listener of [...listeners]) {
    try {
      listener(event);
    } catch {
      /* 一个订阅者出错不该拖垮其它人 */
    }
  }
}

function handleMessage(raw: unknown) {
  if (!raw || typeof raw !== 'object') return;
  const message = raw as BridgeWireMessage;

  switch (message.kind) {
    case 'result': {
      const resolve = pending.get(message.id);
      if (!resolve) return;
      pending.delete(message.id);
      resolve(message.result);
      break;
    }

    case 'event':
      dispatch(message.event);
      break;

    default:
      break;
  }
}

/** 打开（或复用）到 background 的 Port。拿到 Port 本身就会把 service worker 唤醒。 */
function openPort(): Browser.runtime.Port | null {
  if (port) return port;

  try {
    port = browser.runtime.connect({ name: BRIDGE_PORT });
  } catch {
    // 非扩展环境（例如把组件当普通网页跑）没有 runtime
    return null;
  }

  port.onMessage.addListener(handleMessage);
  port.onDisconnect.addListener(() => {
    port = null;
    // 走到这里通常是 background 的 service worker 被回收了。
    // 挂着的命令直接判失败，下一次调用会重新 connect 把它唤醒。
    settleAll('后台连接已断开');
  });

  return port;
}

/** 下发一条命令，等 background 回一份最新快照 */
export async function request(command: BridgeCommand): Promise<BridgeResult> {
  const active = openPort();
  if (!active) return unavailable('当前环境没有扩展消息通道');

  const id = (commandSeq += 1);

  return new Promise<BridgeResult>((resolve) => {
    const timer = window.setTimeout(() => {
      if (pending.delete(id)) resolve(unavailable('后台没有响应'));
    }, REQUEST_TIMEOUT);

    pending.set(id, (result) => {
      window.clearTimeout(timer);
      resolve(result);
    });

    try {
      active.postMessage({ kind: 'command', id, command } satisfies BridgeWireMessage);
    } catch (error) {
      window.clearTimeout(timer);
      pending.delete(id);
      port = null;
      resolve(unavailable(error instanceof Error ? error.message : String(error)));
    }
  });
}

/**
 * 订阅 background 的推送。返回取消订阅的函数。
 *
 * 调用它会顺带把 Port 打开 —— 推送只在端口连着的时候才收得到。
 */
export function onBridgeEvent(listener: EventListener): () => void {
  listeners.add(listener);
  openPort();

  return () => {
    listeners.delete(listener);
  };
}
