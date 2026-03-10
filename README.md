# FrameStack

`FrameStack` is a next-generation Cisco network simulation platform foundation built with enterprise architecture principles.

## Current Foundation

- Clean Architecture: `Domain`, `Application`, `Infrastructure`, `Api`.
- Explicit contracts and DI composition via extension methods.
- CQRS-lite (`Command/Query` + handlers + dispatchers).
- Image and runtime domain model:
  - `EmulatorImage` for local IOS/IOS-XE artifacts;
  - `EmulationSession` for runtime lifecycle orchestration.
- Native execution core baseline:
  - `FrameStack.Emulation` with `CPU core / memory bus / machine loop`;
  - initial `MIPS32` instruction support (NOP, ADDI, J, BREAK) as the ISA expansion starting point.
- Infrastructure adapters:
  - in-memory repositories;
  - `NativeRuntimeOrchestrator` as a temporary runtime integration adapter.

## Project Structure

- `src/FrameStack.Domain` - domain model and invariants.
- `src/FrameStack.Application` - use-case layer and application ports.
- `src/FrameStack.Emulation` - native simulation/execution core.
- `src/FrameStack.Infrastructure` - infrastructure adapters.
- `src/FrameStack.Api` - REST API and composition root.
- `tests/*` - unit and integration smoke tests.

## Quick Start

```bash
dotnet restore
dotnet test
dotnet run --project src/FrameStack.Api
```

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
