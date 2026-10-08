<script lang="ts" setup>
import { computed } from 'vue';

/**
 * 从 assets/icons 里按需读取 SVG（构建期以 ?raw 内联，无额外网络请求）。
 *
 * 图标来自 Lucide 图标集（ISC 协议，文件头保留了 license 注释）。
 * 想换图标：把同名 .svg 丢进 assets/icons 即可，组件不用改。
 */
const modules = import.meta.glob('../assets/icons/*.svg', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>;

const ICONS: Record<string, string> = {};
for (const [path, raw] of Object.entries(modules)) {
  ICONS[path.slice(path.lastIndexOf('/') + 1).replace(/\.svg$/, '')] = raw;
}

const props = withDefaults(
  defineProps<{
    /** assets/icons 下的文件名（不含 .svg），如 share-2 */
    name: string;
    /** 边长，数字按 px 处理 */
    size?: number | string;
    /** 描边粗细，CSS 覆盖 SVG 的 presentation 属性 */
    stroke?: number;
    /** 是否旋转（加载态） */
    spin?: boolean;
  }>(),
  { size: 20, stroke: 2, spin: false },
);

const svg = computed(() => ICONS[props.name] ?? '');

const style = computed(() => {
  const edge = typeof props.size === 'number' ? `${props.size}px` : props.size;
  return {
    width: edge,
    height: edge,
    '--icon-stroke': String(props.stroke),
  };
});
</script>

<template>
  <span
    class="ksw-icon"
    :class="{ 'ksw-icon--spin': spin }"
    :style="style"
    aria-hidden="true"
    v-html="svg"
  />
</template>
