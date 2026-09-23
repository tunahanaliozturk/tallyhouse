<script setup lang="ts">
import { useQuery } from "@tanstack/vue-query";
import { computed } from "vue";

import { api } from "@/api/client";
import type { SessionsQuery } from "@/api/contract";
import DailyChart from "@/components/DailyChart.vue";
import DateRangeFields from "@/components/DateRangeFields.vue";
import QueryState from "@/components/QueryState.vue";
import { useSubmitted } from "@/composables/useSubmitted";
import { addDays, formatCount, formatDuration, utcToday } from "@/format";

const { draft, submitted, submit } = useSubmitted<SessionsQuery>({
    from: addDays(utcToday(), -13),
    to: utcToday(),
});

const result = useQuery({
    queryKey: computed(() => ["sessions", submitted.value]),
    queryFn: () => api.sessions(submitted.value),
});
</script>

<template>
    <section aria-labelledby="sessions-title">
        <header class="page-header">
            <h1 id="sessions-title">Sessions</h1>
            <p>
                Events stitched into sessions by a 30 minute inactivity gap, cut at midnight UTC.
                Days are recomputed when events for them arrive, so a late event can merge two
                sessions.
            </p>
        </header>

        <form class="query-form" @submit.prevent="submit">
            <DateRangeFields v-model:from="draft.from" v-model:to="draft.to" />
            <button type="submit" class="button">Run</button>
        </form>

        <QueryState
            :loading="result.isFetching.value && !result.data.value"
            :error="result.error.value"
        />

        <template v-if="result.data.value">
            <p v-if="result.data.value.days.length === 0" class="note">
                No sessions in this range yet.
            </p>
            <template v-else>
                <DailyChart
                    :points="
                        result.data.value.days.map((day) => ({ day: day.day, value: day.sessions }))
                    "
                    label="Sessions"
                />
                <div class="table-scroll">
                    <table class="data">
                        <caption class="visually-hidden">
                            Sessions per day
                        </caption>
                        <thead>
                            <tr>
                                <th scope="col">Day</th>
                                <th scope="col">Sessions</th>
                                <th scope="col">Users</th>
                                <th scope="col">Median length</th>
                                <th scope="col">Events per session</th>
                            </tr>
                        </thead>
                        <tbody>
                            <tr v-for="day in result.data.value.days" :key="day.day">
                                <th scope="row">{{ day.day }}</th>
                                <td class="number">{{ formatCount(day.sessions) }}</td>
                                <td class="number">{{ formatCount(day.users) }}</td>
                                <td class="number">
                                    {{ formatDuration(day.medianDurationSeconds) }}
                                </td>
                                <td class="number">{{ day.eventsPerSession.toFixed(1) }}</td>
                            </tr>
                        </tbody>
                    </table>
                </div>
            </template>
        </template>
    </section>
</template>
