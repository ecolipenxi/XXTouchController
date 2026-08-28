namespace XXTouchController.Models;

public sealed record AiErrorAnalysis(
    string Summary,
    string RootCause,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Steps,
    string FixedLua,
    string Confidence,
    IReadOnlyList<string> Warnings);

public sealed record AiAnalysisRequest(
    string Logs,
    string? LuaFileName,
    string? LuaSource,
    string? DeviceContext,
    byte[]? SnapshotJpeg);
