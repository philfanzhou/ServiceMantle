# SignaCore legacy replacement audit

This audit is the deletion gate for ServiceMantle issue #105 and the source for the staged-removal Workstream #106. It inventories SignaCore management-infrastructure code without deleting it or changing a ServiceMantle public contract.

The audited source is [`philfanzhou/SignaCore@23c2f666`](https://github.com/philfanzhou/SignaCore/tree/23c2f666726186b772737988fae8ba8a8ce1f2da). The exact commit passed its full [GitHub CI run](https://github.com/philfanzhou/SignaCore/actions/runs/32585823676), including unit, integration/HTTP contract, PostgreSQL, frontend and container smoke paths. `manifest.json` records the executable commands and named tests used as present-behavior evidence.

## Decision rules

1. A deletion candidate must identify the exact old paths or symbols, at least one executable behavior test, one and only one replacement reference, and its prerequisite issues.
2. A planned replacement or any coverage gap forces `disposition=blocked`. A later PR cannot mark an item ready until the replacement exists and the gap is closed by executable tests.
3. A deletion batch must contain every candidate in its subsystem exactly once, directly enforce every candidate prerequisite in its native `Blocked by` set, and be blocked by the preceding batch. The manifest tests enforce the partition, prerequisite containment and order.
4. Product-owned boundaries are retained. A whole-file deletion candidate may not overlap a retained path; method-level splits are explicit (for example management audit actions can move while login history remains).
5. The fixed source commit must be deliberately advanced and re-audited when SignaCore changes. Evidence from a moving branch is not accepted.

## Deletion sequence

| Order | Subsystem | Candidate IDs | Unique replacement/integration gate | Tracking task |
|---:|---|---|---|---|
| 1 | Migration | `migration-orchestration`, `installation-state-persistence` | #70, #71 | #128 |
| 2 | Bootstrap | `bootstrap-file-lifecycle`, `bootstrap-management-mode`, `bootstrap-startup-branch` | `BootstrapFileStore`, #95, #116 | #129 |
| 3 | Setup | `setup-lifecycle`, `setup-mode-gate` | #100, #116 | #130 |
| 4 | Configuration | `configuration-catalog`, `configuration-snapshot-storage`, `configuration-management-api`, `legacy-configuration-upgrade` | #101 | #131 |
| 5 | Audit | `management-audit-storage`, `management-audit-query-api` | `EfCoreManagementAuditWriter`, #98 | #132 |
| 6 | Session | `admin-data-protection-key-store`, `admin-cookie-session` | #102 | #133 |
| 7 | Health | `phase-health-endpoints`, `signing-key-readiness` | #103 | #134 |
| 8 | Consul | `consul-registration-lifecycle` | #104 | #135 |

All eight tasks are native sub-issues of #106. Their GitHub `Blocked by` relationships include both capability prerequisites and the preceding deletion batch, so the prose table is not the dependency source of truth.

## Post-deletion acceptance

#107 runs only after #106 and all eight deletion batches complete. It validates the integrated upgrade, new-install, failure-recovery and multi-instance behavior; it is not a prerequisite of any deletion candidate or batch. Keeping this acceptance edge downstream avoids making #106 depend on its own completion.

## Explicit coverage gaps

- Legacy configuration import: the ServiceMantle-backed upgrade must retain legacy-key aliases and incomplete-import fail-closed behavior.
- Management session: the replacement must add a real two-instance Cookie round trip before the old registration is removed.
- Consul: current evidence proves only that the provider is disabled by default. Enabled registration, Readiness gating, retry, duplicate registration and shutdown deregistration have no executable characterization yet. #135 must add those tests before switching implementations.

These gaps are intentionally represented as blocking data in the manifest. They are not deferred review notes.

## Product boundaries that remain in SignaCore

The following are not reusable management infrastructure and are not candidates for deletion:

- OAuth grants, JWT issuance, refresh-token rotation and token revocation;
- accounts, credentials, lockout, user login and login-history records;
- application registration, gateway authentication and cross-application exchange;
- SMS/OTP, LDAP and WeChat admission/provider behavior;
- callback registration and its SSRF policy;
- signing-key lifecycle, private-key protection, JWKS and discovery documents (only the Readiness adapter moves);
- SignaCore-owned migrations for identity, token, application and provider tables;
- the administration product UI, whose infrastructure API call sites are adapted in place.

The manifest lists concrete paths and behavior tests for each retained boundary. Future cleanup PRs must keep those paths outside whole-file deletion scopes.

## Executable validation

Run the ServiceMantle guard tests with:

```bash
dotnet test --project tests/ServiceMantle.Tests/ServiceMantle.Tests.csproj
```

`SignaCoreLegacyMigrationManifestTests` fails when a candidate loses evidence or a unique replacement, a gap is marked ready, a candidate prerequisite is not enforced by its batch, post-deletion acceptance leaks into the deletion gate, a batch omits/duplicates a candidate, the order chain breaks, a tracking task is missing, or a retained product path overlaps a deletion path.
