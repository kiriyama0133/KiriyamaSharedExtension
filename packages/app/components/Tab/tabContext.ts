import type { ComputedRef, InjectionKey } from 'vue';

/** Tabs 通过 provide/inject 把选中态下发给 TabPanel，不用逐层传 props */
export interface TabsContext {
  /** 当前选中的 tab value */
  active: ComputedRef<string>;
  /** 切换选中项 */
  setActive: (value: string) => void;
  /** id 前缀，保证同一页面上多组 Tabs 的 aria 关联不冲突 */
  uid: string;
}

export const TABS_INJECTION_KEY: InjectionKey<TabsContext> = Symbol('ksw-tabs');
