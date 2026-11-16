# Code style

Applies to CPR. Follow [.editorconfig](.editorconfig) and the conventions below.

## Scope and maintenance

- Explicit user instructions and the latest corrections take precedence. Update the relevant rule here and apply it to affected code in the same pass. Push only when explicitly instructed for the current changes; an earlier push request does not authorize later pushes.
- Use descriptive commit subjects without an `Auto:` prefix for changes to this CPR repository.
- Keep this guide concise: merge overlapping rules, replace stale guidance, and link to existing workflow documentation instead of repeating it.
- Match surrounding conventions where unspecified. Keep changes focused; avoid unrelated rewrites, single-use indirection, and duplicated validation rules. Do not generalize isolated patterns without direction.
- Do not add test projects, test files, or temporary verification artifacts unless requested.
- Keep verification proportional to the change and preserve observable behavior when applying style changes.
- Preserve encoding, line endings, import order, tuple spacing, trailing commas, and resource lifetimes. Existing files may use LF and UTF-8 BOMs despite `.editorconfig`.

## Naming

| Kind | Convention |
| --- | --- |
| C# types, methods, properties, events, exported columns | PascalCase; preserve acronyms such as `CPR`, `SQL`, `HTTP` |
| Interfaces | `I` prefix, as in `IService` |
| Parameters and larger callback parameters | `tName`; caught exceptions use `tException` |
| Locals | Explicit type and `tempName`, as in `int tempCount` |
| Loop indices and short lambdas | Short meaningful names: `i`, `j`, `x` |
| Private/protected instance fields | `_name` |
| Public fields | camelCase |
| Member constants | `UPPER_SNAKE_CASE` |

- Boolean names, including nullable booleans, must use the `Is` prefix with the applicable casing and variable prefix: `IsEnabled`, `isEnabled`, `tIsEnabled`, `tempIsEnabled`, and `_isEnabled`. Preserve external API/query names.
- Async methods end in `Async`; place `CancellationToken tCancel` last.
- Preserve external API/query names. Follow local conventions for static readonly fields and local constants.

## Layout and control flow

- Use tabs at width four, block-scoped namespaces, and external `using` directives. Entry points use top-level statements.
- Put braces on their own lines and around control-flow bodies. Methods and constructors use block bodies; simple properties and indexers may use expressions.
- Space nonempty parentheses and brackets: `Add( tValue )`, `tValues[ i ]`, `new double[ tLength ]`. Keep empty delimiters, casts, and attribute brackets compact: `new()`, `[]`, `(double)tValue`, `[Route( "csv" )]`.
- Keep complete `if` conditions on one line. Follow surrounding wrapping elsewhere.
- Use short stage/calculation comments without ending punctuation. Separate standalone comments from preceding code with a blank line; keep each comment attached to the code it describes. Routine XML documentation is unnecessary.
- Do not use `continue`. Prefer guarded blocks or `else`; combine nested conditions when neither has an `else`.
- Prefer straightforward guards over short early exits, without excessive nesting, artificial flags, or `goto`. Preserve cancellation, side effects, and disposal timing.
- Separate logical stages, declaration groups, assignments/calls, calculations/output writes, and control-flow blocks with blank lines. Keep related declarations together.
- Add a blank line before `return` or `yield return`, except directly after an opening brace, comment, or case label. Do not pad block interiors or closing parentheses.
- Separate parallel calls from preceding code, but keep their introductory comments attached. Format every `Parallel.For` and `Parallel.ForEach` as below; other long calls may use the same layout.

```csharp
Parallel.For
(
	0,
	tempListLength,
	tParallelOptions,
	tIndex =>
	{
		tempValues[ tIndex ] = tInputs[ tIndex ];
	}
);
```

## Types and expressions

