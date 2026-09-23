<script setup lang="ts">
import { useQuery } from "@tanstack/vue-query";
import { computed, useId } from "vue";

import { api } from "@/api/client";
import type { RetentionQuery } from "@/api/contract";
import DateRangeFields from "@/components/DateRangeFields.vue";
import EventSelect from "@/components/EventSelect.vue";
import QueryState from "@/components/QueryState.vue";
import RetentionGrid from "@/components/RetentionGrid.vue";
import { useSchemas } from "@/composables/useSchemas";
import { useSubmitted } from "@/composables/useSubmitted";
import { addDays, utcToday } from "@/format";

const { events } = useSchemas();
const daysId = useId();

const { draft, submitted, submit } = useSubmitted<RetentionQuery>({
    startEvent: "signup",
    returnEvent: "page_view",
    from: addDays(utcToday(), -13),
    to: utcToday(),
    days: 7,
});

const result = useQuery({
    queryKey: computed(() => ["retention", submitted.value]),
    queryFn: () => api.retention(submitted.value),
});

const shownDays = computed(() => Number(submitted.value.days));
</script>

<template>
    <section aria-labelledby="retention-title">
        <header class="page-header">
            <h1 id="retention-title">Retention</h1>
            <p>
                Users grouped by the day they first did the start event, and how many came back each
                day after.
            </p>
        </header>

        <form class="query-form" @submit.prevent="submit">
            <EventSelect v-model="draft.startEvent" label="Start event" :events="events" />
            <EventSelect v-model="draft.returnEvent" label="Return event" :events="events" />
            <DateRangeFields v-model:from="draft.from" v-model:to="draft.to" />
            <div class="field">
                <label :for="daysId">Days to follow</label>
                <select :id="daysId" v-model="draft.days">
                    <option :value="7">7</option>
                    <option :value="14">14</option>
                    <option :value="30">30</option>
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
                Counts are estimates from a sample.
            </p>
            <p v-if="result.data.value.cohorts.length === 0" class="note">
                Nobody did the start event in this range.
            </p>
            <RetentionGrid v-else :cohorts="result.data.value.cohorts" :days="shownDays" />
        </template>
    </section>
</template>
