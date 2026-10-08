import { computed, onBeforeUnmount, onMounted, ref, type ComputedRef, type Ref } from 'vue';
import type { ThemePreference } from '@/types';

/**
 * 判断宿主页面当前是不是暗色。
 *
 * Shadow Root 里 `dark:` 这类选择器不会跨shadow边界匹配到宿主的 <html class="dark">，
 * 所以要把宿主的主题"镜像"到组件根节点上，同时把真实值读出来。
 * 优先看宿主自己挂的标记，都没有才退回系统偏好。
 */
function detectHostDark(): boolean {
  const el = document.documentElement;
  if (el.classList.contains('dark')) return true;
  if (el.classList.contains('light')) return false;

  const attr =
    el.getAttribute('data-theme') ??
    el.getAttribute('data-color-mode') ??
    el.getAttribute('data-bs-theme');
  if (attr === 'dark') return true;
  if (attr === 'light') return false;

  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;
}

/**
 * 跟随宿主页面主题。
 *
 * @param preference 'auto' 跟随宿主页面；'light'/'dark' 强制指定
 */
export function useHostTheme(
  preference: Ref<ThemePreference> | ComputedRef<ThemePreference>,
): { isDark: ComputedRef<boolean>; hostDark: Ref<boolean> } {
  const hostDark = ref(false);
  const media = window.matchMedia?.('(prefers-color-scheme: dark)');

  const sync = () => {
    hostDark.value = detectHostDark();
  };

  let observer: MutationObserver | undefined;

  onMounted(() => {
    sync();
    // 宿主切主题通常只改 <html> 上的 class / data-*，盯这一处就够了
    observer = new MutationObserver(sync);
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['class', 'data-theme', 'data-color-mode', 'data-bs-theme'],
    });
    media?.addEventListener?.('change', sync);
  });

  onBeforeUnmount(() => {
    observer?.disconnect();
    media?.removeEventListener?.('change', sync);
  });

  const isDark = computed(() => {
    if (preference.value === 'dark') return true;
    if (preference.value === 'light') return false;
    return hostDark.value;
  });

  return { isDark, hostDark };
}
