using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.EntitySystem;
using Timberborn.Goods;
using Timberborn.Localization;
using Timberborn.Persistence;
using Timberborn.QuickNotificationSystem;
using Timberborn.ScienceSystem;
using Timberborn.StatusSystem;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;
using Timberborn.WorldPersistence;
using Timberborn.Workshops;
using UnityEngine;

namespace Mods.Caravans.Scripts {
  public class CaravanPost : TickableComponent,
                             IAwakableComponent,
                             IInitializableEntity,
                             IPersistentEntity,
                             IManufactoryLimiter {

    private const int RouteCheckIntervalTicks = 100;
    private const string RewardRecipePrefix = "CaravanReward";

    private static readonly ComponentKey CaravanPostKey = new ComponentKey("CaravanPost");
    private static readonly ListKey<string> RecipeIdsKey = new ListKey<string>("RecipeIds");
    private static readonly ListKey<float> ReturnTimesKey = new ListKey<float>("ReturnTimes");
    private static readonly ListKey<float> DepartureTimesKey = new ListKey<float>("DepartureTimes");
    private static readonly ListKey<bool> LostKey = new ListKey<bool>("Lost");

    private readonly IDayNightCycle _dayNightCycle;
    private readonly ILoc _loc;
    private readonly MapEdgeRouteFinder _routeFinder;
    private readonly ScienceService _scienceService;
    private readonly QuickNotificationService _notificationService;
    private readonly CaravanVisualizer _visualizer;

    private readonly List<string> _recipeIds = new List<string>();
    private readonly List<float> _returnTimes = new List<float>();
    private readonly List<float> _departureTimes = new List<float>();
    private readonly List<bool> _lost = new List<bool>();
    private readonly List<Vector3> _path = new List<Vector3>();

    private CaravanPostSpec _spec;
    private Manufactory _manufactory;
    private BuildingAccessible _buildingAccessible;
    private StatusToggle _noRouteStatus;
    private StatusToggle _noCapacityStatus;
    private bool _hasRoute;
    private float _routeLength;
    private bool _visualsRestorePending;
    private int _ticksSinceRouteCheck = RouteCheckIntervalTicks;

    public CaravanPost(IDayNightCycle dayNightCycle,
                       ILoc loc,
                       MapEdgeRouteFinder routeFinder,
                       ScienceService scienceService,
                       QuickNotificationService notificationService,
                       CaravanVisualizer visualizer) {
      _visualizer = visualizer;
      _dayNightCycle = dayNightCycle;
      _loc = loc;
      _routeFinder = routeFinder;
      _scienceService = scienceService;
      _notificationService = notificationService;
    }

    public int CaravansOnTheRoad => _recipeIds.Count;

    public int MaxCaravans => _spec.MaxConcurrentCaravans;

    public bool HasRoute => _hasRoute;

    public float RouteLength => _routeLength;

    public float TripHours(CaravanTradeSpec trade) {
      var walking = 2f * _routeLength / _spec.WalkingTilesPerHour;
      return Mathf.Clamp(walking + trade.AwayHours, _spec.MinTravelHours, _spec.MaxTravelHours);
    }

    public RecipeSpec CurrentRecipe => _manufactory.HasCurrentRecipe ? _manufactory.CurrentRecipe : null;

    public CaravanTradeSpec CurrentTrade => CurrentRecipe == null ? null : FindTrade(CurrentRecipe.Id);

    public float HoursUntilReturn(int index) {
      var hoursPerDay = _dayNightCycle.DaytimeLengthInHours + _dayNightCycle.NighttimeLengthInHours;
      return Mathf.Max(0f, (_returnTimes[index] - _dayNightCycle.PartialDayNumber) * hoursPerDay);
    }

    public void Awake() {
      _spec = GetComponent<CaravanPostSpec>();
      _manufactory = GetComponent<Manufactory>();
      _buildingAccessible = GetComponent<BuildingAccessible>();
      _manufactory.ProductionFinished += OnProductionFinished;
      // Placeholder icon until the mod ships its own status sprite.
      _noRouteStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
          "UnspecifiedRecipe", _loc.T(_spec.NoRouteStatusLocKey), _loc.T(_spec.NoRouteStatusLocKey));
      _noCapacityStatus = StatusToggle.CreateNormalStatus(
          "UnspecifiedRecipe", _loc.T(_spec.NoCapacityStatusLocKey));
    }

    public void InitializeEntity() {
      // Reward-only recipes exist just to give the inventory output capacity; keep them out of the selector.
      _manufactory.ProductionRecipes = _manufactory.ProductionRecipes.RemoveAll(
          recipe => recipe.Id.StartsWith(RewardRecipePrefix));
      var statusSubject = GetComponent<StatusSubject>();
      statusSubject.RegisterStatus(_noRouteStatus);
      statusSubject.RegisterStatus(_noCapacityStatus);
    }

    public override void Tick() {
      if (++_ticksSinceRouteCheck >= RouteCheckIntervalTicks) {
        // Random restart spreads the checks of several posts across ticks.
        _ticksSinceRouteCheck = Random.Range(0, RouteCheckIntervalTicks / 2);
        UpdateRoute();
      }
      if (_visualsRestorePending && _hasRoute) {
        RestoreVisuals();
      }
      ResolveReturnedCaravans();
    }

    private void RestoreVisuals() {
      _visualsRestorePending = false;
      for (var i = 0; i < _recipeIds.Count; i++) {
        _visualizer.Send(_path, _departureTimes[i], _returnTimes[i], !_lost[i], LegFraction(i));
      }
    }

