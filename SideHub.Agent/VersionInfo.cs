using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SideHub.Agent;

/// <summary>
/// Versions reported to the backend on connection: the agent's own (set from the release tag via
/// <c>-p:Version=</c>), the runtime CLIs found on this machine and whether they are logged in.
/// </summary>
public static partial class VersionInfo
{
    /// <summary>Runtimes probed with <c>&lt;cli&gt; --version</c>; keys match the backend runtime names.</summary>
    public static IReadOnlyList<string> ProbedClis { get; } = ["claude", "codex", "gemini", "copilot", "agy"];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// The CLIs that say whether they are logged in, and how: a CLI that is not would stop a run on its login screen.
    /// Only a clear answer counts (see <see cref="ParseLoggedIn"/>); an older CLI without the command is left unknown.
    /// Copilot has no such command: see <see cref="CopilotAuthProbe"/>.
    /// </summary>
    private static readonly Dictionary<string, string> AuthStatusCommands = new()
    {
        ["claude"] = "claude auth status",
        ["codex"] = "codex login status",
    };

    private static readonly SemaphoreSlim ProbeLock = new(1, 1);
    private static IReadOnlyDictionary<string, string>? _cliVersions;
    private static IReadOnlyDictionary<string, bool> _cliAuth = new Dictionary<string, bool>();
    private static IReadOnlyDictionary<string, CliAccount> _cliAccounts = new Dictionary<string, CliAccount>();
    private static IReadOnlyDictionary<string, CliBilling> _cliBilling = new Dictionary<string, CliBilling>();
    private static IReadOnlyDictionary<string, string> _cliAccountKeys = new Dictionary<string, string>();
    private static DateTime _probedAt;

    // The CLIs a run found logged out (NoteLoggedOut) and when, held against a status command that still says "logged
    // in" (AfterProbe). Guards them and the swap of the probed states: a run notes one while a probe is in flight.
    private static readonly object StateLock = new();
    private static readonly Dictionary<string, DateTime> FoundLoggedOut = new(StringComparer.Ordinal);

    public static string AgentVersion { get; } = ReadAgentVersion();

    private static string ReadAgentVersion()
    {
        var informational = typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // The SDK appends "+<commit sha>" to the informational version.
        return informational?.Split('+')[0] ?? "unknown";
    }

    /// <summary>The last probe result while it is fresh, otherwise null (probe with <see cref="GetCliVersionsAsync"/>).</summary>
    public static IReadOnlyDictionary<string, string>? CachedCliVersions =>
        _cliVersions is not null && DateTime.UtcNow - _probedAt < CacheTtl ? _cliVersions : null;

    /// <summary>Whether the installed CLIs are logged in, from the same probe as <see cref="CachedCliVersions"/>; a CLI
    /// whose state is unknown is absent. Null while the versions are not probed.</summary>
    public static IReadOnlyDictionary<string, bool>? CachedCliAuth => CachedCliVersions is null ? null : _cliAuth;

    /// <summary>The account each CLI that tells it runs with (only Copilot), from the same probe as <see cref="CachedCliAuth"/>.</summary>
    public static IReadOnlyDictionary<string, CliAccount>? CachedCliAccounts => CachedCliVersions is null ? null : _cliAccounts;

    /// <summary>How each logged-in CLI that tells is paid for (claude, codex), from the same probe as <see cref="CachedCliAuth"/>.</summary>
    public static IReadOnlyDictionary<string, CliBilling>? CachedCliBilling => CachedCliVersions is null ? null : _cliBilling;

    /// <summary>The plan each installed CLI runs on, as a fingerprint (<see cref="CliAccountKeys"/>), from the same probe
    /// as <see cref="CachedCliAuth"/>.</summary>
    public static IReadOnlyDictionary<string, string>? CachedCliAccountKeys => CachedCliVersions is null ? null : _cliAccountKeys;

    /// <summary>
    /// Detected CLI versions, keyed by runtime. Probed at most once per <see cref="CacheTtl"/> so that
    /// reconnections don't spawn the CLIs again; a CLI that is missing or times out is simply absent.
    /// Slow (login shell + CLI startup, several seconds): never await it on the connection path.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> GetCliVersionsAsync(CancellationToken ct)
    {
        await ProbeLock.WaitAsync(ct);
        try
        {
            if (CachedCliVersions is { } cached)
                return cached;
            return await ProbeAllAsync(statusTrusted: false, DateTime.UtcNow, ct);
        }
        finally
        {
            ProbeLock.Release();
        }
    }

