<script lang="ts" setup>
import { computed, ref } from 'vue';
import SvgIcon from '@/components/SvgIcon.vue';
import { useShareSession } from '@/composables/useShareSession';
import type { ActivityKind } from '@/types';

/**
 * 共享页：把当前页面推给服务器，并显示观看者与最近事件。
 * 页面内容、观看者、事件流全部取自真实状态（经 background 的 SignalR 连接）。
 */
const session = useShareSession();
const { state } = session;

const faviconFailed = ref(false);
const copied = ref(false);

const showFavicon = computed(() => Boolean(state.page?.favicon) && !faviconFailed.value);

/**
 * 能不能开共享，只看"有没有确定的目标页面 + 连没连上"。
 *
 * 不再看"自己是不是网页上下文"：这张卡片有两种宿主 —— 页面里的浮层和工具栏弹窗。
 * 弹窗自己不是网页，但它可以问当前标签页的 content script 要页面信息
 * （见 useShareSession.resolveTargetPage），所以它照样能开共享。
 */
const canStartSharing = computed(() => Boolean(state.page) && state.connection === 'online');

const startHint = computed(() => {
  if (state.connection !== 'online') return '服务器未连接，可到「设置」检查地址';
  if (!state.page) return '当前标签页不是可共享的网页';
  return '';
});

/** 成员列表含自己，观看人数得把自己排掉（靠服务端 WhoAmI 认领的 isSelf） */
const viewerCount = computed(() => state.peers.filter((peer) => !peer.isSelf).length);

const ACTIVITY_ICON: Record<ActivityKind, string> = {
  share: 'upload-cloud',
  mutation: 'refresh-cw',
  navigate: 'globe',
  join: 'users',
  leave: 'power',
  system: 'radio',
};

async function copyShareCode() {
  if (!state.shareCode) return;
  if (!(await session.copyText(state.shareCode))) return;
  copied.value = true;
  window.setTimeout(() => {
    copied.value = false;
  }, 1600);
}
</script>

