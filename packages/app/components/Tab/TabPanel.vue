<script lang="ts" setup>
import { computed, inject, ref, watch } from 'vue';
import { TABS_INJECTION_KEY } from './tabContext';

/**
 * 单个 Tab 面板。
 *
 * 两件事分开管，别混在一个表达式里：
 *   - 是否可见：根节点用 v-show="active"，非选中就 display:none
 *   - 是否挂载：内容用 v-if，懒挂载——第一次被选中才渲染；之后
 *     keepAlive=true（默认）就一直留着，切走只是藏起来，
 *     组件内部状态（设置页填了一半的服务端地址、滚动位置等）不会丢
 *
 * keepAlive=false 时内容随选中销毁重建，换标签即重置状态。
 */
const props = withDefaults(
  defineProps<{
    /** 与 Tabs 的 TabItem.value 对应 */
    value: string;
    keepAlive?: boolean;
  }>(),
  { keepAlive: true },
);

const injected = inject(TABS_INJECTION_KEY);
if (!injected) throw new Error('[ksw] <TabPanel> 必须放在 <Tabs> 内部使用');
const ctx = injected;

const active = computed(() => ctx.active.value === props.value);
/** 是否被选中过（只置 true，不回退），决定要不要把内容渲染出来 */
const mountedOnce = ref(active.value);

watch(active, (now) => {
  if (now) mountedOnce.value = true;
});
</script>

<template>
  <div
    v-show="active"
    :id="`ksw-panel-${ctx.uid}-${value}`"
    role="tabpanel"
    :aria-labelledby="`ksw-tab-${ctx.uid}-${value}`"
    tabindex="0"
    class="ksw-scroll min-h-0 flex-1 outline-none"
  >
    <div v-if="keepAlive ? mountedOnce : active" class="px-4 pt-3 pb-4">
      <slot />
    </div>
  </div>
</template>