    /// <summary>Probes the versions, then the logins. Call with <see cref="ProbeLock"/> held.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ProbeAllAsync(bool statusTrusted, DateTime askedAt, CancellationToken ct)
    {
        var probes = ProbedClis.Select(async cli => (cli, version: await ProbeVersionAsync(cli, ct)));
        var results = await Task.WhenAll(probes);

        var versions = results
            .Where(r => r.version is not null)
            .ToDictionary(r => r.cli, r => r.version!);
        Store(await ProbeAuthAsync(versions, ct), statusTrusted, askedAt);
        _cliVersions = versions;
        _probedAt = DateTime.UtcNow;
        return versions;
    }

    /// <summary>
    /// Probes again whether the installed CLIs are logged in. True when the state changed and is worth reporting.
    /// </summary>
    /// <param name="always">
    /// A run just exited in failure: probed whatever was known. A login that expires on the way is only seen then
    /// (<c>claude auth status</c> reads the stored login, which claude clears when it fails to renew it). Otherwise
    /// probed only when a CLI was not logged in: the user may have logged in since.
    /// </param>
    /// <param name="statusTrusted">
    /// A terminal of the user just closed: they may have logged in in it, so a CLI a run found logged out
    /// (<see cref="NoteLoggedOut"/>) is believed again when its status command says it is logged in.
    /// </param>
    public static async Task<bool> RefreshCliAuthAsync(bool always, bool statusTrusted, CancellationToken ct)
    {
        // Never probed: the probe of the connection is on its way
        if (_cliVersions is null || !always && !_cliAuth.Values.Contains(false))
            return false;
        // Before the wait for a probe in flight: a run may find a CLI logged out meanwhile
        var askedAt = DateTime.UtcNow;
        await ProbeLock.WaitAsync(ct);
        try
        {
            var (previousVersions, previousAuth, previousAccounts, previousBilling, previousKeys) =
                (_cliVersions, _cliAuth, _cliAccounts, _cliBilling, _cliAccountKeys);
            // Versions older than the cache are probed again with the logins: the next agent.connected reports both
            if (CachedCliVersions is { } versions)
                Store(await ProbeAuthAsync(versions, ct), statusTrusted, askedAt);
            else
                await ProbeAllAsync(statusTrusted, askedAt, ct);
            return !SameEntries(previousVersions, _cliVersions!) || !SameEntries(previousAuth, _cliAuth)
                || !SameEntries(previousAccounts, _cliAccounts) || !SameEntries(previousBilling, _cliBilling)
                || !SameEntries(previousKeys, _cliAccountKeys);
        }
        finally
        {
            ProbeLock.Release();
        }
    }

    /// <summary>
    /// A run's CLI just said it could not authenticate (<see cref="CliLoginFailures"/>): it is reported as not logged in
    /// from now on, without waiting for a probe. True when that changed what is reported.
    /// </summary>
    public static bool NoteLoggedOut(string cli)
    {
        lock (StateLock)
        {
            FoundLoggedOut[cli] = DateTime.UtcNow;
            if (_cliAuth.TryGetValue(cli, out var loggedIn) && !loggedIn)
                return false;
            _cliAuth = new Dictionary<string, bool>(_cliAuth) { [cli] = false };
            return true;
        }
    }

    private static void Store((IReadOnlyDictionary<string, bool> Auth, IReadOnlyDictionary<string, CliAccount> Accounts,
        IReadOnlyDictionary<string, CliBilling> Billing, IReadOnlyDictionary<string, string> AccountKeys) probed,
        bool statusTrusted, DateTime askedAt)
    {
        lock (StateLock)
        {
            (_cliAuth, _cliAccounts, _cliBilling, _cliAccountKeys) =
                (AfterProbe(probed.Auth, FoundLoggedOut, statusTrusted, askedAt), probed.Accounts, probed.Billing, probed.AccountKeys);
        }
    }

