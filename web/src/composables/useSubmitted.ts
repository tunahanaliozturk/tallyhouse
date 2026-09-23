import { ref, toRaw, type Ref } from "vue";

/**
 * A form edits a draft; the query runs on what was last submitted. Keeping the two apart means typing in
 * a date field does not fire a query over a hundred million rows on every keystroke, and the result on
 * screen always matches the parameters that produced it.
 */
export function useSubmitted<T extends object>(initial: T) {
    const draft = ref(structuredClone(initial)) as Ref<T>;
    const submitted = ref(structuredClone(initial)) as Ref<T>;

    function submit() {
        submitted.value = structuredClone(toRaw(draft.value));
    }

    return { draft, submitted, submit };
}
