using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Utils;

public static partial class GenericUtils
{
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

    public static string NormaliseToken(string value)
    {
        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static bool PointInPolygon(double[] point, IReadOnlyList<double[]> polygon)
    {
        if (polygon.Count < 3) return false;

        var x = point[0];
        var y = point[1];
        var inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var xi = polygon[i][0];
            var yi = polygon[i][1];
            var xj = polygon[j][0];
            var yj = polygon[j][1];

            var intersect = ((yi > y) != (yj > y))
                            && (x < (xj - xi) * (y - yi) / (yj - yi) + xi);
            if (intersect) inside = !inside;
        }

        return inside;
    }

    private static string GetTypeLabel(Layer? layerEntry, int typeIndex)
    {
        if (layerEntry is null) return "";
        if (typeIndex >= 0 && typeIndex < layerEntry.TypeDefs.Count)
            return layerEntry.TypeDefs[typeIndex].Label;
        return "";
    }

    private static string GetCountryName(int countryId, IReadOnlyDictionary<int, string> countryNames) =>
        countryNames.TryGetValue(countryId, out var n) ? n : $"Country {countryId}";

    private static int GetCountryForCoordinate(double[] pt, IReadOnlyList<EezPolygon> eezPolygons)
    {
        foreach (var eez in eezPolygons)
        {
            if (PointInPolygon(pt, eez.Points))
                return eez.CountryId;
        }
        return 0;
    } 

}