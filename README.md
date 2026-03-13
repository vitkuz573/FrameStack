# FrameStack

`FrameStack` is a next-generation Cisco network emulation platform foundation built with enterprise architecture principles.

## Current Foundation

- Clean Architecture: `Domain`, `Application`, `Infrastructure`, `Api`.
- Explicit contracts and DI composition via extension methods.
- CQRS-lite (`Command/Query` + handlers + dispatchers).
- Image and runtime domain model:
  - `EmulatorImage` for local IOS/IOS-XE artifacts;
  - `EmulationSession` for runtime lifecycle orchestration.
- Native execution core baseline:
  - `FrameStack.Emulation` with `CPU core / memory bus / machine loop`;
  - initial `MIPS32` support (NOP, ADDI, J, BREAK);
  - initial `PowerPC32` support for real Cisco IOS bootstrap flows (branching, compare, load/store, SPR access).
- Real-image bootstrap pipeline:
  - binary image analyzer (`ELF32`, `RAW`, `GZIP`, `ZIP` detection);
  - image loaders (`ELF32` segments and `RAW` bootstrap mapping);
  - sparse memory bus for large address spaces and realistic image mapping.
- Infrastructure adapters:
  - in-memory repositories;
  - `NativeRuntimeOrchestrator` as a temporary runtime integration adapter.

## Project Structure

- `src/FrameStack.Domain` - domain model and invariants.
- `src/FrameStack.Application` - use-case layer and application ports.
- `src/FrameStack.Emulation` - native emulation/execution core.
- `src/FrameStack.Infrastructure` - infrastructure adapters.
- `src/FrameStack.Api` - REST API and composition root.
- `tests/*` - unit and integration smoke tests.

## Quick Start

```bash
dotnet restore
dotnet test
dotnet run --project src/FrameStack.Api
```

## Image Probe CLI

Run direct preflight analysis/execution on a local image:

```bash
dotnet run --project tools/FrameStack.ImageProbe -- /absolute/path/to/image.bin \
  --instruction-budget 200000 \
  --memory-mb 256 \
  --timeline-steps 64 \
  --register r3=0x1 \
  --register r4=0x8000BD00
```

Arguments:

- `image-path`
- `--instruction-budget` (optional, default `2048`)
- `--memory-mb` (optional, default `256`)
- `--timeline-steps` (optional, default `0`)
- `--register <register>=<value>` overrides (optional, PowerPC only): `r0..r31`, `lr`, `ctr`, `cr`, `xer`, `pc`
- optional stop controls:
  - `--stop-at-pc 0x...`
  - `--stop-at-pc-hit 0x...=<hit-count>`
  - `--stop-on-svc 0x...`
  - `--svc-trace-max <count>`
  - `--stop-on-svc-signature <service>@<caller>/<a0>/<a1>/<a2>/<a3>`
  - `--stop-on-svc-signature-hit <service>@<caller>/<a0>/<a1>/<a2>/<a3>#<hit-count>`
  - `--stop-on-watch32-change 0x...`
  - `--stop-on-watch32-change-reg rN:<offset>`
  - `--stop-on-console-repeat "<text>=<count>"`
- optional trace controls:
  - `--watch32 0x...`
  - `--watch32-reg rN:<offset>`
  - `--trace-watch32-accesses`
  - `--trace-watch32-accesses-max <count>`
  - `--trace-watch32-pc-range <start>:<end>`
  - `--trace-insn-pc-range <start>:<end>`
  - `--trace-insn-max <count>`
  - `--global32 <name>=0x...`
  - `--count-pc 0x...`
  - `--max-hotspots <count>` or `--full-hotspots`
  - `--window 0x...:<before>:<after>`
  - `--progress-every <instructions>`
  - `--report-json /absolute/path/to/report.json`
  - `--profile cisco-c2600-boot` (fast baseline diagnostics)
  - `--profile cisco-c2600-boot-watch` (deep watch32 diagnostics, higher runtime overhead)
  - `--checkpoint-file <path>` / `--checkpoint-force-rebuild`
  - `--svc-return-signature <service>@<caller>/<a0>/<a1>/<a2>/<a3>=<value>`

`--stop-on-console-repeat` stops the run when the captured console stream contains the target text at least `<count>` times. When enabled, probe auto-reduces chunk size to improve stop responsiveness.

Probe output includes:

- execution hot spots and final register snapshot;
- explicit stop reason (`InstructionBudgetReached`, `Halted`, stop conditions);
- exact `count-pc` hit totals (independent from hot spot aggregation);
- supervisor-call counters/subcall counters;
- optional read/write memory access trace for watched words (`--trace-watch32-accesses`);
- optional per-instruction register deltas for selected PC ranges (`--trace-insn-pc-range`);
- captured console stream emitted through firmware monitor calls.

Example (long-run diagnostics with structured report):

```bash
dotnet run --project tools/FrameStack.ImageProbe -- /path/to/cisco.bin \
  --instruction-budget 500000000 \
  --memory-mb 256 \
  --timeline-steps 0 \
  --checkpoint-file .tmp/checkpoints/c2600.chk \
  --profile cisco-c2600-boot \
  --count-pc 0x816E292C \
  --stop-at-pc-hit 0x816E292C=32 \
  --max-hotspots 0 \
  --progress-every 20000000 \
  --report-json .tmp/reports/c2600-run.json
```

## Runtime Runner CLI

Run an image in a continuous execution loop with live console output (without probe traces):

```bash
dotnet run -c Release --project tools/FrameStack.Runner -- /absolute/path/to/image.bin \
  --memory-mb 256
```

Options:

- `image-path`
- `--memory-mb` (optional, default `256`)

`Runner` streams IOS console output and runs until firmware halts or you stop it with `Ctrl+C`.
Use `-c Release` for performance; Debug build is significantly slower.

## Main API

- `POST /api/v1/images` - register a local image artifact.
- `GET /api/v1/images/{id}` - get image details.
- `POST /api/v1/sessions` - create a runtime session.
- `POST /api/v1/sessions/{id}/start` - start a session.
- `POST /api/v1/sessions/{id}/stop` - stop a session.
- `GET /api/v1/sessions/{id}` - get session status.

## Example Image Registration

```json
{
  "vendor": "Cisco",
  "platform": "Router",
  "name": "c2800nm-ipbasek9",
  "version": "12.4-15.T14",
  "artifactPath": "/var/lib/framestack/images/c2800nm-ipbasek9-mz.124-15.T14.bin"
}
```

## Legal Notice

You must have valid legal rights to use real IOS/IOS-XE images.
