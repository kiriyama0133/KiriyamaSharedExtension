<script lang="ts" setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import ShareCard from './ShareCard.vue';
import { useHostTheme } from '@/composables/useHostTheme';
import { useShareSession } from '@/composables/useShareSession';

/**
 * 注入到宿主页面的根组件。
 *
 * 带 Tab 的卡片就是本组件的全部形态——之前的收起态悬浮球去掉了：
 * 卡片本身已经能承担全部交互，再挂一个"球"当入口属于多余的一层。
 *
 * 显隐策略是「静默」：
 *   - 挂载后默认隐藏，页面加载完不会自己出现在右下角；
 *   - 同文档内的页面切换（SPA 软导航 / 前进后退）时静默收起 + 刷新页面信息，
 *     不跨页面残留上一页的面板；
 *   - 用户主动唤出的方式是 Alt+Shift+K（页面上没有常驻入口，所以留了这个键）。
 *
 * 主题：把宿主页面的明暗状态镜像到 .ksw-root 上，让 dark: 变体和 --ui-* 令牌
 * 都能正确取值（Shadow DOM 里选择器跨不过去，只能这样"手动搬"）。
 *
 * 刻意没做"点击页面空白处收起"——这是在页面上长期驻留的工具面板，
 * 用户一边看页面一边操作时不应该被误关。
 */
const props = withDefaults(
  defineProps<{
    /** 预览页用：进来就显示（真实注入场景走默认的隐藏） */
    defaultVisible?: boolean;
  }>(),
  { defaultVisible: false },
);

const session = useShareSession();
const { state } = session;

const visible = ref(props.defaultVisible);
const { isDark } = useHostTheme(computed(() => state.settings.theme));

/** Navigation API：浏览器支持时能捕获 pushState/replaceState 类软导航 */
type NavigationHost = Window & { navigation?: EventTarget };
const navHost = window as NavigationHost;

/**
 * 页面被切换时静默处理：收起面板 + 刷新页面元信息。
 * 只改本地状态，不动共享会话——正在共享时是否该跟着停，由用户决定。
 */
function onLocationChange() {
  state.page = session.capturePage();
  visible.value = false;
}

/** 宿主页面上的按键：Esc 收起，Alt+Shift+K 切换显隐 */
function onWindowKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape' && visible.value) {
    visible.value = false;
    return;
  }

  if (event.altKey && event.shiftKey && event.code === 'KeyK') {
    event.preventDefault();
    visible.value = !visible.value;
  }
}

onMounted(async () => {
  state.page = session.capturePage();
  window.addEventListener('keydown', onWindowKeydown);
  // 整页刷新由 content script 重新注入，天然是隐藏的；这里只管同文档内的软导航
  window.addEventListener('popstate', onLocationChange);
  window.addEventListener('hashchange', onLocationChange);
  navHost.navigation?.addEventListener('navigate', onLocationChange);
  await session.initSession();
});

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onWindowKeydown);
  window.removeEventListener('popstate', onLocationChange);
  window.removeEventListener('hashchange', onLocationChange);
  navHost.navigation?.removeEventListener('navigate', onLocationChange);
});
</script>

<template>
  <div class="ksw-root" :class="{ dark: isDark }">
    <Transition name="ksw-card">
      <ShareCard v-if="visible" @close="visible = false" />
    </Transition>
  </div>
</template>
