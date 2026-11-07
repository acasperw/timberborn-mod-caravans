using System;
using System.Collections.Generic;
using System.IO;
using Timberborn.AssetSystem;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.Timbermesh;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mods.Caravans.Scripts {
  // Drives a caravan model along the road to the map edge, hides it while away, and drives it back if it survives.
  public class CaravanVisualizer : IUpdatableSingleton {

    private const string WheelPrefix = "#Wheel";
    private const float WheelRadius = 0.158f;
    private const float TurnSharpness = 10f;
    private const float HalfWheelbase = 0.25f;

    private readonly IDayNightCycle _dayNightCycle;
    private readonly IAssetLoader _assetLoader;
    private readonly TimbermeshImporter _timbermeshImporter;
    private readonly List<Caravan> _caravans = new List<Caravan>();
    private readonly Dictionary<string, GameObject> _templates = new Dictionary<string, GameObject>();

    public CaravanVisualizer(IDayNightCycle dayNightCycle,
                             IAssetLoader assetLoader,
                             TimbermeshImporter timbermeshImporter) {
      _dayNightCycle = dayNightCycle;
      _assetLoader = assetLoader;
      _timbermeshImporter = timbermeshImporter;
    }

    // currentRoute gives the post's latest route (or null without one); the return leg asks for it again, so a
    // caravan comes home along the roads as they are when it gets back.
    public void Send(string modelPath, Func<CaravanRoute> currentRoute, float departureDay, float returnDay,
                     bool returns, float legFraction) {
      var route = currentRoute();
      var template = GetTemplate(modelPath);
      if (route == null || template == null) {
        return;
      }
      var visual = Object.Instantiate(template);
      visual.name = "Caravan";
      _caravans.Add(new Caravan(visual, route, currentRoute, departureDay, returnDay, returns, legFraction));
    }

    public void UpdateSingleton() {
      if (_caravans.Count == 0) {
        return;
      }
      // PartialDayNumber only moves once per tick; the fluid time also moves between ticks, for smooth motion.
      var now = _dayNightCycle.DayNumber + _dayNightCycle.FluidSecondsPassedToday / _dayNightCycle.DayLengthInSeconds;
      var turn = 1f - Mathf.Exp(-TurnSharpness * Time.deltaTime);
      for (var i = _caravans.Count - 1; i >= 0; i--) {
        var caravan = _caravans[i];
        if (now >= caravan.ReturnDay) {
          Object.Destroy(caravan.Visual);
          _caravans.RemoveAt(i);
        } else {
          caravan.Update(now, turn);
        }
      }
    }

    private GameObject GetTemplate(string modelPath) {
      if (!_templates.TryGetValue(modelPath ?? string.Empty, out var template)) {
        template = LoadTemplate(modelPath);
        _templates[modelPath ?? string.Empty] = template;
      }
      return template;
    }

    private GameObject LoadTemplate(string modelPath) {
      var data = string.IsNullOrEmpty(modelPath) ? null : _assetLoader.LoadSafe<BinaryData>(modelPath);
      if (data == null) {
        Debug.LogWarning($"[Caravans] Caravan model '{modelPath}' not found; caravans will not be shown.");
        return null;
      }
      // Inactive, so the template and fresh copies of it stay hidden until a caravan is on the road.
      var root = new GameObject("CaravanTemplate");
      root.SetActive(false);
      using (var stream = new MemoryStream(data.Bytes)) {
        _timbermeshImporter.Import(stream, root.transform);
      }
      return root;
    }

    private class Caravan {

      private readonly Func<CaravanRoute> _currentRoute;
      private readonly Transform _transform;
      private readonly List<Transform> _wheels = new List<Transform>();
      private readonly List<Quaternion> _wheelRotations = new List<Quaternion>();
      private readonly float _departureDay;
      private readonly bool _returns;
      private readonly float _legFraction;
      private CaravanRoute _route;
      private bool _onReturnLeg;
      private bool _visible;
      private float _distance;
      private float _rolled;

      public Caravan(GameObject visual, CaravanRoute route, Func<CaravanRoute> currentRoute, float departureDay,
                     float returnDay, bool returns, float legFraction) {
        Visual = visual;
        _transform = visual.transform;
        _route = route;
        _currentRoute = currentRoute;
        _departureDay = departureDay;
        ReturnDay = returnDay;
        _returns = returns;
        _legFraction = legFraction;
        foreach (var child in visual.GetComponentsInChildren<Transform>(true)) {
          if (child.name.StartsWith(WheelPrefix, StringComparison.Ordinal)) {
            _wheels.Add(child);
            _wheelRotations.Add(child.localRotation);
          }
        }
      }

      public GameObject Visual { get; }
      public float ReturnDay { get; }

      public void Update(float now, float turn) {
        var t = (now - _departureDay) / (ReturnDay - _departureDay);
        float progress;
        float direction;
        if (t < _legFraction) {
          progress = t / _legFraction;
          direction = 1f;
        } else if (_returns && t > 1f - _legFraction) {
          if (!_onReturnLeg) {
            _onReturnLeg = true;
            _route = _currentRoute() ?? _route;
          }
          progress = (1f - t) / _legFraction;
          direction = -1f;
        } else {
          SetVisible(false);
          return;
        }
        var distance = Mathf.Clamp01(progress) * _route.Length;
        // Faces along the curve between the axles, so it also tilts on stairs and slopes.
        var forward = _route.PositionAt(distance + direction * HalfWheelbase)
                      - _route.PositionAt(distance - direction * HalfWheelbase);
        var snap = !_visible;
        if (forward.sqrMagnitude > 1e-6f) {
          var target = Quaternion.LookRotation(forward, Vector3.up);
          _transform.SetPositionAndRotation(_route.PositionAt(distance),
                                            snap ? target : Quaternion.Slerp(_transform.rotation, target, turn));
        } else {
          _transform.position = _route.PositionAt(distance);
        }
        if (!snap) {
          Roll(Mathf.Abs(distance - _distance));
        }
        _distance = distance;
        SetVisible(true);
      }

      private void SetVisible(bool visible) {
        if (_visible != visible) {
          _visible = visible;
          Visual.SetActive(visible);
        }
      }

      private void Roll(float distance) {
        if (_wheels.Count == 0 || distance <= 0f) {
          return;
        }
        _rolled = (_rolled + distance / WheelRadius * Mathf.Rad2Deg) % 360f;
        var spin = Quaternion.AngleAxis(_rolled, Vector3.right);
        for (var i = 0; i < _wheels.Count; i++) {
          _wheels[i].localRotation = _wheelRotations[i] * spin;
        }
      }

    }

  }
}