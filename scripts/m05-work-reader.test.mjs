import assert from "node:assert/strict";
import { verifiedWorkTitle, verifiedCompletedWorkAnswer, serviceOneWorkRequest } from "./m05-work-reader.mjs";

const thread = "work_real_fixture";
const turn = { status: "completed", id: "turn_completed", completedAt: 1234 };
const item = { id: "msg_Opaque", turnId: turn.id, role: "assistant",
    truncated: false, completedAtMs: 1234567, text: " complete\noriginal " };
const envelope = { threadId: thread, latestTurn: turn, items: [item] };
const record = { ...envelope, title: "Verified title" };
assert.deepEqual(verifiedCompletedWorkAnswer(envelope, thread), { message_id: item.id, text: item.text });
assert.equal(verifiedCompletedWorkAnswer(envelope, "similar_other"), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, latestTurn: { ...turn, status: "running" } }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [{ ...item, truncated: true }] }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [item, { ...item, id: "msg_other" }] }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [{ ...item, role: "user" }] }, thread), null);
assert.equal(verifiedCompletedWorkAnswer({ ...envelope, items: [{ ...item, turnId: "turn_prior" }] }, thread), null);
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
host.next = async () => ({ operation_id: key.operation_id, action: "discover" });
assert.equal(await serviceOneWorkRequest(host), "discovery_resolved");
assert.deepEqual(response.choices, [{ conversation_id: thread, title: "Verified title" }]);
console.log("PASS authorized Work reader validation and operation dispatch");
