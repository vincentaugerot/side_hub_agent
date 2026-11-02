# Security

## Trust model

The SideHub agent is a **remote execution agent**. The SideHub backend it connects to
(`sidehubUrl`) drives it: it opens terminals, types into them, runs commands and writes
files on the machine where the agent runs, as the OS user that started the agent.

> **A compromised backend, or anyone who holds a valid backend session for your workspace,
> can run arbitrary code on the agent's machine.**

This is by design, not a bug. The agent is not a sandbox, and none of the restrictions below
change that: a backend that can type into a shell (`pty.input`) can do anything that user can
do. Treat the agent like an SSH key handed to the SideHub backend.

### What the backend can do

| Message | Effect |
|---|---|
| `pty.start` / `pty.input` | Spawns an allowlisted shell and types arbitrary input into it |
| `command.execute` | Runs a one-shot command in a shell (can be disabled, see below) |
| `file.write.*`, `terminal.attachment.enqueue` | Writes files under the working directory and pastes text into a PTY (can be disabled, see below) |

### What the agent does protect

These controls reduce mistakes and limit what a *misbehaving* (not malicious) backend can
silently do. They are not a security boundary against a compromised backend.

- **Transport**: `sidehubUrl` must be `wss://` (TLS) except for `localhost`; the agent token
  is sent in a header, never in the URL.
- **Shell allowlist**: `pty.start` only spawns `bash`, `zsh`, `sh`, `dash`, `fish`, `pwsh`
  (Unix) or `cmd`, `powershell`, `pwsh` (Windows). Names are resolved by the agent from fixed
  system directories (`/bin`, `/usr/bin`, `/usr/local/bin`, `/opt/homebrew/bin`), never from
  `PATH`; any other binary or path is refused and no PTY is started. `command.execute` resolves
  its shells (`bash`, `sh`, `zsh`, `pwsh`, `cmd`) through the same policy.
- **PTY environment**: the PTY does not inherit the daemon's environment (API keys, cloud
  credentials…), only an allowlist. The backend may only add `SIDEHUB_*` / telemetry
  variables, plus the workspace secrets it marks in `secretKeys` (UPPER_SNAKE_CASE names), and
  cannot override `PATH`, `LD_PRELOAD`, `NODE_OPTIONS`, rcfiles or the agent-owned `SIDEHUB_*`
  variables (`PtyEnvironmentPolicy`). `command.execute` children get the same allowlist
  (`DaemonEnvironmentPolicy`). Both then load the user's own shell init like any login
  terminal (`/etc/profile`, `~/.profile`, `~/.bashrc`; `command.execute` runs `bash -l -c`):
  whatever those files export is visible to the commands, so keep secrets out of them.
- **No agent token in terminals**: the agent token never enters a PTY environment. A terminal
  only gets the token SideHub scoped to it in `SIDEHUB_AGENT_TOKEN`: a run token (`sh_run_…`)
  for a SideHub-launched run, a session token (`sh_pty_…`) for an interactive terminal. Both
  are short-lived, die with their run / terminal, are denied agent management and session
  launches, and cannot open the agent WebSocket. Without one, `sidehub-cli` is unavailable.
  The setup token (`SIDEHUB_SETUP_TOKEN`) is removed from the agent's environment once read
  and is not passed to the background daemon, so git, commands and `pty-helper` never inherit it.
- **Working directory**: PTY working directories and `file.write` paths are confined to the
  agent's `workingDirectory`, symbolic links resolved. This only limits where a terminal
  *starts*; the shell itself can still `cd` anywhere.
