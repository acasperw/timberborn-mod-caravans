using System.Collections.Generic;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using UnityEngine;

namespace Mods.Caravans.Scripts {
  // Placeholder visuals: a box walks the road out, hides while away, then walks back if it survives.
  public class CaravanVisualizer : IUpdatableSingleton {

    private static readonly string[] ShaderNames = {
        "Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default", "Universal Render Pipeline/Lit"
    };

    private readonly IDayNightCycle _dayNightCycle;
    private readonly List<Caravan> _caravans = new List<Caravan>();
    private Material _material;

    public CaravanVisualizer(IDayNightCycle dayNightCycle) {
      _dayNightCycle = dayNightCycle;
    }

    public void Send(List<Vector3> path, float departureDay, float returnDay, bool returns, float legFraction) {
      if (path.Count < 2) {
        Debug.Log($"[Caravans] No placeholder: path has {path.Count} points");
        return;
      }
      var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
      box.name = "CaravanPlaceholder";
      box.transform.localScale = new Vector3(0.5f, 0.5f, 0.8f);
      Object.Destroy(box.GetComponent<Collider>());
      var renderer = box.GetComponent<Renderer>();
      renderer.sharedMaterial = _material ??= CreateMaterial();
      Debug.Log($"[Caravans] Placeholder spawned: {path.Count} path points, {path[0]} -> {path[path.Count - 1]}, " +
                $"day {departureDay:0.000} -> {returnDay:0.000}, returns={returns}");
      _caravans.Add(new Caravan(box, new List<Vector3>(path), departureDay, returnDay, returns, legFraction));
    }

    private static Material CreateMaterial() {
      foreach (var shaderName in ShaderNames) {
        var shader = Shader.Find(shaderName);
        if (shader != null) {
          var material = new Material(shader);
          material.color = new Color(1f, 0.5f, 0.05f);
          return material;
        }
      }
      return null;
    }

    public void UpdateSingleton() {
      var now = _dayNightCycle.PartialDayNumber;
      for (var i = _caravans.Count - 1; i >= 0; i--) {
        var caravan = _caravans[i];
        if (now >= caravan.ReturnDay) {
          Object.Destroy(caravan.Box);
          _caravans.RemoveAt(i);
          continue;
        }
        UpdateCaravan(caravan, now);
      }
    }

    private static void UpdateCaravan(Caravan caravan, float now) {
      var total = caravan.ReturnDay - caravan.DepartureDay;
      var t = (now - caravan.DepartureDay) / total;
      float? progress = null;
      if (t < caravan.LegFraction) {
        progress = t / caravan.LegFraction;
      } else if (t > 1f - caravan.LegFraction && caravan.Returns) {
        progress = (1f - t) / caravan.LegFraction;
      }
      caravan.Box.SetActive(progress.HasValue);
      if (progress.HasValue) {
        caravan.Box.transform.position = caravan.PositionAt(progress.Value) + Vector3.up * 0.3f;
      }
    }

    private class Caravan {

      private readonly List<Vector3> _path;
      private readonly float[] _distances;

      public Caravan(GameObject box, List<Vector3> path, float departureDay, float returnDay, bool returns,
                     float legFraction) {
        Box = box;
        LegFraction = legFraction;
        _path = path;
        DepartureDay = departureDay;
        ReturnDay = returnDay;
        Returns = returns;
        _distances = new float[path.Count];
        for (var i = 1; i < path.Count; i++) {
          _distances[i] = _distances[i - 1] + Vector3.Distance(path[i - 1], path[i]);
        }
      }

      public GameObject Box { get; }
      public float DepartureDay { get; }
      public float ReturnDay { get; }
      public bool Returns { get; }
      public float LegFraction { get; }

      public Vector3 PositionAt(float progress) {
        var target = progress * _distances[_distances.Length - 1];
        for (var i = 1; i < _path.Count; i++) {
          if (target <= _distances[i]) {
            var segment = _distances[i] - _distances[i - 1];
            var local = segment > 0f ? (target - _distances[i - 1]) / segment : 0f;
            return Vector3.Lerp(_path[i - 1], _path[i], local);
          }
        }
        return _path[_path.Count - 1];
      }

    }

  }
}
