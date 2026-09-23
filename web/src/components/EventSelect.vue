<script setup lang="ts">
import { useId } from "vue";

defineProps<{ label: string; events: readonly string[]; optional?: boolean }>();

// An empty string is "any event", which the caller turns into an absent filter. A select cannot hold null.
const model = defineModel<string>({ required: true });
const id = useId();
</script>

<template>
    <div class="field">
        <label :for="id">{{ label }}</label>
        <select :id="id" v-model="model">
            <option v-if="optional" value="">Any event</option>
            <option v-for="event in events" :key="event" :value="event">{{ event }}</option>
        </select>
    </div>
</template>
