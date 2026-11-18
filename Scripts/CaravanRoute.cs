using System.Collections.Generic;
using UnityEngine;

namespace Mods.Caravans.Scripts {
  // A road route with its corners rounded off and timed like the beavers' paths, for driving a caravan smoothly.
  // The route is split into sections wherever the game makes characters jump instantly (on and off ziplines at
  // stations); a section is either all road or all zipline cable.
  public class CaravanRoute {

    private const float CornerRadius = 0.5f;
    private const int CornerSteps = 6;
    private const float StraightAngle = 2f;
    // The game marks instant jumps with float.MaxValue; anything this fast is treated as one.
    private const float JumpSpeed = 1e6f;

    private readonly List<Section> _sections;

    private CaravanRoute(List<Section> sections) {
      _sections = sections;
    }

    // How long the route takes, in tiles at normal walking speed: jumps are free and ziplines are faster.
    public float TravelLength => _sections[_sections.Count - 1].EndTime;

    public static CaravanRoute Create(IReadOnlyList<Corner> corners) {
      if (corners == null) {
        return null;
      }
      var points = new List<Corner>(corners.Count);
      foreach (var corner in corners) {
        if (points.Count > 0 && (corner.Position - points[points.Count - 1].Position).sqrMagnitude <= 1e-6f) {
          points[points.Count - 1] = corner;
        } else {
          points.Add(corner);
        }
      }
      var sections = new List<Section>();
      var current = new List<Corner>();
      var time = 0f;
      for (var i = 0; i < points.Count; i++) {
        var point = points[i];
        if (current.Count >= 2 && current[0].OnCable != current[current.Count - 1].OnCable) {
          // The last point starts a different kind of travel, so it ends this section and starts the next.
          var last = current[current.Count - 1];
          time = AddSection(sections, current, time);
          current.Add(last);
        }
        current.Add(point);
        if (point.Speed >= JumpSpeed) {
          time = AddSection(sections, current, time);
        }
      }
      AddSection(sections, current, time);
      return sections.Count == 0 ? null : new CaravanRoute(sections);
    }

    // Where the caravan is after travelling the given time, and which way the stretch of road around it points.
    public Sample SampleAt(float time, float halfWheelbase) {
      var index = 0;
      while (index < _sections.Count - 1 && time > _sections[index].EndTime) {
        index++;
      }
      var section = _sections[index];
      var distance = section.DistanceAt(time);
      return new Sample(section.PositionAt(distance),
                        section.PositionAt(distance + halfWheelbase) - section.PositionAt(distance - halfWheelbase),
                        section.OnCable, index);
    }

    private static float AddSection(List<Section> sections, List<Corner> corners, float startTime) {
      var endTime = startTime;
      if (corners.Count >= 2) {
        var section = new Section(Smooth(corners), startTime);
        if (section.Length > 1e-4f) {
          sections.Add(section);
          endTime = section.EndTime;
        }
      }
      corners.Clear();
      return endTime;
    }

    // Replaces every bend (in the ground plane or in height, like at stairs) with a curve that starts and ends up to
    // half a tile either side of the corner, so the caravan turns and climbs gradually instead of snapping.
    private static List<Corner> Smooth(List<Corner> points) {
      var result = new List<Corner>(points.Count * 3) { points[0] };
      for (var i = 1; i < points.Count - 1; i++) {
        Corner previous = points[i - 1], corner = points[i], next = points[i + 1];
        Vector3 incoming = corner.Position - previous.Position, outgoing = next.Position - corner.Position;
        if (Vector3.Angle(incoming, outgoing) < StraightAngle) {
          result.Add(corner);
          continue;
        }
        var start = corner.Position - incoming.normalized * Mathf.Min(CornerRadius, incoming.magnitude / 2f);
        var end = corner.Position + outgoing.normalized * Mathf.Min(CornerRadius, outgoing.magnitude / 2f);
        for (var step = 0; step <= CornerSteps; step++) {
          var t = step / (float) CornerSteps;
          var u = 1f - t;
          var speed = t < 0.5f ? previous.Speed : corner.Speed;
          result.Add(new Corner(u * u * start + 2f * u * t * corner.Position + t * t * end, speed, corner.OnCable));
        }
      }
      result.Add(points[points.Count - 1]);
      return result;
    }

    public readonly struct Corner {

      // Speed is the game's speed multiplier for the stretch leaving this corner.
      public Corner(Vector3 position, float speed, bool onCable) {
        Position = position;
        Speed = speed;
        OnCable = onCable;
      }

      public Vector3 Position { get; }
      public float Speed { get; }
      public bool OnCable { get; }

    }

    public readonly struct Sample {

      public Sample(Vector3 position, Vector3 forward, bool onCable, int section) {
        Position = position;
        Forward = forward;
        OnCable = onCable;
        Section = section;
      }

      public Vector3 Position { get; }
      public Vector3 Forward { get; }
      public bool OnCable { get; }
      public int Section { get; }

    }

    private class Section {

      private readonly Vector3[] _points;
      private readonly float[] _distances;
      private readonly float[] _times;

      public Section(List<Corner> corners, float startTime) {
        OnCable = corners[0].OnCable;
        _points = new Vector3[corners.Count];
        _distances = new float[corners.Count];
        _times = new float[corners.Count];
        _points[0] = corners[0].Position;
        _times[0] = startTime;
        for (var i = 1; i < corners.Count; i++) {
          _points[i] = corners[i].Position;
          var length = Vector3.Distance(_points[i - 1], _points[i]);
          var speed = corners[i - 1].Speed;
          _distances[i] = _distances[i - 1] + length;
          _times[i] = _times[i - 1] + (speed > 0f && speed < JumpSpeed ? length / speed : length);
        }
      }

      public bool OnCable { get; }
      public float Length => _distances[_distances.Length - 1];
      public float EndTime => _times[_times.Length - 1];

      public float DistanceAt(float time) {
        if (time <= _times[0]) {
          return 0f;
        }
        var index = System.Array.BinarySearch(_times, time);
        if (index >= 0) {
          return _distances[index];
        }
        index = ~index;
        if (index >= _times.Length) {
          return Length;
        }
        var span = _times[index] - _times[index - 1];
        return span > 0f
            ? Mathf.Lerp(_distances[index - 1], _distances[index], (time - _times[index - 1]) / span)
            : _distances[index];
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
        if (index >= _points.Length) {
          return _points[_points.Length - 1];
        }
        var segment = _distances[index] - _distances[index - 1];
        return segment > 0f
            ? Vector3.Lerp(_points[index - 1], _points[index], (distance - _distances[index - 1]) / segment)
            : _points[index];
      }

    }

  }
}