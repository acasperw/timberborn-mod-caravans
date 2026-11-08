using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.BlockObjectTools;
using Timberborn.Buildings;
using Timberborn.BuildingTools;
using Timberborn.CoreUI;
using Timberborn.EntitySystem;
using Timberborn.Goods;
using Timberborn.Localization;
using Timberborn.Persistence;
using Timberborn.ScienceSystem;
using Timberborn.SingletonSystem;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;
using Timberborn.WorldPersistence;

namespace Mods.Caravans.Scripts {
  public class CaravanResearch : ILoadableSingleton, ISaveableSingleton {

    private static readonly SingletonKey ResearchKey = new SingletonKey("CaravanResearch");
    private static readonly ListKey<string> UnlockedKey = new ListKey<string>("Unlocked");
    private static readonly ListKey<string> PendingKey = new ListKey<string>("Pending");

    private readonly ISingletonLoader _loader;
    private readonly BuildingService _buildings;
    private readonly BuildingUnlockingService _buildingUnlocking;
    private readonly HashSet<string> _unlocked = new HashSet<string>();
    private readonly HashSet<string> _pending = new HashSet<string>();
    private readonly List<CaravanPost> _posts = new List<CaravanPost>();
    private bool _ironTeeth;

    public CaravanResearch(ISingletonLoader loader, BuildingService buildings,
                           BuildingUnlockingService buildingUnlocking) {
      _loader = loader;
      _buildings = buildings;
      _buildingUnlocking = buildingUnlocking;
    }

    public void Load() {
      if (_loader.TryGetSingleton(ResearchKey, out var saved)) {
        foreach (var name in saved.Get(UnlockedKey)) {
          _unlocked.Add(name);
        }
        foreach (var name in saved.Get(PendingKey)) {
          _pending.Add(name);
        }
      }
      // Vanilla's locker must see these templates as unlocked before tools are registered.
      _ironTeeth = _buildings.Buildings.Any(building =>
          _buildings.GetTemplateName(building) == "DistrictCenter.IronTeeth");
      foreach (var building in _buildings.Buildings) {
        var name = _buildings.GetTemplateName(building);
        if (IsForeignTarget(name)) {
          _buildingUnlocking.UnlockIgnoringCost(building);
        }
        if (_ironTeeth && name == "LargeWindTurbine.Folktails") {
          var cost = building.BuildingCost.Select(item => item.Id == "Paper" ? item with { Id = "Plank" } : item)
              .GroupBy(item => item.Id)
              .Select(group => new GoodAmountSpec { Id = group.Key, Amount = group.Sum(item => item.Amount) })
              .ToImmutableArray();
          typeof(BuildingSpec).GetProperty(nameof(BuildingSpec.BuildingCost)).SetValue(building, cost);
        }
      }
    }

    public void Save(ISingletonSaver saver) {
      var saved = saver.GetSingleton(ResearchKey);
      saved.Set(UnlockedKey, _unlocked);
      saved.Set(PendingKey, _pending);
    }

    public bool Available(string name) => !_unlocked.Contains(name) && !_pending.Contains(name);

    public int ScienceCost(string name) {
      var cost = _buildings.GetBuildingTemplate(name).ScienceCost;
      return cost == 0 ? 4000 : cost * 5;
    }

    public void Register(CaravanPost post) {
      _posts.Add(post);
      post.RefreshResearchRecipes();
    }

    public void Unregister(CaravanPost post) {
      _posts.Remove(post);
    }

    public void Start(string name) {
      _pending.Add(name);
      RefreshPosts();
    }

    public void Lost(string name) {
      _pending.Remove(name);
      RefreshPosts();
    }

    public void Complete(string name) {
      _pending.Remove(name);
      _unlocked.Add(name);
      RefreshPosts();
    }

    public bool Unlocked(string name) => _unlocked.Contains(name);

    public bool IsForeignTarget(string name) => _ironTeeth
      ? name == "UndergroundPile.Folktails" || name == "LargeWindTurbine.Folktails"
      : name == "LargePowerWheel.IronTeeth" || name == "LargeIndustrialPile.IronTeeth";

    public string DisplayNameLocKey(string name) => _buildings.GetBuildingTemplate(name)
      .GetSpec<LabeledEntitySpec>().DisplayNameLocKey;

    private void RefreshPosts() {
      foreach (var post in _posts) {
        post.RefreshResearchRecipes();
      }
    }

  }

  public class CaravanResearchLocker : IToolLocker {

    private readonly CaravanResearch _research;
    private readonly BuildingService _buildings;
    private readonly DialogBoxShower _dialogBoxShower;
    private readonly ILoc _loc;

    public CaravanResearchLocker(CaravanResearch research, BuildingService buildings,
                                 DialogBoxShower dialogBoxShower, ILoc loc) {
      _research = research;
      _buildings = buildings;
      _dialogBoxShower = dialogBoxShower;
      _loc = loc;
    }

    public bool ShouldLock(ITool tool) {
      if (!BuildingToolLocker.TryGetBuildingFromTool(tool, out var building)) {
        return false;
      }
      var name = _buildings.GetTemplateName(building);
      return _research.IsForeignTarget(name) && !_research.Unlocked(name);
    }

    public void TryToUnlock(ITool tool, Action successCallback, Action failCallback) {
      _dialogBoxShower.Create().SetMessage(_loc.T("Caravans.Research.TradeOnly"))
          .SetConfirmButton(failCallback).Show();
    }

  }

  public class CaravanResearchToolDisabler : IToolDisabler {

    private readonly CaravanResearch _research;

    public CaravanResearchToolDisabler(CaravanResearch research) {
      _research = research;
    }

    public bool IsEnabled(ITool tool) {
      if (tool is BlockObjectTool buildingTool) {
        var name = buildingTool.Template.GetSpec<Timberborn.TemplateSystem.TemplateSpec>().TemplateName;
        return !_research.IsForeignTarget(name) || _research.Unlocked(name);
      }
      return true;
    }

  }

  public class CaravanResearchToolUnlocker {

    private readonly ToolUnlockingService _toolUnlocking;
    private readonly ToolButtonService _toolButtons;

    public CaravanResearchToolUnlocker(ToolUnlockingService toolUnlocking, ToolButtonService toolButtons) {
      _toolUnlocking = toolUnlocking;
      _toolButtons = toolButtons;
    }

    public void Unlock(string name) {
      foreach (var button in _toolButtons.ToolButtons) {
        if (button.Tool is BlockObjectTool tool &&
            tool.Template.GetSpec<Timberborn.TemplateSystem.TemplateSpec>().TemplateName == name) {
          if (_toolUnlocking.IsLocked(tool)) {
            _toolUnlocking.Unlock(tool);
          }
          button.UpdateToolVisibility();
        }
      }
    }

  }
}