    private float LegFraction(int index) {
      var hoursPerDay = _dayNightCycle.DaytimeLengthInHours + _dayNightCycle.NighttimeLengthInHours;
      var tripHours = (_returnTimes[index] - _departureTimes[index]) * hoursPerDay;
      var legHours = Mathf.Min(_routeLength / _spec.WalkingTilesPerHour, tripHours * 0.45f);
      return tripHours > 0f ? legHours / tripHours : 0.1f;
    }

    public float ProductionEfficiency() {
      return CanSendCaravan() ? 1f : 0f;
    }

    public float MaxProductionProgressChange(float expectedProductionProgressChange) {
      return CanSendCaravan() ? 1f : 0f;
    }

    public void Save(IEntitySaver entitySaver) {
      var component = entitySaver.GetComponent(CaravanPostKey);
      component.Set(RecipeIdsKey, _recipeIds);
      component.Set(ReturnTimesKey, _returnTimes);
      component.Set(DepartureTimesKey, _departureTimes);
      component.Set(LostKey, _lost);
    }

    public void Load(IEntityLoader entityLoader) {
      if (!entityLoader.TryGetComponent(CaravanPostKey, out var component)) {
        return;
      }
      _recipeIds.AddRange(component.Get(RecipeIdsKey));
      _returnTimes.AddRange(component.Get(ReturnTimesKey));
      if (component.Has(DepartureTimesKey)) {
        _departureTimes.AddRange(component.Get(DepartureTimesKey));
      }
      while (_departureTimes.Count < _recipeIds.Count) {
        _departureTimes.Add(_returnTimes[_departureTimes.Count] - 0.5f);
      }
      if (component.Has(LostKey)) {
        _lost.AddRange(component.Get(LostKey));
      }
      while (_lost.Count < _recipeIds.Count) {
        _lost.Add(false);
      }
      _visualsRestorePending = _recipeIds.Count > 0;
    }

    private bool CanSendCaravan() {
      return _hasRoute && _recipeIds.Count < _spec.MaxConcurrentCaravans;
    }

    private void UpdateRoute() {
      _hasRoute = _routeFinder.TryFindRoadToMapEdge(_buildingAccessible.CalculateAccess(), _path);
      _routeLength = _hasRoute ? PathLength(_path) : 0f;
      UpdateStatuses();
    }

    private static float PathLength(List<Vector3> path) {
      var length = 0f;
      for (var i = 1; i < path.Count; i++) {
        length += Vector3.Distance(path[i - 1], path[i]);
      }
      return length;
    }

    private void UpdateStatuses() {
      var noRoute = !_hasRoute;
      var noCapacity = _hasRoute && _recipeIds.Count >= _spec.MaxConcurrentCaravans;
      if (noRoute != _noRouteStatus.IsActive) {
        _noRouteStatus.Toggle(noRoute);
      }
      if (noCapacity != _noCapacityStatus.IsActive) {
        _noCapacityStatus.Toggle(noCapacity);
      }
    }

    private void OnProductionFinished(object sender, System.EventArgs e) {
      var recipe = _manufactory.CurrentRecipe;
      var trade = FindTrade(recipe.Id);
      if (trade == null) {
        return;
      }
      var tripHours = TripHours(trade);
      var departure = _dayNightCycle.PartialDayNumber;
      var returnTime = _dayNightCycle.DayNumberHoursFromNow(tripHours);
      var lost = Random.value < trade.LossChance;
      _recipeIds.Add(recipe.Id);
      _returnTimes.Add(returnTime);
      _departureTimes.Add(departure);
      _lost.Add(lost);
      if (_path.Count >= 2) {
        _visualizer.Send(_path, departure, returnTime, !lost, LegFraction(_recipeIds.Count - 1));
      }
      _notificationService.SendNotification(_loc.T("Caravans.Notification.Departed", tripHours));
      UpdateStatuses();
    }

    private void ResolveReturnedCaravans() {
      for (var i = _recipeIds.Count - 1; i >= 0; i--) {
        if (_dayNightCycle.PartialDayNumber < _returnTimes[i]) {
          continue;
        }
        var trade = FindTrade(_recipeIds[i]);
        if (trade != null && _lost[i]) {
          _notificationService.SendWarningNotification(_loc.T("Caravans.Notification.Lost"));
        } else if (trade != null && TryDeliver(trade)) {
          _notificationService.SendNotification(_loc.T("Caravans.Notification.Returned"));
        } else if (trade != null) {
          continue;
        }
        _recipeIds.RemoveAt(i);
        _returnTimes.RemoveAt(i);
        _departureTimes.RemoveAt(i);
        _lost.RemoveAt(i);
        UpdateStatuses();
      }
    }

    private bool TryDeliver(CaravanTradeSpec trade) {
      var inventory = _manufactory.Inventory;
      // Ignores capacity so rewards still arrive if the player switched recipes while the caravan was away.
      foreach (var reward in trade.GoodRewards) {
        inventory.GiveExistingIgnoringCapacity(reward.ToGoodAmount());
      }
      _scienceService.AddPoints(trade.ScienceReward);
      return true;
    }

    private CaravanTradeSpec FindTrade(string recipeId) {
      foreach (var trade in _spec.Trades) {
        if (trade.RecipeId == recipeId) {
          return trade;
        }
      }
      return null;
    }

  }
}
