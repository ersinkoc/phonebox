using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>
/// Google Antigravity integration: PreToolUse and Stop hooks in ~/.gemini/config/hooks.json post
/// tool permission prompts and turn completion events to the agent.
/// </summary>
public static class AntigravityHook
{
    public const string PreToolUseEndpoint = "/api/hooks/antigravity/permission";
    public const string StopEndpoint = "/api/hooks/antigravity/stop";

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string HooksConfigPath = Path.Combine(Home, ".gemini", "config", "hooks.json");

    /// <summary>
    /// Handles one PreToolUse hook call; returns Antigravity decision payload object.
    /// </summary>
    public static async Task<object> HandlePreToolUseAsync(
        JsonElement input,
        ConfigStore cfg,
        ApprovalStore approvals,
        SessionMonitor sessions,
        CancellationToken aborted)
    {
        var c = cfg.Get().Claude;
        if (!c.PhoneApprovals || (c.OnlyWhenAway && Idle.Seconds() < 60))
        {
            // Default to allow so Antigravity falls back to desktop terminal prompt
            return new { decision = "allow" };
        }

        var toolCall = input.TryGetProperty("toolCall", out var tc) && tc.ValueKind == JsonValueKind.Object ? tc : default;
        var toolName = (toolCall.ValueKind == JsonValueKind.Object ? Str(toolCall, "name") : null) ?? Str(input, "tool_name") ?? "a tool";
        var args = (toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("args", out var a)) ? a : (input.TryGetProperty("tool_input", out var ti) ? ti : default);

        var cwd = Str(input, "cwd") ?? "";
        var conversationId = Str(input, "conversationId") ?? Str(input, "session_id") ?? "";
        var session = sessions.Snapshot().FirstOrDefault(s => s.Id == "antigravity:" + conversationId);
        var project = session?.Name ?? ClaudeHook.ProjectName(cwd);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var approval = new Approval(
            ApprovalStore.NewId(),
            "Antigravity",
            conversationId,
            project,
            FormatToolTitle(toolName),
            FormatDetail(toolName, args),
            args.ValueKind == JsonValueKind.Object ? Str(args, "description") : null,
            now,
            now + Math.Clamp(c.TimeoutSec, 10, 3600) * 1000L,
            "permission");

        var decision = await approvals.AskAsync(approval, aborted);

        if (decision is null or "skip")
        {
            // Timeout or unpaired phone: fall back to desktop prompt
            return new { decision = "allow" };
        }

        if (decision == "allow")
        {
            return new { decision = "auto_approve" };
        }

        return new
        {
            decision = "deny",
            reason = "Denied from PhoneBox panel."
        };
    }

    /// <summary>
    /// Handles one Stop hook call; pushes notification to PhoneBox panel when Antigravity finishes turn.
    /// </summary>
    public static object HandleStop(JsonElement input, PushStore push)
    {
        var conversationId = Str(input, "conversationId") ?? "";
        var cwd = Str(input, "cwd") ?? "";
        var project = ClaudeHook.ProjectName(cwd);
        var reason = Str(input, "terminationReason") ?? "NO_TOOL_CALL";

        var text = string.IsNullOrWhiteSpace(project)
            ? "Antigravity is waiting for your next prompt"
            : $"Antigravity is waiting for your next prompt in {project}";

        push.Emit("notify", JsonSerializer.SerializeToElement(new
        {
            title = "Antigravity",
            text,
            level = "info",
            page = "ai",
            reason
        }));

        return new { decision = "stop" };
    }

    private static string FormatToolTitle(string tool) => tool switch
    {
        "run_command" => "Run a command",
        "write_to_file" => "Write a file",
        "replace_file_content" or "multi_replace_file_content" => "Edit a file",
        "read_file" => "Read a file",
        _ when tool.StartsWith("browser_") => "Browser " + tool[8..].Replace('_', ' '),
        _ when tool.StartsWith("mcp_") => "Use MCP " + tool[4..].Replace('_', ' '),
        _ => "Use " + tool
    };

    private static string FormatDetail(string tool, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        string? s = tool switch
        {
            "run_command" => Str(args, "CommandLine") ?? Str(args, "command"),
            "write_to_file" or "replace_file_content" or "multi_replace_file_content" or "read_file" =>
                Str(args, "Path") ?? Str(args, "file_path") ?? Str(args, "TargetFile"),
            _ => null
        };

        if (tool == "write_to_file" && Str(args, "Content") is { } content)
        {
            s = (s is not null ? s + "\n\n" : "") + Head(content, 12);
        }
        else if (tool == "replace_file_content" && Str(args, "Replacement") is { } rep)
        {
            s = (s is not null ? s + "\n\n" : "") + Head(rep, 12);
        }

        s ??= JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
        return s.Length > 3000 ? s[..3000] + "…" : s;
    }

    private static string Head(string text, int lines)
    {
        var l = text.Split('\n');
        return string.Join('\n', l.Take(lines)) + (l.Length > lines ? $"\n… ({l.Length - lines} more lines)" : "");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------------------------------------------------------------- hooks.json configuration

    public static string HookCommand(int port, string endpoint)
    {
        var curl = OperatingSystem.IsWindows() ? "curl.exe" : "curl";
        return $"{curl} -s --max-time 3600 -X POST http://127.0.0.1:{port}{endpoint} -H \"Content-Type: application/json\" --data-binary \"@-\"";
    }

    public static bool Installed()
    {
        try
        {
            if (!File.Exists(HooksConfigPath)) return false;
            var root = JsonNode.Parse(File.ReadAllText(HooksConfigPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            return root?["phonebox_bridge"] is not null;
        }
        catch { return false; }
    }

    public static string? Configure(bool install, int port, int timeoutSec)
    {
        try
        {
            JsonObject root;
            if (File.Exists(HooksConfigPath))
            {
                var text = File.ReadAllText(HooksConfigPath);
                root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                       ?? new JsonObject();
                File.WriteAllText(HooksConfigPath + ".phonebox.bak", text);
            }
            else
            {
                root = new JsonObject();
            }

            root.Remove("phonebox_bridge");

            if (install)
            {
                var bridge = new JsonObject
                {
                    ["PreToolUse"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["matcher"] = "run_command|write_to_file|replace_file_content|multi_replace_file_content|browser_.*",
                            ["hooks"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "command",
                                    ["command"] = HookCommand(port, PreToolUseEndpoint),
                                    ["timeout"] = Math.Clamp(timeoutSec, 10, 3600) + 15
                                }
                            }
                        }
                    },
                    ["Stop"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = HookCommand(port, StopEndpoint),
                            ["timeout"] = 10
                        }
                    }
                };
                root["phonebox_bridge"] = bridge;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(HooksConfigPath)!);
            var tmp = HooksConfigPath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, HooksConfigPath, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
