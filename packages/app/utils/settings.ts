import { storage } from 'wxt/utils/storage';
import type { WidgetSettings } from '@/types';

/**
 * 设置的单一来源。
 *
 * 独立成模块而不是留在 `useShareSession` 里，是因为 **background 也要读它** ——
 * 连接由 background 持有，它得自己知道连哪个地址、要不要自动重连。
 * 若从 useShareSession 读就会把整个 Vue 拖进 service worker 的包。
 */

export const DEFAULT_SETTINGS: WidgetSettings = {
  /**
   * 本地开发时的默认地址。
   *
   * 填的是**完整的 Hub 地址**：服务端把共享 Hub 挂在 `/share` 下。
   * 不用手写 `ws://` —— SignalR 的 JS 客户端会自己按可用传输决定用 WebSocket 还是长轮询。
   */
  serverUrl: 'http://localhost:5000/share',
  autoReconnect: true,
  syncCursor: true,
  theme: 'auto',
};

/** 设置落盘到 browser.storage.local；非扩展环境（纯网页预览）降级为内存态 */
const settingsItem = (() => {
  try {
    return storage.defineItem<WidgetSettings>('local:ksw-settings', {
      fallback: DEFAULT_SETTINGS,
    });
  } catch {
    return null;
  }
})();

/** 读设置。读不到就退回默认值，不让存储故障挡住连接。 */
export async function loadSettings(): Promise<WidgetSettings> {
  if (!settingsItem) return { ...DEFAULT_SETTINGS };

  try {
    const saved = await settingsItem.getValue();
    return { ...DEFAULT_SETTINGS, ...(saved ?? {}) };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

/** 存设置。存不下也不影响本次会话。 */
export async function saveSettings(settings: WidgetSettings): Promise<void> {
  try {
    await settingsItem?.setValue({ ...settings });
  } catch {
    /* 忽略 */
  }
}
