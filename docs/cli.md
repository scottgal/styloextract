# StyloExtract CLI Reference

`stylo-extract` is the command-line interface for StyloExtract. It exposes all library capabilities via five subcommands: `extract`, `install-browsers`, `export`, `import`, and `monitor`.

## Installation

```bash
# .NET global tool (once published to NuGet)
dotnet tool install -g Mostlylucid.StyloExtract.Cli

# Build from source
git clone https://github.com/mostlylucid/stylobot-extract
cd stylobot-extract
dotnet build src/StyloExtract.Cli
# Binary: src/StyloExtract.Cli/bin/Debug/net10.0/stylo-extract
```

---

## Global notes

### `--host-hash-key`

The template store keys templates by an HMAC hash of the hostname. This means the raw hostname is never stored, only its hash.

When you omit `--host-hash-key`, a random 32-byte key is generated at process start and discarded on exit. Templates written with one random key will not be found in a subsequent process that generated a different random key.

To share templates across process restarts or across machines (e.g. export from prod, import to staging), you must supply the same stable key to every process:

```bash
# Generate a key once and store it securely
openssl rand -base64 32 > /etc/styloextract/hmac.key

# Use it consistently
stylo-extract extract https://example.com \
  --store prod.db \
  --host-hash-key "$(cat /etc/styloextract/hmac.key)"
```

---

## `extract`

Extract a single page and print the result to stdout.

```
stylo-extract extract <source> [options]
```

### Arguments

| Name | Description |
|---|---|
| `<source>` | Path to a local HTML file, or an `http://` / `https://` URL |

### Options

| Option | Default | Description |
|---|---|---|
| `--profile <profile>` | `RagFull` | Extraction profile. One of: `RagFull`, `Title`, `Minimal` |
| `--json` | false | Output the full `ExtractionResult` as indented JSON instead of Markdown |
| `--store <path>` | `styloextract-templates.db` | Path to the SQLite template store. Created on first run if it does not exist. |
| `--host-hash-key <key>` | (none) | Base64 HMAC key. See global notes above. |
| `--rendered` / `-r` | false | Fetch the URL via Playwright (headless Chromium) to get JS-rendered HTML. Auto-installs Chromium on first use. Ignored for local file paths. |

### Output

Default (Markdown): extracted content written to `stdout`. Errors written to `stderr`.

With `--json`: the full `ExtractionResult` record serialised as indented JSON:

```json
{
  "sourceUri": "https://example.com/article",
  "title": "Article title",
  "match": {
    "templateId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "templateVersion": 2,
    "fingerprintHex": "...",
    "status": "FastPathHit",
    "similarity": 0.97,
    "observationCount": 14,
    "latencyMatch": "00:00:00.0000160",
    "latencyTotal": "00:00:00.0123000"
  },
  "markdown": "# Article title\n\n...",
  "blocks": [...],
  "stats": { ... }
}
```

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Source file not found, URL fetch failed, or browser install failed when `--rendered` is set |

### Examples

```bash
# Extract a local file
stylo-extract extract article.html

# Extract a URL, output JSON
stylo-extract extract https://example.com/blog/post --json

# Extract with a persistent store and a stable HMAC key
stylo-extract extract https://example.com/blog/post \
  --store /var/lib/styloextract/prod.db \
  --host-hash-key "$(cat /etc/styloextract/hmac.key)"

# Extract a JS-rendered SPA page
stylo-extract extract https://spa-example.com/page --rendered

# Extract with a minimal profile (main content only)
stylo-extract extract https://example.com/blog/post --profile Minimal
```

---

## `install-browsers`

Download and install Playwright browser binaries.

```
stylo-extract install-browsers [--browser <name>]
```

This subcommand is equivalent to running `playwright install <browser>`. The `extract --rendered` flag calls this automatically on first use, but you can run it explicitly during deployment or in a container build step.

### Options

| Option | Default | Description |
|---|---|---|
| `--browser <name>` | `chromium` | Browser to install. One of: `chromium`, `firefox`, `webkit` |

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Browser installed successfully |
| Non-zero | Playwright install script returned a non-zero exit code |

### Example

```bash
# Install Chromium (default)
stylo-extract install-browsers

# Install Firefox
stylo-extract install-browsers --browser firefox
```

---

## `export`

Export all learned templates for a named host to a portable JSON bundle.

```
stylo-extract export --store <path> --host <name> --out <file> [--host-hash-key <key>]
```

The JSON bundle can be imported into any StyloExtract store using `import`. Use this for:
- Migrating templates from one environment to another (prod -> staging)
- Backing up learned templates before clearing a store
- Sharing a template bundle with a team member who is extracting from the same site

### Options

| Option | Required | Description |
|---|---|---|
| `--store <path>` | yes | Path to the SQLite store to read from |
| `--host <name>` | yes | Human-readable host name (e.g. `example.com`). Must match the name used during extraction. |
| `--out <file>` | yes | Output file path for the JSON bundle |
| `--host-hash-key <key>` | no | HMAC key. Must match the key that was used when extracting from this host. |

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Export succeeded; file written |
| Non-zero | Store not found, host not found, or I/O error |

