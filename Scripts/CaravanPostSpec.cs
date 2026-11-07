using System.Collections.Immutable;
using Timberborn.BlueprintSystem;
using Timberborn.Goods;

namespace Mods.Caravans.Scripts {
  public record CaravanPostSpec : ComponentSpec {

    [Serialize]
    public int MaxConcurrentCaravans { get; init; }

    [Serialize]
    public string CaravanModel { get; init; }

    [Serialize]
    public float WalkingTilesPerHour { get; init; }

    [Serialize]
    public float MinTravelHours { get; init; }

    [Serialize]
    public float MaxTravelHours { get; init; }

    [Serialize]
    public string NoRouteStatusLocKey { get; init; }

    [Serialize]
    public string NoCapacityStatusLocKey { get; init; }

    [Serialize]
    public ImmutableArray<CaravanTradeSpec> Trades { get; init; }

  }

  public record CaravanTradeSpec {

    [Serialize]
    public string RecipeId { get; init; }

    [Serialize]
    public float AwayHours { get; init; }

    [Serialize]
    public float LossChance { get; init; }

    [Serialize]
    public int ScienceReward { get; init; }

    [Serialize]
    public ImmutableArray<GoodAmountSpec> GoodRewards { get; init; }

  }
}
