using System;
using System.Collections.Generic;
using System.IO;
using Timberborn.AssetSystem;
using Timberborn.Navigation;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.Timbermesh;
using Timberborn.TubeSystem;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mods.Caravans.Scripts {
  // Drives a caravan model along the road to the map edge, hides it while away, and drives it back if it survives.
  // Like beavers, it jumps on and off ziplines at the stations, hangs from the cable in between (its drawbar and rear
  // arm swung up like a gondola's), and disappears inside tubeways.
  public class CaravanVisualizer : IUpdatableSingleton {

    private const string WheelPrefix = "#Wheel";
    private const string PulleyPrefix = "#Pulley";
    private const string DrawbarName = "#Drawbar";
    private const string RearArmName = "#RearArm";
    private const float WheelRadius = 0.158f;
    private const float PulleyRadius = 0.03f;
    private const float TurnSharpness = 10f;
    private const float HalfWheelbase = 0.25f;

    // Road bumps, driven by the distance travelled so they speed up and stop with the caravan.
    private const float RockDegrees = 1.4f;
    private const float RockWavelength = 1.3f;
    private const float JolterDegrees = 0.6f;
    private const float JolterWavelength = 0.53f;
    private const float NodDegrees = 0.7f;
    private const float NodWavelength = 0.71f;
    private const float BumpHeight = 0.012f;
    private const float BumpWavelength = 0.41f;

    // Zipline cables run this far above the path corners the game gives for them (ZiplinePathTransformer).
    private const float CableHeight = 0.44f;
    // How far below the cable the model's origin hangs if it has no arms to hang from.
    private const float FallbackHangDepth = 0.75f;
    private const float SwayDegrees = 2.5f;
    private const float SwayPeriod = 2.3f;
    private const float BoardingSwingDegrees = 9f;
    private const float BoardingSwingPeriod = 1.6f;
    private const float BoardingSwingDecay = 1.3f;

    private readonly IDayNightCycle _dayNightCycle;
    private readonly IAssetLoader _assetLoader;
    private readonly TimbermeshImporter _timbermeshImporter;
    private readonly TubeMap _tubeMap;
    private readonly List<Caravan> _caravans = new List<Caravan>();
    private readonly Dictionary<string, GameObject> _templates = new Dictionary<string, GameObject>();

    public CaravanVisualizer(IDayNightCycle dayNightCycle,
                             IAssetLoader assetLoader,
                             TimbermeshImporter timbermeshImporter,
                             TubeMap tubeMap) {
      _dayNightCycle = dayNightCycle;
      _assetLoader = assetLoader;
      _timbermeshImporter = timbermeshImporter;
      _tubeMap = tubeMap;
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
      _caravans.Add(new Caravan(this, visual, route, currentRoute, departureDay, returnDay, returns, legFraction));
    }

    public void UpdateSingleton() {
      if (_caravans.Count == 0) {
        return;
      }
      // PartialDayNumber only moves once per tick; the fluid time also moves between ticks, for smooth motion.
      var seconds = _dayNightCycle.FluidSecondsPassedToday;
      var now = _dayNightCycle.DayNumber + seconds / _dayNightCycle.DayLengthInSeconds;
      var gameSeconds = _dayNightCycle.DayNumber * _dayNightCycle.DayLengthInSeconds + seconds;
      var turn = 1f - Mathf.Exp(-TurnSharpness * Time.deltaTime);
      for (var i = _caravans.Count - 1; i >= 0; i--) {
        var caravan = _caravans[i];
        if (now >= caravan.ReturnDay) {
          Object.Destroy(caravan.Visual);
          _caravans.RemoveAt(i);
        } else {
          caravan.Update(now, gameSeconds, turn);
        }
      }
    }

    private bool IsInTube(Vector3 position) {
      var tube = _tubeMap.GetTubeAt(NavigationCoordinateSystem.WorldToGridInt(position));
      return (bool) tube && tube.CanBeVisited;
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

    private static float Wave(float distance, float wavelength, float phase = 0f) {
      return Mathf.Sin(2f * Mathf.PI * distance / wavelength + phase);
    }

    // A hinged arm (the drawbar, or the rear arm only shown on ziplines) with a pulley on its tip. On a cable it
    // swings round its hinge to point straight up, and the caravan hangs from the pulley's top. The wagon tilts with
    // the cable while the arms stay upright, which keeps both pulleys on an inclined cable: the hinges' height
    // difference (wheelbase * sin) matches the cable's rise between them (wheelbase * cos * tan).
    private class Arm {

      private readonly Transform _arm;
      private readonly Quaternion _lowered;
      private readonly Vector3 _reach;
      private readonly Vector3 _hook;
      private readonly bool _cableOnly;

      public Arm(Transform arm, Transform pulley, bool cableOnly) {
        _arm = arm;
        _lowered = arm.localRotation;
        _cableOnly = cableOnly;
        Pulley = pulley;
        PulleyRotation = pulley.localRotation;
        _reach = _lowered * pulley.localPosition;
        _hook = pulley.localPosition + pulley.localPosition.normalized * PulleyRadius;
      }

      public Transform Pulley { get; }
      public Quaternion PulleyRotation { get; }
      public Vector3 Hook => _arm.TransformPoint(_hook);

      public void SetRaised(bool raised) {
        if (!raised) {
          _arm.localRotation = _lowered;
        }
        Pulley.gameObject.SetActive(raised);
        if (_cableOnly) {
          _arm.gameObject.SetActive(raised);
        }
      }

      // Points the arm at the world's up, turning only round its hinge (so a sideways sway still leans the arms).
      public void PointUp() {
        var up = _arm.parent.InverseTransformDirection(Vector3.up);
        up.x = 0f;
        if (up.sqrMagnitude > 1e-6f) {
          _arm.localRotation = Quaternion.FromToRotation(_reach, up) * _lowered;
        }
      }

    }

    private class Caravan {

      private readonly CaravanVisualizer _visualizer;
      private readonly Func<CaravanRoute> _currentRoute;
      private readonly Transform _transform;
      private readonly List<Transform> _wheels = new List<Transform>();
      private readonly List<Quaternion> _wheelRotations = new List<Quaternion>();
      private readonly List<Arm> _arms = new List<Arm>();
      private readonly float _departureDay;
      private readonly bool _returns;
      private readonly float _legFraction;
      private CaravanRoute _route;
      private bool _onReturnLeg;
      private bool _visible;
      private bool? _hanging;
      private int _section = -1;
      private Vector3 _position;
      private Quaternion _heading;
      private float _odometer;
      private float _rolled;
      private float _pulleyTurn;
      private float _boardedAt;

      public Caravan(CaravanVisualizer visualizer, GameObject visual, CaravanRoute route,
                     Func<CaravanRoute> currentRoute, float departureDay, float returnDay, bool returns,
                     float legFraction) {
        _visualizer = visualizer;
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
          } else if (child.name == DrawbarName || child.name == RearArmName) {
            var pulley = FindPulley(child);
            if (pulley) {
              _arms.Add(new Arm(child, pulley, child.name == RearArmName));
            }
          }
        }
      }

      public GameObject Visual { get; }
      public float ReturnDay { get; }

      public void Update(float now, float gameSeconds, float turn) {
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
            _section = -1;
          }
          progress = (1f - t) / _legFraction;
          direction = -1f;
        } else {
          SetVisible(false);
          _section = -1;
          return;
        }
        var sample = _route.SampleAt(Mathf.Clamp01(progress) * _route.TravelLength, HalfWheelbase);
        // A new section means the caravan just jumped on or off a zipline, so it settles at once instead of turning.
        var jumped = sample.Section != _section;
        var snap = !_visible || jumped;
        if (jumped && sample.OnCable) {
          _boardedAt = gameSeconds;
        }
        _section = sample.Section;
        var moved = snap ? 0f : Vector3.Distance(sample.Position, _position);
        _position = sample.Position;

        if (!sample.OnCable && _visualizer.IsInTube(sample.Position)) {
          SetVisible(false);
          return;
        }
        // Faces along the curve between the axles, so it also tilts on stairs, slopes and inclined zipline cables.
        var forward = direction * sample.Forward;
        if (forward.sqrMagnitude > 1e-6f) {
          var target = Quaternion.LookRotation(forward, Vector3.up);
          _heading = snap ? target : Quaternion.Slerp(_heading, target, turn);
        } else if (snap) {
          _heading = _transform.rotation;
        }
        SetHanging(sample.OnCable);
        if (sample.OnCable) {
          Hang(sample.Position, gameSeconds, moved);
        } else {
          Drive(sample.Position, moved);
        }
        SetVisible(true);
      }

      private static Transform FindPulley(Transform arm) {
        foreach (var child in arm.GetComponentsInChildren<Transform>(true)) {
          if (child.name.StartsWith(PulleyPrefix, StringComparison.Ordinal)) {
            return child;
          }
        }
        return null;
      }

      // Rocks over the bumps in the road and turns the wheels.
      private void Drive(Vector3 position, float moved) {
        _odometer += moved;
        var roll = RockDegrees * Wave(_odometer, RockWavelength)
                   + JolterDegrees * Wave(_odometer, JolterWavelength, 1.7f);
        var pitch = NodDegrees * Wave(_odometer, NodWavelength, 0.6f);
        var bump = BumpHeight * 0.5f * (1f + Wave(_odometer, BumpWavelength));
        _transform.SetPositionAndRotation(position + Vector3.up * bump,
                                          _heading * Quaternion.Euler(pitch, 0f, roll));
        Roll(moved);
      }

      // Hangs from the pulleys on the cable, tilted along it, swinging back as it sets off and swaying on the way.
      private void Hang(Vector3 position, float gameSeconds, float moved) {
        var sinceBoarding = gameSeconds - _boardedAt;
        var pitch = BoardingSwingDegrees * Mathf.Exp(-sinceBoarding / BoardingSwingDecay)
                    * Wave(sinceBoarding, BoardingSwingPeriod);
        var roll = SwayDegrees * Wave(gameSeconds, SwayPeriod);
        var rotation = _heading * Quaternion.Euler(pitch, 0f, roll);
        var cable = position + Vector3.up * CableHeight;
        if (_arms.Count == 0) {
          _transform.SetPositionAndRotation(cable - rotation * (Vector3.up * FallbackHangDepth), rotation);
          return;
        }
        _transform.SetPositionAndRotation(cable, rotation);
        var hook = Vector3.zero;
        foreach (var arm in _arms) {
          arm.PointUp();
          hook += arm.Hook;
        }
        _transform.position = cable - (hook / _arms.Count - cable);
        TurnPulleys(moved);
      }

      private void SetHanging(bool hanging) {
        if (_hanging != hanging) {
          _hanging = hanging;
          foreach (var arm in _arms) {
            arm.SetRaised(hanging);
          }
        }
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

      // The pulleys ride on top of the cable, so they turn the opposite way to the road wheels.
      private void TurnPulleys(float distance) {
        if (distance <= 0f) {
          return;
        }
        _pulleyTurn = (_pulleyTurn - distance / PulleyRadius * Mathf.Rad2Deg) % 360f;
        var spin = Quaternion.AngleAxis(_pulleyTurn, Vector3.right);
        foreach (var arm in _arms) {
          arm.Pulley.localRotation = arm.PulleyRotation * spin;
        }
      }

    }

  }
}