### Key mismatch

If you exported templates using a random key (no `--host-hash-key`) and then try to export them with a different key, the host hash will not match and the export will produce an empty bundle. Always use the same stable key throughout the lifecycle of a store.

### Example

```bash
stylo-extract export \
  --store /var/lib/styloextract/prod.db \
  --host example.com \
  --out /tmp/example-com-templates.json \
  --host-hash-key "$(cat /etc/styloextract/hmac.key)"

# Output (stderr):
# Exported templates for example.com -> /tmp/example-com-templates.json
```

---

## `import`

Import a JSON template bundle into a SQLite store.

```
stylo-extract import --store <path> --host <name> --in <file> [--host-hash-key <key>]
```

### Options

| Option | Required | Description |
|---|---|---|
| `--store <path>` | yes | Path to the SQLite store to write into. Created if it does not exist. |
| `--host <name>` | yes | Human-readable host name. Should match the name used during export. |
| `--in <file>` | yes | Path to the JSON bundle produced by `export` |
| `--host-hash-key <key>` | no | HMAC key. Must match the key used during `export` for templates to resolve correctly in subsequent extraction calls. |

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Import succeeded |
| Non-zero | Input file not found or JSON parse error |

### Output (stderr)

```
Imported 12, replaced 3
```

- `Imported`: templates added that did not previously exist in the target store
- `Replaced`: templates that already existed and were overwritten with the bundle version

### Example

```bash
stylo-extract import \
  --store /var/lib/styloextract/staging.db \
  --host example.com \
  --in /tmp/example-com-templates.json \
  --host-hash-key "$(cat /etc/styloextract/hmac.key)"
```

---

## `monitor`

Watch a list of URLs on a poll interval and emit NDJSON version-change events to stdout.

```
stylo-extract monitor --urls <file> --store <path> [options]
```

The monitor runs `ExtractAsync` on each URL in the list on every poll. Because StyloExtract tracks template versions, a structural change to a page's layout triggers a `TemplateRefit` or `VersionDetected` event. These events are serialised as NDJSON (one JSON object per line) and written to `stdout`, making it easy to pipe to `jq`, write to a log aggregator, or POST to a webhook.

Press `Ctrl-C` to stop.

### Options

| Option | Required | Default | Description |
|---|---|---|---|
| `--urls <file>` | yes | - | Path to a newline-delimited text file of URLs to monitor. Blank lines and lines starting with `#` are ignored. |
| `--store <path>` | yes | - | Path to the SQLite template store |
| `--interval <hh:mm:ss>` | no | `01:00:00` | Time between poll cycles |
| `--host-hash-key <key>` | no | (random) | HMAC key for host hashing. Required for persistent template matching across restarts. |
| `--webhook <url>` | no | (none) | URL to POST each event to as `application/json`. Non-2xx responses are logged to `stderr` and ignored. |
| `--pretty` | no | false | Write indented JSON instead of compact NDJSON. Useful for development; not recommended for log pipelines. |

### URL file format

```
# blog posts to monitor
https://example.com/blog/post-1
https://example.com/blog/post-2

# product pages
https://shop.example.com/products/widget
```

### Event format (NDJSON)

Each event is a serialised `VersionChangeEvent`:

```json
{"url":"https://example.com/blog/post-1","templateId":"3fa85f64-...","previousVersion":1,"newVersion":2,"timestamp":"2026-06-21T09:00:00Z","diffs":[...]}
```

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Exited cleanly via Ctrl-C |
| Non-zero | Startup error (URL file not found, store path unwritable) |

### Examples

```bash
# Basic monitor, 30-minute interval
stylo-extract monitor \
  --urls urls.txt \
  --store monitor.db \
  --interval 00:30:00 \
  --host-hash-key "$(cat /etc/styloextract/hmac.key)"

# Monitor with webhook delivery
stylo-extract monitor \
  --urls urls.txt \
  --store monitor.db \
  --interval 01:00:00 \
  --webhook https://hooks.example.com/styloextract \
  --host-hash-key "$(cat /etc/styloextract/hmac.key)"

# Monitor output piped to jq for filtering
stylo-extract monitor \
  --urls urls.txt \
  --store monitor.db \
  --interval 00:15:00 | jq 'select(.newVersion > 1)'
```

---

## Profiles

| Profile | Output behaviour |
|---|---|
| `RagFull` | Full article content, secondary blocks, headings, lists, code blocks, links |
| `Title` | Title only |
| `Minimal` | Main content only, minimal Markdown formatting |

---

## Benchmarking

Run the benchmark suite to verify fast-path performance:

```bash
dotnet run --project bench/StyloExtract.Benchmarks -c Release

# CI regression mode (fails if mean exceeds threshold)
dotnet run --project bench/StyloExtract.Benchmarks -c Release -- --regression
```

Key benchmark:
- `FastPathMatchBench.ProbeFastPath`: mean 16 µs (target: < 1 ms)
