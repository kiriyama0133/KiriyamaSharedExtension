<script lang="ts" setup>
import { onBeforeUnmount, onMounted, ref } from 'vue';
import ShareWidget from '@/components/ShareWidget.vue';
import { useShareSession } from '@/composables/useShareSession';
import { createRecorder, type Recorder } from '@/utils/recorder';

/**
 * 预览页本体。
 *
 * 除了看样式，这里还顺手把采集器也跑起来，所以它是一个端到端的手工测试台：
 * 点下面的按钮会真的改动本页 DOM，回到卡片的「共享」页就能看到
 * 已同步 / 批次 的计数往上走。
 *
 * 注意 preview 是扩展页面，content script 不会在这里运行，
 * 所以采集器要在本组件里手动起一份。
 */
const session = useShareSession();

/** 采集器要忽略这块，否则卡片自己的渲染会被当成页面变更，形成死循环 */
const widgetHost = ref<HTMLElement | null>(null);

const isDark = ref(false);
const clicks = ref(0);
const highlighted = ref(false);
const tags = ref<string[]>(['design', 'frontend']);

let recorder: Recorder | null = null;

function toggleTheme() {
  isDark.value = !isDark.value;
  // 挂在 <html> 上，正好可以验证 useHostTheme 对宿主主题的感知
  document.documentElement.classList.toggle('dark', isDark.value);
}

function addTag() {
  tags.value.push(`tag-${tags.value.length + 1}`);
}

onMounted(async () => {
  await session.initSession();

  // 把宿主节点标成"不要采集"：rrweb 的 blockClass 是查 classList 的，
  // 标在宿主上就能连它的子树一起跳过
  widgetHost.value?.classList.add('rr-block');

  recorder = createRecorder({
    onStats: (stats) => session.setObserverStats(stats),
    onBaseline: () => session.noteBaseline(),
    onOps: (ops) => session.noteOps(ops),
  });
  recorder.start();
});

onBeforeUnmount(() => recorder?.stop());
</script>

<template>
  <div class="min-h-screen bg-[var(--ui-canvas)] text-[var(--ui-text)]">
    <header
      class="sticky top-0 z-10 flex items-center gap-3 border-b border-[var(--ui-line)] bg-[var(--ui-canvas)] px-6 py-3"
    >
      <div>
        <h1 class="text-[15px] font-bold text-[var(--ui-text-strong)]">KiriyamaShare 预览</h1>
        <p class="text-[11.5px] text-[var(--ui-text-muted)]">
          右下角是注入到页面的共享卡片（Esc 收起 / Alt+Shift+K 唤回）；下面的按钮会真的改动这个页面
        </p>
      </div>
      <span class="flex-1" />
      <button
        type="button"
        class="rounded-[9px] border border-[var(--ui-line)] px-3 py-1.5 text-[12px] font-semibold hover:border-[var(--ui-accent)]"
        @click="toggleTheme"
      >
        {{ isDark ? '切到亮色' : '切到暗色' }}
      </button>
    </header>

    <main class="mx-auto max-w-3xl space-y-4 px-6 py-8">
      <section class="rounded-[14px] border border-[var(--ui-line)] bg-[var(--ui-primary,var(--ui-surface))] p-5">
        <h2 class="text-[14px] font-bold text-[var(--ui-text-strong)]">造点 DOM 变更</h2>
        <p class="mt-1 text-[12px] leading-relaxed text-[var(--ui-text-muted)]">
          三种变更各一个：文本（characterData）、属性（attributes）、子节点（childList）。
          在悬浮窗里点「开始共享」之后，这些都会被采集到。
        </p>

        <div class="mt-4 flex flex-wrap items-center gap-2">
          <button
            type="button"
            class="rounded-[9px] bg-[var(--ui-accent)] px-3 py-1.5 text-[12px] font-semibold text-white"
            @click="clicks += 1"
          >
            点击计数：<strong>{{ clicks }}</strong>
          </button>

          <button
            type="button"
            class="rounded-[9px] border border-[var(--ui-line)] px-3 py-1.5 text-[12px] font-semibold hover:border-[var(--ui-accent)]"
            @click="highlighted = !highlighted"
          >
            切换高亮
          </button>

          <button
            type="button"
            class="rounded-[9px] border border-[var(--ui-line)] px-3 py-1.5 text-[12px] font-semibold hover:border-[var(--ui-accent)]"
            @click="addTag"
          >
            追加标签
          </button>
        </div>

        <!-- 属性变更的观察目标 -->
        <div
          class="mt-4 rounded-[10px] border p-3 text-[12px] transition-colors"
          :class="
            highlighted
              ? 'border-[var(--ui-accent)] bg-[var(--ui-accent)]/15 font-semibold'
              : 'border-[var(--ui-line)] text-[var(--ui-text-muted)]'
          "
          :data-state="highlighted ? 'on' : 'off'"
        >
          这个方块会随「切换高亮」改 class 与 data-state，供属性变更采集。
        </div>

        <!-- 子节点变更的观察目标 -->
        <ul class="mt-3 flex flex-wrap gap-1.5">
          <li
            v-for="tag in tags"
            :key="tag"
            class="rounded-[7px] border border-[var(--ui-line)] px-2 py-0.5 text-[11px]"
          >
            {{ tag }}
          </li>
        </ul>
      </section>

      <section class="rounded-[14px] border border-[var(--ui-line)] bg-[var(--ui-surface)] p-5">
        <h2 class="text-[14px] font-bold text-[var(--ui-text-strong)]">示例正文</h2>
        <p class="mt-2 text-[12.5px] leading-relaxed text-[var(--ui-text-muted)]">
          这块内容只是用来撑版面的，方便看卡片在真实密度下的观感。改尺寸、间距、
          暗色表现时，在这个页面调比在真实网站上快得多，改完直接 HMR 生效。
        </p>
      </section>
    </main>

    <!-- 注入的卡片：预览里显式打开，省一次 Alt+Shift+K -->
    <div ref="widgetHost">
      <ShareWidget :default-visible="true" />
    </div>
  </div>
</template>
