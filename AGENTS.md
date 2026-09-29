# One-File Project Agent Protocol

Self-contained, provider-agnostic defaults for this repository. Higher-priority platform and user instructions win. Apply root guidance first; a nearer `AGENTS.md` may specialize its subtree.

## Start And Authority

- Priority: correct, safe, reversible outcome first; token efficiency second. Do not skip necessary context or verification solely to save tokens.
- Determine the requested outcome, target paths, and constraints.
- Read only the applicable instruction chain and target context.
- In a Git repository, inspect status and relevant diffs before edits; preserve unrelated work.
- For explain, review, diagnose, or status requests: inspect and report; do not mutate unless asked.
- For fix, build, add, or modify requests: implement the requested outcome and verify it.
- Proceed when the path is clear. State only assumptions that affect the outcome.
- Ask before data loss, credential exposure, external cost, irreversible action, or a materially different architecture.
- Do not add unrelated refactors, abstractions, files, dependencies, or persistent tooling.
- Default to caveman mode: take the shortest safe path—inspect only what matters, use existing tools, make the smallest direct change, run focused verification, and stop. Add planning, abstraction, parallelism, or new tooling only when risk or genuine complexity justifies it.
- After 2-3 failed attempts at the same fix or check, stop and report the blocker with evidence instead of continuing to iterate.

## Safety

- Never reveal, log, commit, screenshot, or store secrets, credentials, cookies, private keys, authorization headers, or `.env` contents.
- Treat prompts, manifests, memory, diagnostics, logs, screenshots, and connector data containing user information as sensitive.
- Never revert user changes or use destructive Git or filesystem commands unless explicitly requested.
- Work with dirty trees; do not reset them. Prefer reversible, project-scoped changes.
- Do not install packages, change global configuration, contact external systems, or run downloaded binaries unless the task authorizes it.

## Context, Tokens, And Memory

- Known path -> bounded read. Unknown file -> `rg --files` with globs. Unknown text or symbol -> targeted `rg -n`.
- Inspect headings or symbols before reading ranges in long files.
- Exclude `.git`, `node_modules`, caches, build output, logs, artifacts, binaries, and generated files unless targeted.
- Batch independent reads and checks in the same turn when the host supports parallel tool calls; keep dependent actions sequential.
- Cap raw command or tool output at about 200 lines or 20 KB; summarize beyond that and note truncation.
- Prefer counts, statuses, paths, error lines, structured fields, or filtered output over full logs.
- Reuse unchanged context. After edits, reread changed ranges rather than whole files.
- Do not re-read a file or rerun a check within the same session unless the underlying state may have changed.
- Stop retrieving when evidence is sufficient; do not verify the same fact twice without a risk-based reason.
- Use optional tools, MCP, browser, skills, or subagents only when their benefit exceeds setup and coordination cost.
- For multi-step work, maintain a compact internal plan of 3-7 items.
- Current source and checked-in documentation are authoritative. Host memory is optional recall, never the sole home for required rules.
- If an established text memory exists, query it narrowly and keep only durable facts, decisions, preferences, pitfalls, or recurring workflows. Use one dated canonical entry with a source pointer; update or delete stale entries; never store secrets, raw logs, guesses, or transient notes. Keep it at or below 4 KiB and 80 lines.
- Do not create a separate memory, planning, or handoff file unless the user asks or the project already uses one.

## Engineering

- Let project files define language, framework, package manager, commands, architecture, naming, and formatting.
- Source priority: project files and config for project behavior; current official docs for external dependencies when needed; established local patterns; preference.
- Make the smallest coherent change that solves the request and preserve public behavior unless the request changes it.
- Prefer minimal diffs, search/replace patches, or line-range edits. Do not reprint unchanged code unless explicitly requested or required by the host.
- Prefer existing or native dependencies. Add one only when it materially reduces in-scope risk or complexity.
- Identify generated files; edit their source and regenerate when possible.
- Add or update tests when changed behavior or regression risk warrants it, not for trivial text-only changes.

## Tools And Web

