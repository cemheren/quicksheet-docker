using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickSheetDocker;

/// <summary>
/// QuickSheet extension: Docker container monitoring via the Docker Engine API.
/// Communicates over /var/run/docker.sock (Linux/macOS) or named pipe (Windows).
/// Zero NuGet dependencies — uses built-in BCL only.
/// 
/// Cell prefixes:
///   docker:              → list running containers (name, image, status, ports)
///   docker: all          → list all containers (including stopped)
///   docker: stats        → CPU/memory usage of running containers
///   docker: images       → list local images (repo, tag, size)
///   docker: inspect NAME → detailed info about a specific container
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        // QuickSheet extension protocol: read JSON-lines from stdin, write to stdout
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonSerializer.Deserialize<ExtensionRequest>(line);
                if (request == null) continue;

                var cellValue = request.Value?.Trim() ?? "";
                var result = await HandleCommand(cellValue);
                var response = new ExtensionResponse
                {
                    Id = request.Id,
                    Result = result
                };
                var json = JsonSerializer.Serialize(response);
                Console.WriteLine(json);
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                // Write error as response
                var errorResponse = new { id = "", result = $"Error: {ex.Message}" };
                Console.WriteLine(JsonSerializer.Serialize(errorResponse));
                Console.Out.Flush();
            }
        }

        return 0;
    }

    static async Task<string> HandleCommand(string command)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subcommand = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        return subcommand switch
        {
            "" or "containers" => await ListContainers(false),
            "all" => await ListContainers(true),
            "stats" => await GetStats(),
            "images" => await ListImages(),
            "inspect" => await InspectContainer(arg),
            _ => $"Unknown: {subcommand}. Try: docker: | docker: all | docker: stats | docker: images | docker: inspect <name>"
        };
    }

    static HttpClient CreateDockerClient()
    {
        var socketPath = GetDockerSocket();
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(socketPath);
                await socket.ConnectAsync(endpoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    static string GetDockerSocket()
    {
        // Check DOCKER_HOST env var first
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrEmpty(dockerHost) && dockerHost.StartsWith("unix://"))
            return dockerHost["unix://".Length..];

        // Default paths
        if (OperatingSystem.IsWindows())
            return @"\\.\pipe\docker_engine";

        // Linux/macOS: check user socket first, then system socket
        var userSocket = $"/run/user/{Environment.GetEnvironmentVariable("UID") ?? "1000"}/docker.sock";
        if (File.Exists(userSocket)) return userSocket;
        return "/var/run/docker.sock";
    }

    static async Task<string> ListContainers(bool showAll)
    {
        using var client = CreateDockerClient();
        var url = showAll ? "/containers/json?all=true" : "/containers/json";

        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return $"Docker API error: {response.StatusCode}";

        var json = await response.Content.ReadAsStringAsync();
        var containers = JsonSerializer.Deserialize<List<ContainerInfo>>(json);
        if (containers == null || containers.Count == 0)
            return showAll ? "No containers found" : "No running containers";

        var sb = new StringBuilder();
        sb.AppendLine($"{'{'}{containers.Count} container{(containers.Count != 1 ? "s" : "")}{'}'}");
        sb.AppendLine("NAME | IMAGE | STATUS | PORTS");
        sb.AppendLine("─────┼───────┼────────┼──────");

        foreach (var c in containers.Take(20))
        {
            var name = c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.Id?[..12] ?? "?";
            var image = TruncateString(c.Image ?? "?", 20);
            var status = TruncateString(c.Status ?? c.State ?? "?", 18);
            var ports = FormatPorts(c.Ports);
            sb.AppendLine($"{name} | {image} | {status} | {ports}");
        }

        if (containers.Count > 20)
            sb.AppendLine($"... and {containers.Count - 20} more");

        return sb.ToString().TrimEnd();
    }

    static async Task<string> GetStats()
    {
        using var client = CreateDockerClient();

        // First get running containers
        var listResponse = await client.GetAsync("/containers/json");
        if (!listResponse.IsSuccessStatusCode)
            return $"Docker API error: {listResponse.StatusCode}";

        var listJson = await listResponse.Content.ReadAsStringAsync();
        var containers = JsonSerializer.Deserialize<List<ContainerInfo>>(listJson);
        if (containers == null || containers.Count == 0)
            return "No running containers";

        var sb = new StringBuilder();
        sb.AppendLine("NAME | CPU% | MEM | MEM%");
        sb.AppendLine("─────┼──────┼─────┼─────");

        // Get stats for each container (one-shot, no stream)
        foreach (var c in containers.Take(10))
        {
            var name = c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.Id?[..12] ?? "?";
            try
            {
                var statsResponse = await client.GetAsync($"/containers/{c.Id}/stats?stream=false");
                if (!statsResponse.IsSuccessStatusCode)
                {
                    sb.AppendLine($"{name} | err | - | -");
                    continue;
                }

                var statsJson = await statsResponse.Content.ReadAsStringAsync();
                var stats = JsonSerializer.Deserialize<ContainerStats>(statsJson);
                if (stats == null)
                {
                    sb.AppendLine($"{name} | ? | - | -");
                    continue;
                }

                var cpuPct = CalculateCpuPercent(stats);
                var memUsage = stats.MemoryStats?.Usage ?? 0;
                var memLimit = stats.MemoryStats?.Limit ?? 1;
                var memPct = memLimit > 0 ? (double)memUsage / memLimit * 100 : 0;
                var memStr = FormatBytes(memUsage);

                sb.AppendLine($"{TruncateString(name, 16)} | {cpuPct:F1}% | {memStr} | {memPct:F1}%");
            }
            catch
            {
                sb.AppendLine($"{TruncateString(name, 16)} | err | - | -");
            }
        }

        if (containers.Count > 10)
            sb.AppendLine($"... and {containers.Count - 10} more");

        return sb.ToString().TrimEnd();
    }

    static async Task<string> ListImages()
    {
        using var client = CreateDockerClient();
        var response = await client.GetAsync("/images/json");
        if (!response.IsSuccessStatusCode)
            return $"Docker API error: {response.StatusCode}";

        var json = await response.Content.ReadAsStringAsync();
        var images = JsonSerializer.Deserialize<List<ImageInfo>>(json);
        if (images == null || images.Count == 0)
            return "No local images";

        var sb = new StringBuilder();
        sb.AppendLine($"{'{'}{images.Count} image{(images.Count != 1 ? "s" : "")}{'}'}");
        sb.AppendLine("REPOSITORY:TAG | SIZE");
        sb.AppendLine("───────────────┼─────");

        foreach (var img in images.Take(20))
        {
            var repoTag = img.RepoTags?.FirstOrDefault() ?? "<none>:<none>";
            var size = FormatBytes(img.Size);
            sb.AppendLine($"{TruncateString(repoTag, 35)} | {size}");
        }

        if (images.Count > 20)
            sb.AppendLine($"... and {images.Count - 20} more");

        return sb.ToString().TrimEnd();
    }

    static async Task<string> InspectContainer(string nameOrId)
    {
        if (string.IsNullOrEmpty(nameOrId))
            return "Usage: docker: inspect <container-name-or-id>";

        using var client = CreateDockerClient();
        var response = await client.GetAsync($"/containers/{nameOrId}/json");
        if (!response.IsSuccessStatusCode)
            return response.StatusCode == HttpStatusCode.NotFound
                ? $"Container '{nameOrId}' not found"
                : $"Docker API error: {response.StatusCode}";

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        sb.AppendLine($"Container: {nameOrId}");

        if (root.TryGetProperty("State", out var state))
        {
            sb.AppendLine($"State: {GetString(state, "Status")} (PID: {GetString(state, "Pid")})");
            sb.AppendLine($"Started: {GetString(state, "StartedAt")}");
        }

        if (root.TryGetProperty("Config", out var config))
        {
            sb.AppendLine($"Image: {GetString(config, "Image")}");
            if (config.TryGetProperty("Env", out var env) && env.GetArrayLength() > 0)
            {
                var envCount = env.GetArrayLength();
                sb.AppendLine($"Env vars: {envCount}");
            }
        }

        if (root.TryGetProperty("NetworkSettings", out var net) &&
            net.TryGetProperty("Networks", out var networks))
        {
            foreach (var network in networks.EnumerateObject().Take(3))
            {
                var ip = "";
                if (network.Value.TryGetProperty("IPAddress", out var ipProp))
                    ip = ipProp.GetString() ?? "";
                sb.AppendLine($"Network: {network.Name} ({ip})");
            }
        }

        if (root.TryGetProperty("Mounts", out var mounts) && mounts.GetArrayLength() > 0)
        {
            sb.AppendLine($"Mounts: {mounts.GetArrayLength()}");
        }

        return sb.ToString().TrimEnd();
    }

    // --- Helper methods ---

    static double CalculateCpuPercent(ContainerStats stats)
    {
        var cpuDelta = (stats.CpuStats?.CpuUsage?.TotalUsage ?? 0) -
                       (stats.PrecpuStats?.CpuUsage?.TotalUsage ?? 0);
        var systemDelta = (stats.CpuStats?.SystemCpuUsage ?? 0) -
                          (stats.PrecpuStats?.SystemCpuUsage ?? 0);
        var numCpus = stats.CpuStats?.OnlineCpus ?? 1;

        if (systemDelta > 0 && cpuDelta > 0)
            return (double)cpuDelta / systemDelta * numCpus * 100.0;
        return 0;
    }

    static string FormatPorts(List<PortInfo>? ports)
    {
        if (ports == null || ports.Count == 0) return "-";
        var mapped = ports
            .Where(p => p.PublicPort > 0)
            .Select(p => $"{p.PublicPort}→{p.PrivatePort}")
            .Take(3);
        var result = string.Join(", ", mapped);
        return string.IsNullOrEmpty(result) ? $"{ports[0].PrivatePort}/{ports[0].Type}" : result;
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0}KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1}GB";
    }

    static string TruncateString(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var val) ? val.ToString() : "?";
}

