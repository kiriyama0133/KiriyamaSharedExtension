<script lang="ts" setup>
import { computed, nextTick, onBeforeUnmount, onMounted, provide, ref, watch } from 'vue';
import SvgIcon from '@/components/SvgIcon.vue';
import type { TabItem } from '@/types';
import { TABS_INJECTION_KEY } from './tabContext';

/**
 * Tab 组件（受控 + 可复用）
 *
 *   <Tabs v-model="active" :tabs="TABS">
 *     <TabPanel value="share"> … </TabPanel>
 *     <TabPanel value="subscribe"> … </TabPanel>
 *   </Tabs>
 *
 * - 指示条跟着选中项滑动（位置/宽度实测，中文标签与图标都撑得住）
 * - 键盘可访问：←/→ 切换，Home/End 跳首尾
 * - aria 关联齐全，读屏能正确播报
 */
const props = withDefaults(
  defineProps<{
    modelValue: string;
    tabs: TabItem[];
    /** aria id 前缀，同页面多组 Tabs 靠它去重 */
    idPrefix?: string;
  }>(),
  { idPrefix: 'ksw' },
);

const emit = defineEmits<{ 'update:modelValue': [value: string] }>();

/** 每个实例一个自增后缀，避免多组 Tabs 的 aria id 撞车 */
let instanceSeq = 0;

const uid = `${props.idPrefix}-${(instanceSeq += 1)}`;

const tablistEl = ref<HTMLElement | null>(null);
const btnEls = ref<HTMLElement[]>([]);
/** 指示条的位置和宽度；ready=false 时先隐藏，免得首帧从左上角飞过来 */
const indicator = ref({ left: 0, width: 0, ready: false });

const active = computed(() => props.modelValue);

function setActive(value: string) {
  if (value !== props.modelValue) emit('update:modelValue', value);
}

provide(TABS_INJECTION_KEY, { active, setActive, uid });

function setBtnEl(el: Element | { $el?: Element } | null, index: number) {
  const node = el instanceof Element ? el : (el?.$el ?? null);
  if (node) btnEls.value[index] = node as HTMLElement;
}

/** 实测当前选中按钮的位置，把指示条挪过去 */
function measure() {
  const index = props.tabs.findIndex((t) => t.value === props.modelValue);
  const btn = btnEls.value[index];
  if (!btn) {
    indicator.value = { ...indicator.value, ready: false };
    return;
  }
  indicator.value = { left: btn.offsetLeft, width: btn.offsetWidth, ready: true };
}

function onKeydown(event: KeyboardEvent) {
  const index = props.tabs.findIndex((t) => t.value === props.modelValue);
  let next = index;

  switch (event.key) {
    case 'ArrowLeft':
      next = (index - 1 + props.tabs.length) % props.tabs.length;
      break;
    case 'ArrowRight':
      next = (index + 1) % props.tabs.length;
      break;
    case 'Home':
      next = 0;
      break;
    case 'End':
      next = props.tabs.length - 1;
      break;
    default:
      return;
  }

  event.preventDefault();
  const target = props.tabs[next];
  if (!target) return;
  setActive(target.value);
  void nextTick(() => btnEls.value[next]?.focus());
}

let observer: ResizeObserver | undefined;

onMounted(() => {
  measure();
  observer = new ResizeObserver(measure);
  if (tablistEl.value) observer.observe(tablistEl.value);
  window.addEventListener('resize', measure);
});

onBeforeUnmount(() => {
  observer?.disconnect();
  window.removeEventListener('resize', measure);
});

// 换标签、改标签数量都要重新量；卡片展开有动画，首帧宽度可能是 0
watch(() => [props.modelValue, props.tabs.length], () => void nextTick(measure));
</script>

<template>
  <div class="flex min-h-0 flex-1 flex-col">
    <div
      ref="tablistEl"
      role="tablist"
      aria-orientation="horizontal"
      class="ksw-tabs relative mx-3 flex items-center gap-1 rounded-[11px] border border-[var(--ui-line)] bg-[var(--ui-surface)] p-[3px]"
      @keydown="onKeydown"
    >
      <span
        class="ksw-tab-indicator"
        :class="{ 'ksw-tab-indicator--hidden': !indicator.ready }"
        :style="{ transform: `translateX(${indicator.left}px)`, width: `${indicator.width}px` }"
      />

      <button
        v-for="(tab, index) in tabs"
        :id="`ksw-tab-${uid}-${tab.value}`"
        :key="tab.value"
        :ref="(el) => setBtnEl(el as Element | null, index)"
        type="button"
        role="tab"
        class="ksw-tab"
        :class="{ 'ksw-tab--active': tab.value === modelValue }"
        :aria-selected="tab.value === modelValue"
        :aria-controls="`ksw-panel-${uid}-${tab.value}`"
        :tabindex="tab.value === modelValue ? 0 : -1"
        @click="setActive(tab.value)"
      >
        <SvgIcon :name="tab.icon" :size="15" :stroke="2.1" />
        <span>{{ tab.label }}</span>
      </button>
    </div>

    <slot />
  </div>
</template>
