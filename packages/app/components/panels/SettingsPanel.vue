<script lang="ts" setup>
import { computed, ref, watch } from 'vue';
import SvgIcon from '@/components/SvgIcon.vue';
import SwitchRow from '@/components/ui/SwitchRow.vue';
import { useShareSession } from '@/composables/useShareSession';
import type { ThemePreference } from '@/types';

/** 设置页：服务端地址、行为开关、主题、本地数据 */
const session = useShareSession();
const { state } = session;

/** 输入框用草稿态，失焦或回车才提交，避免每敲一个字符就重连 */
const serverDraft = ref(state.settings.serverUrl);
watch(
  () => state.settings.serverUrl,
  (value) => {
    serverDraft.value = value;
  },
);

const THEMES: { value: ThemePreference; label: string }[] = [
  { value: 'auto', label: '跟随页面' },
  { value: 'light', label: '亮色' },
  { value: 'dark', label: '暗色' },
];

const connectionLabel = computed(
  () =>
    ({
      offline: '未连接',
      connecting: '连接中…',
      online: '已连接',
      error: '连接失败',
    })[state.connection],
);

const connectionDotClass = computed(
  () =>
    ({
      offline: 'bg-[var(--ui-wrong)]',
      connecting: 'animate-pulse bg-[#e0a83a]',
      online: 'bg-[#35c46a]',
      error: 'bg-[var(--ui-wrong)]',
    })[state.connection],
);

function commitServer() {
  const value = serverDraft.value.trim();
  if (!value || value === state.settings.serverUrl) return;
  session.updateSettings({ serverUrl: value });
}

async function reconnect() {
  session.disconnect();
  await session.connect();
}
</script>

<template>
  <div class="space-y-3">
    <!-- 服务器地址与连接状态 -->
    <section>
      <p class="ksw-label mb-1.5">服务器</p>
      <div class="relative">
        <SvgIcon
          name="globe"
          :size="13"
          class="pointer-events-none absolute top-1/2 left-2.5 -translate-y-1/2 text-[var(--ui-text-muted)]"
        />
        <input
          v-model="serverDraft"
          class="ksw-input pl-8"
          type="text"
          spellcheck="false"
          autocomplete="off"
          placeholder="http://localhost:5000/share"
          @keydown.enter="commitServer"
          @blur="commitServer"
        />
      </div>

      <div class="mt-2 flex items-center gap-2">
        <span class="flex items-center gap-1.5 text-[11px] text-[var(--ui-text-muted)]">
          <i class="h-[7px] w-[7px] rounded-full" :class="connectionDotClass" />
          {{ connectionLabel }}
        </span>
        <span class="flex-1" />
        <button
          type="button"
          class="ksw-btn h-[26px] px-2 text-[11px]"
          :disabled="state.connection === 'connecting'"
          @click="reconnect"
        >
          <SvgIcon name="refresh-cw" :size="12" :spin="state.connection === 'connecting'" />
          重新连接
        </button>
      </div>

      <!-- 失败原因必须露出来，不然只有一个"连接失败"没法排查 -->
      <p
        v-if="state.connectionError"
        class="mt-1.5 rounded-[9px] border border-[var(--ui-line)] bg-[var(--ui-surface)] px-2 py-1.5 text-[10.5px] leading-snug break-words text-[var(--ui-wrong)]"
      >
        {{ state.connectionError }}
      </p>
    </section>

    <!-- 行为 -->
    <section>
      <p class="ksw-label mb-1">行为</p>
      <SwitchRow
        icon="radio"
        label="断线自动重连"
        description="网络恢复后自动重新建立订阅"
        :model-value="state.settings.autoReconnect"
        @update:model-value="session.updateSettings({ autoReconnect: $event })"
      />
      <SwitchRow
        icon="eye"
        label="同步鼠标光标"
        description="让协作者看到你指向的位置"
        :model-value="state.settings.syncCursor"
        @update:model-value="session.updateSettings({ syncCursor: $event })"
      />
    </section>

    <!-- 外观 -->
    <section>
      <p class="ksw-label mb-1.5">外观</p>
      <div
        class="flex gap-1 rounded-[11px] border border-[var(--ui-line)] bg-[var(--ui-surface)] p-[3px]"
      >
        <button
          v-for="item in THEMES"
          :key="item.value"
          type="button"
          class="h-[26px] flex-1 rounded-[8px] text-[11px] font-semibold transition-colors"
          :class="
            state.settings.theme === item.value
              ? 'bg-[var(--ksw-accent)] text-[var(--ksw-accent-fg)]'
              : 'text-[var(--ui-text-muted)] hover:text-[var(--ui-text)]'
          "
          :aria-pressed="state.settings.theme === item.value"
          @click="session.updateSettings({ theme: item.value })"
        >
          {{ item.label }}
        </button>
      </div>
    </section>

    <!-- 本地数据 -->
    <section>
      <p class="ksw-label mb-1.5">数据</p>
      <div class="flex items-center justify-between gap-2 px-1">
        <span class="text-[11px] text-[var(--ui-text-muted)]">
          本地事件记录
          <span class="ksw-mono text-[var(--ui-text)]">{{ state.activity.length }}</span>
          条
        </span>
        <button
          type="button"
          class="ksw-btn ksw-btn--danger h-[26px] px-2 text-[11px]"
          @click="session.clearCache()"
        >
          <SvgIcon name="trash-2" :size="12" />
          清空
        </button>
      </div>
    </section>
  </div>
</template>
