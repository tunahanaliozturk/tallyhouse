const integer = new Intl.NumberFormat("en", { maximumFractionDigits: 0 });
const compact = new Intl.NumberFormat("en", { notation: "compact", maximumFractionDigits: 1 });
const percent = new Intl.NumberFormat("en", { style: "percent", maximumFractionDigits: 1 });

export const formatCount = (value: number): string => integer.format(value);

export const formatCompact = (value: number): string => compact.format(value);

export const formatPercent = (value: number): string => percent.format(value);

/** "3d 4h", "2h 15m", "45s": the two largest units, which is all a funnel reader needs. */
export function formatDuration(seconds: number): string {
    if (seconds < 60) {
        return `${Math.round(seconds)}s`;
    }

    const units: [number, string][] = [
        [86_400, "d"],
        [3_600, "h"],
        [60, "m"],
    ];

    const parts: string[] = [];
    let rest = Math.round(seconds);

    for (const [size, label] of units) {
        if (rest >= size && parts.length < 2) {
            parts.push(`${Math.floor(rest / size)}${label}`);
            rest %= size;
        }
    }

    return parts.join(" ");
}

/** Today in UTC, as the server counts days. */
export function utcToday(): string {
    return new Date().toISOString().slice(0, 10);
}

export function addDays(day: string, days: number): string {
    const date = new Date(`${day}T00:00:00Z`);
    date.setUTCDate(date.getUTCDate() + days);
    return date.toISOString().slice(0, 10);
}
