<script setup lang="ts">
import { useQuery } from "@tanstack/vue-query";
import { computed, useId } from "vue";

import { api } from "@/api/client";
import type { FilterOperator, SegmentQuery } from "@/api/contract";
import DailyChart from "@/components/DailyChart.vue";
import DateRangeFields from "@/components/DateRangeFields.vue";
import EventSelect from "@/components/EventSelect.vue";
import QueryState from "@/components/QueryState.vue";
import { useSchemas } from "@/composables/useSchemas";
import { useSubmitted } from "@/composables/useSubmitted";
import { addDays, formatCount, utcToday } from "@/format";

interface TrendForm {
    event: string;
    from: string;
    to: string;
    property: string;
    operator: FilterOperator;
    value: string;
}

const operators: { value: FilterOperator; label: string }[] = [
    { value: "eq", label: "is" },
    { value: "neq", label: "is not" },
    { value: "gt", label: "greater than" },
    { value: "lt", label: "less than" },
];

const { events, properties } = useSchemas();
const ids = { property: useId(), operator: useId(), value: useId() };

const { draft, submitted, submit } = useSubmitted<TrendForm>({
    event: "page_view",
    from: addDays(utcToday(), -29),
    to: utcToday(),
    property: "",
    operator: "eq",
    value: "",
});

const query = computed<SegmentQuery>(() => ({
    event: submitted.value.event === "" ? null : submitted.value.event,
    from: submitted.value.from,
    to: submitted.value.to,
    filters:
        submitted.value.property === ""
            ? null
            : [
                  {
                      property: submitted.value.property,
                      operator: submitted.value.operator,
                      values: [submitted.value.value],
                  },
              ],
}));

const result = useQuery({
    queryKey: computed(() => ["segment", query.value]),
    queryFn: () => api.segment(query.value),
});

const totals = computed(() => ({
    events: (result.data.value?.days ?? []).reduce((sum, day) => sum + day.events, 0),
    late: result.data.value?.lateEvents ?? 0,
}));
</script>

<template>
    <section aria-labelledby="trends-title">
        <header class="page-header">
            <h1 id="trends-title">Trends</h1>
            <p>Daily event and user counts, optionally narrowed by a property.</p>
        </header>

        <form class="query-form" @submit.prevent="submit">
            <EventSelect v-model="draft.event" label="Event" :events="events" optional />
            <DateRangeFields v-model:from="draft.from" v-model:to="draft.to" />
            <div class="field">
                <label :for="ids.property">Where property</label>
                <select :id="ids.property" v-model="draft.property">
                    <option value="">No filter</option>
                    <option v-for="property in properties" :key="property" :value="property">
                        {{ property }}
                    </option>
                </select>
            </div>
            <template v-if="draft.property !== ''">
                <div class="field">
                    <label :for="ids.operator">Comparison</label>
                    <select :id="ids.operator" v-model="draft.operator">
                        <option
                            v-for="operator in operators"
                            :key="operator.value"
                            :value="operator.value"
                        >
                            {{ operator.label }}
                        </option>
                    </select>
                </div>
                <div class="field">
                    <label :for="ids.value">Value</label>
                    <input :id="ids.value" v-model="draft.value" type="text" required />
                </div>
            </template>
            <button type="submit" class="button">Run</button>
        </form>

        <QueryState
            :loading="result.isFetching.value && !result.data.value"
            :error="result.error.value"
        />

        <template v-if="result.data.value">
            <p class="summary">
                {{ formatCount(totals.events) }} events in range.
                <span v-if="totals.late > 0">
                    {{ formatCount(totals.late) }} more arrived after their day had closed and are
                    counted separately, not added to a day that was already reported.
                </span>
            </p>
            <p v-if="result.data.value.sampling.isEstimate" class="note">
                Counts are estimates from a sample.
            </p>
            <h2>Events</h2>
            <DailyChart
                :points="result.data.value.days.map((day) => ({ day: day.day, value: day.events }))"
                label="Events"
            />
            <h2>Users</h2>
            <DailyChart
                :points="result.data.value.days.map((day) => ({ day: day.day, value: day.users }))"
                label="Users"
            />
        </template>
    </section>
</template>
