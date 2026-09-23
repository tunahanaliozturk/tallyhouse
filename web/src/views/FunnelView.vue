<script setup lang="ts">
import { useQuery } from "@tanstack/vue-query";
import { computed, useId } from "vue";

import { api } from "@/api/client";
import type { FunnelQuery } from "@/api/contract";
import DateRangeFields from "@/components/DateRangeFields.vue";
import EventSelect from "@/components/EventSelect.vue";
import FunnelChart from "@/components/FunnelChart.vue";
import QueryState from "@/components/QueryState.vue";
import { useSchemas } from "@/composables/useSchemas";
import { useSubmitted } from "@/composables/useSubmitted";
import { addDays, utcToday } from "@/format";

const maxSteps = 8;

const windows = [
    { label: "1 hour", seconds: 3_600 },
    { label: "1 day", seconds: 86_400 },
    { label: "7 days", seconds: 604_800 },
    { label: "30 days", seconds: 2_592_000 },
];

const { events } = useSchemas();
const windowId = useId();

const { draft, submitted, submit } = useSubmitted<FunnelQuery>({
    steps: ["signup", "activate", "purchase"],
    from: addDays(utcToday(), -29),
    to: utcToday(),
    windowSeconds: 604_800,
});

const result = useQuery({
    queryKey: computed(() => ["funnel", submitted.value]),
    queryFn: () => api.funnel(submitted.value),
});

function addStep() {
    const next = events.value.find((event) => !draft.value.steps.includes(event));

    if (next && draft.value.steps.length < maxSteps) {
        draft.value.steps.push(next);
    }
}

function removeStep(index: number) {
    draft.value.steps.splice(index, 1);
}
</script>

<template>
    <section aria-labelledby="funnel-title">
        <header class="page-header">
            <h1 id="funnel-title">Funnels</h1>
            <p>
                Users who did every step in order, with the whole chain inside the conversion
                window.
            </p>
        </header>

        <form class="query-form" @submit.prevent="submit">
            <fieldset class="steps">
                <legend>Steps</legend>
                <div v-for="(_, index) in draft.steps" :key="index" class="steps__row">
                    <EventSelect
                        v-model="draft.steps[index]!"
                        :label="`Step ${index + 1}`"
                        :events="events"
                    />
                    <button
                        v-if="draft.steps.length > 2"
                        type="button"
                        class="button button--quiet"
                        :aria-label="`Remove step ${index + 1}`"
                        @click="removeStep(index)"
                    >
                        Remove
                    </button>
                </div>
                <button
                    v-if="draft.steps.length < maxSteps"
                    type="button"
                    class="button button--quiet"
                    @click="addStep"
                >
                    Add a step
                </button>
            </fieldset>

            <DateRangeFields v-model:from="draft.from" v-model:to="draft.to" />

            <div class="field">
                <label :for="windowId">Conversion window</label>
                <select :id="windowId" v-model="draft.windowSeconds">
                    <option v-for="window in windows" :key="window.seconds" :value="window.seconds">
                        {{ window.label }}
                    </option>
                </select>
            </div>

            <button type="submit" class="button">Run</button>
        </form>

        <QueryState
            :loading="result.isFetching.value && !result.data.value"
            :error="result.error.value"
        />

        <template v-if="result.data.value">
            <p v-if="result.data.value.sampling.isEstimate" class="note">
                At least one step is sampled, so user counts are estimates scaled by
                {{ result.data.value.sampling.scale }}.
            </p>
            <FunnelChart
                :steps="result.data.value.steps"
                :estimate="result.data.value.sampling.isEstimate"
            />
        </template>
    </section>
</template>
