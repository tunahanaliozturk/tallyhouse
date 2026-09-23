<script setup lang="ts">
import { computed } from "vue";

import type { FunnelStep } from "@/api/contract";
import { formatCount, formatDuration, formatPercent } from "@/format";

const props = defineProps<{ steps: readonly FunnelStep[]; estimate: boolean }>();

// Bars are scaled to the first step, which is the whole population the funnel is about.
const rows = computed(() => {
    const first = props.steps[0]?.users ?? 0;

    return props.steps.map((step, index) => ({
        ...step,
        index,
        width: first === 0 ? 0 : (step.users / first) * 100,
    }));
});
</script>

<template>
    <ol class="funnel" aria-label="Funnel steps">
        <li v-for="row in rows" :key="row.event" class="funnel__step">
            <div class="funnel__label">
                <span class="funnel__index">{{ row.index + 1 }}</span>
                <span class="funnel__event">{{ row.event }}</span>
            </div>
            <div class="funnel__bar" role="presentation">
                <div class="funnel__fill" :style="{ width: `${row.width}%` }"></div>
            </div>
            <dl class="funnel__figures">
                <div>
                    <dt>Users</dt>
                    <dd>{{ estimate ? "≈ " : "" }}{{ formatCount(row.users) }}</dd>
                </div>
                <div v-if="row.index > 0">
                    <dt>From previous</dt>
                    <dd>{{ formatPercent(row.conversionFromPrevious) }}</dd>
                </div>
                <div v-if="row.index > 0">
                    <dt>Overall</dt>
                    <dd>{{ formatPercent(row.conversionFromStart) }}</dd>
                </div>
                <div v-if="row.medianSecondsFromStart !== null">
                    <dt>Median time</dt>
                    <dd>{{ formatDuration(row.medianSecondsFromStart) }}</dd>
                </div>
            </dl>
        </li>
    </ol>
</template>