- Keep enum members on consecutive lines without blank lines between them.
- Keep auto-properties on one line, including both accessors and any initializer. Keep expression-bodied properties on one line. Do not put blank lines between consecutive properties, including properties with annotations; keep annotations and property comments directly attached.
- Avoid redundant numeric casts and wrapping parentheses where implicit conversion already applies, such as an integer passed to a long parameter. Keep casts that control division, overload resolution, or required narrowing conversions. Cast a simple member/indexer directly, such as `(int)tValue`; do not wrap the operand in extra parentheses. Do not parenthesize ternary expressions in lambda bodies or arguments when precedence already gives the intended result.
- Do not null-check an object immediately after constructing it with `new`, or readonly fields guaranteed to be initialized by a successful constructor.
- Use target-typed `new` and collection expressions: `[]`, `[ tValue ]`, `[ .. tItems ]`. Prefer spreads over enumerable `ToArray()`; retain API-specific uses, null propagation, or anonymous-type inference where needed.
- Prefer equivalent `?.`, `?[]`, and `??` access, calls, and assignments. Keep guards for multiple operations, expression-tree restrictions, and short-circuit behavior.
- Use public positional records with PascalCase parameters for immutable data holders and completed snapshots. Check actual mutation needs; setters, serialization, computed properties, and `ToString()` do not require classes.
- Keep all record constructor signatures on one line. Preserve argument order, defaults, serialization, and display behavior when converting; use generated equality.
- Prefer records for data holders, including settings and request/response DTOs. Keep services and EF entities as classes; EF entities need reference identity for tracking. Existing readonly record structs remain suitable value carriers.
- Use primary constructors for simple services and ordinary constructors for substantial initialization.

## Loops and calculations

- Prefer backward indexed loops when order is irrelevant. Preserve chronology, cumulative calculations, output order, tie-breaking, random-number consumption, and numerical accumulation order.
- Cache invariant bounds in `tempLength`/`tempCount` locals. Never read `.Length` in a forward loop header; reading `.Length` or `.Count` once in a backward initializer is fine. Preserve deliberately changing bounds and cancellation checks.
- Use typed intermediates and indexed loops for numerical work; LINQ is appropriate for filtering, ordering, and projection. Prefer `++i`/`--i` unless the previous value is consumed.
- Parallelize independent, substantial work by passing the existing caller-owned `ParallelOptions` instance directly (such as `_parallelOptions`); do not clone its settings into a new instance. Keep chronological dependencies ordered.
- Reuse LINQ and standard library operations instead of adding trivial wrappers.
- Do not cap floating-point indicator calculations at `float.MaxValue` or `double.MaxValue` as defensive overflow handling. Preserve formula-defined limits and missing-observation semantics.

## Organization