<template>
  <div class="space-y-3">
    <!-- 被共享的目标页面 -->
    <section class="rounded-[12px] border border-[var(--ui-line)] bg-[var(--ui-surface)] p-3">
      <p class="ksw-label mb-2">当前页面</p>
      <div class="flex items-start gap-2.5">
        <span
          class="grid h-[30px] w-[30px] shrink-0 place-items-center overflow-hidden rounded-[8px] border border-[var(--ui-line)] bg-[var(--ui-canvas)]"
        >
          <img
            v-if="showFavicon"
            :src="state.page?.favicon || ''"
            alt=""
            class="h-[16px] w-[16px]"
            @error="faviconFailed = true"
          />
          <SvgIcon v-else name="globe" :size="14" />
        </span>

        <div class="min-w-0 flex-1">
          <p class="line-clamp-2 text-[12px] leading-snug font-semibold text-[var(--ui-text-strong)]">
            {{ state.page?.title || (state.resolvingPage ? '读取中…' : '当前标签页不是可共享的网页') }}
          </p>
          <p class="ksw-mono mt-0.5 truncate text-[var(--ui-text-muted)]">
            {{ state.page?.host || '—' }}
          </p>
        </div>
      </div>
    </section>

    <!-- 未共享：开始按钮 -->
    <section v-if="!state.sharing">
      <button
        type="button"
        class="ksw-btn ksw-btn--primary h-[36px] w-full text-[12.5px]"
        :disabled="!canStartSharing"
        @click="session.startSharing()"
      >
        <SvgIcon name="upload-cloud" :size="14" />
        开始共享当前页面
      </button>
      <p
        v-if="startHint"
        class="mt-1.5 text-center text-[10.5px] text-[var(--ui-text-muted)]"
      >
        {{ startHint }}
      </p>
    </section>

    <!-- 共享中：共享码 + 观看人数 + 停止 -->
    <section v-else class="rounded-[12px] border border-[var(--ui-line)] bg-[var(--ui-surface)] p-3">
      <div class="flex items-center gap-2">
        <span class="relative flex h-[8px] w-[8px]">
          <span
            class="absolute inline-flex h-full w-full animate-ping rounded-full bg-[#35c46a] opacity-70"
          />
          <span class="relative inline-flex h-[8px] w-[8px] rounded-full bg-[#35c46a]" />
        </span>
        <p class="flex-1 text-[12px] font-semibold text-[var(--ui-text-strong)]">正在共享</p>
        <span class="ksw-chip">{{ viewerCount }} 人观看</span>
      </div>

      <div class="mt-2.5 flex items-center gap-2">
        <div
          class="flex min-w-0 flex-1 items-center gap-1.5 rounded-[9px] border border-[var(--ui-line)] bg-[var(--ui-canvas)] px-2 py-1.5"
        >
          <SvgIcon name="link-2" :size="12" class="shrink-0 text-[var(--ui-text-muted)]" />
          <span class="ksw-mono truncate tracking-[0.14em] text-[var(--ui-text-strong)]">
            {{ state.shareCode }}
          </span>
        </div>
        <button type="button" class="ksw-btn h-[30px] px-2.5" @click="copyShareCode">
          <SvgIcon :name="copied ? 'check' : 'copy'" :size="12" />
          {{ copied ? '已复制' : '复制' }}
        </button>
      </div>

      <button
        type="button"
        class="ksw-btn ksw-btn--danger mt-2 h-[30px] w-full"
        @click="session.stopSharing()"
      >
        <SvgIcon name="power" :size="12" />
        停止共享
      </button>
    </section>

    <!-- DOM 采集状态：共享时能直观看到链路是否在跑 -->
    <section
      v-if="state.sharing"
      class="rounded-[12px] border border-[var(--ui-line)] bg-[var(--ui-surface)] p-3"
    >
      <div class="flex items-center gap-2">
        <SvgIcon
          name="radio"
          :size="12"
          :class="state.observer.running ? 'text-[#35c46a]' : 'text-[var(--ui-text-muted)]'"
        />
        <p class="ksw-label">DOM 采集</p>
        <span class="flex-1" />
        <span class="ksw-chip">{{ state.observer.running ? '监听中' : '未启动' }}</span>
      </div>

      <dl class="mt-2 grid grid-cols-3 gap-1.5 text-center">
        <div class="rounded-[9px] bg-[var(--ui-canvas)] px-1 py-1.5">
          <dt class="text-[10px] text-[var(--ui-text-muted)]">已同步</dt>
          <dd class="ksw-mono font-semibold text-[var(--ui-text-strong)]">
            {{ state.mutationCount }}
          </dd>
        </div>
        <div class="rounded-[9px] bg-[var(--ui-canvas)] px-1 py-1.5">
          <dt class="text-[10px] text-[var(--ui-text-muted)]">批次</dt>
          <dd class="ksw-mono font-semibold text-[var(--ui-text-strong)]">
            {{ state.observer.batches }}
          </dd>
        </div>
        <div class="rounded-[9px] bg-[var(--ui-canvas)] px-1 py-1.5">
          <dt class="text-[10px] text-[var(--ui-text-muted)]">丢弃</dt>
          <dd
            class="ksw-mono font-semibold"
            :class="state.observer.dropped ? 'text-[var(--ui-wrong)]' : 'text-[var(--ui-text-strong)]'"
          >
            {{ state.observer.dropped }}
          </dd>
        </div>
      </dl>
    </section>

    <!-- 事件流 -->
    <section>
      <p class="ksw-label mb-1.5">最近事件</p>
      <ul v-if="state.activity.length" class="space-y-0.5">
        <li
          v-for="item in state.activity.slice(0, 5)"
          :key="item.id"
          class="flex items-start gap-2 py-1"
        >
          <SvgIcon
            :name="ACTIVITY_ICON[item.kind]"
            :size="12"
            class="mt-[2.5px] shrink-0 text-[var(--ui-text-muted)]"
          />
          <span class="min-w-0 flex-1 text-[11.5px] leading-snug text-[var(--ui-text)]">
            {{ item.text }}
          </span>
          <span class="shrink-0 text-[10px] text-[var(--ui-text-muted)]">
            {{ session.formatRelative(item.at) }}
          </span>
        </li>
      </ul>
      <p v-else class="py-3 text-center text-[11px] text-[var(--ui-text-muted)]">
        还没有事件
      </p>
    </section>
  </div>
</template>
