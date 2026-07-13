namespace MSPChallenge_Client_Browser.Utils;

/// <summary>
/// Utility methods for geometric calculations and overlap detection.
/// </summary>
public static class GeometryUtils
{
    /// <summary>
    /// Determines whether two geometries overlap based on their types and coordinates.
    /// </summary>
    public static bool HasOverlap(
        string sourceGeoType,
        IReadOnlyList<double[]> sourceCoordinates,
        string targetGeoType,
        IReadOnlyList<double[]> targetCoordinates)
    {
        var aType = NormaliseGeometryKind(sourceGeoType, sourceCoordinates);
        var bType = NormaliseGeometryKind(targetGeoType, targetCoordinates);

        if (aType == "point" && bType == "point")
            return PointDistanceSquared(sourceCoordinates[0], targetCoordinates[0]) <= 1.0;

        if (aType == "point" && bType == "line")
            return PointOnLine(sourceCoordinates[0], targetCoordinates);

        if (aType == "line" && bType == "point")
            return PointOnLine(targetCoordinates[0], sourceCoordinates);

        if (aType == "point" && bType == "polygon")
            return PointInPolygon(sourceCoordinates[0], targetCoordinates);

        if (aType == "polygon" && bType == "point")
            return PointInPolygon(targetCoordinates[0], sourceCoordinates);

        if (aType == "line" && bType == "line")
            return PolylineIntersectsPolyline(sourceCoordinates, targetCoordinates);

        if (aType == "line" && bType == "polygon")
            return PolylineIntersectsPolygon(sourceCoordinates, targetCoordinates);

        if (aType == "polygon" && bType == "line")
            return PolylineIntersectsPolygon(targetCoordinates, sourceCoordinates);

        return PolygonsOverlap(sourceCoordinates, targetCoordinates);
    }

    /// <summary>
    /// Normalizes a geometry type string to one of: "point", "line", or "polygon".
    /// Falls back to heuristics based on coordinate count if the type string is ambiguous.
    /// </summary>
    public static string NormaliseGeometryKind(string geoType, IReadOnlyList<double[]> coordinates)
    {
        var key = geoType.Trim().ToLowerInvariant();
        if (key.Contains("point")) return "point";
        if (key.Contains("line")) return "line";
        if (key.Contains("polygon")) return "polygon";
        if (coordinates.Count <= 1) return "point";
        return coordinates.Count >= 4 ? "polygon" : "line";
    }

    /// <summary>
    /// Calculates the center point of a geometry by computing the center of its bounding box.
    /// </summary>
    public static double[] GetGeometryCenter(IReadOnlyList<double[]> coordinates)
    {
        if (coordinates.Count == 0) return [0d, 0d];

        var minX = coordinates[0][0];
        var maxX = coordinates[0][0];
        var minY = coordinates[0][1];
        var maxY = coordinates[0][1];

        for (var i = 1; i < coordinates.Count; i++)
        {
            var c = coordinates[i];
            if (c[0] < minX) minX = c[0];
            if (c[0] > maxX) maxX = c[0];
            if (c[1] < minY) minY = c[1];
            if (c[1] > maxY) maxY = c[1];
        }

        return [minX + (maxX - minX) / 2d, minY + (maxY - minY) / 2d];
    }

