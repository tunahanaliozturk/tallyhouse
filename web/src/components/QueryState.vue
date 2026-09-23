<script setup lang="ts">
import { computed } from "vue";

import { ApiError } from "@/api/client";

const props = defineProps<{ loading: boolean; error: unknown }>();

const problems = computed(() => (props.error instanceof ApiError ? props.error.problems : []));

const message = computed(() => {
    if (props.error instanceof ApiError) {
        return props.error.message;
    }

    return props.error instanceof Error ? props.error.message : "Something went wrong.";
});
</script>

<template>
    <p v-if="loading" class="state" role="status">Running the query…</p>
    <div v-else-if="error" class="state state--error" role="alert">
        <p>{{ message }}</p>
        <ul v-if="problems.length">
            <li v-for="problem in problems" :key="problem">{{ problem }}</li>
        </ul>
    </div>
</template>
