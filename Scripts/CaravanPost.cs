using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
                             IDeletableEntity,
                             IPersistentEntity,
                             IManufactoryLimiter {

    private const string RewardRecipePrefix = "CaravanReward";

    private static readonly ComponentKey CaravanPostKey = new ComponentKey("CaravanPost");
    private static readonly ListKey<string> RecipeIdsKey = new ListKey<string>("RecipeIds");
    private static readonly ListKey<float> ReturnTimesKey = new ListKey<float>("ReturnTimes");
    private static readonly ListKey<float> DepartureTimesKey = new ListKey<float>("DepartureTimes");
    private static readonly ListKey<bool> LostKey = new ListKey<bool>("Lost");
    private static readonly ListKey<bool> ScienceReservedKey = new ListKey<bool>("ScienceReserved");

    private readonly IDayNightCycle _dayNightCycle;
    private readonly ILoc _loc;
    private readonly MapEdgeRouteFinder _routeFinder;
    private readonly ScienceService _scienceService;
    private readonly QuickNotificationService _notificationService;
    private readonly CaravanVisualizer _visualizer;
    private readonly CaravanResearch _research;
    private readonly CaravanResearchToolUnlocker _researchToolUnlocker;
    private readonly List<Trip> _trips = new List<Trip>();
    private ImmutableArray<RecipeSpec> _selectableRecipes;

    private CaravanPostSpec _spec;
    private Manufactory _manufactory;
    private BuildingAccessible _buildingAccessible;
    private StatusToggle _noRouteStatus;
    private StatusToggle _noCapacityStatus;
    private CaravanRoute _route;
    private int _routeVersion = -1;
    private bool _visualsRestorePending;

    public CaravanPost(IDayNightCycle dayNightCycle,
                       ILoc loc,
                       MapEdgeRouteFinder routeFinder,
                       ScienceService scienceService,
                       QuickNotificationService notificationService,
                       CaravanVisualizer visualizer,
                       CaravanResearch research,
                       CaravanResearchToolUnlocker researchToolUnlocker) {
      _dayNightCycle = dayNightCycle;
      _loc = loc;
      _routeFinder = routeFinder;
      _scienceService = scienceService;
      _notificationService = notificationService;
      _visualizer = visualizer;
      _research = research;
      _researchToolUnlocker = researchToolUnlocker;
    }

    public int CaravansOnTheRoad => _trips.Count;

    public int MaxCaravans => _spec.MaxConcurrentCaravans;

    public float RouteLength => _route?.Length ?? 0f;

    public RecipeSpec CurrentRecipe => _manufactory.HasCurrentRecipe ? _manufactory.CurrentRecipe : null;

    public CaravanTradeSpec CurrentTrade => CurrentRecipe == null ? null : FindTrade(CurrentRecipe.Id);

    public int ResearchScienceCost(CaravanTradeSpec trade) => _research.ScienceCost(trade.BuildingUnlock);

    public string ResearchBuildingNameLocKey(CaravanTradeSpec trade) =>
      _research.DisplayNameLocKey(trade.BuildingUnlock);

    public int ScienceNeededToDepart {
      get {
        var name = CurrentTrade?.BuildingUnlock;
        return string.IsNullOrEmpty(name) ? 0 : Mathf.Max(0, _research.ScienceCost(name) - _scienceService.SciencePoints);
      }
    }

    private float HoursPerDay => _dayNightCycle.DaytimeLengthInHours + _dayNightCycle.NighttimeLengthInHours;

    public float TripHours(CaravanTradeSpec trade) {
      var walking = 2f * RouteLength / _spec.WalkingTilesPerHour;
      return Mathf.Clamp(walking + trade.AwayHours, _spec.MinTravelHours, _spec.MaxTravelHours);
    }

    public float HoursUntilReturn(int index) {
      return Mathf.Max(0f, (_trips[index].ReturnDay - _dayNightCycle.PartialDayNumber) * HoursPerDay);
    }

    public int ScienceNeededOnReturn(int index) {
      var trip = _trips[index];
      var name = FindTrade(trip.RecipeId)?.BuildingUnlock;
      if (trip.Lost || trip.ScienceReserved || string.IsNullOrEmpty(name) || HoursUntilReturn(index) > 0f) {
        return 0;
      }
      return Mathf.Max(0, _research.ScienceCost(name) - _scienceService.SciencePoints);
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
      _selectableRecipes = _manufactory.ProductionRecipes;
      _research.Register(this);
      var statusSubject = GetComponent<StatusSubject>();
      statusSubject.RegisterStatus(_noRouteStatus);
      statusSubject.RegisterStatus(_noCapacityStatus);
    }

    public override void Tick() {
      if (CurrentTrade?.BuildingUnlock is string name && !_research.Available(name)) {
        _manufactory.SetRecipe(null);
      }
      if (_routeVersion != _routeFinder.RoadsVersion) {
        _routeVersion = _routeFinder.RoadsVersion;
        _route = _routeFinder.FindRoute(_buildingAccessible.CalculateAccess());
        UpdateStatuses();
      }
      if (_visualsRestorePending && _route != null) {
        _visualsRestorePending = false;
        foreach (var trip in _trips) {
          SendVisual(trip);
        }
      }
      ResolveReturnedCaravans();
    }

    public float ProductionEfficiency() {
      return CanSendCaravan() ? 1f : 0f;
    }

    public float MaxProductionProgressChange(float expectedProductionProgressChange) {
      return CanSendCaravan() ? 1f : 0f;
    }

    public void Save(IEntitySaver entitySaver) {
      var recipeIds = new List<string>(_trips.Count);
      var returnTimes = new List<float>(_trips.Count);
      var departureTimes = new List<float>(_trips.Count);
      var lost = new List<bool>(_trips.Count);
      var scienceReserved = new List<bool>(_trips.Count);
      foreach (var trip in _trips) {
        recipeIds.Add(trip.RecipeId);
        returnTimes.Add(trip.ReturnDay);
        departureTimes.Add(trip.DepartureDay);
        lost.Add(trip.Lost);
        scienceReserved.Add(trip.ScienceReserved);
      }
      var component = entitySaver.GetComponent(CaravanPostKey);
      component.Set(RecipeIdsKey, recipeIds);
      component.Set(ReturnTimesKey, returnTimes);
      component.Set(DepartureTimesKey, departureTimes);
      component.Set(LostKey, lost);
      component.Set(ScienceReservedKey, scienceReserved);
    }

    public void Load(IEntityLoader entityLoader) {
      if (!entityLoader.TryGetComponent(CaravanPostKey, out var component)) {
        return;
      }
      var recipeIds = component.Get(RecipeIdsKey);
      var returnTimes = component.Get(ReturnTimesKey);
      // Older saves lack departure times and losses.
      var departureTimes = component.Has(DepartureTimesKey) ? component.Get(DepartureTimesKey) : null;
      var lost = component.Has(LostKey) ? component.Get(LostKey) : null;
      var scienceReserved = component.Has(ScienceReservedKey) ? component.Get(ScienceReservedKey) : null;
      for (var i = 0; i < recipeIds.Count; i++) {
        _trips.Add(new Trip(recipeIds[i],
                            departureTimes != null && i < departureTimes.Count
                                ? departureTimes[i]
                                : returnTimes[i] - 0.5f,
                            returnTimes[i],
                            lost != null && i < lost.Count && lost[i],
                            scienceReserved != null && i < scienceReserved.Count && scienceReserved[i]));
      }
      _visualsRestorePending = _trips.Count > 0;
    }

    private bool CanSendCaravan() {
      var name = CurrentTrade?.BuildingUnlock;
      return _route != null && _trips.Count < _spec.MaxConcurrentCaravans &&
             (string.IsNullOrEmpty(name) ||
              (_research.Available(name) && _scienceService.SciencePoints >= _research.ScienceCost(name)));
    }

    public void RefreshResearchRecipes() {
      _manufactory.ProductionRecipes = _selectableRecipes.RemoveAll(recipe => {
        var name = FindTrade(recipe.Id)?.BuildingUnlock;
        return !string.IsNullOrEmpty(name) && !_research.Available(name);
      });
      if (CurrentRecipe != null && !_manufactory.ProductionRecipes.Contains(CurrentRecipe)) {
        _manufactory.SetRecipe(null);
      }
    }

    public void DeleteEntity() {
      _research.Unregister(this);
      foreach (var trip in _trips) {
        var name = FindTrade(trip.RecipeId)?.BuildingUnlock;
        if (!string.IsNullOrEmpty(name)) {
          if (trip.ScienceReserved) {
            _scienceService.AddPoints(_research.ScienceCost(name));
          }
          _research.Lost(name);
        }
      }
    }

    private void UpdateStatuses() {
      var noRoute = _route == null;
      var noCapacity = !noRoute && _trips.Count >= _spec.MaxConcurrentCaravans;
      if (noRoute != _noRouteStatus.IsActive) {
        _noRouteStatus.Toggle(noRoute);
      }
      if (noCapacity != _noCapacityStatus.IsActive) {
        _noCapacityStatus.Toggle(noCapacity);
      }
    }

    private void OnProductionFinished(object sender, System.EventArgs e) {
      var trade = FindTrade(_manufactory.CurrentRecipe.Id);
      if (trade == null) {
        return;
      }
      var tripHours = TripHours(trade);
      var scienceReserved = !string.IsNullOrEmpty(trade.BuildingUnlock);
      if (scienceReserved) {
        _scienceService.SubtractPoints(_research.ScienceCost(trade.BuildingUnlock));
      }
      var trip = new Trip(trade.RecipeId, _dayNightCycle.PartialDayNumber,
                          _dayNightCycle.DayNumberHoursFromNow(tripHours), Random.value < trade.LossChance,
                          scienceReserved);
      _trips.Add(trip);
      if (!string.IsNullOrEmpty(trade.BuildingUnlock)) {
        _research.Start(trade.BuildingUnlock);
      }
      SendVisual(trip);
      _notificationService.SendNotification(_loc.T("Caravans.Notification.Departed", tripHours));
      UpdateStatuses();
    }

    private void SendVisual(Trip trip) {
      _visualizer.Send(_spec.CaravanModel, () => _route, trip.DepartureDay, trip.ReturnDay, !trip.Lost,
                       LegFraction(trip));
    }

    // Share of the trip spent driving each way, at walking speed but leaving at least 10% of the trip "away".
    private float LegFraction(Trip trip) {
      var tripHours = (trip.ReturnDay - trip.DepartureDay) * HoursPerDay;
      if (tripHours <= 0f) {
        return 0.1f;
      }
      return Mathf.Min(RouteLength / _spec.WalkingTilesPerHour, tripHours * 0.45f) / tripHours;
    }

    private void ResolveReturnedCaravans() {
      var now = _dayNightCycle.PartialDayNumber;
      for (var i = _trips.Count - 1; i >= 0; i--) {
        var trip = _trips[i];
        if (now < trip.ReturnDay) {
          continue;
        }
        var trade = FindTrade(trip.RecipeId);
        if (trade != null && trip.Lost) {
          if (!string.IsNullOrEmpty(trade.BuildingUnlock)) {
            if (trip.ScienceReserved) {
              _scienceService.AddPoints(_research.ScienceCost(trade.BuildingUnlock));
            }
            _research.Lost(trade.BuildingUnlock);
          }
          _notificationService.SendWarningNotification(_loc.T("Caravans.Notification.Lost"));
        } else if (trade != null) {
          if (!trip.ScienceReserved && !string.IsNullOrEmpty(trade.BuildingUnlock) &&
              _scienceService.SciencePoints < _research.ScienceCost(trade.BuildingUnlock)) {
            continue;
          }
          Deliver(trade, trip.ScienceReserved);
          _notificationService.SendNotification(_loc.T("Caravans.Notification.Returned"));
        }
        _trips.RemoveAt(i);
        UpdateStatuses();
      }
    }

    private void Deliver(CaravanTradeSpec trade, bool scienceReserved) {
      var inventory = _manufactory.Inventory;
      // Ignores capacity so rewards still arrive if the player switched recipes while the caravan was away.
      foreach (var reward in trade.GoodRewards) {
        inventory.GiveExistingIgnoringCapacity(reward.ToGoodAmount());
      }
      _scienceService.AddPoints(trade.ScienceReward);
      if (!string.IsNullOrEmpty(trade.BuildingUnlock)) {
        if (!scienceReserved) {
          _scienceService.SubtractPoints(_research.ScienceCost(trade.BuildingUnlock));
        }
        _research.Complete(trade.BuildingUnlock);
        _researchToolUnlocker.Unlock(trade.BuildingUnlock);
      }
    }

    private CaravanTradeSpec FindTrade(string recipeId) {
      foreach (var trade in _spec.Trades) {
        if (trade.RecipeId == recipeId) {
          return trade;
        }
      }
      return null;
    }

    private class Trip {

      public Trip(string recipeId, float departureDay, float returnDay, bool lost, bool scienceReserved) {
        RecipeId = recipeId;
        DepartureDay = departureDay;
        ReturnDay = returnDay;
        Lost = lost;
        ScienceReserved = scienceReserved;
      }

      public string RecipeId { get; }
      public float DepartureDay { get; }
      public float ReturnDay { get; }
      public bool Lost { get; }
      public bool ScienceReserved { get; }

    }

  }
}