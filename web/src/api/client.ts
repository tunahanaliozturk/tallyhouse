import type { z } from "zod";

import {
    FunnelResult,
    QuarantinePage,
    RetentionResult,
    SchemaList,
    SegmentResult,
    SessionsResult,
    type FunnelQuery,
    type RetentionQuery,
    type SegmentQuery,
    type SessionsQuery,
} from "./contract";

/**
 * A failed request, carrying what the server said. Validation failures come back as a list of problems,
 * which the forms show as they are: the server's wording is already written for a person.
 */
export class ApiError extends Error {
    constructor(
        readonly status: number,
        message: string,
        readonly problems: readonly string[],
    ) {
        super(message);
        this.name = "ApiError";
    }
}

async function request<T>(path: string, schema: z.ZodType<T>, init?: RequestInit): Promise<T> {
    const response = await fetch(path, {
        ...init,
        headers: {
            Accept: "application/json",
            ...(init?.body ? { "Content-Type": "application/json" } : {}),
        },
    });

    if (!response.ok) {
        throw await toError(response);
    }

    return schema.parse(await response.json());
}

async function toError(response: Response): Promise<ApiError> {
    try {
        const body: unknown = await response.json();

        if (typeof body === "object" && body !== null) {
            const title =
                "title" in body && typeof body.title === "string"
                    ? body.title
                    : response.statusText;
            const errors =
                "errors" in body && typeof body.errors === "object" && body.errors !== null
                    ? body.errors
                    : {};
            const problems = Object.values(errors)
                .flat()
                .filter((value): value is string => typeof value === "string");

            return new ApiError(response.status, title, problems);
        }
    } catch {
        // Not JSON: fall through to the status line.
    }

    return new ApiError(response.status, response.statusText || `HTTP ${response.status}`, []);
}

function post<T>(path: string, body: unknown, schema: z.ZodType<T>): Promise<T> {
    return request(path, schema, { method: "POST", body: JSON.stringify(body) });
}

export const api = {
    schemas: () => request("/v1/schemas", SchemaList),
    funnel: (query: FunnelQuery) => post("/v1/queries/funnel", query, FunnelResult),
    retention: (query: RetentionQuery) => post("/v1/queries/retention", query, RetentionResult),
    segment: (query: SegmentQuery) => post("/v1/queries/segment", query, SegmentResult),
    sessions: (query: SessionsQuery) => post("/v1/queries/sessions", query, SessionsResult),
    quarantine: (cursor: string | null) =>
        request(
            `/v1/quarantine?limit=25${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`,
            QuarantinePage,
        ),
};
