import assert from "node:assert/strict";
import { verifiedWorkTitle, verifiedCompletedWorkAnswer, collectNativeWorkPages, serviceOneWorkRequest, serviceWorkWake } from "./m05-work-reader.mjs";

const thread = "work_real_fixture";
const turn = { status: "completed", id: "turn_completed", completedAt: 1234 };
const item = { id: "msg_Opaque", turnId: turn.id, role: "assistant", phase: "final_answer",
    truncated: false, completedAtMs: 1234567, text: " complete\noriginal " };
const envelope = { threadId: thread, latestTurn: turn, items: [item], nextCursor: null };
const record = { ...envelope, title: "Verified title" };
assert.deepEqual(verifiedCompletedWorkAnswer(envelope, thread), { message_id: item.id, text: item.text });
assert.equal(verifiedCompletedWorkAnswer(envelope, "similar_other"), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, latestTurn: { ...turn, status: "running" } }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [{ ...item, truncated: true }] }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [item, { ...item, id: "msg_other" }] }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [{ ...item, role: "user" }] }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [{ ...item, turnId: "turn_prior" }] }, thread), null);
const commentary = { ...item, id: "msg_commentary", phase: "commentary" };
const phaseLess = { ...item };
delete phaseLess.phase;
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [phaseLess] }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [commentary] }, thread), null);
assert.deepEqual(verifiedCompletedWorkAnswer({ ...envelope, items: [commentary, item] }, thread),
    { message_id: item.id, text: item.text });
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [commentary, { ...item, truncated: true }] }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [{ ...phaseLess, id: "other_unknown" }, item] }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [{ ...commentary, id: item.id }, item] }, thread), null);
assert.deepEqual(verifiedWorkTitle({ threadId: thread, product: "ChatGPT Work", metadata: record }),
    { conversation_id: thread, title: "Verified title" });
assert.equal(verifiedWorkTitle({ threadId: thread, product: "ChatGPT Work",
    metadata: { ...record, threadId: "other_history_id" } }), null);
assert.equal(verifiedWorkTitle({ threadId: thread, product: "ChatGPT", metadata: record }), null);

const key = { operation_id: "operation_1", source_id: "local_work", conversation_id: thread };
let response;
const host = {
    next: async () => ({ ...key, action: "selected_answer", message_id: item.id }),
    readWork: async target => {
        assert.equal(target, thread);
        return { state: "authorized", product: "ChatGPT Work", envelope };
    },
    resolveAnswer: async result => { response = result; },
    resolveChoices: async result => { response = result; },
    resolveDiscovery: async result => { response = result; },
    workMetadata: async () => [
        { threadId: thread, product: "ChatGPT Work", metadata: record },
        { threadId: "misjoined", product: "ChatGPT Work", metadata: record }
    ]
};
assert.equal(await serviceOneWorkRequest(host), "answer_resolved");
assert.deepEqual(response, { ...key, state: "available", message_id: item.id, text: item.text });
host.next = async () => ({ ...key, action: "selected_answer", message_id: "msg_opaque" });
assert.equal(await serviceOneWorkRequest(host), "body_unavailable");
assert.deepEqual(response, { ...key, state: "unavailable" });
host.next = async () => ({ ...key, action: "list_answers" });
assert.equal(await serviceOneWorkRequest(host), "answer_choices_resolved");
assert.equal(response.choices[0].message_id, item.id);
assert.equal(Object.hasOwn(response.choices[0], "text"), false);
const prior = { ...item, id: "msg_prior_final", turnId: "turn_prior" };
host.next = async () => ({ ...key, action: "selected_answer", message_id: prior.id });
assert.equal(await serviceOneWorkRequest({
    ...host, readWork: async () => ({ state: "authorized", product: "ChatGPT Work", envelope: { ...envelope, items: [prior, item] } })
}), "body_unavailable");
assert.deepEqual(response, { ...key, state: "unavailable" });
host.next = async () => ({ ...key, action: "list_answers" });
assert.equal(await serviceOneWorkRequest({
    ...host, readWork: async () => ({ state: "authorized", product: "ChatGPT Work", envelope: { ...envelope, items: [phaseLess] } })
}), "body_unavailable");
assert.deepEqual(response, { ...key, state: "unavailable" });
host.next = async () => ({ operation_id: key.operation_id, action: "discover" });
assert.equal(await serviceOneWorkRequest(host), "discovery_resolved");
assert.deepEqual(response.choices, [{ conversation_id: thread, title: "Verified title" }]);
console.log("PASS authorized Work reader validation and operation dispatch");

const operation = "11111111-2222-4333-8444-555555555555";
const wake = { name: "work.retrieval.requested", data: { channel_id: "hagoromo-work-retrieval",
    operation_id: operation, source_id: "must_not_grant_access", conversation_id: "unauthorized" } };
let reads = 0;
const safeHost = {
    ...host,
    getPending: async id => { assert.equal(id, operation); return { ...key, operation_id: operation, action: "selected_answer", message_id: item.id }; },
    allowedWork: async id => id === thread,
    readWork: async id => { reads++; return host.readWork(id); }
};
assert.equal(await serviceWorkWake(wake, safeHost), "answer_resolved");
assert.equal(reads, 1);
assert.equal(response.conversation_id, thread);
assert.equal(await serviceWorkWake(wake, { ...safeHost, allowedWork: async () => false }), "access_failed");
assert.equal(reads, 1);
assert.equal(response.state, "not_authorized");
assert.equal(await serviceWorkWake(wake, { ...safeHost, getPending: async () => key }), "stale_wake");
assert.equal(await serviceWorkWake({ ...wake, name: "message.created" }, safeHost), "invalid_wake");
assert.equal(reads, 1);
let metadataReads = 0;
assert.equal(await serviceWorkWake(wake, {
    ...safeHost,
    getPending: async () => ({ operation_id: operation, action: "discover" }),
    discoveryScope: async () => [thread, "unauthorized"],
    exactWorkMetadata: async id => {
        metadataReads++;
        assert.equal(id, thread);
        return { threadId: id, product: "ChatGPT Work", metadata: record };
    }
}), "discovery_resolved");
assert.equal(metadataReads, 1);
assert.deepEqual(response.choices, [{ conversation_id: thread, title: "Verified title" }]);
console.log("PASS scoped Work event wakes and no event data grants reads");

const firstPage = { ...envelope, nextCursor: "cursor_1" };
const emptyPage = { ...envelope, items: [], nextCursor: "cursor_2" };
const laterAssistant = { ...item, id: "msg_other_in_same_turn" };
const lastPage = { ...envelope, items: [laterAssistant] };
assert.equal(verifiedCompletedWorkAnswer(firstPage, thread), null);
const joined = await collectNativeWorkPages(async (id, cursor) => {
    assert.equal(id, thread);
    return { state: "authorized", product: "ChatGPT Work",
        envelope: cursor === "cursor_1" ? emptyPage : lastPage };
}, thread, firstPage);
assert.equal(joined.items.length, 2);
assert.equal(verifiedCompletedWorkAnswer(joined, thread), null);
assert.equal(await collectNativeWorkPages(async () => ({
    state: "authorized", product: "ChatGPT Work", envelope: firstPage
}), thread, firstPage), null);
assert.equal(await collectNativeWorkPages(async () => ({
    state: "authorized", product: "ChatGPT Work",
    envelope: { ...lastPage, latestTurn: { ...turn, id: "changed" } }
}), thread, firstPage), null);
console.log("PASS native pagination rejects hidden same-turn ambiguity, cursor loops and changed snapshots");
