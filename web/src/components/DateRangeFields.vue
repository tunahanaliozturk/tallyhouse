<script setup lang="ts">
import { useId } from "vue";

const from = defineModel<string>("from", { required: true });
const to = defineModel<string>("to", { required: true });

const id = useId();
</script>

<template>
    <div class="field">
        <label :for="`${id}-from`">From</label>
        <input :id="`${id}-from`" v-model="from" type="date" required />
    </div>
    <div class="field">
        <label :for="`${id}-to`">To</label>
        <input :id="`${id}-to`" v-model="to" type="date" :min="from" required />
    </div>
</template>