- Match namespaces to folders and files to their main types.
- `Program.cs` hosts ASP.NET Core and configures services; HTTP routes belong in `Controller/`.
- Keep EF Core PostgreSQL entities, enums and `Context` in `SQL/`. Use the `cpr` schema and maintain it manually in `orchestrator/Create.sql`, including seed data and application-role grants; do not add EF migrations or automatic schema creation.
- Use entity-prefixed SQL keys such as `PullRequestId` and `WorkerId`. Configure EF mapping with entity annotations and conventions; keep `Context` free of `OnModelCreating`.
- Keep review prompts and step settings in application configuration. Validate them at startup; keep prompt types and rendering in `Service/Review`.
- Keep the original PR scope through explicit rereview and interactive validation. Previously reviewed merge repair covers conflicts and subsequent integration repairs with affected checks; preserve historical coverage boundaries for unchanged code. Require a retained per-file audit of complete affected functions and PR-introduced types for naming, spacing, comments, organization, optimization, and memory. Full review accounts for every original PR and repair path; final validation must complete missing work. Formatting or a narrow repair diff cannot establish full coverage.
- Require explicit optimization and memory-lifetime passes within that scope. Record candidate decisions and supporting measurements or inspection limits, including when no changes are justified.
- Keep workspace ownership separate from active worker assignments. Preserve the validated workspace and release its worker while awaiting approval; reserve it again for editing or publication.
- Serialize queue changes with worker scheduling and publication. Rereview invalidates approval; removing tracking preserves work and never closes the GitHub PR.
- Bind each comment preview to its exact prepared tree and run. The first confirmed publication carries the full CR report, even after blocked approvals and manual conflict resolution. Later published revisions get summaries of their new changes. Retain unpublished session updates until publication, post only after a confirmed push, and deduplicate by published revision as well as delivery marker.
- Prefer visible PR-comment tables when reporting measured performance or memory optimizations, with workloads, units, baselines, before/after values, and clearly labeled changes. Distinguish original PR gains from CPR improvements and keep uncertainty and tradeoffs nearby. PRs without such optimizations need no performance table or empty performance section; use concise prose for applicable resource conclusions.
- Generate descriptive commit messages with an `Auto: ` subject prefix from the final reviewed changes, retain them with the validated tree, and show them before approval. Publication must use that message rather than a generic review label.
- Retain `Merged`, `Closed`, and `Removed` records outside active merge ordering. Merged and closed PRs retain only database history; automatically delete their files under worker locks. Removed PRs preserve files until explicit purge. Track remaining copies per worker and record completion after every recorded copy and orchestrator storage is deleted.
- Recover report-only failures without repeating completed review work. Preserve original reports and check outcomes, bind reuse to the retained original code/head/target snapshot, and rerun completion gates. Include recovery in tweak and focused repair; newer target revisions and manual edits retain separate coverage, and historical recovery must not stage or overwrite the live workspace. Generate missing publication text through a bounded fresh read-only writing pass; never invent evidence or silently fall back to full review. If only the final validation stage was prevented by a structural report failure, allow bounded read-only validation of complete saved evidence and disclose the different validation scope. Keep substantive review and test work explicit. Before blocking a completed report, attempt one bounded read-only diagnosis and supported metadata repair. Retain the cause, evidence, repair outcome, and precise next action; never loop indefinitely, falsify check outcomes, or silently start another full review.
- Persist each attempt's compact review result and usage before deleting files; distinguish complete, partial, and unknown usage. Completion gates must explain missing requirements so retries can address them. Write review artifacts directly into persistent run storage, grant the reviewer access to that directory, and keep failure recovery out of bounded temporary filesystems.
- Block stale or interrupted reviews, revoke stale approvals, and release idle workers without discarding unpublished work. After approved publication, follow later commits on the same PR branch without requiring rereview; keep the CPR-published revision and report history separate from the observed head. Evaluate every reported GitHub CI check on the current head, including non-required checks. Store merges and closures regardless of CI state. Advance only when all pass, are skipped, or are cancelled; queued, running, failed, and unknown results must not be bypassed.
- Watch new CodeRabbit findings without replaying old comments. Ignore blank comments, generated status/walkthrough updates, and addressed findings. Review summaries must report a positive Actionable comments posted count; ignore missing, blank, or zero counts even with nitpick or outside-diff sections; fingerprint finding content independently of remapped commit IDs and review-info footers. Wait for review completion and a quiet interval, with a two-minute quiet fallback when completion is unavailable. Persist batching and attempt identity, respect worker/manual ownership, retain per-comment dispositions and affected evidence, and require fresh publication approval. Keep feedback repair focused with one attempt per batch and no elapsed-time cutoff by default; explicit cancellation and connection failures still stop execution. Use the assigned required_paths inventory consistently in both repair and evidence prompts, avoid unrelated broad checks, and never silently restart a full review.
- Keep explicit manual tweak sessions separate from automated review: allow local Git integration, check ancestry and snapshot integrity on exit, and record manual provenance without renewing prior review coverage. Snapshot the working tree at tweak entry and summarize only that session's edits and recorded checks through a brief read-only pass on exit. Carry earlier full reports and unpublished session summaries separately without claiming renewed review coverage. Preserve separate publication approval and never merge the GitHub PR automatically.
- Persist explicit maintainer resolutions on the PR and capture them in each review run. Apply them to the named policy findings without repeating resolved approval requests; retain remaining technical checks and separate publication approval. Record acceptance as a maintainer decision, not independently verified provenance.
- Group `Service/` by responsibility. Keep service records with the services that use them and request records with their controllers.
- Check affected dependency declarations during cleanup and final validation; remove confirmed unused dependencies while accounting for feature/target conditions, build scripts, and tests. Keep merge-repair dependency checks scoped to the integration.
- Remove unused parameters and obsolete code, comments, and configuration together. Do not keep compatibility scaffolding for discarded prototypes.
- Keep Discord notifications as text with clickable links; suppress embeds and preview thumbnails.
- Use structured logging templates and preserve cancellation and resource lifetimes.
