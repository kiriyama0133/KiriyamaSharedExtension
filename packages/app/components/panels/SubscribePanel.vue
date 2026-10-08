<script lang="ts" setup>
import SvgIcon from '@/components/SvgIcon.vue';
import SwitchControl from '@/components/ui/SwitchControl.vue';
import { useShareSession } from '@/composables/useShareSession';

/**
 * 订阅页：服务器上正在进行的共享会话列表，打开开关即订阅。
 *
 * 订阅成功会把"观看页"在新标签页里打开（`viewer.html`，扩展自己的页面）——
 * 只订阅是什么都看不到的。页面内容由观看页自己拿整页快照重建，
 * 这个面板只负责列表与订阅状态。
 */
const session = useShareSession();
const { state } = session;
</script>

<template>
  <div class="space-y-3">
    <div class="flex items-center justify-between">
      <p class="ksw-label">服务器共享会话</p>
      <button
        type="button"
        class="ksw-btn ksw-btn--ghost h-[26px] px-2 text-[11px]"
        :disabled="state.loadingRooms"
        @click="session.refreshRooms()"
      >
        <SvgIcon name="refresh-cw" :size="12" :spin="state.loadingRooms" />
        {{ state.loadingRooms ? '同步中' : '刷新' }}
      </button>
    </div>

    <!-- 首次加载的骨架屏 -->
    <ul v-if="state.loadingRooms && !state.rooms.length" class="space-y-1.5">
      <li
        v-for="i in 3"
        :key="i"
        class="h-[46px] animate-pulse rounded-[10px] bg-[var(--ui-surface)]"
      />
    </ul>

    <ul v-else-if="state.rooms.length" class="space-y-1">
      <li v-for="room in state.rooms" :key="room.id" class="ksw-row items-start">
        <span
          class="mt-[1px] grid h-[26px] w-[26px] shrink-0 place-items-center rounded-[8px] bg-[var(--ksw-accent-soft)] text-[var(--ui-text-muted)]"
        >
          <SvgIcon name="link-2" :size="13" />
        </span>

        <span class="min-w-0 flex-1">
          <span class="block truncate text-[12px] font-semibold text-[var(--ui-text-strong)]">
            {{ room.title }}
          </span>
          <span
            class="mt-1 flex flex-wrap items-center gap-1.5 text-[10.5px] text-[var(--ui-text-muted)]"
          >
            <span class="truncate">{{ room.host }}</span>
            <span class="inline-flex items-center gap-0.5">
              <SvgIcon name="users" :size="10" />
              {{ room.peerCount }}
            </span>
            <span v-if="room.latencyMs !== null" class="ksw-chip">{{ room.latencyMs }}ms</span>
            <span class="ksw-chip">{{ session.formatRelative(room.updatedAt) }}</span>
          </span>
        </span>

        <span class="mt-[3px] flex items-center gap-1.5">
          <button
            v-if="room.subscribed"
            type="button"
            class="ksw-btn h-[24px] px-1.5 text-[11px]"
            @click="session.watchRoom(room.id)"
          >
            <SvgIcon name="eye" :size="12" />
            观看
          </button>

          <SwitchControl
            :model-value="room.subscribed"
            :label="`订阅 ${room.title}`"
            @update:model-value="session.toggleSubscription(room.id)"
          />
        </span>
      </li>
    </ul>

    <!-- 空态 -->
    <div v-else class="flex flex-col items-center gap-2 py-8 text-center">
      <SvgIcon name="inbox" :size="26" class="text-[var(--ui-text-muted)]" />
      <p class="text-[11.5px] text-[var(--ui-text-muted)]">服务器上还没有可订阅的会话</p>
      <button type="button" class="ksw-btn h-[28px]" @click="session.refreshRooms()">
        重新拉取
      </button>
    </div>
  </div>
</template>
