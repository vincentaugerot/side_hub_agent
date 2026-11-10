import * as pty from 'node-pty';
import * as readline from 'readline';
import { readFileSync } from 'fs';
import { basename } from 'path';

let ptyProcess = null;
let ptyShell = null;

// Daemon environment variables passed through to PTY sessions. Everything else is dropped:
// the daemon may have been started from a shell holding secrets (API keys, cloud credentials,
// tokens) that the terminal must not inherit. What the shell needs beyond this list comes from
// the user's own profile (login shell / rcfile) or from the agent (config.env).
// Names are compared in upper case (Windows environment names are case-insensitive).
const ALLOWED_ENV_NAMES = new Set([
  // Identity, paths, locale
  'HOME', 'USER', 'LOGNAME', 'SHELL', 'PATH', 'LANG', 'LANGUAGE', 'TZ', 'TMPDIR', 'EDITOR', 'VISUAL', 'PAGER',
  // Session plumbing (sockets, not secrets)
  'SSH_AUTH_SOCK', 'DBUS_SESSION_BUS_ADDRESS', 'WSL_DISTRO_NAME', 'WSL_INTEROP',
  // Network proxies, needed by the CLIs behind a corporate proxy
  'HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY', 'ALL_PROXY',
  // Toolchain locations
  'NVM_DIR', 'NVM_BIN', 'NVM_INC', 'VOLTA_HOME', 'PNPM_HOME', 'BUN_INSTALL',
  'DOTNET_ROOT', 'JAVA_HOME', 'GOPATH', 'GOROOT', 'CARGO_HOME', 'RUSTUP_HOME',
  'HOMEBREW_PREFIX', 'HOMEBREW_CELLAR', 'HOMEBREW_REPOSITORY',
  // CLI configuration directories
  'CLAUDE_CONFIG_DIR', 'CODEX_HOME',
  // Windows system variables
  'SYSTEMROOT', 'SYSTEMDRIVE', 'WINDIR', 'COMSPEC', 'PATHEXT', 'OS', 'TEMP', 'TMP',
  'USERNAME', 'USERDOMAIN', 'USERPROFILE', 'HOMEDRIVE', 'HOMEPATH', 'COMPUTERNAME',
  'APPDATA', 'LOCALAPPDATA', 'PROGRAMDATA', 'PROGRAMFILES', 'PROGRAMFILES(X86)', 'PROGRAMW6432',
  'COMMONPROGRAMFILES', 'COMMONPROGRAMFILES(X86)', 'PUBLIC',
  'PROCESSOR_ARCHITECTURE', 'NUMBER_OF_PROCESSORS',
]);
const ALLOWED_ENV_PREFIXES = ['LC_', 'XDG_'];

function filterDaemonEnv(env) {
  const filtered = {};
  for (const [key, value] of Object.entries(env)) {
    const name = key.toUpperCase();
    if (ALLOWED_ENV_NAMES.has(name) || ALLOWED_ENV_PREFIXES.some(prefix => name.startsWith(prefix))) {
      filtered[key] = value;
    }
  }
  return filtered;
}

const rl = readline.createInterface({
  input: process.stdin,
  output: process.stdout,
  terminal: false
});

function send(msg) {
  console.log(JSON.stringify(msg));
}

function handleMessage(line) {
  try {
    const msg = JSON.parse(line);

    switch (msg.type) {
      case 'start':
        startPty(msg);
        break;
      case 'input':
        if (ptyProcess) {
          ptyProcess.write(msg.data);
          // Enter starts (or ends) a command: say it before the next output
          if (msg.data.includes('\r')) scheduleForegroundCheck();
        }
        break;
      case 'resize':
        if (ptyProcess) {
          ptyProcess.resize(msg.cols, msg.rows);
        }
        break;
      case 'stop':
        stopPty();
        break;
      case 'ping':
        send({ type: 'pong', id: msg.id, ptyRunning: ptyProcess !== null });
        break;
    }
  } catch (e) {
    send({ type: 'error', message: e.message });
  }
}

function startPty(config) {
  if (ptyProcess) {
    send({ type: 'error', message: 'PTY already running' });
    return;
  }

  const shell = config.shell || process.env.SHELL || '/bin/bash';
  const cwd = config.cwd || process.cwd();
  const cols = config.cols || 80;
  const rows = config.rows || 24;
  const extraEnv = config.env || {};

  const env = {
    ...filterDaemonEnv(process.env),
    TERM: 'xterm-256color',
    COLORTERM: 'truecolor',
    COLUMNS: String(cols),
    LINES: String(rows),
    ...extraEnv
  };

  // Default to a login shell so .profile / .bashrc run. When the agent
  // provided a SideHub rcfile (only meaningful for bash), invoke bash as an
  // interactive non-login shell with --rcfile pointing at that file: our
  // rcfile manually re-sources the standard init files and then re-prepends
  // the cli-wrappers dir to PATH so user PATH overrides can't shadow it.
  let shellArgs = ['-l'];
  const sidehubBashrc = env.SIDEHUB_BASHRC;
  const isBash = /(^|\/)bash$/.test(shell);
  if (isBash && sidehubBashrc) {
    shellArgs = ['--rcfile', sidehubBashrc, '-i'];
  }
  if (process.platform === 'win32') {
    shellArgs = windowsShellArgs(shell);
  }

  try {
    ptyProcess = pty.spawn(shell, shellArgs, {
      name: 'xterm-256color',
      cols: cols,
      rows: rows,
      cwd: cwd,
      env: env
    });

    ptyShell = shell;

    ptyProcess.onData((data) => {
      send({ type: 'output', data: data });
      scheduleForegroundCheck();
    });

    ptyProcess.onExit(({ exitCode, signal }) => {
      send({ type: 'exit', exitCode: exitCode, signal: signal });
      ptyProcess = null;
      stopForegroundWatch();
    });

    startForegroundWatch();

    send({ type: 'started', shell: shell, pid: ptyProcess.pid });
  } catch (e) {
    send({ type: 'error', message: e.message });
  }
}

