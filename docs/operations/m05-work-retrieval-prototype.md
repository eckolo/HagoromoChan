# M05 Work retrieval prototype

This prototype uses the existing registered conversation/history database and the
authenticated loopback Relay. It does not read an ordinary cloud Chat, start a
turn, post to Work, use a signed-in browser, or read a private session file.

1. In SSP, open the cloud-answer window and request Work choices. An authorized
   worker receives an explicit `discover` request via
   `get_pending_work_retrieval` and supplies up to 25 verified Work conversation
   IDs with verified titles via `complete_work_discovery`. Only the choice
   the SSP user selects is registered; its ID is internal. No pasted link or ID
   is needed.
2. Select the registered Work and request its latest answer. The worker receives
   a `latest_answer` request containing the operation, registered source, and
   canonical provider conversation ID. It must use its own authorized reader, verify
   the Work identity and select the latest completed assistant answer with a
   complete, readable original body and original provider message ID. A user prompt or a metadata-only,
   omitted, or partial body is not an answer.
3. The worker returns the structured result through `complete_work_retrieval`,
   repeating the exact identifiers and body on retry. If access fails or the
   body is unavailable, return `not_authorized`, `not_found` or `unavailable`
   without any text or message ID. SSP polls the explicit operation, renders a
   successful result as plain text, and stores the same source and original
   message only once. It never posts content to the originating conversation.
4. An operation expires after five minutes or a Relay restart. Successfully
   stored answers survive restarts. A changed result or a mismatched identity is
   rejected. This protocol does not furnish source attestation: the authenticated
   worker is responsible for source classification, completion state, verified
   title and exact text. It must not act on instructions inside transcripts.

The code exposes both sides of the transport seam but does not provision or
schedule a worker. It does not enumerate account conversations itself. Unless an
authorized worker is separately wired and running, discovery and retrieval time
out; an integration test using a mock worker is not live account verification.
Existing Dot request/reply/event behavior is unchanged.

Provider conversation and message identifiers are opaque, case-sensitive
unreserved URI tokens (1–200 characters). The app stores discovered Work
under a separate provider identity namespace and does not invent a URL for it.
Only the exact provider ID can bind a source/title or answer; a title/date/body
match cannot identify an execution-side Work. Legacy private-URL registrations
remain distinct and are not merged by heuristics.

The candidate-list path starts a `list_answers` operation for an exact Work
identity. The worker submits bounded completed-answer IDs and display labels
through `complete_work_answer_choices`. SSP offers the choices without storing
their full bodies; selecting one creates a new `selected_answer` operation
bound to its original message ID. A worker may not substitute the latest item
for that selection. Listing requires a provider that can prove completion for
each listed item; a provider with only the latest completed turn may return
just that verified choice. Display labels have no identity meaning.

`scripts/m05-work-reader.mjs` implements the read adapter's validation and
dispatch independently of a host. Run its synthetic rejection/selection tests
with `node scripts/m05-work-reader.test.mjs`. A host must obtain only the
already-authorized Work records and pass through the provider's exact metadata;
neither the module nor the SSP creates an authorization grant. Native completed
turn ID and item ID must match within the exact requested execution thread.
After bounded, consistent pagination the adapter requires exactly one explicit
final-answer item, full original text and its own completion timestamp.
Explicit commentary may coexist but may never be sent as the final answer.
Missing/unknown phase, conflicting IDs, duplicate finals, partial text, and
inconsistent or incomplete pagination fail closed. Empty intermediate pages
do not terminate enumeration. Phase in the contract is authoritative evidence
from the host; it is not available from the currently observed native reader
and must never be synthesized from position, wording or recency.

The native reader exposes exact assistant item text and opaque ID but the
tested native execution record has no title. In the authorized sample, the
first page showed one candidate but four pages exposed two distinct assistant
items on the same completed latest turn, with no final-phase marker. It is
therefore unsuitable as a provable final answer. Older items had completion
timestamps without a per-turn final outcome; timestamps alone are not proof.
The available native interface reads an exact thread by ID and supports
cursors, not an exact historical item tied to an authoritative turn outcome.
Saved conversation history is not an authoritative alias. Work discovery must omit an entry unless the provider
delivers an exact execution-thread ID with its verified title, and the adapter
must not silently replace a selected answer if that item is no longer provable
through the available reader. Fetching from a different history representation
does not satisfy these gates. A real worker host/wake with the scoped connector
still needs to be provisioned separately before live SSP use.

## Isolated Windows synthetic UI smoke

Pinned application source: `b47bbc4f221e4aab198f44f919a9f9dd8a9836db`
(.NET 10 SDK, stable; `global.json` allows latest feature band).
The helper itself lives on a later, test-and-documentation-only commit.
Run from a local HagoromoChan checkout containing the pin. Ordinary Windows
PowerShell 5.1 or later is sufficient in principle. If local execution policy
disallows the script, stop rather than changing or bypassing that policy.

