using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SideHub.Cli;

public class SideHubApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _workspaceId;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string? DefaultAgentId { get; }

    public SideHubApiClient(string apiUrl, string agentToken, string workspaceId, string? defaultAgentId = null)
    {
        _workspaceId = workspaceId;
        DefaultAgentId = string.IsNullOrWhiteSpace(defaultAgentId) ? null : defaultAgentId;
        // X-Agent-Token goes on every request: redirects are only followed within the API's origin.
        var handler = new SameOriginRedirectHandler(new SocketsHttpHandler { AllowAutoRedirect = false });
        _http = new HttpClient(handler) { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Add("X-Agent-Token", agentToken);
    }

    // --- Drive ---

    public async Task<JsonElement> GetDriveTreeAsync()
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/drive");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetDriveItemAsync(string itemId)
    {
        var resp = await _http.GetAsync($"api/drive/{itemId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CreateDriveItemAsync(string title, string? content, string? parentId, string? type = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = title, ["type"] = type ?? "page" };
        if (content is not null) body["content"] = content;
        if (parentId is not null) body["parentId"] = parentId;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/drive", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateDriveItemAsync(string itemId, string? title, string? content)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null) body["title"] = title;
        if (content is not null) body["content"] = content;

        var resp = await _http.PutAsJsonAsync($"api/drive/{itemId}", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public record DriveDownloadInfo(string FileName, string? MimeType, long? FileSize, string DownloadUrl);

    public async Task<DriveDownloadInfo> GetDriveDownloadInfoAsync(string itemId)
    {
        var item = await GetDriveItemAsync(itemId);

        var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
        var url = item.TryGetProperty("downloadUrl", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url))
        {
            var label = type is null ? "this item" : $"item of type '{type}'";
            throw new InvalidOperationException($"No downloadable file for {label}. Use `drive read` for text pages.");
        }

        var fileName = item.TryGetProperty("fileName", out var f) ? f.GetString() : null;
        if (string.IsNullOrEmpty(fileName))
            fileName = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? itemId : itemId;

        string? mimeType = item.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
        long? fileSize = item.TryGetProperty("fileSize", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;

        return new DriveDownloadInfo(fileName!, mimeType, fileSize, url!);
    }

    private static readonly HttpClient _downloadClient = new();

    public async Task DownloadToStreamAsync(string presignedUrl, Stream destination, CancellationToken ct = default)
    {
        using var resp = await _downloadClient.GetAsync(presignedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Download failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        await resp.Content.CopyToAsync(destination, ct);
    }

    public async Task DeleteDriveItemAsync(string itemId)
    {
        var resp = await _http.DeleteAsync($"api/drive/{itemId}");
        await EnsureSuccessAsync(resp);
    }

    public async Task<JsonElement> MoveDriveItemAsync(string itemId, string? newParentId, string? afterSiblingId)
    {
        var body = new Dictionary<string, object?>
        {
            ["newParentId"] = newParentId
        };
        if (afterSiblingId is not null) body["afterSiblingId"] = afterSiblingId;

        var resp = await _http.PutAsJsonAsync($"api/drive/{itemId}/move", body);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    public async Task<JsonElement> UploadFileAsync(string localPath, string? parentId, string? title)
    {
        if (!File.Exists(localPath))
            throw new InvalidOperationException($"File not found: {localPath}");

        using var content = new MultipartFormDataContent();
        await using var fs = File.OpenRead(localPath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMimeType(localPath));
        content.Add(fileContent, "file", Path.GetFileName(localPath));

        var url = $"api/workspaces/{_workspaceId}/drive/upload";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(parentId)) query.Add($"parentId={Uri.EscapeDataString(parentId)}");
        if (!string.IsNullOrEmpty(title)) query.Add($"title={Uri.EscapeDataString(title)}");
        if (query.Count > 0) url += "?" + string.Join("&", query);

        var resp = await _http.PostAsync(url, content);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<string> GetDriveJsonAsync(string itemId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"api/drive/{itemId}/json", ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> ReplaceDriveJsonAsync(string itemId, string jsonContent, CancellationToken ct = default)
    {
        using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync($"api/drive/{itemId}/json", content, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> PatchDriveJsonAsync(string itemId, string patchJsonArray, CancellationToken ct = default)
    {
        using var content = new StringContent(patchJsonArray, Encoding.UTF8, "application/json-patch+json");
        var req = new HttpRequestMessage(HttpMethod.Patch, $"api/drive/{itemId}/json") { Content = content };
        var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<JsonElement> UploadJsonBytesAsync(string fileName, byte[] bytes, string? parentId, string? title, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(fileContent, "file", fileName);

        var url = $"api/workspaces/{_workspaceId}/drive/upload";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(parentId)) query.Add($"parentId={Uri.EscapeDataString(parentId)}");
        if (!string.IsNullOrEmpty(title)) query.Add($"title={Uri.EscapeDataString(title)}");
        if (query.Count > 0) url += "?" + string.Join("&", query);

        var resp = await _http.PostAsync(url, content, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    public async Task<JsonElement> GetRecentDriveItemsAsync(int? limit)
    {
        var url = "api/drive/recent";
        var query = new List<string>();
        if (limit is not null) query.Add($"limit={limit.Value}");
        // Scope to current workspace for relevance.
        query.Add($"workspaceId={_workspaceId}");
        url += "?" + string.Join("&", query);

        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetArchivedDriveItemsAsync()
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/drive/archived");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task RestoreDriveItemAsync(string itemId)
    {
        var resp = await _http.PostAsync($"api/drive/{itemId}/restore", null);
        await EnsureSuccessAsync(resp);
    }

    public async Task<JsonElement> GetStorageUsageAsync()
    {
        var resp = await _http.GetAsync("api/drive/storage/usage");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- SQLite ---

    public async Task<JsonElement> SqliteQueryAsync(
        string itemId, string sql, IReadOnlyList<object?> parameters, int? rowLimit, int? timeoutSeconds,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["sql"] = sql,
            ["parameters"] = parameters,
        };
        if (rowLimit is not null) body["rowLimit"] = rowLimit.Value;
        if (timeoutSeconds is not null) body["timeoutSeconds"] = timeoutSeconds.Value;

        var resp = await _http.PostAsJsonAsync($"api/drive/{itemId}/sqlite/query", body, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    public async Task<JsonElement> SqliteExecAsync(
        string itemId, string sql, IReadOnlyList<object?> parameters, bool allowDdl, int? timeoutSeconds,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["sql"] = sql,
            ["parameters"] = parameters,
            ["allowDdl"] = allowDdl,
        };
        if (timeoutSeconds is not null) body["timeoutSeconds"] = timeoutSeconds.Value;

        var resp = await _http.PostAsJsonAsync($"api/drive/{itemId}/sqlite/exec", body, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    public async Task<JsonElement> SqliteSchemaAsync(string itemId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"api/drive/{itemId}/sqlite/schema", ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    public async Task<JsonElement> CreateSqliteDatabaseAsync(
        string title, string? initialSchemaSql, string? parentId, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["title"] = title };
        if (!string.IsNullOrEmpty(initialSchemaSql)) body["initialSchemaSql"] = initialSchemaSql;
        if (!string.IsNullOrEmpty(parentId)) body["parentId"] = parentId;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/drive/sqlite", body, ct);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    private static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".txt" or ".md" => "text/plain",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".zip" => "application/zip",
        ".sqlite" or ".sqlite3" or ".db" => "application/vnd.sqlite3",
        _ => "application/octet-stream"
    };

    // --- Repositories ---

    public async Task<JsonElement> GetRepositoriesAsync()
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/repositories");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Resolves a repository given by id or by (case-insensitive) name to its id.</summary>
    public async Task<string> ResolveRepositoryIdAsync(string idOrName)
    {
        var result = await GetRepositoriesAsync();
        var repositories = result.TryGetProperty("repositories", out var r) ? r.EnumerateArray().ToList() : [];
        var match = repositories.FirstOrDefault(repo =>
            string.Equals(repo.GetProperty("id").GetString(), idOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(repo.GetProperty("name").GetString(), idOrName, StringComparison.OrdinalIgnoreCase));

        if (match.ValueKind == JsonValueKind.Undefined)
        {
            var known = string.Join(", ", repositories.Select(repo => repo.GetProperty("name").GetString()));
            throw new InvalidOperationException(
                $"Repository '{idOrName}' not found in this workspace. Known repositories: {(known.Length > 0 ? known : "(none)")}");
        }

        return match.GetProperty("id").GetString()!;
    }

    // --- Repository questions ---

    /// <summary>202 <c>{ question, agentName }</c>: a read-only run of the repository's agent looks for the answer.</summary>
    public Task<JsonElement> AskRepositoryQuestionAsync(string repository, string question) =>
        PostAsync($"api/workspaces/{_workspaceId}/repository-questions", new { repository, question });

    public async Task<JsonElement> GetRepositoryQuestionAsync(string questionId)
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/repository-questions/{questionId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetRepositoryQuestionsAsync(int? limit)
    {
        var url = $"api/workspaces/{_workspaceId}/repository-questions" + (limit is { } l ? $"?limit={l}" : "");
        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- Tasks ---

    public async Task<JsonElement> GetTasksAsync(string? status)
    {
        var url = $"api/workspaces/{_workspaceId}/tasks";
        if (!string.IsNullOrEmpty(status)) url += $"?status={Uri.EscapeDataString(status)}";

        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CreateTaskAsync(string title, string? description, string? type, string? repositoryId = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = title };
        if (description is not null) body["description"] = description;
        if (type is not null) body["type"] = type;
        if (repositoryId is not null) body["repositoryId"] = repositoryId;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/tasks", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> AddCommentAsync(string taskId, string text)
    {
        var body = new { text };
        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/comments", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> AddBlockerAsync(string taskId, string reason)
    {
        var body = new { reason };
        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/blocker", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CompleteTaskAsync(string taskId, string? summary, string? commitSha)
    {
        var body = new { summary, commitSha };
        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/done", body);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    public async Task<JsonElement> GetTaskAsync(string taskId)
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// The backend PUT replaces every field, so the current task is read first and only the given
    /// fields are overridden; otherwise omitted fields (repository, due date, description…) are wiped.
    /// </summary>
    public async Task<JsonElement> UpdateTaskAsync(string taskId, string? title, string? description, string? type, string? repositoryId = null)
    {
        var current = await GetTaskAsync(taskId);
        object? Current(string name) =>
            current.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

        var body = new Dictionary<string, object?>
        {
            ["title"] = title ?? Current("title"),
            ["description"] = description ?? Current("description"),
            ["type"] = type ?? Current("type"),
            ["ticketReference"] = Current("ticketReference"),
            ["platform"] = Current("platform"),
            ["dueDate"] = Current("dueDate"),
            ["repositoryId"] = repositoryId ?? Current("repositoryId"),
        };

        var resp = await _http.PutAsJsonAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task DeleteTaskAsync(string taskId)
    {
        var resp = await _http.DeleteAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}");
        await EnsureSuccessAsync(resp);
    }

    public async Task<JsonElement> SetTaskStatusAsync(string taskId, string status)
    {
        var body = new { status };
        var req = new HttpRequestMessage(HttpMethod.Patch, $"api/workspaces/{_workspaceId}/tasks/{taskId}/status")
        {
            Content = JsonContent.Create(body)
        };
        var resp = await _http.SendAsync(req);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    public async Task<JsonElement> AddTaskDriveLinkAsync(string taskId, string driveItemId)
    {
        var resp = await _http.PostAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/drive-links/{driveItemId}", null);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    public async Task RemoveTaskDriveLinkAsync(string taskId, string driveItemId)
    {
        var resp = await _http.DeleteAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/drive-links/{driveItemId}");
        await EnsureSuccessAsync(resp);
    }

    public async Task ResolveTaskBlockerAsync(string taskId)
    {
        var resp = await _http.DeleteAsync($"api/workspaces/{_workspaceId}/tasks/{taskId}/blocker");
        await EnsureSuccessAsync(resp);
    }

    public async Task<JsonElement> SearchTasksAsync(string query, string? status, string? type)
    {
        var url = $"api/tasks/search?query={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrEmpty(status)) url += $"&status={Uri.EscapeDataString(status)}";
        if (!string.IsNullOrEmpty(type)) url += $"&type={Uri.EscapeDataString(type)}";

        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- Schedulers ---

    public async Task<JsonElement> GetSchedulersAsync(bool? active)
    {
        var url = $"api/workspaces/{_workspaceId}/scheduled-prompts";
        if (active is not null) url += $"?active={active.Value.ToString().ToLowerInvariant()}";

        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetSchedulerAsync(string id)
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetSchedulerExecutionsAsync(string id)
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}/executions");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CreateSchedulerAsync(string title, string? prompt, string cron, string? description, string? provider, string agentId, string? workflowId)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["cronExpression"] = cron,
            ["agentId"] = agentId
        };
        if (prompt is not null) body["prompt"] = prompt;
        if (workflowId is not null) body["workflowId"] = workflowId;
        if (description is not null) body["scheduleDescription"] = description;
        if (provider is not null) body["provider"] = provider;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/scheduled-prompts", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateSchedulerAsync(string id, string? title, string? prompt, string? cron, string? description, string? provider, string? agentId, string? workflowId, bool? skipWorkReview = null)
    {
        var body = new Dictionary<string, object?>();
        if (title is not null) body["title"] = title;
        if (prompt is not null) body["prompt"] = prompt;
        if (cron is not null) body["cronExpression"] = cron;
        if (description is not null) body["scheduleDescription"] = description;
        if (provider is not null) body["provider"] = provider;
        if (agentId is not null) body["agentId"] = agentId;
        if (workflowId is not null) body["workflowId"] = workflowId;
        if (skipWorkReview is not null) body["skipWorkReview"] = skipWorkReview;

        var resp = await _http.PutAsJsonAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task DeleteSchedulerAsync(string id)
    {
        var resp = await _http.DeleteAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}");
        await EnsureSuccessAsync(resp);
    }

    public async Task<JsonElement> PauseSchedulerAsync(string id)
    {
        var resp = await _http.PostAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}/pause", null);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> ResumeSchedulerAsync(string id)
    {
        var resp = await _http.PostAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}/resume", null);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> TriggerSchedulerAsync(string id)
    {
        var resp = await _http.PostAsync($"api/workspaces/{_workspaceId}/scheduled-prompts/{id}/trigger", null);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- Workflow executions ---

    public async Task<JsonElement> CompleteWorkflowStepAsync(
        string executionId, string stepId, string outputDriveItemId, IReadOnlyDictionary<string, string>? outputs = null)
    {
        var body = new Dictionary<string, object?> { ["outputDriveItemId"] = outputDriveItemId };
        if (outputs is { Count: > 0 }) body["outputs"] = outputs;
        var resp = await _http.PostAsJsonAsync($"api/workflow-executions/{executionId}/steps/{stepId}/complete", body);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    public async Task<JsonElement> FailWorkflowStepAsync(string executionId, string stepId, string reason)
    {
        var body = new { reason };
        var resp = await _http.PostAsJsonAsync($"api/workflow-executions/{executionId}/steps/{stepId}/fail", body);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    // --- Workflows ---

    public async Task<JsonElement> ListWorkflowsAsync()
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/workflows");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetWorkflowAsync(string workflowId)
    {
        var resp = await _http.GetAsync($"api/workflows/{workflowId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> CreateWorkflowAsync(string name, string? description, int? defaultStepTimeoutMinutes)
    {
        var body = new Dictionary<string, object?> { ["name"] = name };
        if (description is not null) body["description"] = description;
        if (defaultStepTimeoutMinutes is not null) body["defaultStepTimeoutMinutes"] = defaultStepTimeoutMinutes;

        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/workflows", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateWorkflowAsync(string workflowId, string? name, string? description, int? defaultStepTimeoutMinutes, bool clearDefaultStepTimeout, bool? skipWorkReview = null)
    {
        var body = new Dictionary<string, object?>();
        if (name is not null) body["name"] = name;
        if (description is not null) body["description"] = description;
        if (defaultStepTimeoutMinutes is not null) body["defaultStepTimeoutMinutes"] = defaultStepTimeoutMinutes;
        if (clearDefaultStepTimeout) body["clearDefaultStepTimeout"] = true;
        if (skipWorkReview is not null) body["skipWorkReview"] = skipWorkReview;

        var req = new HttpRequestMessage(HttpMethod.Patch, $"api/workflows/{workflowId}")
        {
            Content = JsonContent.Create(body)
        };
        var resp = await _http.SendAsync(req);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task DeleteWorkflowAsync(string workflowId)
    {
        var resp = await _http.DeleteAsync($"api/workflows/{workflowId}");
        await EnsureSuccessAsync(resp);
    }

    public sealed record StepInputArg(string Kind, string Id);

    public async Task<JsonElement> AddWorkflowStepAsync(
        string workflowId, string name, string prompt, IEnumerable<StepInputArg> inputs,
        string outputPath, int? timeoutMinutes, string? outputDriveItemId,
        string? provider = null, string? model = null, bool completesOnExit = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["prompt"] = prompt,
            ["inputs"] = inputs.Select(SerializeInput).ToArray(),
            ["outputPath"] = outputPath
        };
        if (timeoutMinutes is not null) body["timeoutMinutes"] = timeoutMinutes;
        if (outputDriveItemId is not null) body["outputDriveItemId"] = outputDriveItemId;
        if (provider is not null) body["provider"] = provider;
        if (model is not null) body["model"] = model;
        // The step ends when its CLI exits, its last answer is the result (SideHub doc 61).
        if (completesOnExit) body["completesOnExit"] = true;

        var resp = await _http.PostAsJsonAsync($"api/workflows/{workflowId}/steps", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> UpdateWorkflowStepAsync(
        string workflowId, string stepId,
        string? name, string? prompt, IEnumerable<StepInputArg>? inputs,
        string? outputPath, int? timeoutMinutes, string? outputDriveItemId, bool clearOutputDriveItemId)
    {
        var body = new Dictionary<string, object?>();
        if (name is not null) body["name"] = name;
        if (prompt is not null) body["prompt"] = prompt;
        if (inputs is not null) body["inputs"] = inputs.Select(SerializeInput).ToArray();
        if (outputPath is not null) body["outputPath"] = outputPath;
        if (timeoutMinutes is not null) body["timeoutMinutes"] = timeoutMinutes;
        if (outputDriveItemId is not null) body["outputDriveItemId"] = outputDriveItemId;
        if (clearOutputDriveItemId) body["clearOutputDriveItemId"] = true;

        var req = new HttpRequestMessage(HttpMethod.Patch, $"api/workflows/{workflowId}/steps/{stepId}")
        {
            Content = JsonContent.Create(body)
        };
        var resp = await _http.SendAsync(req);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> RemoveWorkflowStepAsync(string workflowId, string stepId)
    {
        var resp = await _http.DeleteAsync($"api/workflows/{workflowId}/steps/{stepId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> ReorderWorkflowStepsAsync(string workflowId, IEnumerable<string> orderedStepIds)
    {
        var body = new { orderedStepIds = orderedStepIds.ToArray() };
        var resp = await _http.PostAsJsonAsync($"api/workflows/{workflowId}/steps/reorder", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> RunWorkflowAsync(
        string workflowId, string agentId, string provider, IReadOnlyDictionary<string, string>? inputs = null,
        IReadOnlyDictionary<string, List<string>>? roleHolders = null)
    {
        var body = new Dictionary<string, object?> { ["agentId"] = agentId, ["provider"] = provider };
        if (inputs is { Count: > 0 }) body["inputs"] = inputs;
        // Roles not given keep the workflow's default holders.
        if (roleHolders is { Count: > 0 }) body["roleHolders"] = roleHolders;
        var resp = await _http.PostAsJsonAsync($"api/workflows/{workflowId}/run", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetWorkflowExecutionAsync(string executionId)
    {
        var resp = await _http.GetAsync($"api/workflow-executions/{executionId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetWorkflowExecutionsAsync(string workflowId, int? limit)
    {
        var url = $"api/workflows/{workflowId}/executions";
        if (limit is not null) url += $"?limit={limit.Value}";
        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetWorkflowExecutionRunsAsync(string executionId)
    {
        var resp = await _http.GetAsync($"api/workflow-executions/{executionId}/runs");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> ExportWorkflowAsync(string workflowId)
    {
        var resp = await _http.GetAsync($"api/workflows/{workflowId}/export");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Creates a workflow in the workspace from a <c>sidehub.workflow/v1</c> document (what export writes).</summary>
    public async Task<JsonElement> ImportWorkflowAsync(JsonElement document, string? name)
    {
        var body = new Dictionary<string, object?> { ["document"] = document };
        if (name is not null) body["name"] = name;
        var resp = await _http.PostAsJsonAsync($"api/workspaces/{_workspaceId}/workflows/import", body);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> DuplicateWorkflowAsync(string workflowId, string name)
    {
        var resp = await _http.PostAsJsonAsync($"api/workflows/{workflowId}/duplicate", new { name });
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- Workflow execution control (people, or a terminal they opened) ---

    public Task<JsonElement> CancelWorkflowExecutionAsync(string executionId, string? reason) =>
        PostAsync($"api/workflow-executions/{executionId}/cancel", new { reason });

    public Task<JsonElement> RetryWorkflowFromStepAsync(string executionId, string stepId) =>
        PostAsync($"api/workflow-executions/{executionId}/steps/{stepId}/retry", new { });

    public Task<JsonElement> ManualCompleteWorkflowStepAsync(string executionId, string stepId, string outputDriveItemId) =>
        PostAsync($"api/workflow-executions/{executionId}/steps/{stepId}/manual-complete", new { outputDriveItemId });

    public Task<JsonElement> ManualFailWorkflowStepAsync(string executionId, string stepId, string reason) =>
        PostAsync($"api/workflow-executions/{executionId}/steps/{stepId}/manual-fail", new { reason });

    public Task<JsonElement> ValidateWorkflowStepAsync(string executionId, string stepId) =>
        PostAsync($"api/workflow-executions/{executionId}/steps/{stepId}/validate", new { });

    /// <summary>Decides an approval step: <paramref name="decision"/> is <c>approve</c>, <c>reject</c> or <c>request-changes</c>.</summary>
    public Task<JsonElement> DecideWorkflowStepAsync(string executionId, string stepId, string decision, string? comment) =>
        PostAsync($"api/workflow-executions/{executionId}/steps/{stepId}/{decision}", new { comment });

    /// <summary>Decides a tool call a step stopped for: <paramref name="decision"/> is <c>approve</c> or <c>deny</c>.</summary>
    public Task<JsonElement> DecideToolApprovalAsync(string toolApprovalId, string decision, string? comment) =>
        PostAsync($"api/tool-approvals/{toolApprovalId}/{decision}", new { comment });

    // --- Work reviews ---

    public record ReviewFilter(string? Status, string? TaskId, string? RunId, int? Take);

    public async Task<JsonElement> GetReviewsAsync(ReviewFilter filter)
    {
        var query = new List<string>();
        if (filter.Status is not null) query.Add($"status={Uri.EscapeDataString(filter.Status)}");
        if (filter.TaskId is not null) query.Add($"taskId={Uri.EscapeDataString(filter.TaskId)}");
        if (filter.RunId is not null) query.Add($"runId={Uri.EscapeDataString(filter.RunId)}");
        if (filter.Take is { } take) query.Add($"take={take}");

        var url = $"api/workspaces/{_workspaceId}/reviews" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetReviewAsync(string reviewId)
    {
        var resp = await _http.GetAsync($"api/reviews/{reviewId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The diff of one round (or of one file of it), read live from the agent: plain text, never stored.</summary>
    public async Task<string> GetReviewDiffAsync(string reviewId, int round, string? path)
    {
        var url = $"api/reviews/{reviewId}/diff?round={round}";
        if (!string.IsNullOrEmpty(path)) url += $"&path={Uri.EscapeDataString(path)}";
        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary><paramref name="action"/>: validate, dismiss or request-fix (the comment is then required).</summary>
    public Task<JsonElement> DecideReviewAsync(string reviewId, string action, string? comment) =>
        PostAsync($"api/reviews/{reviewId}/decision", new { action, comment });

    public Task<JsonElement> RequestPeerReviewAsync(string reviewId, int round, string provider, string? model) =>
        PostAsync($"api/reviews/{reviewId}/peer-reviews", new { round, provider, model });

    // --- Agent task queue ---

    public async Task<JsonElement> GetTaskQueueAsync(string agentId)
    {
        var resp = await _http.GetAsync($"api/agents/{agentId}/task-queue");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <param name="prompt">Null: SideHub uses the task's own first message.</param>
    public Task<JsonElement> EnqueueTaskAsync(string agentId, string taskId, string cli, string? prompt, bool commit) =>
        PostAsync($"api/agents/{agentId}/task-queue/items", new { taskId, cli, prompt, commit });

    public async Task<JsonElement> RemoveQueuedTaskAsync(string agentId, string itemId)
    {
        var resp = await _http.DeleteAsync($"api/agents/{agentId}/task-queue/items/{itemId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> MoveQueuedTaskAsync(string agentId, string itemId, int index)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"api/agents/{agentId}/task-queue/items/{itemId}/position")
        {
            Content = JsonContent.Create(new { index })
        };
        var resp = await _http.SendAsync(req);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Moves a waiting task to the end of another agent's queue (same workspace and repository, doc 67); the target's
    /// queue comes back. <paramref name="cli"/> null keeps the task's.
    /// </summary>
    public Task<JsonElement> MoveQueuedTaskToAgentAsync(string agentId, string itemId, string targetAgentId, string? cli) =>
        PostAsync($"api/agents/{agentId}/task-queue/items/{itemId}/move-to-agent", new { targetAgentId, cli });

    /// <summary><paramref name="action"/>: pause, resume or skip.</summary>
    public Task<JsonElement> ChangeTaskQueueAsync(string agentId, string action) =>
        PostAsync($"api/agents/{agentId}/task-queue/{action}", null);

    // --- Outgoing commits ---

    public async Task<JsonElement> GetOutgoingCommitsAsync(string agentId)
    {
        var resp = await _http.GetAsync($"api/agents/{agentId}/outgoing");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Pushes the agent's branch up to <paramref name="head"/>, the commit shown: the agent refuses if it moved since.</summary>
    public Task<JsonElement> PushOutgoingCommitsAsync(string agentId, string head) =>
        PostAsync($"api/agents/{agentId}/push", new { head });

    // --- Workspace secrets ---

    /// <summary>Names and usage of the workspace's secrets: the API never returns a value.</summary>
    public async Task<JsonElement> GetSecretsAsync()
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/secrets");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- Workspace MCP servers ---

    public async Task<JsonElement> GetMcpServersAsync()
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/mcp-servers");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Creates the server or replaces its definition (<paramref name="definition"/> is the request body as is).</summary>
    public async Task<(JsonElement Server, bool Created)> SetMcpServerAsync(string name, object definition)
    {
        var resp = await _http.PutAsJsonAsync($"api/workspaces/{_workspaceId}/mcp-servers/{Uri.EscapeDataString(name)}", definition);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(), resp.StatusCode == System.Net.HttpStatusCode.Created);
    }

    public async Task DeleteMcpServerAsync(string name)
    {
        var resp = await _http.DeleteAsync($"api/workspaces/{_workspaceId}/mcp-servers/{Uri.EscapeDataString(name)}");
        await EnsureSuccessAsync(resp);
    }

    // --- Workflow event triggers ---

    public async Task<JsonElement> GetWorkflowEventTriggersAsync(string workflowId)
    {
        var resp = await _http.GetAsync($"api/workflows/{workflowId}/event-triggers");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public Task<JsonElement> CreateWorkflowEventTriggerAsync(
        string workflowId, string type, object? filter, string agentId, string? provider, IReadOnlyDictionary<string, string>? inputMapping) =>
        PostAsync($"api/workflows/{workflowId}/event-triggers", new { type, filter, agentId, provider, inputMapping });

    /// <summary>Null fields are kept; <paramref name="filter"/> and <paramref name="inputMapping"/> replace the whole value.</summary>
    public async Task<JsonElement> UpdateWorkflowEventTriggerAsync(
        string workflowId, string triggerId, object? filter, string? agentId, string? provider,
        IReadOnlyDictionary<string, string>? inputMapping, bool? isActive)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"api/workflows/{workflowId}/event-triggers/{triggerId}")
        {
            Content = JsonContent.Create(new { filter, agentId, provider, inputMapping, isActive })
        };
        var resp = await _http.SendAsync(req);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task DeleteWorkflowEventTriggerAsync(string workflowId, string triggerId)
    {
        var resp = await _http.DeleteAsync($"api/workflows/{workflowId}/event-triggers/{triggerId}");
        await EnsureSuccessAsync(resp);
    }

    // --- Runs ---

    public record RunFilter(string? Status, string? Runtime, string? AgentId, DateTime? From, DateTime? To, int Page, int PageSize);

    public async Task<JsonElement> GetRunsAsync(RunFilter filter)
    {
        var query = new List<string> { $"page={filter.Page}", $"pageSize={filter.PageSize}" };
        if (filter.Status is not null) query.Add($"status={Uri.EscapeDataString(filter.Status)}");
        if (filter.Runtime is not null) query.Add($"runtime={Uri.EscapeDataString(filter.Runtime)}");
        if (filter.AgentId is not null) query.Add($"agentId={Uri.EscapeDataString(filter.AgentId)}");
        if (filter.From is { } from) query.Add($"from={Uri.EscapeDataString(from.ToString("O"))}");
        if (filter.To is { } to) query.Add($"to={Uri.EscapeDataString(to.ToString("O"))}");

        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/runs?{string.Join("&", query)}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetRunAsync(string runId)
    {
        var resp = await _http.GetAsync($"api/workspaces/{_workspaceId}/runs/{runId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // --- Cockpit and identity ---

    public async Task<JsonElement> GetCockpitNowAsync()
    {
        var resp = await _http.GetAsync($"api/cockpit/now?workspaceId={_workspaceId}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Usage and costs of this workspace's CLI sessions in <c>[from, to)</c>, grouped by <paramref name="groupBy"/>.</summary>
    public async Task<JsonElement> GetCockpitUsageAsync(DateTime from, DateTime to, string groupBy, string? split, string? tz)
    {
        var url = $"api/cockpit/usage?{RangeQuery(from, to)}&groupBy={Uri.EscapeDataString(groupBy)}";
        if (split is not null) url += $"&split={Uri.EscapeDataString(split)}";
        if (tz is not null) url += $"&tz={Uri.EscapeDataString(tz)}";
        var resp = await _http.GetAsync(url);
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> GetCockpitReliabilityAsync(DateTime from, DateTime to)
    {
        var resp = await _http.GetAsync($"api/cockpit/reliability?{RangeQuery(from, to)}");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private string RangeQuery(DateTime from, DateTime to) =>
        $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&workspaceId={_workspaceId}";

    public async Task<JsonElement> GetIdentityAsync()
    {
        var resp = await _http.GetAsync("api/agent/me");
        await EnsureSuccessAsync(resp);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Dictionary<string, object?> SerializeInput(StepInputArg input) => input.Kind switch
    {
        "drive" => new() { ["type"] = "drive_document", ["driveItemId"] = input.Id },
        "step" => new() { ["type"] = "previous_step_output", ["stepId"] = input.Id },
        _ => throw new InvalidOperationException($"Unknown input kind: {input.Kind}")
    };

    /// <summary>POSTs <paramref name="body"/> as JSON (none when null) and returns the answer; default when it is empty.</summary>
    private async Task<JsonElement> PostAsync(string url, object? body)
    {
        var resp = body is null ? await _http.PostAsync(url, null) : await _http.PostAsJsonAsync(url, body);
        await EnsureSuccessAsync(resp);
        var content = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(content) ? default : JsonSerializer.Deserialize<JsonElement>(content);
    }

    public void Dispose() => _http.Dispose();

    public static string Serialize(JsonElement element) =>
        JsonSerializer.Serialize(element, JsonOptions);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        string? serverMessage = null;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                serverMessage = msg.GetString();
            else if (json.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                serverMessage = err.GetString();
        }
        catch { /* body is not JSON */ }

        var message = (int)response.StatusCode switch
        {
            // Run tokens expire with the run's timeout and are revoked when it ends: tell the LLM
            // not to retry, every later call will fail the same way.
            401 => "Authentication failed (HTTP 401): the SideHub token of this session is expired or revoked"
                + (serverMessage is null ? "" : $" ({serverMessage})")
                + ". Do not retry sidehub-cli commands: they will all fail. Report the problem and stop using sidehub-cli.",
            // Most refusals depend on who the token speaks for (agent, run, or the user of a terminal they opened).
            403 => (serverMessage ?? "Access denied.") + " To see who this session acts for: sidehub-cli whoami.",
            404 => serverMessage ?? "Resource not found.",
            _ => serverMessage ?? $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
        };

        throw new HttpRequestException(message, null, response.StatusCode);
    }
}