// ConPTY consoles start on the OEM code page (437, 850…): a CLI writing UTF-8 bytes to the
// console (Copilot's box drawing, spinners) comes out as "Γöé". The shell switches its console
// to UTF-8 (65001) first; the CLIs launched in it share that console.
function windowsShellArgs(shell) {
  const name = shell.split(/[\\/]/).pop().toLowerCase();
  if (name === 'cmd.exe' || name === 'cmd') {
    return ['/K', 'chcp 65001 >nul'];
  }
  if (['powershell.exe', 'powershell', 'pwsh.exe', 'pwsh'].includes(name)) {
    return ['-NoLogo', '-NoExit', '-Command',
      '[Console]::InputEncoding = [Console]::OutputEncoding = $OutputEncoding = New-Object System.Text.UTF8Encoding $false'];
  }
  return [];
}

function stopPty() {
  stopForegroundWatch();
  if (ptyProcess) {
    ptyProcess.kill();
    ptyProcess = null;
  }
}

// What runs in the foreground of the terminal: null while the shell waits at its prompt, else the name of the
// program it runs (`sleep`, `npm`, `claude`), never its arguments (they can hold a secret). The agent keeps the
// backend told, so closing a tab asks first while a command still runs. Checked shortly after each output or
// Enter (a command starts or ends with some) and every FOREGROUND_POLL_MS; sent only when it changed.
// Unknown on Windows (ConPTY has no foreground process group): nothing is sent there.
const FOREGROUND_POLL_MS = 2000;
const FOREGROUND_SETTLE_MS = 200;
const MAX_PROCESS_NAME_LENGTH = 64;
let foregroundTimer = null;
let foregroundPoll = null;
let lastForeground; // undefined: nothing sent yet

function startForegroundWatch() {
  if (process.platform === 'win32') return;
  lastForeground = undefined;
  foregroundPoll = setInterval(checkForeground, FOREGROUND_POLL_MS);
  scheduleForegroundCheck();
}

function stopForegroundWatch() {
  clearInterval(foregroundPoll);
  clearTimeout(foregroundTimer);
  foregroundPoll = null;
  foregroundTimer = null;
}

function scheduleForegroundCheck() {
  if (!foregroundPoll || foregroundTimer) return;
  foregroundTimer = setTimeout(() => {
    foregroundTimer = null;
    checkForeground();
  }, FOREGROUND_SETTLE_MS);
}

function checkForeground() {
  if (!ptyProcess) return;
  const current = readForeground();
  if (current === undefined || current === lastForeground) return;
  lastForeground = current;
  send({ type: 'foreground', process: current });
}

/** null: the shell is at its prompt; a name: the program in the foreground; undefined: not known. */
function readForeground() {
  try {
    if (process.platform === 'linux') {
      // Field 8 of /proc/<shell>/stat is the terminal's foreground process group (after the name in parentheses)
      const stat = readFileSync(`/proc/${ptyProcess.pid}/stat`, 'utf8');
      const tpgid = Number(stat.slice(stat.lastIndexOf(')') + 2).split(' ')[5]);
      if (!(tpgid > 0)) return undefined;
      if (tpgid === ptyProcess.pid) return null;
      return processName(readFileSync(`/proc/${tpgid}/comm`, 'utf8'));
    }
    // macOS: node-pty names the foreground process group's leader
    const name = ptyProcess.process;
    if (!name) return undefined;
    return basename(name) === basename(ptyShell) ? null : processName(name);
  } catch {
    // The group ended between the two reads, or /proc is not readable: the next check tells
    return undefined;
  }
}

function processName(raw) {
  // A program can rename itself with its arguments (`npm run build`): only the first word is kept
  const name = basename(String(raw).trim().split(/\s+/)[0] || '').replace(/[\x00-\x1f\x7f]/g, '');
  return name ? name.slice(0, MAX_PROCESS_NAME_LENGTH) : undefined;
}

rl.on('line', handleMessage);

rl.on('close', () => {
  stopPty();
  process.exit(0);
});

// Signal ready
send({ type: 'ready' });
