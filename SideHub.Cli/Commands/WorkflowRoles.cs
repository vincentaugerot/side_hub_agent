using System.Text.Json;

namespace SideHub.Cli.Commands;

/// <summary>
/// Roles of a workflow (SideHub doc 72): a step given to a role (po, pm, architect, dev, qa) waits for one of the
/// members who hold it on the execution. <c>workflow run --role qa=alice@x.io</c> picks the holders at launch, in
/// place of the workflow's default holders; <c>workflow get</c> and <c>workflow execution-get</c> show them.
/// </summary>
public static class WorkflowRoles
{
    public const string Flag = "--role";

    /// <summary>
    /// Collects every <c>--role key=holder</c> (repeatable, several holders separated by commas): a holder is a member's
    /// e-mail or id. A key given twice adds its holders; keys match without case, as SideHub compares them.
    /// </summary>
    public static bool TryCollect(string[] args, out Dictionary<string, List<string>> holders, out string? error)
    {
        holders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != Flag) continue;
            var pair = i + 1 < args.Length ? args[i + 1] : "";
            var separator = pair.IndexOf('=');
            var key = separator > 0 ? pair[..separator].Trim() : "";
            var members = separator > 0
                ? pair[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            if (key.Length == 0 || members.Length == 0)
            {
                error = $"{Flag} expects key=<member e-mail or id>, e.g. {Flag} qa=alice@example.com (got '{pair}').";
                return false;
            }
            if (!holders.TryGetValue(key, out var list))
                holders[key] = list = [];
            foreach (var member in members)
                if (!list.Contains(member, StringComparer.OrdinalIgnoreCase))
                    list.Add(member);
            i++;
        }
        return true;
    }

    /// <summary>
    /// The e-mail of each member by user id, to name the default holders SideHub returns as user ids. Empty when the
    /// workflow has no default holder or the member list cannot be read: the ids are shown then.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> MemberEmailsAsync(SideHubApiClient client, JsonElement workflow)
    {
        var emails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Items(workflow, "roles").Any(r => Holders(r, "defaultHolders").Count > 0))
            return emails;
        try
        {
            var result = await client.GetWorkspaceMembersAsync();
            var members = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("members", out var m) ? m : result;
            if (members.ValueKind == JsonValueKind.Array)
                foreach (var member in members.EnumerateArray())
                    if (Prop(member, "userId") is { } userId && Prop(member, "email") is { } email)
                        emails[userId] = email;
        }
        catch (Exception)
        {
            // Best effort: the roles still show, with ids.
        }
        return emails;
    }

    /// <summary>The lines of <c>workflow get</c> for the workflow's roles; none when it has no role.</summary>
    /// <param name="emails">E-mail by user id (<see cref="MemberEmailsAsync"/>), for default holders given as ids.</param>
    public static IEnumerable<string> DescribeWorkflowRoles(JsonElement workflow, IReadOnlyDictionary<string, string>? emails = null)
    {
        var roles = Items(workflow, "roles");
        if (roles.Count == 0)
            yield break;
        yield return $"Roles (workflow run {Flag} key=<e-mail>, default holders otherwise):";
        foreach (var role in roles)
        {
            var defaults = Holders(role, "defaultHolders")
                .Select(h => emails is not null && emails.TryGetValue(h, out var email) ? email : h)
                .ToList();
            var holders = defaults.Count == 0 ? "no default holder" : $"default holders: {string.Join(", ", defaults)}";
            yield return $"  {Name(role)}: {holders}";
            if (Prop(role, "description") is { Length: > 0 } description)
                yield return $"      {description.ReplaceLineEndings(" ")}";
        }
    }

    /// <summary>"qa (QA)" for a step given to a role, with the role's label when the workflow defines it; null otherwise.</summary>
    public static string? StepRole(JsonElement step, JsonElement workflow)
    {
        if (Prop(step, "role") is not { Length: > 0 } key)
            return null;
        var role = Items(workflow, "roles").FirstOrDefault(r => string.Equals(Prop(r, "key"), key, StringComparison.OrdinalIgnoreCase));
        return role.ValueKind == JsonValueKind.Object ? Name(role) : key;
    }

    /// <summary>
    /// The lines of <c>workflow execution-get</c> for the holders frozen on the execution
    /// (<c>roleHolders: [{ key, label, description, holders: [{ userId, email, active }] }]</c>).
    /// </summary>
    public static IEnumerable<string> DescribeExecutionHolders(JsonElement execution)
    {
        var roles = Items(execution, "roleHolders");
        if (roles.Count == 0)
            yield break;
        yield return "Role holders:";
        foreach (var role in roles)
            yield return $"  {Name(role)}: {HolderList(Holders(role, "holders"))}";
    }

    /// <summary>" on behalf of the qa holder" when an owner or admin took the decision in place of the role's holders.</summary>
    public static string OnBehalfOf(JsonElement decision, string property = "onBehalfOfRole") =>
        Prop(decision, property) is { Length: > 0 } role ? $" on behalf of the {role} holder" : "";

    private static string HolderList(IReadOnlyList<string> holders) =>
        holders.Count == 0 ? "nobody" : string.Join(", ", holders);

    private static string Name(JsonElement role)
    {
        var key = Prop(role, "key") ?? "?";
        return Prop(role, "label") is { Length: > 0 } label && !string.Equals(label, key, StringComparison.Ordinal)
            ? $"{key} ({label})" : key;
    }

    private static List<string> Holders(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var holders) ? HoldersOf(holders) : [];

    /// <summary>
    /// Each holder as its e-mail (its id otherwise), marked when it is no longer an active member: a user id for the
    /// default holders of a workflow, <c>{ userId, email, active }</c> for the holders of an execution.
    /// </summary>
    private static List<string> HoldersOf(JsonElement holders)
    {
        if (holders.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var holder in holders.EnumerateArray())
        {
            if (holder.ValueKind == JsonValueKind.String)
            {
                list.Add(holder.GetString()!);
                continue;
            }
            if (holder.ValueKind != JsonValueKind.Object)
                continue;
            var name = Prop(holder, "email") ?? Prop(holder, "userId");
            if (name is null)
                continue;
            var inactive = holder.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.False;
            list.Add(inactive ? $"{name} (inactive)" : name);
        }
        return list;
    }

    private static List<JsonElement> Items(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static string? Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
