using System.Collections.Generic;
using System.Text;
using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.EntityPanelSystem;
using Timberborn.Localization;
using UnityEngine.UIElements;

namespace Mods.Caravans.Scripts {
  internal class CaravanPostFragment : IEntityPanelFragment {

    private const string SubPanelClass = "entity-sub-panel";
    private const string SubBoxClass = "bg-sub-box--green";
    private const string TextClass = "game-text-normal";
    private const float RefreshIntervalSeconds = 0.25f;

    private readonly ILoc _loc;
    private readonly StringBuilder _text = new StringBuilder();
    private VisualElement _root;
    private Label _label;
    private CaravanPost _caravanPost;
    private float _nextRefresh;

    public CaravanPostFragment(ILoc loc) {
      _loc = loc;
    }

    public VisualElement InitializeFragment() {
      _root = new NineSliceVisualElement();
      _root.AddToClassList(SubPanelClass);
      _root.AddToClassList(SubBoxClass);
      _root.ToggleDisplayStyle(false);

      _label = new Label();
      _label.AddToClassList(TextClass);
      _label.style.color = UnityEngine.Color.white;
      _label.style.whiteSpace = WhiteSpace.Normal;
      _root.Add(_label);
      return _root;
    }

    public void ShowFragment(BaseComponent entity) {
      _caravanPost = entity.GetComponent<CaravanPost>();
      if (_caravanPost is not null) {
        _root.ToggleDisplayStyle(true);
        _nextRefresh = 0f;
        UpdateFragment();
      }
    }

    public void ClearFragment() {
      _caravanPost = null;
      _root.ToggleDisplayStyle(false);
    }

    public void UpdateFragment() {
      if (_caravanPost is null || UnityEngine.Time.unscaledTime < _nextRefresh) {
        return;
      }
      _nextRefresh = UnityEngine.Time.unscaledTime + RefreshIntervalSeconds;
      _text.Clear();
      AppendTradeInfo();
      AppendCaravans();
      var text = _text.ToString();
      if (text != _label.text) {
        _label.text = text;
      }
    }

    private void AppendTradeInfo() {
      var trade = _caravanPost.CurrentTrade;
      var recipe = _caravanPost.CurrentRecipe;
      if (trade == null || recipe == null) {
        _text.AppendLine(_loc.T("Caravans.Panel.NoTrade"));
        return;
      }
      _text.AppendLine(_loc.T("Caravans.Panel.Loading", recipe.CycleDurationInHours));
      _text.AppendLine(_loc.T("Caravans.Panel.Trip", _caravanPost.TripHours(trade), trade.LossChance * 100f));
      _text.AppendLine(_loc.T("Caravans.Panel.Reward", FormatRewards(trade)));
    }

    private string FormatRewards(CaravanTradeSpec trade) {
      var rewards = new List<string>();
      if (trade.ScienceReward > 0) {
        rewards.Add(_loc.T("Caravans.Panel.ScienceReward", trade.ScienceReward));
      }
      foreach (var reward in trade.GoodRewards) {
        rewards.Add($"{reward.Amount} {_loc.T($"Good.{reward.Id}.PluralDisplayName")}");
      }
      return string.Join(", ", rewards);
    }

    private void AppendCaravans() {
      _text.AppendLine(_loc.T("Caravans.Panel.Away", _caravanPost.CaravansOnTheRoad, _caravanPost.MaxCaravans));
      for (var i = 0; i < _caravanPost.CaravansOnTheRoad; i++) {
        _text.AppendLine(_loc.T("Caravans.Panel.Returns", _caravanPost.HoursUntilReturn(i)));
      }
    }

  }
}
