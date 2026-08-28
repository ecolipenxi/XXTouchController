using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using XXTouchController.Models;

namespace XXTouchController.Services;

public sealed class OpenAiErrorAnalysisService
{
    private const string DefaultEndpoint = "https://api.openai.com/v1/responses";
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };
    private readonly HttpClient _client;
    private readonly string _endpoint;

    private static readonly string Instructions = """
        Bạn là kỹ sư chẩn đoán lỗi cho XXTouch Controller và Lua chạy trên iPhone.
        Chỉ phân tích dữ liệu người dùng cung cấp và đề xuất cách khắc phục.
        Tuyệt đối không ra lệnh điều khiển thiết bị, không yêu cầu Start/Stop/Wake/Sleep,
        không tạo lệnh shell/PowerShell và không giả vờ đã thực hiện thay đổi.
        Không được suy đoán bằng chứng không có trong log, Lua hoặc ảnh.
        Nếu sửa Lua, trả về toàn bộ file Lua đã sửa trong fixed_lua; giữ hành vi cũ tối đa
        và chỉ sửa phần liên quan. Nếu chưa đủ bằng chứng để sửa an toàn, để fixed_lua rỗng
        và nêu dữ liệu cần thu thập trong steps. Trả lời tiếng Việt rõ ràng, ngắn gọn.
        """;

    public OpenAiErrorAnalysisService(HttpClient? client = null, string? endpoint = null)
    {
        _client = client ?? SharedClient;
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint;
    }

    public async Task<AiErrorAnalysis> AnalyzeAsync(
        string apiKey,
        string model,
        AiAnalysisRequest analysis,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Chưa cấu hình OpenAI API key.");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Chưa cấu hình model.");

        var content = new List<object>
        {
            new
            {
                type = "input_text",
                text = BuildInput(analysis)
            }
        };
        if (analysis.SnapshotJpeg is { Length: > 0 })
        {
            content.Add(new
            {
                type = "input_image",
                image_url = $"data:image/jpeg;base64,{Convert.ToBase64String(analysis.SnapshotJpeg)}",
                detail = "low"
            });
        }

        var payload = new
        {
            model = model.Trim(),
            instructions = Instructions,
            input = new[]
            {
                new { role = "user", content = content.ToArray() }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "lua_error_analysis",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            summary = new { type = "string" },
                            root_cause = new { type = "string" },
                            evidence = new { type = "array", items = new { type = "string" } },
                            steps = new { type = "array", items = new { type = "string" } },
                            fixed_lua = new { type = "string" },
                            confidence = new
                            {
                                type = "string",
                                @enum = new[] { "thấp", "trung bình", "cao" }
                            },
                            warnings = new { type = "array", items = new { type = "string" } }
                        },
                        required = new[]
                        {
                            "summary", "root_cause", "evidence", "steps",
                            "fixed_lua", "confidence", "warnings"
                        }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException(response.StatusCode, body);

        using var document = JsonDocument.Parse(body);
        var outputText = ReadOutputText(document.RootElement);
        if (string.IsNullOrWhiteSpace(outputText))
            throw new InvalidDataException("OpenAI không trả về nội dung phân tích.");

        using var result = JsonDocument.Parse(outputText);
        var root = result.RootElement;
        return new AiErrorAnalysis(
            ReadString(root, "summary"),
            ReadString(root, "root_cause"),
            ReadArray(root, "evidence"),
            ReadArray(root, "steps"),
            ReadString(root, "fixed_lua"),
            ReadString(root, "confidence"),
            ReadArray(root, "warnings"));
    }

    private static string BuildInput(AiAnalysisRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Hãy chẩn đoán lỗi từ dữ liệu sau.");
        builder.AppendLine();
        builder.AppendLine("THÔNG TIN THIẾT BỊ:");
        builder.AppendLine(request.DeviceContext ?? "Không có.");
        builder.AppendLine();
        builder.AppendLine("LOG GẦN NHẤT:");
        builder.AppendLine(string.IsNullOrWhiteSpace(request.Logs) ? "Không có." : request.Logs);
        builder.AppendLine();
        builder.AppendLine($"FILE LUA: {request.LuaFileName ?? "Không có"}");
        builder.AppendLine(request.LuaSource ?? "Không có mã Lua.");
        builder.AppendLine();
        builder.AppendLine(request.SnapshotJpeg is null
            ? "Không gửi ảnh màn hình."
            : "Có kèm một ảnh màn hình hiện tại để làm bằng chứng.");
        return builder.ToString();
    }

    private static string ReadOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct) &&
            direct.ValueKind == JsonValueKind.String)
            return direct.GetString() ?? "";

        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var contents) ||
                contents.ValueKind != JsonValueKind.Array) continue;
            foreach (var content in contents.EnumerateArray())
            {
                if (content.TryGetProperty("type", out var type) &&
                    type.GetString() == "output_text" &&
                    content.TryGetProperty("text", out var text))
                    return text.GetString() ?? "";
            }
        }
        return "";
    }

    private static Exception CreateApiException(HttpStatusCode status, string body)
    {
        var message = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var detail))
                message = detail.GetString() ?? "";
        }
        catch (JsonException) { }

        var friendly = status switch
        {
            HttpStatusCode.Unauthorized => "API key không hợp lệ hoặc đã bị thu hồi.",
            HttpStatusCode.TooManyRequests => "OpenAI đang giới hạn yêu cầu hoặc tài khoản đã hết hạn mức.",
            HttpStatusCode.BadRequest => "Dữ liệu gửi tới OpenAI không hợp lệ.",
            _ => $"OpenAI trả về HTTP {(int)status}."
        };
        if (!string.IsNullOrWhiteSpace(message)) friendly += $" {message}";
        return new HttpRequestException(friendly, null, status);
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static IReadOnlyList<string> ReadArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
            : [];
}
