# Structured logging security contract

`ServiceMantle.Logging.StructuredLogSanitizer` creates a new, sink-neutral object graph for structured logging. It does not configure a logging provider or sink.

## Guaranteed boundaries

- Built-in secret-shaped field names (password, secret, token, API key, connection string, credentials, private/root/master keys, setup code, authorization, and cookies) are replaced in full. Configured denied fields extend this list; allow rules never override a deny.
- Authentication/cookie Headers and configured denied Headers are replaced in full, case-insensitively.
- `ISensitiveLogValue`, configured sensitive types, Bootstrap configuration, credentials, authentication Header values, database connections/builders, cryptographic private-key types, HTTP messages/content/Headers, certificates, and binary values are never destructured.
- Dictionaries, JSON, public object members, collections, and non-string scalars are recursively copied into a new sanitized graph.
- Invalid names are removed. Circular references, depth/collection limits, reflection failures, enumeration failures, and cleaning exceptions produce stable safe markers. The sanitizer never falls back to an original object or original value.
- Exception messages, stack traces, and `Data` are not emitted; only exception type structure is retained.

## Output guarantees

The sanitized graph is built only from these shapes:

- `null`, `string`, `bool`
- finite numeric scalars (`byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`)
- `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid`
- `IReadOnlyDictionary<string, object?>` and `IReadOnlyList<object?>` composed of the above

`JsonSerializer.Serialize` is guaranteed not to throw on the result under its default options. Enums are normalized to `long`; enum dictionary keys use that normalized value's invariant decimal text. For a `ulong`-backed enum above `long.MaxValue`, normalization preserves the 64-bit pattern through an unchecked conversion and therefore produces the corresponding negative `long`; it does not preserve the original unsigned numeric value. Non-finite `float`/`double` values (`NaN`, `PositiveInfinity`, `NegativeInfinity`) cannot be represented by every sink, so they are replaced with `[UNREPRESENTABLE_VALUE]` at the single point where a scalar becomes output; no input path can bypass this.

## Cost bounds and explicit non-guarantees

`MaximumDepth`, `MaximumCollectionCount`, and `MaximumStringLength` bound the sanitizer's own recursion, element enumeration, and string handling. They are applied before a child value is read, including at the `SanitizeFields` and `SanitizeHeaders` entry points, so a lazy or infinite sequence terminates at the configured count.

These limits do not bound work that happens inside the caller's own types. A single member getter, custom `JsonConverter`, or `IEnumerator.MoveNext` call may allocate or compute arbitrarily before the sanitizer regains control; the sanitizer only guarantees that it stops reading further members once a limit is reached.

The sanitizer is not a denial-of-service boundary. Do not hand it an object graph built directly from untrusted input and rely on these limits for protection; bound the graph at the trust boundary instead.

## Free-text boundary

Free-text cleaning is deliberately best effort. It removes log-injection control characters and recognizes explicit sensitive assignments, connection strings, credential-bearing URIs, bearer tokens, JWT-like values, and PEM private-key blocks.

It cannot reliably identify an unlabelled opaque secret that looks like an ordinary identifier, nor every encoded, transformed, encrypted, or product-specific secret format. Never interpolate secrets into free text. Put potentially sensitive data in a denied structured field/Header, implement `ISensitiveLogValue`, or register its type in `StructuredLogSanitizerOptions.SensitiveTypes`.

The sanitizer only governs values passed through it. It cannot protect separate sink configuration, message templates, scope data, or fields bypassing the sanitizer.

## ASP.NET Core sensitive Header snapshot

The optional `ServiceMantle.AspNetCore` `AddSensitiveHeaders` capability builds one immutable,
case-insensitive startup snapshot. It always includes
`StructuredLogSanitizerDefaults.BuiltInDeniedHeaderNames`; consumers can append valid HTTP token
names but cannot remove a built-in. Repeated names and casing variants collapse into one entry.
Configuration collections are enumerated once when the Host starts, and later mutations are ignored.

The DI-provided `StructuredLogSanitizer` and `ServiceMantleRequestHeaderDiagnosticProjector` consume
that snapshot. The projector copies an ASP.NET Core request Header collection into the sanitizer;
denied single-value and multi-value Headers therefore produce only `[REDACTED]`. A request Header
enumeration failure produces only `[SANITIZATION_FAILED]` and never falls back to the original values.

This capability does not mutate `HttpRequest.Headers`, configure logging or tracing, inspect Activity
tags, or govern third-party diagnostics. Product-specific Header names are not built in and must be
registered by the consuming service. Runtime updates, removals, and hot reload are not supported.
