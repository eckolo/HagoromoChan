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
    if (!envelope || envelope.threadId !== requestedThreadId || !id(requestedThreadId) ||
        envelope.nextCursor !== null) return null;
    const turn = envelope.latestTurn;
    if (!turn || turn.status !== "completed" || !id(turn.id) ||
        !Number.isFinite(turn.completedAt) || turn.completedAt <= 0 || !Array.isArray(envelope.items)) return null;
    // A completed turn and even a single complete assistant item are not
    // answer classification. The host may supply an explicit authoritative
    // phase; the current native cloud reader does not expose it. Do not
    // populate phase from position, text, timestamp, or a history match.
    const items = envelope.items.filter(item => item?.turnId === turn.id && item.role === "assistant");
    if (items.some(item => !id(item.id) ||
        (item.phase !== "commentary" && item.phase !== "final_answer")) ||
        new Set(items.map(item => item.id)).size !== items.length) return null;
    const finals = items.filter(item => item.phase === "final_answer");
    if (finals.length !== 1) return null;
    const [answer] = finals;
    if (!id(answer.id) || answer.truncated !== false || !text(answer.text) ||
        !Number.isFinite(answer.completedAtMs) || answer.completedAtMs <= 0) return null;
    return { message_id: answer.id, text: answer.text };
}

// Do not assume a first page has every assistant item in the latest turn.
// The native reader may return empty intermediate pages or split a turn.
export async function collectNativeWorkPages(readWork, requestedThreadId, first) {
    let envelope = first;
    const firstTurn = first?.latestTurn;
    if (!firstTurn || first?.threadId !== requestedThreadId) return null;
    const seen = new Set();
    const items = [];
    for (let page = 0; page < 12; page++) {
        const turn = envelope?.latestTurn;
        if (envelope?.threadId !== requestedThreadId || !turn ||
            turn.id !== firstTurn.id || turn.status !== firstTurn.status ||
            turn.completedAt !== firstTurn.completedAt || !Array.isArray(envelope.items)) return null;
        items.push(...envelope.items);
        if (items.length > 200) return null;
        const cursor = envelope.nextCursor;
        if (cursor === null) return { ...first, items, nextCursor: null };
        if (typeof cursor !== "string" || !cursor || cursor.length > 4096 || seen.has(cursor) || page === 11) return null;
        seen.add(cursor);
        const response = await readWork(requestedThreadId, cursor);
        if (!response || response.state !== "authorized" || response.product !== "ChatGPT Work") return null;
        envelope = response.envelope;
    }
    return null;
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
    const response = await host.readWork(request.conversation_id, null);
    if (!response || response.state !== "authorized" || response.product !== "ChatGPT Work") {
        await host.resolveAnswer({ ...key, state: response?.state === "not_found" ? "not_found" : "not_authorized" });
        return "access_failed";
    }
    const snapshot = await collectNativeWorkPages(
        (threadId, cursor) => host.readWork(threadId, cursor), request.conversation_id, response.envelope);
    // Native readWork exposes a thread snapshot and cursors, not an
    // authoritative exact-item route with the older turn's final status.
    // A previously offered answer is unavailable once it is no longer the
    // verified final of this snapshot; a cached ID cannot prove its outcome.
    const answer = verifiedCompletedWorkAnswer(snapshot, request.conversation_id);
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

// Trusted platform event delivery is just a wake-up. Ignore every user-controlled
// routing field and retrieve the exact pending operation over the authenticated
// worker connection before deciding what to read. The host owns the allowlist.
export async function serviceWorkWake(wake, host) {
    const operation = wake?.data?.operation_id;
    if (wake?.name !== "work.retrieval.requested" || wake?.data?.channel_id !== "hagoromo-work-retrieval" ||
        typeof operation !== "string" || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(operation))
        return "invalid_wake";
    const pending = await host.getPending(operation);
    if (!pending || pending.operation_id !== operation) return "stale_wake";
    const scoped = {
        next: async () => pending,
        workMetadata: async () => {
            const accepted = [];
            const scope = await host.discoveryScope();
            for (const threadId of [...new Set(scope)].slice(0, 25)) {
                if (!id(threadId) || !await host.allowedWork(threadId)) continue;
                const record = await host.exactWorkMetadata(threadId);
                if (record?.threadId === threadId && verifiedWorkTitle(record)) accepted.push(record);
            }
            return accepted;
        },
        readWork: async (threadId, cursor) => await host.allowedWork(threadId)
            ? await host.readWork(threadId, cursor) : { state: "not_authorized" },
        resolveDiscovery: data => host.resolveDiscovery(data),
        resolveChoices: data => host.resolveChoices(data),
        resolveAnswer: data => host.resolveAnswer(data)
    };
    return serviceOneWorkRequest(scoped);
}
