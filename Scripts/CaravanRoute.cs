using System.Collections.Generic;
using UnityEngine;

namespace Mods.Caravans.Scripts {
  // A road route with its corners rounded off, measured along its length, for driving a caravan smoothly.
  public class CaravanRoute {

    private const float CornerRadius = 0.5f;
    private const int CornerSteps = 6;
    private const float StraightAngle = 2f;

    private readonly List<Vector3> _points;
    private readonly float[] _distances;

    private CaravanRoute(List<Vector3> points) {
      _points = points;
      _distances = new float[points.Count];
      for (var i = 1; i < points.Count; i++) {
        _distances[i] = _distances[i - 1] + Vector3.Distance(points[i - 1], points[i]);
      }
    }

    public float Length => _distances[_distances.Length - 1];

    public static CaravanRoute Create(IReadOnlyList<Vector3> corners) {
      if (corners == null) {
        return null;
      }
      var points = new List<Vector3>(corners.Count);
      foreach (var corner in corners) {
        if (points.Count == 0 || (corner - points[points.Count - 1]).sqrMagnitude > 1e-6f) {
          points.Add(corner);
        }
      }
      return points.Count < 2 ? null : new CaravanRoute(Smooth(points));
    }

    public Vector3 PositionAt(float distance) {
      if (distance <= 0f) {
        return _points[0];
      }
      var index = System.Array.BinarySearch(_distances, distance);
      if (index >= 0) {
        return _points[index];
      }
      index = ~index;
      if (index >= _points.Count) {
        return _points[_points.Count - 1];
      }
      var segment = _distances[index] - _distances[index - 1];
      return Vector3.Lerp(_points[index - 1], _points[index], (distance - _distances[index - 1]) / segment);
    }

    // Replaces every bend (in the ground plane or in height, like at stairs) with a curve that starts and ends up to
    // half a tile either side of the corner, so the caravan turns and climbs gradually instead of snapping.
    private static List<Vector3> Smooth(List<Vector3> points) {
      var result = new List<Vector3>(points.Count * 3) { points[0] };
      for (var i = 1; i < points.Count - 1; i++) {
        Vector3 previous = points[i - 1], corner = points[i], next = points[i + 1];
        Vector3 incoming = corner - previous, outgoing = next - corner;
        if (Vector3.Angle(incoming, outgoing) < StraightAngle) {
          result.Add(corner);
          continue;
        }
        var start = corner - incoming.normalized * Mathf.Min(CornerRadius, incoming.magnitude / 2f);
        var end = corner + outgoing.normalized * Mathf.Min(CornerRadius, outgoing.magnitude / 2f);
        for (var step = 0; step <= CornerSteps; step++) {
          var t = step / (float) CornerSteps;
          var u = 1f - t;
          result.Add(u * u * start + 2f * u * t * corner + t * t * end);
        }
      }
      result.Add(points[points.Count - 1]);
      return result;
    }

  }
}
