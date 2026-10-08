<script lang="ts" setup>
import { computed, ref } from 'vue';
import SvgIcon from '@/components/SvgIcon.vue';
import Tabs from '@/components/Tab/Tabs.vue';
import TabPanel from '@/components/Tab/TabPanel.vue';
import SharePanel from '@/components/panels/SharePanel.vue';
import SubscribePanel from '@/components/panels/SubscribePanel.vue';
import SettingsPanel from '@/components/panels/SettingsPanel.vue';
import { TABS, useShareSession } from '@/composables/useShareSession';

/**
 * 卡片：头部 + Tab（共享 / 订阅 / 设置）+ 底部状态条。
 * 卡片本体只负责布局，业务状态都在 useShareSession 里。
 *
 * variant 决定它长在哪儿：
 *   - 'overlay'（默认）：注入到宿主页面上的浮层，绝对定位、右下角、带 ✕
 *   - 'page'：popup 之类的独立页面里，走正常文档流，没有 ✕（点外面就关了）
 */
const props = withDefaults(defineProps<{ variant?: 'overlay' | 'page' }>(), {
  variant: 'overlay',
});

const isPage = computed(() => props.variant === 'page');

const emit = defineEmits<{ close: [] }>();

const session = useShareSession();
const { state } = session;

const tab = ref<string>('share');

const subtitle = computed(() => {
  if (state.sharing) return `正在共享 · ${state.shareCode}`;
  return state.page?.host || '未开始共享';
});

const connectionDotClass = computed(
  () =>
    ({
      offline: 'bg-[var(--ui-wrong)]',
      connecting: 'animate-pulse bg-[#e0a83a]',
      online: 'bg-[#35c46a]',
      error: 'bg-[var(--ui-wrong)]',
    })[state.connection],
);

const connectionLabel = computed(
  () =>
    ({
      offline: '未连接',
      connecting: '连接中',
      online: '已连接',
      error: '连接失败',
    })[state.connection],
);

/** 成员列表含自己，观看人数得把自己排掉（靠服务端 WhoAmI 认领的 isSelf） */
const viewerCount = computed(() => state.peers.filter((peer) => !peer.isSelf).length);
</script>

<template>
  <div class="ksw-card" :class="{ 'ksw-card--page': isPage }">
    <!-- 头部 -->
    <header class="flex items-center gap-2 border-b border-[var(--ui-line)] px-3 py-2.5">
      <span
        class="grid h-[28px] w-[28px] shrink-0 place-items-center rounded-[9px] bg-[var(--ksw-accent)] text-[var(--ksw-accent-fg)]"
      >
        <SvgIcon name="share-2" :size="15" :stroke="2.3" />
      </span>

      <div class="min-w-0 flex-1">
        <p class="truncate text-[12.5px] leading-tight font-bold text-[var(--ui-text-strong)]">
          KiriyamaShare
        </p>
        <p class="truncate text-[10.5px] text-[var(--ui-text-muted)]">{{ subtitle }}</p>
      </div>

      <button
        v-if="!isPage"
        type="button"
        class="ksw-iconbtn"
        aria-label="收起面板"
        @click="emit('close')"
      >
        <SvgIcon name="x" :size="15" />
      </button>
    </header>

    <!-- Tab 三页 -->
    <Tabs v-model="tab" :tabs="[...TABS]" id-prefix="ksw-card" class="pt-3">
      <TabPanel value="share"><SharePanel /></TabPanel>
      <TabPanel value="subscribe"><SubscribePanel /></TabPanel>
      <TabPanel value="settings"><SettingsPanel /></TabPanel>
    </Tabs>

    <!-- 底部状态条 -->
    <footer
      class="flex items-center justify-between gap-2 border-t border-[var(--ui-line)] px-3 py-2"
    >
      <span class="flex items-center gap-1.5 text-[10.5px] text-[var(--ui-text-muted)]">
        <i class="h-[6px] w-[6px] rounded-full" :class="connectionDotClass" />
        {{ connectionLabel }}
      </span>
      <span class="flex items-center gap-1 text-[10.5px] text-[var(--ui-text-muted)]">
        <SvgIcon name="users" :size="12" />
        {{ viewerCount }} 人观看
      </span>
    </footer>
  </div>
</template>
