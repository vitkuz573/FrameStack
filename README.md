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
dotnet run --project tools/FrameStack.ImageProbe -- /absolute/path/to/image.bin 200000 256 64 r3=0x1 r4=0x8000BD00
```

Arguments:

- `image-path`
- `instruction-budget` (optional, default `2048`)
- `memory-mb` (optional, default `256`)
- `timeline-steps` (optional, default `0`)
- `register=value` overrides (optional, PowerPC only): `r0..r31`, `lr`, `ctr`, `cr`, `xer`, `pc`

Probe output includes:

- execution hot spots and final register snapshot;
- supervisor-call counters/subcall counters;
- captured console stream emitted through firmware monitor calls.

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
