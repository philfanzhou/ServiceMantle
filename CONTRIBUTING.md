# Contributing

## Delivery scope

One pull request closes exactly one task issue. Implementation, failure, cancellation, security, and applicable concurrency tests land in that same pull request. When a change grows a second independent package, contract, or endpoint group, split the issue instead of widening the pull request.

## Review policy

These three rules exist because a pull request can converge on its acceptance criteria and still fail to close. Each rule names a specific way that happens.

### 1. Write the non-guarantees before the code

An acceptance criterion phrased as a universal claim — "no secret ever reaches the sink", "any input is handled safely", "traversal is always bounded" — has no natural stopping point. Review can always produce one more input shape that was not considered, so the pull request never reaches a state its author can call finished.

Before implementation starts, a security- or robustness-sensitive issue must state what it does **not** guarantee, with the same precision as what it does. `LOGGING_SECURITY.md` is the reference shape: alongside the guaranteed boundaries it declares the exact free-text limits, the output types the caller may rely on, and the costs the traversal limits do not bound.

A declared non-guarantee turns "this path is unbounded" from a review finding into a known, accepted boundary. Without that section, the same observation reopens the pull request indefinitely.

### 2. Fix by invariant, not by branch

When a finding names one code path, first ask whether the same defect exists on the other paths that reach the same output. If it does, the fix belongs at the point where those paths converge — and if no such point exists, creating one is the fix.

`StructuredLogSanitizer.NormalizeSafeScalar` is the worked example: every safe scalar becomes output through that one method, so a value shape that no sink can represent is rejected once rather than per branch. Patching the reported branch alone tends to relocate the defect instead of removing it, and the relocated defect returns as the next round's finding.

Pair such a fix with a test that asserts the invariant over a set of inputs, not the single reported case. `Sanitized_output_is_always_serializable_by_the_default_serializer` is that test for the example above.

### 3. Budget the review rounds by severity

Findings that break a stated guarantee are fixed in the pull request, however many rounds it takes.

Findings that do not break a stated guarantee — resource shaping, defence in depth, internal structure — are fixed for at most three rounds. After that they move to a follow-up issue carrying the finding text verbatim, and the pull request merges. Deferring them is a scheduling decision, not a quality concession: their cost is bounded and visible in the tracker, whereas an open pull request accrues rebase, re-review, and integration cost on every round.

State the deferral in the pull request description with a link to the follow-up issue.
