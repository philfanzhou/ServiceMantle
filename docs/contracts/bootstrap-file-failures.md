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
| `Create` on a target the operating system refused to link over | `TargetAlreadyExists` |
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

`Create` never overwrites an existing file, and one operating system step decides the single winner
among concurrent creators - not a preceding existence check. The store writes the complete file to a
temporary path beside the target and then links that file to the target, which the operating system
refuses rather than replaces when the target is already taken. A creator whose link was refused
because the target is taken reports `TargetAlreadyExists`, on the operating system's own answer
rather than on a separate observation; a creator whose link was refused for any other reason,
including a file system that does not support links, reports `Unavailable`.

Nothing is placed at the target before the content is complete. A create that fails, and a process
that dies at any point during one, therefore leaves the target exactly as it found it: a failed
create is indistinguishable from one that never ran, and a retry is never blocked by the remains of
an earlier attempt. What can be left behind is the temporary file beside the target, which no
operation reads and which never affects what a later call reports.

## Reader sharing during a replace

Every read of the target - `TryLoad`, `Load`, and the manager status read that goes through them -
opens the file through one place, read-only, sharing read and delete. Sharing delete is what an
atomic replace of the target needs while such a handle is open: on Windows, `ReplaceFileW` opens the
replaced target with `DELETE` access, and a handle that does not share delete makes that open fail
and turns the replace into `Unavailable`. On Unix the rename does not consult open handles at all,
so a Unix run cannot show the difference.

The flag widens only what *other* handles are allowed to ask for. The store's read handle still has
read access and nothing more, and a reader keeps observing the file it opened; a replacement becomes
visible at the next open, never inside an open stream.

The guarantee is limited to the store's own read-only handle, on a normal local file system with
usable permissions and no outside interference: such a handle does not, by missing delete sharing,
stop a replace. Nothing here promises that a replace succeeds against an outside exclusive handle,
a changed ACL, anti-virus software, a failing disk, or an arbitrary file system, and it adds no
cross-process update exclusion, power-loss durability, hard I/O time bound, or snapshot consistency
under external modification.

## Limits

- The classification does not subdivide every platform I/O error, and it is never widened by
  matching error strings.
- The state a check observed is not held: an external process may create, replace, or delete the
  target immediately afterwards.
- A reader never observes a partially written target from a create. The target becomes a second
  name for a file that is already complete, so a concurrent reader sees the target absent or sees
  the finished file.
- `Create` requires a directory whose file system supports links, which the temporary file and the
  target always share because they sit side by side. A file system that refuses links cannot
  publish a new bootstrap file at all, and reports `Unavailable` rather than falling back to a
  publish that could overwrite a concurrent creator.
- A create that fails may leave its temporary file beside the target, named
  `.{file name}.{random}.tmp`. It is never read, never consulted for any classification, and never
  reused; removing it is housekeeping, not recovery. `Replace` consumes its temporary file instead.
- This contract adds no single-winner guarantee for concurrent updates, no cross-process update
  exclusion, no power-loss durability, and no hard time bound.
- `Message`, `FilePath`, and `InnerException` remain local diagnostic detail. They carry no new
  redaction guarantee and must not be serialized to an HTTP response; a consumer projects the
  classification only.
