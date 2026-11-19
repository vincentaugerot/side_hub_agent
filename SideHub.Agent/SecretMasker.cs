using System.Text;

namespace SideHub.Agent;

/// <summary>
/// Replaces the values of the secrets the backend injects into a PTY (the <c>additionalEnv</c> keys
/// listed in <c>secretKeys</c> of <c>pty.start</c>) with <c>***</c> in its output, before it is sent
/// (<c>pty.output</c>) or kept in the replayed history (<see cref="PtyOutputBuffer"/>).
/// Output arrives in chunks, so a value cut between two chunks is caught too: the end of a chunk
/// that could be the start of a secret is held back until the next chunk tells whether it is one,
/// or <see cref="Flush"/> releases it. Best effort, like CI log masking: a value printed transformed
/// (encoded, reversed, interleaved with escape codes) is not recognized.
/// One masker per PTY, fed by its single output reader; <see cref="Add"/> alone may come from another thread (a secret
/// a test sets or reads while the terminal runs, see <c>sidehub-cli test value</c>).
/// </summary>
public sealed class SecretMasker
{
    public const string Replacement = "***";

    /// <summary>Shorter values are not masked: hiding every "1" or "on" would mangle the whole terminal.</summary>
    public const int MinSecretLength = 4;

    /// <summary>The most secrets added to one terminal while it runs: enough for the values of a test run.</summary>
    public const int MaxAddedSecrets = 50;

    /// <summary>What is masked, replaced as a whole when a secret is added: the reader always sees one consistent set.</summary>
    /// <param name="Secrets">Longest first, so a secret that starts with another one is masked whole.</param>
    private sealed record Mask(string[] Secrets, HashSet<char> FirstChars)
    {
        public static Mask Of(IEnumerable<string> secrets)
        {
            var list = secrets
                .Where(s => s.Length >= MinSecretLength)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(s => s.Length)
                .ToArray();
            return new Mask(list, list.Select(s => s[0]).ToHashSet());
        }
    }

    private volatile Mask _mask;
    private int _added;
    private string _pending = string.Empty;

    private SecretMasker(IEnumerable<string> secrets) => _mask = Mask.Of(secrets);

    /// <summary>A masker that masks nothing yet, for a terminal that started without secrets and gets one later.</summary>
    public static SecretMasker Empty() => new([]);

    /// <summary>
    /// Masks <paramref name="value"/> from now on (and each of its lines). Returns false when it is too short to mask,
    /// already masked, or when <see cref="MaxAddedSecrets"/> were added.
    /// </summary>
    public bool Add(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinSecretLength)
            return false;
        var current = _mask;
        if (current.Secrets.Contains(value, StringComparer.Ordinal) || _added >= MaxAddedSecrets)
            return false;
        _added++;
        var lines = value.Contains('\n') ? value.Split('\n').Select(l => l.TrimEnd('\r')) : [];
        _mask = Mask.Of(current.Secrets.Append(value).Concat(lines));
        return true;
    }

    /// <summary>
    /// A masker for the values of <paramref name="secretKeys"/> in <paramref name="additionalEnv"/>,
    /// or null when there is nothing to mask. A multi-line value is also masked line by line: the
    /// terminal turns its <c>\n</c> into <c>\r\n</c>, so the value is never printed as is.
    /// </summary>
    public static SecretMasker? For(IReadOnlyDictionary<string, string>? additionalEnv, IEnumerable<string>? secretKeys)
    {
        if (additionalEnv is null || secretKeys is null) return null;

        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in secretKeys)
        {
            if (key is null || !additionalEnv.TryGetValue(key, out var value) || string.IsNullOrEmpty(value)) continue;
            secrets.Add(value);
            if (value.Contains('\n'))
                foreach (var line in value.Split('\n'))
                    secrets.Add(line.TrimEnd('\r'));
        }

        return secrets.Any(s => s.Length >= MinSecretLength) ? new SecretMasker(secrets) : null;
    }

    /// <summary>True while the end of the last chunk is held back, waiting for the next one.</summary>
    public bool HasPending => _pending.Length > 0;

    /// <summary>
    /// <paramref name="chunk"/> (after what was held back) with every secret masked, minus a tail
    /// that could be the start of a secret: that tail is held back for the next call.
    /// </summary>
    public string Process(string chunk) => Scan(_pending + chunk, holdPartial: true);

    /// <summary>Releases what was held back (masked), when no more output is expected soon.</summary>
    public string Flush() => Scan(_pending, holdPartial: false);

    private string Scan(string text, bool holdPartial)
    {
        _pending = string.Empty;
        var mask = _mask;
        var output = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (mask.FirstChars.Contains(text[i]))
            {
                if (MatchAt(mask, text, i) is { } length)
                {
                    output.Append(Replacement);
                    i += length;
                    continue;
                }
                if (holdPartial && IsSecretPrefix(mask, text, i))
                {
                    _pending = text[i..];
                    break;
                }
            }
            output.Append(text[i]);
            i++;
        }
        return output.ToString();
    }

    private static int? MatchAt(Mask mask, string text, int index)
    {
        foreach (var secret in mask.Secrets)
            if (secret.Length <= text.Length - index
                && string.CompareOrdinal(text, index, secret, 0, secret.Length) == 0)
                return secret.Length;
        return null;
    }

    /// <summary>The rest of <paramref name="text"/> from <paramref name="index"/> is the start of a secret.</summary>
    private static bool IsSecretPrefix(Mask mask, string text, int index)
    {
        var remaining = text.Length - index;
        foreach (var secret in mask.Secrets)
            if (secret.Length > remaining
                && string.CompareOrdinal(text, index, secret, 0, remaining) == 0)
                return true;
        return false;
    }
}
