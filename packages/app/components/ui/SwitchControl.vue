<script lang="ts" setup>
/**
 * 裸开关（不带文字，供自定义布局使用）。
 * role="switch" + aria-checked 让读屏能读成开关而不是按钮。
 */
withDefaults(
  defineProps<{
    modelValue: boolean;
    /** 无障碍名称；外层已有可见文字时也要传，读屏只认这个 */
    label: string;
    disabled?: boolean;
  }>(),
  { disabled: false },
);

const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>();
</script>

<template>
  <button
    type="button"
    role="switch"
    class="ksw-switch"
    :class="{ 'ksw-switch--on': modelValue }"
    :aria-checked="modelValue"
    :aria-label="label"
    :disabled="disabled"
    @click="emit('update:modelValue', !modelValue)"
  >
    <span class="ksw-switch__knob" />
  </button>
</template>
