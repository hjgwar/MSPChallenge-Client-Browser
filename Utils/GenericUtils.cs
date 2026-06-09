using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Utils;

public static partial class GenericUtils
{
    public static bool IsFinalisedPlanState(string state) =>
        state.Equals("CONSULTATION", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("APPROVAL",     StringComparison.OrdinalIgnoreCase) ||
        state.Equals("APPROVED",     StringComparison.OrdinalIgnoreCase) ||
        state.Equals("IMPLEMENTED",  StringComparison.OrdinalIgnoreCase);

    public static bool IsApprovalCompleteState(string? state) =>
        state is not null &&
        (state.Equals("APPROVED",    StringComparison.OrdinalIgnoreCase) ||
         state.Equals("IMPLEMENTED", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("ARCHIVED",    StringComparison.OrdinalIgnoreCase));
         
    public static int SeveritySortRank(string? severity)
    {
        return NormaliseSeverity(severity) switch
        {
            "ERROR" => 3,
            "WARNING" => 2,
            "INFO" => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Infers point/line/polygon from coordinate count when layer metadata is unavailable.
    /// </summary>
    public static string InferGeoType(IReadOnlyList<PlanGeometryItem> geometries)
    {
        if (geometries.Count == 0) return "point";
        var pts = geometries[0].Coordinates;
        if (pts.Count <= 1) return "point";
        if (pts.Count > 3)
        {
            var first = pts[0]; var last = pts[^1];
            if (Math.Abs(first[0] - last[0]) < 1.0 && Math.Abs(first[1] - last[1]) < 1.0)
                return "polygon";
        }
        return "line";
    }

    public static string NormaliseSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var key = NormaliseToken(raw);
        return key switch
        {
            "error" or "err" or "danger" or "2" => "ERROR",
            "warning" or "warn" or "1" => "WARNING",
            "info" or "information" or "0" => "INFO",
            _ => key.ToUpperInvariant()
        };
    }

    private static string NormaliseToken(string value)
    {
        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    

}