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
    private Toggle _notificationsToggle;
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

      _notificationsToggle = new Toggle {
        name = "CaravanNotificationsToggle",
        text = _loc.T("Caravans.Panel.ShowNotifications"),
        focusable = true,
        tabIndex = 0
      };
      _notificationsToggle.AddToClassList("game-toggle");
      _notificationsToggle.AddToClassList(TextClass);
      _notificationsToggle.style.alignSelf = Align.FlexStart;
      _notificationsToggle.style.marginTop = 4;
      _notificationsToggle.style.borderTopWidth = _notificationsToggle.style.borderBottomWidth = 1;
      _notificationsToggle.style.borderLeftWidth = _notificationsToggle.style.borderRightWidth = 1;
      SetNotificationsFocus(false);
      _notificationsToggle.RegisterCallback<FocusInEvent>(evt => SetNotificationsFocus(true));
      _notificationsToggle.RegisterCallback<FocusOutEvent>(evt => SetNotificationsFocus(false));
      _notificationsToggle.RegisterValueChangedCallback(evt => {
        if (_caravanPost is not null) {
          _caravanPost.NotificationsEnabled = evt.newValue;
        }
      });
      _root.Add(_notificationsToggle);
      return _root;
    }

    public void ShowFragment(BaseComponent entity) {
      _caravanPost = entity.GetComponent<CaravanPost>();
      if (_caravanPost is not null) {
        _notificationsToggle.SetValueWithoutNotify(_caravanPost.NotificationsEnabled);
        _root.ToggleDisplayStyle(true);
        _nextRefresh = 0f;
        UpdateFragment();
      }
    }

    public void ClearFragment() {
      _caravanPost = null;
      _root.ToggleDisplayStyle(false);
    }

    private void SetNotificationsFocus(bool focused) {
      var color = focused ? UnityEngine.Color.white : UnityEngine.Color.clear;
      _notificationsToggle.style.borderTopColor = _notificationsToggle.style.borderBottomColor = color;
      _notificationsToggle.style.borderLeftColor = _notificationsToggle.style.borderRightColor = color;
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