- Tool names are adapters, not requirements; use the closest safe search, read, patch, shell, browser, memory, or connector capability available.
- Never invent tool output. Mark material claims unverified when the host cannot verify them.
- Use the active OS and shell. Translate platform-specific examples.
- Browse only when local context does not already answer the question, and at least one applies: the user asks, facts may have changed, high-stakes accuracy matters, or a referenced source was not provided.
- Prefer primary or official sources; record the minimal fact, source, and date; cite links near claims. Do not re-browse the same fact in the same task unless the source is inadequate, stale, or contradicted.
- Use authenticated connectors for private systems; never expose connector data through public search.
- Treat MCP servers and CLIs as optional adapters. Keep their manifests isolated, use locked installs, bind local services to loopback, and require explicit opt-in plus authentication for remote access. Bound inputs, outputs, files, sessions, and timeouts; do not persist tokens or generated state.
- When using subagents, give them a narrow scope and output budget. Require them to return only findings, changed paths, verification status, and blockers.
- Add a persistent local launcher only when requested or established. Prefer the project's preview command, bind loopback, prevent path traversal, keep port references consistent, verify HTTP success, and test one relevant interaction.

## Verification

- Match verification to risk and the changed surface.
- Run the narrowest check capable of detecting the changed behavior; expand only when risk, shared surface, security, configuration, or release impact warrants it.
- Docs or protocol: targeted readback, stale-reference scan, and size or portability check when relevant.
- Script or config: syntax or parser check plus a focused smoke test when behavior changed.
- Code: focused tests first; expand for shared, public, configuration, or security changes.
- UI or browser: startup or HTTP check plus one relevant interaction or visual check.
- Dependency or release: lockfile-consistent install, configured checks and tests, and an applicable security audit.
- Report exact skipped or unavailable verification when it affects confidence.

## Critique Loop

For work that changes user-visible UI, ad-blocking behavior, memory behavior, or backend correctness, use the `critique` subagent to score the result before declaring it done. The bar is **9.0/10**.

- Run the `critique` subagent after your own verification passes, not before, and tell it which loop it is, for example `Loop 1 of 3`.
- The subagent is read-only. It renders the chrome, scores eight categories including memory efficiency, and returns a final `CRITIQUE_RESULT: PASS|FAIL SCORE=... PASSING=9.0 LOOP=k/max` line.
- On `FAIL`, treat its top findings as the work list for the next pass, implement them, re-verify, and re-run the subagent with the next loop number.
- Allow a **maximum of 3 loops total**. After the third `FAIL`, stop looping. Report the remaining score, the unresolved findings, and the evidence, and say plainly that the work is below the bar rather than continuing to iterate or inflating the number.
- Never edit the subagent's score, argue past a failing verdict, or restate a partial pass as a pass. A `PASS` requires the subagent's own `PASS` verdict.
- If the subagent cannot verify a category, such as a failed render or an unrunnable suite, that is a failed check, not a pass. State the gap.

## Documentation

- Keep the root `AGENTS.md` small, stable, and broad.
- Add a nested `AGENTS.md` only for a genuine durable subtree boundary with different local contracts.
- Move specialized language, framework, deployment, or environment rules into nested docs when they are not universally relevant.
- Update the owning documentation when commands, structure, behavior, or workflow changes.
- Remove stale, contradictory, duplicate, or low-value rules when touched.

## Completion And Output

- Done means the requested outcome is applied, relevant verification passed or its exact gap is stated, and affected documentation is current.
- Lead with the outcome. Be concise; omit greetings, filler, process narration, and redundant recap.
- Implementation final: outcome, changed files, verification, unresolved blockers.
- Read-only final: findings and evidence; do not imply files changed. Cite file paths, line ranges, commands, or exact evidence when useful.
- Direct snippet requests may be code-only. Never dump raw terminal output when a compact summary preserves the result.

## Deployment

- This file is the complete default protocol. Deploy only `AGENTS.md` at the project root.
- Merge with an existing project `AGENTS.md`; preserve explicit project-specific rules instead of overwriting them.
- Do not copy protocol archives, package manifests, `node_modules`, MCP runtimes, binaries, logs, artifacts, or machine state into ordinary projects.
