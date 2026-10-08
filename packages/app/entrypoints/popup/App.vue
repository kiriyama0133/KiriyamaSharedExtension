<script lang="ts" setup>
import { computed, onMounted } from 'vue';
import ShareCard from '@/components/ShareCard.vue';
import { useHostTheme } from '@/composables/useHostTheme';
import { useShareSession } from '@/composables/useShareSession';

/**
 * 工具栏弹窗：和注入到页面上的那张卡片是同一套东西，只是不再漂浮在页面右下角。
 *
 * 两处差异都写在 variant="page" 里：走正常文档流（不再 absolute 定位）、
 * 不显示 ✕（弹窗点外面就关了）。头部、三个 Tab、底部状态条全部复用 ShareCard，
 * 不做第二套实现——不然改一处忘一处。
 */
const session = useShareSession();
const { state } = session;

const { isDark } = useHostTheme(computed(() => state.settings.theme));

onMounted(() => {
  void session.initSession();
});
</script>

<template>
  <div class="ksw-root ksw-root--page" :class="{ dark: isDark }">
    <ShareCard variant="page" />
  </div>
</template>