    /// <summary>
    /// The login states to report after a probe. A CLI that a run found logged out (<paramref name="foundLoggedOut"/>:
    /// when each was found) stays so while its status command does not say it too: the command only reads the stored
    /// login, which may look fine when the CLI cannot authenticate with it. It is believed again once it says "not
    /// logged in" (it follows the login from then on), or says otherwise after a terminal of the user closed
    /// (<paramref name="statusTrusted"/>), unless the run found it after that terminal closed (<paramref name="askedAt"/>:
    /// when the probe was asked for). An agent that restarts starts from its status command.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> AfterProbe(IReadOnlyDictionary<string, bool> probed,
        Dictionary<string, DateTime> foundLoggedOut, bool statusTrusted, DateTime askedAt)
    {
        var auth = new Dictionary<string, bool>(probed);
        foreach (var (cli, foundAt) in foundLoggedOut.ToArray())
        {
            var saysLoggedOut = probed.TryGetValue(cli, out var loggedIn) && !loggedIn;
            if (saysLoggedOut || statusTrusted && foundAt < askedAt)
                foundLoggedOut.Remove(cli);
            else
                auth[cli] = false;
        }
        return auth;
    }

    private static bool SameEntries<T>(IReadOnlyDictionary<string, T> previous, IReadOnlyDictionary<string, T> now) =>
        previous.Count == now.Count
        && previous.All(entry => now.TryGetValue(entry.Key, out var value) && EqualityComparer<T>.Default.Equals(value, entry.Value));

    private static async Task<(IReadOnlyDictionary<string, bool> Auth, IReadOnlyDictionary<string, CliAccount> Accounts,
        IReadOnlyDictionary<string, CliBilling> Billing, IReadOnlyDictionary<string, string> AccountKeys)> ProbeAuthAsync(
        IReadOnlyDictionary<string, string> versions, CancellationToken ct)
    {
        var copilot = versions.ContainsKey("copilot") ? ProbeCopilotAsync(ct) : Task.FromResult<CopilotAuth?>(null);
        var probes = AuthStatusCommands
            .Where(command => versions.ContainsKey(command.Key))
            .Select(async command => (cli: command.Key, output: await RunCliAsync(command.Value, ct)));
        var results = (await Task.WhenAll(probes))
            .Where(r => r.output is not null)
            .Select(r => (r.cli, exitCode: r.output!.Value.ExitCode, text: r.output.Value.Stdout + "\n" + r.output.Value.Stderr))
            .ToList();
        var auth = results
            .Select(r => (r.cli, loggedIn: ParseLoggedIn(r.cli, r.exitCode, r.text)))
            .Where(r => r.loggedIn is not null)
            .ToDictionary(r => r.cli, r => r.loggedIn!.Value);
        var billing = results
            .Where(r => auth.GetValueOrDefault(r.cli))
            .Select(r => (r.cli, billing: ParseBilling(r.cli, r.text)))
            .Where(r => r.billing is not null)
            .ToDictionary(r => r.cli, r => r.billing!);
        if (versions.ContainsKey("agy") && AntigravitySignedIn(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            auth["agy"] = true;
        var accounts = new Dictionary<string, CliAccount>();
        if (await copilot is { } copilotAuth)
        {
            auth["copilot"] = copilotAuth.LoggedIn;
            if (copilotAuth.Account is { } account)
                accounts["copilot"] = account;
        }
        var identities = new Dictionary<string, string?>
        {
            ["claude"] = auth.GetValueOrDefault("claude")
                ? CliAccountKeys.ClaudeIdentity(results.FirstOrDefault(r => r.cli == "claude").text)
                : null,
            ["copilot"] = CliAccountKeys.CopilotIdentity(accounts.GetValueOrDefault("copilot")),
        };
        var keys = CliAccountKeys.For(versions.Keys, identities,
            CliAccountKeys.MachineIdentity(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        return (auth, accounts, billing, keys);
    }

    /// <summary>
    /// agy has no status command: its Google sign-in leaves a token in <c>~/.gemini/antigravity-cli/</c> on Linux (the
    /// token itself is never read). Without the file the state is unknown, not signed out: on a desktop the token is in
    /// the OS keyring, and an API key needs none.
    /// </summary>
    public static bool AntigravitySignedIn(string home) =>
        !string.IsNullOrEmpty(home)
        && new FileInfo(Path.Combine(home, ".gemini", "antigravity-cli", "antigravity-oauth-token")) is { Exists: true, Length: > 0 };

    /// <summary>Starts Copilot's headless server for one <see cref="CopilotAuthProbe.QueryAsync"/>, then kills it.</summary>
    private static async Task<CopilotAuth?> ProbeCopilotAsync(CancellationToken ct)
    {
        if (ShellStartInfo(CopilotAuthProbe.CommandLine) is not { } psi)
            return null;
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            return await CopilotAuthProbe.QueryAsync(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, ProbeTimeout, ct);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    /// <summary>
    /// From the command's output (stdout and stderr): claude prints <c>{"loggedIn": true|false, …}</c> on stdout, codex
    /// "Logged in using …" or "Not logged in" on stderr. Anything else (a CLI that predates the command, an error) is
    /// unknown: null.
    /// </summary>
    public static bool? ParseLoggedIn(string cli, int exitCode, string output)
    {
        switch (cli)
        {
            case "claude":
                var match = LoggedInPattern().Match(output);
                return match.Success ? match.Groups[1].Value == "true" : null;
            case "codex":
                if (output.Contains("Not logged in", StringComparison.Ordinal))
                    return false;
                return exitCode == 0 && output.Contains("Logged in", StringComparison.Ordinal) ? true : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// How a logged-in CLI is paid for, from the same status output as <see cref="ParseLoggedIn"/>. claude:
    /// <c>"authMethod": "claude.ai"</c> is a subscription (its <c>subscriptionType</c> is the plan), <c>api_key</c> and
    /// <c>third_party</c> (Bedrock, Vertex…) are billed per use; <c>oauth_token</c> stays unknown (a subscription token
    /// or a gateway's). codex: "Logged in using ChatGPT" or "… using an API key". Null when unknown.
    /// </summary>
    public static CliBilling? ParseBilling(string cli, string output)
    {
        switch (cli)
        {
            case "claude":
                var method = AuthMethodPattern().Match(output) is { Success: true } m ? m.Groups[1].Value : null;
                return method switch
                {
                    "claude.ai" => new CliBilling(CliBilling.Subscription,
                        SubscriptionTypePattern().Match(output) is { Success: true } plan ? plan.Groups[1].Value : null),
                    "api_key" or "third_party" => new CliBilling(CliBilling.Api, null),
                    _ => null,
                };
            case "codex":
                if (output.Contains("Logged in using ChatGPT", StringComparison.Ordinal))
                    return new CliBilling(CliBilling.Subscription, null);
                return output.Contains("Logged in using an API key", StringComparison.Ordinal) ? new CliBilling(CliBilling.Api, null) : null;
            default:
                return null;
        }
    }

    private static async Task<string?> ProbeVersionAsync(string cli, CancellationToken ct) =>
        await RunCliAsync($"{cli} --version", ct) is { ExitCode: 0 } result ? ParseVersion(result.Stdout) : null;

    /// <summary>Runs a CLI command line; null when it could not run or timed out.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)?> RunCliAsync(string commandLine, CancellationToken ct)
    {
        if (ShellStartInfo(commandLine) is not { } psi)
            return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            // No terminal and nothing to read: a CLI that took the arguments for a prompt ends instead of waiting.
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (Exception)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return null;
        }
    }

    /// <summary>A command line run with redirected input and output; null when no shell can run it.</summary>
    private static ProcessStartInfo? ShellStartInfo(string commandLine)
    {
        // Through a login shell so the user's PATH (nvm, ~/.local/bin, …) resolves the CLI like in a PTY.
        // cmd.exe is resolved to its absolute path and told not to look the CLI up in the daemon's
        // working directory (a repository) before the PATH.
        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (ExecutableResolver.Resolve("cmd.exe") is not { } cmd)
                return null;
            psi = new ProcessStartInfo(cmd) { ArgumentList = { "/c", commandLine } };
            psi.Environment[ExecutableResolver.NoCurrentDirectoryLookupVariable] = "1";
        }
        else
        {
            psi = new ProcessStartInfo(File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh") { ArgumentList = { "-l", "-c", commandLine } };
        }
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        return psi;
    }

    /// <summary>"2.1.3 (Claude Code)" → "2.1.3", "codex-cli 0.46.0" → "0.46.0".</summary>
    public static string? ParseVersion(string output)
    {
        var match = VersionPattern().Match(output);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?(-[0-9A-Za-z.\-]+)?")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"""loggedIn""\s*:\s*(true|false)")]
    private static partial Regex LoggedInPattern();

    [GeneratedRegex(@"""authMethod""\s*:\s*""([a-z0-9_.]+)""")]
    private static partial Regex AuthMethodPattern();

    [GeneratedRegex(@"""subscriptionType""\s*:\s*""([a-z0-9_]+)""")]
    private static partial Regex SubscriptionTypePattern();
}

/// <summary>How a CLI is paid for, as reported to the backend: never a credential.</summary>
/// <param name="Mode"><see cref="Subscription"/> or <see cref="Api"/> (the backend's billing modes).</param>
/// <param name="Plan">The subscription when the CLI tells (claude: "pro", "max", "team", "enterprise").</param>
public sealed record CliBilling(string Mode, string? Plan)
{
    public const string Subscription = "subscription";
    public const string Api = "api";
}