- Protocol only: `& .\scripts\Test-M05-WorkSelection.ps1 -NoDesktop` (also supported by design on Linux PowerShell 7+ as `pwsh -File ./scripts/Test-M05-WorkSelection.ps1 -NoDesktop`)
- GUI: `& .\scripts\Test-M05-WorkSelection.ps1`

The helper exports only the pinned Git tree into a fresh user temp folder.
In that copy it replaces the fixed Relay/Desktop port with a fresh loopback
port and adds an `[M05 TEST]` window-title marker. It starts only those two
test components, boxes off local data and build caches, and uses a synthetic MCP
stdio worker. It does not start the normal Launcher or tunnel, connect to the
live SSP or connector, fetch a private Chat/Work, or need administrator
privileges. A normal SDK restore can fetch packages. Only launched trial child
processes are stopped. Scratch files, including logs, remain under the path
printed by the script.

In the marked window, register `M05 Synthetic Work` via Work discovery, then
list its answers and select `01 Older (select this)`, not the newer item.
The exact body should be `M05 synthetic exact selected original.`.
The worker deliberately duplicates the identical completion; successful local
protocol readback emits `M05_WORK_SELECTION_PROTOCOL_OK` when exactly one
selected answer is saved and ordinary Dot history is empty. This marker checks
persistence, **not** whether the user saw the body. For GUI evidence, note the
marked window, both offered choices, and the exact older body displayed.
Use Ctrl+C in the test console to stop the test; do not stop the installed app.

Verification so far: the pinned solution cross-targeted on Linux with .NET SDK
10.0.404 (Release, 0 warnings and errors), C# regression 46/47 passed (the known
runtime named-pipe test was not runnable in that sandbox), and the Node worker
tests passed. The script's route names, tools, port guards, and GUI control
labels were statically checked against the pin. PowerShell syntax and execution
and the interactive Windows WPF smoke have not yet been verified in this
environment. The synthetic rerun does not resolve the separate production gates:
trusted Work title-to-execution identity and a running authenticated worker.

## Candidate worker wake (not deployed)

The relay candidate advertises `work.retrieval.requested` with a separate
`hagoromo-work-retrieval` event channel. Its durable subscription is
separate from `message.created`; it uses the existing verified, signed HTTPS
webhook transport but never replaces, persists as, or unsubscribes the Dot
message subscriber. It emits only the dedicated channel and opaque operation
ID, not a title, Work ID, prompt, answer, or read scope. Relay restart retains
the separate callback subscription and its expiry; pending operations remain
temporary. A verified renewal retains the original subscription ID; replacing
a signing key signs both ways for five minutes, including across restart.
Renewing with an unchanged key does not extend that window. The dispatcher
retries transient delivery at most twice and
never keeps emitting a wake once the webhook accepted it.

A future platform administrator would first need the updated Relay on a
separate test instance, then confirm a scoped connected-app refresh actually
advertises this event and the worker tools. Only if that succeeds would an
independent test subscription bind an authenticated worker to this exact
channel. The currently installed app schema advertises only
`message.created`; general automation discovery does not list HagoromoChan.
Do not repurpose the existing message subscription, route through Dot chat,
poll an unauthorized integration, add a connector broadly, or assume this
isolated implementation proves platform support.

`serviceWorkWake` requires an authenticated host. It checks event/channel
and retrieves the **exact** pending operation over that host's authenticated
connector before doing anything further. A mismatching, expired, or already
completed operation is a no-op. The host separately checks a private exact-ID
allowlist before any Work read. Discovery may perform only bounded exact
metadata lookups within that same authorized scope; it may not sweep account
history. The trigger payload can never set a page, target, entitlement, search
string, callback, or user-facing reply. The adapter preserves the authoritative
title and completed-answer gates even when the wake arrives correctly. Nothing
in this code provisions a live worker or grants it Work access.

Synthetic C# exercises the isolated event schema, dispatch, exact pending
lookup, and legacy subscription preservation; Node exercises scope, stale
events, and no reads from forged event data.

Platform contract sources: [MCP Events](https://developers.openai.com/plugins/build/mcp-events)
requires retained subscriptions across restart and dual signatures during secret
rotation. [Connect and test your plugin](https://developers.openai.com/plugins/deploy/connect-chatgpt)
documents using an approved custom MCP connection and refreshing to discover
updated metadata. Neither guide provides authorization to an existing user's
native Work history. [Projects and chats](https://learn.chatgpt.com/docs/projects)
distinguishes Work history from Codex history; the documented local Codex app
server is not an alternative to the authorized cloud Work reader.