- **File writes**: everything written into the working directory (`file.write`, terminal image
  attachments under `.sidehub-images/`, the skill files `AGENTS.md` / `GEMINI.md` /
  `.claude/commands/sidehub.md`) goes through `FileWritePolicy`: never under `.git/` or
  `.sidehub/`, never into a file that makes a tool run commands (`.claude/settings*.json`,
  `.mcp.json`, `.gemini/settings.json`, `.agents/hooks.json`, `.agents/mcp_config.json`,
  `.codex/config.toml`, `.envrc`, `.vscode/tasks.json`,
  `settings.json`, `launch.json`), and on Windows never through an 8.3 short name or an
  alternate data stream. The file is then opened folder by folder without following any
  symbolic link (`openat` + `O_NOFOLLOW` on Linux/macOS), so a link committed in the repository
  or planted after the check makes the write fail. A linked `AGENTS.md` / `GEMINI.md` / `.claude`
  therefore gets no skill section. With `allowFileWrite: false` the skill files are not written
  either.
- **Secrets in logs**: commands, terminal output and tokens are not written to the agent
  logs; setup tokens are kept out of the process arguments.
- **Injected secrets in terminal output**: the values of the variables `pty.start` marks as
  secrets (`secretKeys`, e.g. workspace secrets given to a run) are replaced with `***` in the
  terminal output before it is sent and in the history replayed on reconnect, even when a value is
  cut between two output chunks. Values shorter than 4 characters are not masked, and like CI log
  masking it is best effort: a value printed encoded or transformed is not recognized.
- **Workspace MCP servers**: the MCP servers `pty.start` hands a run (`mcpServers`) reach the
  CLI only through per-invocation options of `sidehub-cli launch` (no `.mcp.json` or settings file
  in the repository). A value may only reference, as `${NAME}`, a workspace secret admitted in that
  PTY: a server reading any other variable (`${SIDEHUB_AGENT_TOKEN}`, `${PATH}`…), an http server
  that is not https (http only to localhost), or an invalid name is ignored (`McpServerPolicy`).
  The launcher never writes a secret value into a command line or a file: the CLI reads it from its
  environment. A `stdio` server runs its command on this machine.
- **Untrusted `.sidehub/` content**: `.sidehub/` sits in the work tree, so a commit can fill
  it. At startup the agent ignores (with a warning) any `.sidehub/*.json` tracked by git, that
  is a symbolic link or that belongs to another user: such a config could point the agent to
  another backend, which would then run commands in its terminals. Logs, PID file, pending
  usage reports and notification FIFOs under `.sidehub/run/` are never written or chmodded
  through a symbolic link or an entry owned by another user; the agent refuses to start
  instead.
- **No root by default**: on Linux and macOS, `setup`, `start`, `restart` and the daemon refuse to
  run with effective UID 0: as root, the backend would control the whole machine. `--allow-root`
  (or `SIDEHUB_ALLOW_ROOT=1`) overrides it and logs a warning. `install.sh` run as root (`curl … |
  sudo bash`) installs the binaries but does not configure or start the agent. Service templates
  for an unprivileged user ship in `contrib/` (systemd unit with `NoNewPrivileges`,
  `ProtectSystem=full`, `PrivateTmp`…, and a per-user launchd agent).
- **Opt-out switches**: `command.execute` and file writes can be turned off per agent.

### What the agent does not protect

- Code execution by the backend through a PTY (`pty.input`): always possible.
- Access to anything the agent's OS user can read or write, including outside
  `workingDirectory`.
