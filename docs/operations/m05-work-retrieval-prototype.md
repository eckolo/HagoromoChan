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
