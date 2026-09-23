<script setup lang="ts">
import { computed } from "vue";

import type { RetentionCohort } from "@/api/contract";
import { formatCount, formatPercent } from "@/format";

const props = defineProps<{ cohorts: readonly RetentionCohort[]; days: number }>();

const offsets = computed(() => Array.from({ length: props.days + 1 }, (_, day) => day));

const rows = computed(() =>
    props.cohorts.map((cohort) => ({
        day: cohort.day,
        users: cohort.users,
        cells: offsets.value.map((offset) => {
            const retained = cohort.retained[offset] ?? 0;
            const share = cohort.users === 0 ? 0 : retained / cohort.users;
            return { offset, retained, share };
        }),
    })),
);

// The colour carries the same number as the text, so nothing is lost for someone who cannot see it. The
// shade stops at 60% so the text on it keeps its contrast in both colour schemes.
const shade = (share: number): string => `rgb(31 111 235 / ${(0.06 + share * 0.54).toFixed(2)})`;
</script>

<template>
    <div class="table-scroll">
        <table class="retention">
            <caption>
                Share of each cohort that returned, by days since their first event
            </caption>
            <thead>
                <tr>
                    <th scope="col">Cohort</th>
                    <th scope="col">Users</th>
                    <th v-for="offset in offsets" :key="offset" scope="col">Day {{ offset }}</th>
                </tr>
            </thead>
            <tbody>
                <tr v-for="row in rows" :key="row.day">
                    <th scope="row">{{ row.day }}</th>
                    <td class="number">{{ formatCount(row.users) }}</td>
                    <td
                        v-for="cell in row.cells"
                        :key="cell.offset"
                        class="retention__cell"
                        :style="{ backgroundColor: shade(cell.share) }"
                        :title="`${formatCount(cell.retained)} of ${formatCount(row.users)}`"
                    >
                        {{ formatPercent(cell.share) }}
                    </td>
                </tr>
            </tbody>
        </table>
    </div>
</template>