- The agent token, stored outside the project in `~/.config/sidehub/tokens/`
  (`%LOCALAPPDATA%\SideHub\tokens\` on Windows), `0600`: anyone who can read it can
  impersonate the agent. It is kept out of the work tree so a CLI in a terminal does not find
  it next to the code, but terminals run as the agent's user, so a process there can still read
  it. SideHub bounds what the token can do: it acts on its own agent only, and never drives or
  reads terminals (exec, terminal input and output are reserved to users).
- Network egress from the shells and CLIs the agent runs.

## Hardening options (`agent.json`)

| Field | Default | Effect when `false` |
|---|---|---|
| `allowCommandExecute` | `true` | `command.execute` is refused with `command.failed` |
| `allowFileWrite` | `true` | `file.write.*` is refused with `command.failed`; terminal image attachments are dropped; skill files are not written |

Both default to `true` so existing setups keep working (terminal image upload relies on file
writes). Turning them off narrows the surface but, as explained above, does not stop a
backend that can open a terminal.

Recommended practice:

- Run the agent as a **dedicated, unprivileged user**, never as `root` (the agent refuses root
  unless `--allow-root`), ideally as a service from `contrib/systemd/sidehub-agent@.service`.
- Point `workingDirectory` at the project only, and run the agent in a container or VM when
  the machine holds anything you would not hand to the backend.
- Rotate the agent token (delete and recreate the agent in SideHub) if it may have leaked.

## Verifying a release

Releases are built by GitHub Actions from a tag (`.github/workflows/release.yml`); no binary
is committed to the repository. Each release ships:

- `sidehub-agent-<platform>.tar.gz` / `.zip`: the agent, `sidehub-cli`, `pty-helper` (with its
  `node_modules`, installed by the CI with `npm ci --omit=dev --ignore-scripts` from the committed
  lockfile, `node-pty` pinned to an exact version) and `cli-wrappers`. `node-pty` is a
  `1.2.0` prerelease on purpose: the latest stable (`1.1.0`) ships no Linux prebuilds, so it
  would have to be compiled at install time, running its install scripts. Move to `1.2.x`
  once it is released;
- `checksums.sha256`: SHA-256 of every archive;
- `checksums.sha256.sig`: RSA signature (PKCS#1 v1.5, SHA-256) of `checksums.sha256` by the
  release signing key, whose public half is embedded in `install.sh` / `install.ps1`;
- on Windows, `sidehub-agent.exe`, `sidehub-cli.exe` and node-pty's native modules carry an
  Authenticode signature (Azure Artifact Signing, timestamped) once Windows signing is set up
  (see below), so SmartScreen and Smart App Control see a known publisher. node-pty's `conpty`
  files keep Microsoft's own signature. Check with `Get-AuthenticodeSignature .\sidehub-agent.exe`;
- a [build provenance attestation](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations)
  (Sigstore, signed with the workflow's OIDC identity) for each archive and for
  `checksums.sha256`, proving it was built by this repository's release workflow from the
  tagged commit.

**Install scripts.** `install.sh` / `install.ps1` resolve the version to a tag, download the
archive through the SideHub API, then download `checksums.sha256` and `checksums.sha256.sig`
**directly from GitHub Releases**. The signature is checked against the embedded public key
(`openssl` on macOS/Linux, .NET on Windows), then the archive's SHA-256 against
`checksums.sha256`, before anything is extracted. A missing or invalid signature, a missing
checksum entry or a mismatch aborts the install. Because the checksum does not come from the
SideHub proxy, a compromised proxy cannot serve a tampered archive with a matching checksum; because
it is signed with a key that lives outside GitHub Releases, write access to the release
(`contents: write`) cannot replace both the archive and its checksum either.

- Releases up to `v1.0.61` predate the signature: they are installed on the checksum alone, with a
  warning. A later release without `checksums.sha256.sig` is refused, so deleting the signature
  does not downgrade the check.
- Releases before `v1.0.59` are refused: their archive does not bundle the Node.js dependencies,
  which would have to be installed from the registry with package scripts. The install never runs
  `npm`.
- The install folder is `SIDEHUB_INSTALL_DIR` (default `/usr/local/lib/sidehub-agent`,
  `%LOCALAPPDATA%\Programs\sidehub-agent` on Windows). It is wiped on reinstall only if it is empty or
  holds a previous agent install (`.sidehub-agent-install` marker, or the agent binary and
  `pty-helper/`); any other existing folder aborts the install. The legacy `INSTALL_DIR` variable is
  ignored.

**Release signing key.** The private key is the `RELEASE_SIGNING_KEY` secret of the release
workflow, which refuses to publish a release without it and checks the signature against the key
embedded in `install.sh` before publishing. To rotate it (on a trusted machine, never on an
agent host):

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out release-signing-key.pem
openssl pkey -in release-signing-key.pem -pubout   # → RELEASE_SIGNING_PUBKEY in install.sh
# → $ReleaseSigningKeyXml in install.ps1 (ReleaseSigningKeyTests checks both match)
python3 -c "import base64,subprocess as s;h=s.check_output(['openssl','rsa','-in','release-signing-key.pem','-noout','-modulus']).decode().strip().split('=')[1];print('<RSAKeyValue><Modulus>'+base64.b64encode(bytes.fromhex(h)).decode()+'</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>')"
```

then store `release-signing-key.pem` as the `RELEASE_SIGNING_KEY` secret and keep it offline. Releases
signed with the previous key stay installable only with scripts that still embed it.

**Windows code signing.** The release workflow signs the Windows executables with
[Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/) when the
`ARTIFACT_SIGNING_ENDPOINT` repository variable is set; until then they ship unsigned and the run
shows a warning. Once it is set, a missing secret or a signature that does not verify (signed,
timestamped, chained to a trusted root) fails the release. To set it up:

1. In Azure, create an Artifact Signing account, complete the identity validation (check the
   eligibility of individuals and organizations by country) and create a *Public Trust* certificate
   profile.
2. Register an application (service principal) with a client secret, and give it the
   *Artifact Signing Certificate Profile Signer* role on the certificate profile only.
3. In the repository (*Settings → Secrets and variables → Actions*):
   - variables: `ARTIFACT_SIGNING_ENDPOINT` (the account's region endpoint, e.g.
     `https://weu.codesigning.azure.net/`), `ARTIFACT_SIGNING_ACCOUNT`,
     `ARTIFACT_SIGNING_CERTIFICATE_PROFILE`;
   - secrets: `ARTIFACT_SIGNING_TENANT_ID`, `ARTIFACT_SIGNING_CLIENT_ID`,
     `ARTIFACT_SIGNING_CLIENT_SECRET`.

The client secret expires (24 months at most): renew it before then, or releases fail. Signatures are
timestamped, so executables already released stay valid after the short-lived signing certificate
expires.

**Immutable releases.** Enable *Settings → General → Releases → Enable release immutability* on the
repository: once published, a release's assets can no longer be replaced or deleted. The release
workflow creates the release as a draft, attaches every asset, then publishes it, as immutable
releases require.

The release workflow pins every action by commit SHA (updated by Dependabot) and runs with
`contents: read` except for the job that publishes the release. Release tags are never moved:
each release is a new `v1.0.x` tag.

**Manual verification.**

```bash
TAG=v1.0.53                          # the release to verify
ASSET=sidehub-agent-linux-x64.tar.gz # your platform
curl -fsSLO "https://github.com/vincentaugerot/side_hub_agent/releases/download/$TAG/$ASSET"
curl -fsSLO "https://github.com/vincentaugerot/side_hub_agent/releases/download/$TAG/checksums.sha256"

# 1. Checksum (macOS: shasum -a 256 -c --ignore-missing checksums.sha256)
sha256sum -c --ignore-missing checksums.sha256

# 2. Provenance: built by this repo's release workflow (GitHub CLI >= 2.49)
gh attestation verify "$ASSET" --repo vincentaugerot/side_hub_agent
gh attestation verify checksums.sha256 --repo vincentaugerot/side_hub_agent
```

`gh attestation verify` prints the workflow, commit and tag the file was built from; it fails
if the file was not produced by `vincentaugerot/side_hub_agent`'s workflow. Releases published before
the attestation was added (up to the first tag that includes it) only have `checksums.sha256`.
Releases up to v1.0.118 were built when the repository was `toregua/side_hub_agent`: their
attestation names that repository, so verify them with `--repo toregua/side_hub_agent`.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report it privately through GitHub's
[private vulnerability reporting](https://github.com/vincentaugerot/side_hub_agent/security/advisories/new)
(*Security → Report a vulnerability* on the repository). Include:

- the agent version (the `Agent version:` line in the agent log) and platform,
- the steps to reproduce and the impact,
- whether the issue requires a compromised backend (see the trust model above; issues that
  only restate "the backend can run code on the agent" are expected behavior).

We aim to acknowledge reports within 5 business days and will coordinate a fix and a release
before any public disclosure.
