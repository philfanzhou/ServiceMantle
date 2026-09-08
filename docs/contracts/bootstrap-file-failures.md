# Bootstrap file failure classification contract

`BootstrapException` reports that an instance-local Bootstrap file could not be read or safely
written. `BootstrapException.FailureKind` is the stable, machine-readable reason for that failure,
so a consumer never has to parse the English message, the file path, or a platform exception to tell
one cause from another.

This is a core `ServiceMantle.Bootstrap` capability with no ASP.NET Core, EF Core, or
database-provider dependency. It defines a classification only. It does not define an HTTP endpoint,
a status code mapping, a credential protocol, a cross-process lock, or a retry policy; the Bootstrap
`POST`/`PUT` endpoints that project this classification belong to their own task.

## The closed set

`BootstrapFileFailureKind` is closed. No other value is produced, and a consumer that receives an
unrecognized value must treat it as `Unavailable`.

| Value | Numeric | Meaning |
| --- | --- | --- |
| `Unavailable` | 0 | The file could not be read or written, and the store did not establish that the target already exists or is missing. This is the default for every unclassified failure. |
| `TargetAlreadyExists` | 1 | The operation required a target that does not yet exist, and the store observed an existing target. |
| `TargetMissing` | 2 | The operation required an existing target, and the store proved the target is absent by opening it and being told it is not there. |

The classification is formed inside `BootstrapFileStore`, at the point where the store decides why an
operation failed. `BootstrapConfigurationManager` passes the exception through unchanged, so the
`Create`/`Update` use cases report the same value as the corresponding store call.

## What each operation reports

| Operation and condition | `FailureKind` |
| --- | --- |
| `TryLoad` on a missing file | No exception; returns `null` |
| `Load` on a file the open reported as not found | `TargetMissing` |
| `Replace` on a file the probe open reported as not found | `TargetMissing` |
| `Create` on a target the store observed | `TargetAlreadyExists` |
| Empty file, damaged JSON, unsupported format version, invalid or missing fields | `Unavailable` |
| A file that belongs to a different service | `Unavailable` |
| A write requested for a different service | `Unavailable` |
| A denied read or write | `Unavailable` |
| Any other I/O error | `Unavailable` |

## Evidence, not inference

Only a failure the store proved is classified beyond `Unavailable`.

A negative `File.Exists` result also means "the path could not be inspected" - on a directory the
caller cannot traverse it returns `false` for a file that is present. It is therefore never the
evidence for `TargetMissing`. `Load` reaches that value because the read open itself reported the
file or its directory as not found, and `Replace` opens the target for the same reason. A denied or
failed probe leaves the cause unestablished and is reported as `Unavailable`.

`Create` never overwrites an existing file, and the operating system's atomic exclusive create is
what decides the single winner among concurrent creators - not a preceding existence check. The
store claims the target with an exclusive create, writes the complete file to a temporary path, and
renames it over the reservation it already owns. A creator whose exclusive create failed against a
target that is demonstrably there reports `TargetAlreadyExists`; a creator that could not establish
why its create failed reports `Unavailable`.

A create that is refused, or that fails before its content is published, releases the reservation it
took, so a create that failed while this process was still handling it leaves no target behind. The
release is that call's own work, not a property of the target path; see Limits.

## Limits

- The classification does not subdivide every platform I/O error, and it is never widened by
  matching error strings.
- The state a check observed is not held: an external process may create, replace, or delete the
  target immediately afterwards.
- Between the exclusive reservation and the rename that publishes the content, a concurrent reader
  can observe an incomplete target. It gets `Unavailable`, never a partially written configuration
  and never `null`.
- A reservation is released only by the process that holds it. A process aborted between the
  exclusive create and the rename - killed, restarted, or stopped by power loss - leaves an empty
  file at the target path. A later `Create` then reports `TargetAlreadyExists`, and `Load` and
  `TryLoad` report `Unavailable` rather than `null`, until that file is removed. The store does not
  reclaim an abandoned reservation.
- This contract adds no single-winner guarantee for concurrent updates, no cross-process update
  exclusion, no power-loss durability, and no hard time bound.
- `Message`, `FilePath`, and `InnerException` remain local diagnostic detail. They carry no new
  redaction guarantee and must not be serialized to an HTTP response; a consumer projects the
  classification only.
