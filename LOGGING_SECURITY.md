# Structured logging security contract

`ServiceMantle.Logging.StructuredLogSanitizer` creates a new, sink-neutral object graph for structured logging. It does not configure a logging provider or sink.

## Guaranteed boundaries

- Built-in secret-shaped field names (password, secret, token, API key, connection string, credentials, private/root/master keys, setup code, authorization, and cookies) are replaced in full. Configured denied fields extend this list; allow rules never override a deny.
- Authentication/cookie Headers and configured denied Headers are replaced in full, case-insensitively.
- `ISensitiveLogValue`, configured sensitive types, Bootstrap configuration, credentials, authentication Header values, database connections/builders, cryptographic private-key types, HTTP messages/content/Headers, certificates, and binary values are never destructured.
- Dictionaries, JSON, public object members, collections, and non-string scalars are recursively copied into a new sanitized graph.
- Invalid names are removed. Circular references, depth/collection limits, reflection failures, enumeration failures, and cleaning exceptions produce stable safe markers. The sanitizer never falls back to an original object or original value.
- Exception messages, stack traces, and `Data` are not emitted; only exception type structure is retained.

## Free-text boundary

Free-text cleaning is deliberately best effort. It removes log-injection control characters and recognizes explicit sensitive assignments, connection strings, credential-bearing URIs, bearer tokens, JWT-like values, and PEM private-key blocks.

It cannot reliably identify an unlabelled opaque secret that looks like an ordinary identifier, nor every encoded, transformed, encrypted, or product-specific secret format. Never interpolate secrets into free text. Put potentially sensitive data in a denied structured field/Header, implement `ISensitiveLogValue`, or register its type in `StructuredLogSanitizerOptions.SensitiveTypes`.

The sanitizer only governs values passed through it. It cannot protect separate sink configuration, message templates, scope data, or fields bypassing the sanitizer.
