# SignaCore legacy replacement audit

This audit is the deletion gate for ServiceMantle issue #105 and the source for the staged-removal Workstream #106. It inventories SignaCore management-infrastructure code without deleting it or changing a ServiceMantle public contract.

The audited source is [`philfanzhou/SignaCore@23c2f666`](https://github.com/philfanzhou/SignaCore/tree/23c2f666726186b772737988fae8ba8a8ce1f2da). The exact commit passed its full [GitHub CI run](https://github.com/philfanzhou/SignaCore/actions/runs/32585823676), including unit, integration/HTTP contract, PostgreSQL, frontend and container smoke paths. `manifest.json` records the executable commands and named tests used as present-behavior evidence.

## Decision rules

1. A deletion candidate must identify the exact old paths or validated symbols, at least one executable behavior test, one and only one issue or dotted-identifier replacement reference, and its prerequisite issues.
2. A planned replacement or any coverage gap forces `disposition=blocked`. A later PR cannot mark an item ready until the replacement exists and the gap is closed by executable tests.
3. A deletion batch must contain every candidate in its subsystem exactly once, directly enforce every candidate prerequisite in its native `Blocked by` set, and be blocked by the preceding batch. The manifest tests enforce the partition, prerequisite containment and order.
4. Product-owned boundaries are retained. A deletion path or symbol may not contain, equal or sit beneath a retained path or symbol; retained symbols are recorded explicitly rather than inferred from file names, and method-level splits are explicit (for example management audit actions can move while login history remains).
5. Every retained boundary declares its coverage mode. `path-and-symbol` requires non-empty `paths` **and** non-empty `preservedSymbols`; `path-only` requires non-empty `paths` and an empty `preservedSymbols`. `path-only` exists for non-C# boundaries such as the admin console; it must not be used to escape a symbol requirement that a boundary actually needs.
6. The fixed source commit must be deliberately advanced and re-audited when SignaCore changes. Evidence from a moving branch is not accepted.

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
- the administration product UI, whose infrastructure API call sites are adapted in place (declared `path-only`: it is not a C# symbol space, so it carries no placeholder symbol).

The manifest lists concrete paths, explicit symbol roots and command-keyed behavior tests for each retained boundary. Future cleanup PRs must keep those paths and symbols outside deletion scopes.

Applied migration files remain immutable history. Removing the installation-state, system-setting, audit-log or data-protection-key mappings therefore requires each deletion batch to update both provider model snapshots and add forward drop migrations; it must not delete the historical `AddSystemSettingsAndInstallationState` or `PersistDataProtectionKeys` migrations. Those snapshot mappings are listed as candidate symbols so the retained migration files can be edited at symbol scope without exposing the product-owned mappings beside them.

## Model snapshot entity inventory

`src/SignaCore.Database/Migrations/**` and `src/SignaCore.Database.Migrations.Sqlite/Migrations/**` are retained as whole paths, so the path gate cannot protect an individual snapshot mapping. Deletion safety there rests entirely on the symbol gate, and a symbol gate is only as safe as its enumeration is complete. `snapshotEntitySets` is that enumeration.

The manifest fixes one entry per provider snapshot — PostgreSQL and SQLite — each recording the repository-relative snapshot path, the snapshot symbol prefix, and the 18 logical entity simple names that snapshot declares. The guard test derives `symbolPrefix + "." + entityName` into 36 provider-specific audit symbols and enforces:

```text
snapshotInventory == preservedSnapshotSymbols ∪ deletionCandidateSnapshotSymbols
preservedSnapshotSymbols ∩ deletionCandidateSnapshotSymbols == ∅
```

It also asserts that both snapshots hold exactly 18 non-duplicated entities, that their logical entity sets are identical, that the two paths and the two prefixes are distinct and sit under the retained `product-schema-migrations` paths, and that a snapshot symbol is claimed only by `product-schema-migrations.preservedSymbols` or by some candidate's `legacySymbols` — never by an unrelated boundary that happens to overlap.

The 36 symbols split 28 preserved / 8 deletion:

| Side | Entities per snapshot |
|---|---|
| Preserved (`product-schema-migrations`) | `AccountEntity`, `AppExchangeTrustEntity`, `AppLdapAccessEntity`, `AppRegistrationEntity`, `AppSmsAccessEntity`, `AppWechatAccessEntity`, `LdapCredentialEntity`, `LoginAttemptEntity`, `LoginHistoryEntity`, `OtpEntity`, `PasswordCredentialEntity`, `RefreshTokenEntity`, `SecurityKeyEntity`, `UserLoginEntity` |
| Deletion candidates | `InstallationStateEntity` (`installation-state-persistence`), `SystemSettingEntity` (`configuration-snapshot-storage`), `AuditLogEntity` (`management-audit-storage`), `DataProtectionKeyEntity` (`admin-data-protection-key-store`) |

`AuditLogEntity` sits on the deletion side because `src/SignaCore.Database/Entity/AuditLogEntity.cs` is already a `management-audit-storage` legacy path: the management audit table moves to `EfCoreManagementAuditWriter`. Leaving its snapshot mapping unclassified would let the entity file be deleted while its mapping stayed behind. The other 14 are product-owned identity, token, application and provider tables.

### Re-collecting the baseline

`snapshotEntitySets` is fixed audit input tied to `source.commit`. The offline guard tests do not reach GitHub and do not claim the fixed inventory matches the remote source; they only prove that once the inventory is fixed, the manifest's preserved/deletion split covers it exactly once. Advancing the pinned commit without re-collecting is a baseline-update process violation, not something a network test papers over.

When advancing `source.commit`, a reviewer must:

1. read `modelBuilder.Entity("...")` calls from both snapshot files at the new commit;
2. update each `snapshotEntitySets[].entities` list to the distinct entity simple names found there (an entity may appear more than once per file; the inventory records the distinct set);
3. classify every added or renamed entity into `product-schema-migrations.preservedSymbols` or a candidate's `legacySymbols`, and remove symbols for entities that no longer exist;
4. re-run the guard tests, which fail on any omission, duplication, double-claim, or divergence between the two snapshots.

## Executable validation

Run the ServiceMantle guard tests with:

```bash
dotnet test --project tests/ServiceMantle.Tests/ServiceMantle.Tests.csproj
```

`SignaCoreLegacyMigrationManifestTests` fails when a candidate loses evidence or a unique replacement, required gap data is dropped, a gap is marked ready, a candidate prerequisite is not enforced by its matching subsystem batch, post-deletion acceptance leaks into the deletion gate, a batch omits/duplicates a candidate, the order chain breaks or cycles, a tracking task is missing, a command is unused, or a retained product path or symbol overlaps a deletion scope.

It additionally fails when a snapshot inventory symbol is unclassified, claimed twice, duplicated inside a snapshot list, or when the two snapshots disagree; when a snapshot path or prefix is duplicated or moves outside the retained migration paths; and when a boundary declares an unknown coverage mode, a `path-only` boundary carries symbols, or a `path-and-symbol` boundary carries none. Missing or misspelled inventory and coverage fields fail at deserialization.
