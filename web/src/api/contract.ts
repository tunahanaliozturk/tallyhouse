// Every response the dashboard reads is parsed here, against schemas that are checked against the types
// generated from the server's OpenAPI document. A field the server renames or adds makes one of these
// `satisfies` clauses fail the type-check, and a response that does not match at runtime fails at the
// fetch, with the path of the offending field, rather than three components later as `undefined`.
import { z } from "zod";

import type { components } from "./schema";

type Schemas = components["schemas"];

// The server writes numbers as numbers. The document also allows strings, because ASP.NET accepts them on
// the way in, so the generated types are wider than anything the dashboard will actually receive.
const count = z.number().int().nonnegative();
const ratio = z.number().min(0);
const day = z.iso.date();

export const Sampled = z.object({
    threshold: count,
    scale: ratio,
    isEstimate: z.boolean(),
}) satisfies z.ZodType<Schemas["Sampled"]>;

export const FunnelStep = z.object({
    event: z.string(),
    users: count,
    conversionFromPrevious: ratio,
    conversionFromStart: ratio,
    medianSecondsFromStart: ratio.nullable(),
}) satisfies z.ZodType<Schemas["FunnelStep"]>;

export const FunnelResult = z.object({
    steps: z.array(FunnelStep),
    sampling: Sampled,
}) satisfies z.ZodType<Schemas["FunnelResult"]>;

export const RetentionCohort = z.object({
    day,
    users: count,
    retained: z.array(count),
}) satisfies z.ZodType<Schemas["RetentionCohort"]>;

export const RetentionResult = z.object({
    cohorts: z.array(RetentionCohort),
    sampling: Sampled,
}) satisfies z.ZodType<Schemas["RetentionResult"]>;

export const SegmentDay = z.object({
    day,
    events: count,
    users: count,
}) satisfies z.ZodType<Schemas["SegmentDay"]>;

export const SegmentResult = z.object({
    days: z.array(SegmentDay),
    lateEvents: count,
    sampling: Sampled,
}) satisfies z.ZodType<Schemas["SegmentResult"]>;

export const SessionsDay = z.object({
    day,
    sessions: count,
    users: count,
    medianDurationSeconds: ratio,
    eventsPerSession: ratio,
}) satisfies z.ZodType<Schemas["SessionsDay"]>;

export const SessionsResult = z.object({
    days: z.array(SessionsDay),
}) satisfies z.ZodType<Schemas["SessionsResult"]>;

export const FieldSpec = z.object({
    type: z.enum(["string", "number", "integer", "boolean"]),
    required: z.boolean(),
    enum: z.array(z.string()).nullable().optional(),
}) satisfies z.ZodType<Schemas["FieldSpec"]>;

export const SchemaSummary = z.object({
    event: z.string(),
    version: count,
    spec: z.object({ properties: z.record(z.string(), FieldSpec) }),
}) satisfies z.ZodType<Schemas["SchemaResponse"]>;

export const SchemaList = z.array(SchemaSummary);

export const QuarantineItem = z.object({
    id: z.uuid(),
    receivedAt: z.iso.datetime({ offset: true }),
    messageId: z.string(),
    event: z.string(),
    reason: z.string(),
    payload: z.string(),
}) satisfies z.ZodType<Schemas["QuarantineItemResponse"]>;

export const QuarantinePage = z.object({
    items: z.array(QuarantineItem),
    next: z.string().nullable(),
}) satisfies z.ZodType<Schemas["QuarantinePageResponse"]>;

/** Request bodies are typed straight from the document: a renamed query field is a type error here. */
export type FunnelQuery = Schemas["FunnelQuery"];
export type RetentionQuery = Schemas["RetentionQuery"];
export type SegmentQuery = Schemas["SegmentQuery"];
export type SessionsQuery = Schemas["SessionsQuery"];
export type FilterOperator = Schemas["FilterOperator"];

export type FunnelResult = z.infer<typeof FunnelResult>;
export type FunnelStep = z.infer<typeof FunnelStep>;
export type RetentionResult = z.infer<typeof RetentionResult>;
export type RetentionCohort = z.infer<typeof RetentionCohort>;
export type SegmentResult = z.infer<typeof SegmentResult>;
export type SegmentDay = z.infer<typeof SegmentDay>;
export type SessionsResult = z.infer<typeof SessionsResult>;
export type SchemaSummary = z.infer<typeof SchemaSummary>;
export type QuarantinePage = z.infer<typeof QuarantinePage>;
export type QuarantineItem = z.infer<typeof QuarantineItem>;