    /// <summary>
    /// Determines if a point lies on or near a polyline (within tolerance of 1.0 units squared).
    /// </summary>
    public static bool PointOnLine(double[] point, IReadOnlyList<double[]> line)
    {
        if (line.Count == 0) return false;
        if (line.Count == 1) return PointDistanceSquared(point, line[0]) <= 1.0;

        for (var i = 1; i < line.Count; i++)
        {
            if (DistancePointToSegmentSquared(point, line[i - 1], line[i]) <= 1.0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determines if two polylines intersect.
    /// </summary>
    public static bool PolylineIntersectsPolyline(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        if (a.Count < 2 || b.Count < 2) return false;

        for (var i = 1; i < a.Count; i++)
        {
            for (var j = 1; j < b.Count; j++)
            {
                if (SegmentsIntersect(a[i - 1], a[i], b[j - 1], b[j]))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines if a polyline intersects or is contained within a polygon.
    /// </summary>
    public static bool PolylineIntersectsPolygon(IReadOnlyList<double[]> line, IReadOnlyList<double[]> polygon)
    {
        if (line.Count < 2 || polygon.Count < 3) return false;
        if (PointInPolygon(line[0], polygon)) return true;

        var polygonSegments = EnumerateSegments(polygon, closed: true);
        for (var i = 1; i < line.Count; i++)
        {
            var lineA = line[i - 1];
            var lineB = line[i];
            foreach (var segment in polygonSegments)
            {
                if (SegmentsIntersect(lineA, lineB, segment.A, segment.B))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines if two polygons overlap (intersect or one contains the other).
    /// </summary>
    public static bool PolygonsOverlap(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        if (a.Count < 3 || b.Count < 3) return false;
        if (PointInPolygon(a[0], b) || PointInPolygon(b[0], a)) return true;

        foreach (var segA in EnumerateSegments(a, closed: true))
        {
            foreach (var segB in EnumerateSegments(b, closed: true))
            {
                if (SegmentsIntersect(segA.A, segA.B, segB.A, segB.B))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Enumerates line segments from a sequence of coordinates.
    /// If closed is true, adds a segment from the last point back to the first (if they differ).
    /// </summary>
    public static IEnumerable<(double[] A, double[] B)> EnumerateSegments(IReadOnlyList<double[]> coords, bool closed)
    {
        if (coords.Count < 2) yield break;
        for (var i = 1; i < coords.Count; i++)
            yield return (coords[i - 1], coords[i]);

        if (closed)
        {
            var first = coords[0];
            var last = coords[^1];
            if (PointDistanceSquared(first, last) > 1.0)
                yield return (last, first);
        }
    }

    /// <summary>
    /// Determines if a point is inside a polygon using the ray casting algorithm.
    /// </summary>
    public static bool PointInPolygon(double[] point, IReadOnlyList<double[]> polygon)
    {
        if (polygon.Count < 3) return false;

        var inside = false;
        var j = polygon.Count - 1;
        for (var i = 0; i < polygon.Count; i++)
        {
            var xi = polygon[i][0];
            var yi = polygon[i][1];
            var xj = polygon[j][0];
            var yj = polygon[j][1];

            var intersects = ((yi > point[1]) != (yj > point[1])) &&
                             (point[0] < (xj - xi) * (point[1] - yi) / ((yj - yi) + 1e-12) + xi);
            if (intersects)
                inside = !inside;

            j = i;
        }

        return inside;
    }

    /// <summary>
    /// Determines if two line segments intersect.
    /// </summary>
    public static bool SegmentsIntersect(double[] p1, double[] p2, double[] q1, double[] q2)
    {
        var o1 = Orientation(p1, p2, q1);
        var o2 = Orientation(p1, p2, q2);
        var o3 = Orientation(q1, q2, p1);
        var o4 = Orientation(q1, q2, p2);

        if (o1 != o2 && o3 != o4) return true;

        if (o1 == 0 && OnSegment(p1, q1, p2)) return true;
        if (o2 == 0 && OnSegment(p1, q2, p2)) return true;
        if (o3 == 0 && OnSegment(q1, p1, q2)) return true;
        if (o4 == 0 && OnSegment(q1, p2, q2)) return true;
        return false;
    }

    /// <summary>
    /// Computes the orientation of an ordered triplet of points.
    /// Returns 0 if collinear, 1 if clockwise, 2 if counterclockwise.
    /// </summary>
    public static int Orientation(double[] p, double[] q, double[] r)
    {
        var value = (q[1] - p[1]) * (r[0] - q[0]) - (q[0] - p[0]) * (r[1] - q[1]);
        if (Math.Abs(value) < 1e-9) return 0;
        return value > 0 ? 1 : 2;
    }

    /// <summary>
    /// Determines if point q lies on line segment pr (given they are collinear).
    /// </summary>
    public static bool OnSegment(double[] p, double[] q, double[] r)
    {
        return q[0] <= Math.Max(p[0], r[0]) + 1e-9 && q[0] + 1e-9 >= Math.Min(p[0], r[0])
            && q[1] <= Math.Max(p[1], r[1]) + 1e-9 && q[1] + 1e-9 >= Math.Min(p[1], r[1]);
    }

    /// <summary>
    /// Computes the squared distance from a point to a line segment.
    /// </summary>
    public static double DistancePointToSegmentSquared(double[] p, double[] a, double[] b)
    {
        var dx = b[0] - a[0];
        var dy = b[1] - a[1];
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
            return PointDistanceSquared(p, a);

        var t = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / (dx * dx + dy * dy);
        t = Math.Max(0, Math.Min(1, t));
        var proj = new[] { a[0] + t * dx, a[1] + t * dy };
        return PointDistanceSquared(p, proj);
    }

    /// <summary>
    /// Computes the squared Euclidean distance between two points.
    /// </summary>
    public static double PointDistanceSquared(double[] a, double[] b)
    {
        var dx = a[0] - b[0];
        var dy = a[1] - b[1];
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Determines which country owns a coordinate based on EEZ polygon intersection.
    /// Returns the country ID or 0 if the point is not within any EEZ.
    /// </summary>
    public static int GetCountryForCoordinate(double[] pt, IReadOnlyList<MSPChallenge_Client_Browser.Models.EezPolygon> eezPolygons)
    {
        foreach (var eez in eezPolygons)
        {
            if (PointInPolygon(pt, eez.Points))
                return eez.CountryId;
        }
        return 0;
    }

    /// <summary>
    /// Determines if coordinate arrays have changed by comparing their lengths and positions.
    /// Uses a tolerance of 1.0 unit for coordinate comparison.
    /// </summary>
    public static bool CoordsChanged(double[][] a, double[][] b)
    {
        if (a.Length != b.Length) return true;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Length < 2 || b[i].Length < 2) return true;
            if (Math.Abs(a[i][0] - b[i][0]) > 1.0 || Math.Abs(a[i][1] - b[i][1]) > 1.0) return true;
        }
        return false;
    }
}
