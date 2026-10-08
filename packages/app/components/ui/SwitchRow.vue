<script lang="ts" setup>
import SvgIcon from '@/components/SvgIcon.vue';
import SwitchControl from './SwitchControl.vue';

/** 开关行：图标 + 标题 + 说明 + 开关，设置页里反复用 */
withDefaults(
  defineProps<{
    modelValue: boolean;
    label: string;
    description?: string;
    /** assets/icons 下的图标名，留空则不显示图标块 */
    icon?: string;
    disabled?: boolean;
  }>(),
  { disabled: false },
);

const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>();
</script>

<template>
  <div class="ksw-row">
    <span
      v-if="icon"
      class="grid h-[26px] w-[26px] shrink-0 place-items-center rounded-[8px] bg-[var(--ksw-accent-soft)] text-[var(--ui-text-muted)]"
    >
      <SvgIcon :name="icon" :size="13" />
    </span>

    <span class="min-w-0 flex-1">
      <span class="block truncate text-[12px] font-semibold text-[var(--ui-text-strong)]">
        {{ label }}
      </span>
      <span v-if="description" class="block truncate text-[10.5px] text-[var(--ui-text-muted)]">
        {{ description }}
      </span>
    </span>

    <SwitchControl
      :model-value="modelValue"
      :label="label"
      :disabled="disabled"
      @update:model-value="emit('update:modelValue', $event)"
    />
  </div>
</template>