// --- JSON models ---

class ExtensionRequest
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

class ExtensionResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
}

class ContainerInfo
{
    [JsonPropertyName("Id")] public string? Id { get; set; }
    [JsonPropertyName("Names")] public List<string>? Names { get; set; }
    [JsonPropertyName("Image")] public string? Image { get; set; }
    [JsonPropertyName("State")] public string? State { get; set; }
    [JsonPropertyName("Status")] public string? Status { get; set; }
    [JsonPropertyName("Ports")] public List<PortInfo>? Ports { get; set; }
}

class PortInfo
{
    [JsonPropertyName("PrivatePort")] public int PrivatePort { get; set; }
    [JsonPropertyName("PublicPort")] public int PublicPort { get; set; }
    [JsonPropertyName("Type")] public string? Type { get; set; }
}

class ImageInfo
{
    [JsonPropertyName("RepoTags")] public List<string>? RepoTags { get; set; }
    [JsonPropertyName("Size")] public long Size { get; set; }
}

class ContainerStats
{
    [JsonPropertyName("cpu_stats")] public CpuStatsInfo? CpuStats { get; set; }
    [JsonPropertyName("precpu_stats")] public CpuStatsInfo? PrecpuStats { get; set; }
    [JsonPropertyName("memory_stats")] public MemoryStatsInfo? MemoryStats { get; set; }
}

class CpuStatsInfo
{
    [JsonPropertyName("cpu_usage")] public CpuUsageInfo? CpuUsage { get; set; }
    [JsonPropertyName("system_cpu_usage")] public long SystemCpuUsage { get; set; }
    [JsonPropertyName("online_cpus")] public int OnlineCpus { get; set; }
}

class CpuUsageInfo
{
    [JsonPropertyName("total_usage")] public long TotalUsage { get; set; }
}

class MemoryStatsInfo
{
    [JsonPropertyName("usage")] public long Usage { get; set; }
    [JsonPropertyName("limit")] public long Limit { get; set; }
}
