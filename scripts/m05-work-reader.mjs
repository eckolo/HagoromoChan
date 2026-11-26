// Deterministic bridge logic for a host with an authorized native Work reader
// and a scoped SSP connector. This module is not itself an authenticated host.
const id = value => typeof value === "string" && /^[A-Za-z0-9._~-]{1,200}$/.test(value) &&
    ![".", "..", "00000000-0000-0000-0000-000000000000"].includes(value);
const text = value => typeof value === "string" && value.trim().length > 0 && value.length <= 32768;
const label = value => typeof value === "string" && value.trim().length > 0 && value.length <= 200 &&
    !/[\u0000-\u001f\u007f-\u009f]/u.test(value);

export function verifiedWorkTitle(candidate) {
    if (!candidate || candidate.product !== "ChatGPT Work" || !id(candidate.threadId)) return null;
    const metadata = candidate.metadata;
    // History searches, title/date/text comparisons and inferred cross-IDs are not proof.
    if (!metadata || metadata.threadId !== candidate.threadId || !label(metadata.title)) return null;
    return { conversation_id: candidate.threadId, title: metadata.title };
}

export function verifiedCompletedWorkAnswer(envelope, requestedThreadId) {
    if (!envelope || envelope.threadId !== requestedThreadId || !id(requestedThreadId)) return null;
    const turn = envelope.latestTurn;
    if (!turn || turn.status !== "completed" || !id(turn.id) ||
        !Number.isFinite(turn.completedAt) || turn.completedAt <= 0 || !Array.isArray(envelope.items)) return null;
    // Do not guess which assistant item is final when the provider supplies no phase.
    const items = envelope.items.filter(item => item?.turnId === turn.id && item.role === "assistant");
    if (items.length !== 1) return null;
    const answer = items[0];
    if (!id(answer.id) || answer.truncated !== false || !text(answer.text) ||
        !Number.isFinite(answer.completedAtMs) || answer.completedAtMs <= 0) return null;
    return { message_id: answer.id, text: answer.text };
}

// next(), workMetadata(), readWork(), resolve*() all come from the authenticated
// host. workMetadata is scoped/authorized by the host, not a broad search.
export async function serviceOneWorkRequest(host) {
    const request = await host.next();
    if (!request) return "idle";
    if (!id(request.operation_id)) return "invalid_operation";
    if (request.action === "discover") {
        const candidates = await host.workMetadata();
        const byId = new Map(), conflicting = new Set();
        for (const item of candidates) {
            const choice = verifiedWorkTitle(item);
            if (!choice) continue;
            const key = choice.conversation_id;
            if (byId.has(key) && byId.get(key).title !== choice.title) conflicting.add(key);
            else byId.set(key, choice);
        }
        const choices = [...byId.values()].filter(choice => !conflicting.has(choice.conversation_id)).slice(0, 25);
        await host.resolveDiscovery({ operation_id: request.operation_id, choices });
        return "discovery_resolved";
    }

    const action = request.action;
    if (!["list_answers", "selected_answer", "latest_answer"].includes(action) ||
        !id(request.source_id) || !id(request.conversation_id) ||
        (action === "selected_answer" && !id(request.message_id))) return "invalid_operation";
    const key = { operation_id: request.operation_id, source_id: request.source_id,
        conversation_id: request.conversation_id };
    const response = await host.readWork(request.conversation_id);
    if (!response || response.state !== "authorized" || response.product !== "ChatGPT Work") {
        await host.resolveAnswer({ ...key, state: response?.state === "not_found" ? "not_found" : "not_authorized" });
        return "access_failed";
    }
    const answer = verifiedCompletedWorkAnswer(response.envelope, request.conversation_id);
    if (!answer || (action === "selected_answer" && answer.message_id !== request.message_id)) {
        await host.resolveAnswer({ ...key, state: "unavailable" });
        return "body_unavailable";
    }
    if (action === "list_answers") {
        await host.resolveChoices({ ...key, choices: [{ message_id: answer.message_id, label: "確認できた完了回答" }] });
        return "answer_choices_resolved";
    }
    await host.resolveAnswer({ ...key, state: "available", ...answer });
    return "answer_resolved";
}
