# Contributing to SideHub Agent

Thanks for your interest in contributing to the SideHub Agent! This guide will help you get set up.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/) (for PTY helper)
- Git

## Getting started

```bash
# Clone the repository
git clone https://github.com/vincentaugerot/side_hub_agent.git
cd side_hub_agent

# Build
dotnet build SideHub.Agent

# Run
dotnet run --project SideHub.Agent
```

## Development workflow

1. **Fork** the repository
2. **Create a branch** from `main`:
   ```bash
   git checkout -b feature/my-feature
   ```
3. **Make your changes** and test locally
4. **Commit** with descriptive messages:
   ```bash
   git commit -m "Add support for custom heartbeat interval"
   ```
5. **Push** and open a Pull Request against `main`

## Branch naming

| Type | Format | Example |
|---|---|---|
| Feature | `feature/<description>` | `feature/custom-heartbeat` |
| Bug fix | `fix/<description>` | `fix/reconnection-loop` |
| Docs | `docs/<description>` | `docs/update-config-guide` |
| Refactor | `refactor/<description>` | `refactor/websocket-client` |

## Project structure

See [Project structure](README.md#project-structure) and [How it works](README.md#how-it-works) in the README.

## Architecture overview

The agent is a .NET 10 console application that:

1. Loads all `.sidehub/*.json` config files of the folder it runs in (one daemon per project folder)
2. Launches one `AgentRunner` per config (in parallel)
3. Each runner creates a `WebSocketClient` that connects to the SideHub backend (outbound `wss://` only)
4. The backend drives everything through PTYs (`pty.start` / `pty.input` / `pty.stop`); coding CLIs are
   started in them through `sidehub-cli launch`
5. Auto-reconnection with exponential backoff ensures resilience; PTY output is buffered and replayed

Key design decisions:
- **Everything is a terminal**: runs and interactive sessions are PTYs the backend types into; there is
  no SDK proxy or structured protocol with the CLIs
- **PTY via Node.js**: Terminal emulation delegates to a Node.js helper using `node-pty` (`pty-helper/`)
- **One launch path**: `sidehub-cli launch` starts claude / codex / gemini / copilot / agy the same way on
  every OS and reports the session id through `NotifyFifo`
- **Usage from local transcripts**: `Usage/` reads token counts from the CLIs' own files; only counts
  are sent
- **Log rotation**: Daemon mode uses rotating logs (10 MB default, 3 archives)

## Building & testing

```bash
# Debug build
dotnet build SideHub.Agent

# Release build
dotnet build SideHub.Agent -c Release

# Run directly
dotnet run --project SideHub.Agent

# Tests
dotnet test SideHub.Agent.Tests

# Publish for your platform
dotnet publish SideHub.Agent -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

## Code style

- Follow existing patterns in the codebase
- Use C# 13 / .NET 10 features where appropriate
- Keep classes focused: one responsibility per file
- Use `Console.WriteLine` with the `[AgentName]` prefix pattern for logging
- Handle cancellation tokens properly for clean shutdown

## Commit messages

Write clear, concise commit messages:

```
Add PTY resize support for terminal sessions
Fix reconnection loop when token is expired
Update README with troubleshooting section
```

- Use imperative mood ("Add feature" not "Added feature")
- Keep the first line under 72 characters
- Add a body for complex changes

## Reporting issues

- Use [GitHub Issues](https://github.com/vincentaugerot/side_hub_agent/issues)
- Include: OS, .NET version, agent version, config (redact tokens), and logs
- For bugs, include steps to reproduce

## Pull Request guidelines

- Keep PRs focused: one feature or fix per PR
- Update the README if your change affects usage or configuration
- Ensure the project builds without warnings
- Test on your target platform before submitting

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
