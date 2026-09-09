# One-time Bootstrap creation credential contract

The Bootstrap creation credential authorizes exactly one anonymous first Bootstrap creation. It is a
core `ServiceMantle.Bootstrap` capability with no ASP.NET Core, EF Core, or database-provider
dependency, and it is not a Setup Code, a management cookie, a database credential, or a Bootstrap
MasterKey.

This document defines the value, the record on disk, and the one-time consumption boundary. It does
not define an HTTP endpoint: the parser, phase gate, rate limiting, candidate validation, and
Bootstrap file write that use the credential belong to the Bootstrap endpoint task.

## The credential

| Property | Value |
| --- | --- |
| Entropy | 32 bytes from the BCL cryptographic random number generator |
| Encoding | 43-character unpadded Base64URL, `[A-Za-z0-9_-]` |
| Matching | Case sensitive, never trimmed or normalized |
| Plaintext exposure | Returned once, by `BootstrapCredentialProvisionResult.Credential.Reveal()` |
| Default lifetime | 15 minutes; the configurable range is 1 to 60 minutes |
| Clock | The store's `TimeProvider` |

`BootstrapCredential` implements `ISensitiveLogValue`. Its `ToString()`, debugger display, the
result projections, the status projection, the digest projection, and the record file name never
contain the plaintext. The digest is `sha256-v1:` followed by 64 lowercase hexadecimal characters
over the exact UTF-8 bytes of the credential; because it is always the same length,
`CryptographicOperations.FixedTimeEquals` compares two full-length operands and never ends early on
a length difference.

ServiceMantle guarantees only that its own persistence, exceptions, results, and diagnostics never
echo the plaintext. It does not clear process memory, and it makes no promise about what a caller,
a debugger, or third-party request logging does with a revealed credential.

## The record on disk

```json
{
  "formatVersion": 1,
  "digest": "sha256-v1:…",
  "issuedAtUtc": "2026-09-06T12:00:00.0000000Z",
  "expiresAtUtc": "2026-09-06T12:15:00.0000000Z"
}
```

The default path is
`<AppContext.BaseDirectory>/config/<normalized-service-id>.bootstrap-credential.json`; an explicit
path may be supplied. On Unix a directory the store creates is `0700` and the record is `0600`; on
Windows the record uses the system ACL of a file created by the current user. Cross-platform owner
and symbolic-link policy is explicitly not part of this contract.

The record protocol is a closed set of testable rules. A raw file above 4 KiB, JSON deeper than 4
levels, a non-object root, an unknown member, a duplicate member, a missing member, a
`formatVersion` other than `1` or written as a string, an unknown digest version or malformed
digest, a timestamp that is not round-trippable UTC, or an expiry at or before the issuance all fail
closed as `bootstrap_credential.unavailable`. A damaged record is never repaired and never becomes
consumable. The versioned digest encoding is the only place a digest reaches disk.

## Provisioning

`ProvisionAsync` is an explicit local operations action. No ServiceMantle management endpoint issues
or rotates a credential.

| Condition | Result |
| --- | --- |
| No record and no Bootstrap file | `Provisioned` with the plaintext, issuance, and expiry |
| A record already exists | `bootstrap_credential.already_exists` |
| A Bootstrap file already exists | `bootstrap_credential.bootstrap_configured` |
| Access denied, I/O failure | `bootstrap_credential.unavailable` |

The record is created with an exclusive create, so the operating system admits exactly one writer
in this or any other process and every loser sees `already_exists` without a plaintext. The store
only tests whether the Bootstrap file exists; it never reads, returns, or modifies its connection
string or MasterKey.

## Consumption

`ConsumeAsync` validates first and claims second:

1. the record is read and parsed under the file protocol above;
2. the candidate is parsed, and a parseable candidate is compared against the stored digest in fixed
   time — an invalid candidate never consumes a valid credential;
3. only a matching, unexpired candidate claims the record with a cross-process atomic rename; a
   caller that loses the rename finds nothing to claim;
4. the claimed record is read back and re-checked, and only a claim whose content still matches
   succeeds.

Expired, malformed, mismatched, already consumed, and never provisioned all collapse into
`bootstrap_credential.invalid`, so a caller cannot tell them apart. Corruption, oversize, access
denial, and I/O failure are `bootstrap_credential.unavailable`. A record replaced between the read
and the claim fails closed: the claim is not restored and an operator provisions a new credential.

Consumption is one-way. The endpoint that owns Bootstrap creation consumes the credential **before**
calling `BootstrapConfigurationManager.CreateAsync`, so a later validation, write, response, or
process failure leaves the credential consumed.

## Record sharing during a claim

Every read of a record - the initial read, the status read, and the re-check of the claimed record -
goes through one place, read-only, sharing read and delete. Sharing delete is what the claim needs:
the claim renames the record, which on Windows requires `DELETE` access on it, and two handles are
compatible there only when each one's share mode covers what the other was granted. Without it, a
reader and a concurrent claim refuse each other, and a refused read reports
`bootstrap_credential.unavailable` for a candidate that is merely wrong. Unix renames never consult
open handles, so this rule has no Unix equivalent.

The flag widens only what *other* handles are allowed to request. The read handle still has read
access and nothing more, no file permission is relaxed, and a reader keeps observing the record it
opened even after the claim renames it.

This removes only the incompatibility between the store's own read handles and its own claim, on a
normal local file system with usable permissions. It does not promise a successful consumption
against an outside exclusive handle, anti-virus software, an arbitrary ACL or file system, a killed
process, or a concurrent local re-provision, and it does not turn a storage failure into
`bootstrap_credential.invalid`: corruption, oversize, access denial, and I/O failure stay
`bootstrap_credential.unavailable`.

## The consume-first window

Consuming the credential and publishing the Bootstrap file are two files and are not one atomic
transaction. This contract chooses the fail-closed ordering and does not compensate automatically:

- a crash between consumption and Bootstrap publication may leave neither a usable credential nor a
  Bootstrap file. `GetStatusAsync` makes that state diagnosable — it reports `NotProvisioned`,
  `Provisioned`, `Expired`, or `Unavailable` together with whether a Bootstrap file exists, without
  the plaintext or the digest — and local operations must provision a new credential explicitly;
- a Bootstrap file written successfully whose response was lost is recovered through the status
  endpoint: retrying the credential fails, while status reports Bootstrap as configured.

## Cancellation

The caller's own token is checked at each operation boundary and propagates unchanged. The file
calls themselves are synchronous: an operation already inside a file call cannot be forcibly
interrupted, and no wall-clock bound is promised for it.

## Explicit non-guarantees

- Credential consumption and Bootstrap file publication are not atomic across the two files.
- No distributed credential, remote issuance, cryptographic hardware, in-process memory clearing, or
  external log confidentiality guarantee is provided.
- Cross-platform file owner and symbolic-link policy is not part of this contract.
- The entropy claim rests on using 32 bytes of the BCL cryptographic random number generator and the
  encoding that follows from it, not on statistical observation of generated values.
