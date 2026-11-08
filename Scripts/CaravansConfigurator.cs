using Bindito.Core;
using Timberborn.EntityPanelSystem;
using Timberborn.TemplateInstantiation;
using Timberborn.ToolSystem;

namespace Mods.Caravans.Scripts {
  [Context("Game")]
  public class CaravansConfigurator : Configurator {

    protected override void Configure() {
      Bind<MapEdgeRouteFinder>().AsSingleton();
      Bind<CaravanVisualizer>().AsSingleton();
      Bind<CaravanResearch>().AsSingleton();
      Bind<CaravanResearchToolUnlocker>().AsSingleton();
      Bind<CaravanPost>().AsTransient();
      Bind<CaravanPostFragment>().AsSingleton();
      MultiBind<IToolLocker>().To<CaravanResearchLocker>().AsSingleton();
      MultiBind<IToolDisabler>().To<CaravanResearchToolDisabler>().AsSingleton();

      MultiBind<TemplateModule>().ToProvider(ProvideTemplateModule).AsSingleton();
      MultiBind<EntityPanelModule>().ToProvider<EntityPanelModuleProvider>().AsSingleton();
    }

    private static TemplateModule ProvideTemplateModule() {
      var builder = new TemplateModule.Builder();
      builder.AddDecorator<CaravanPostSpec, CaravanPost>();
      return builder.Build();
    }

    private class EntityPanelModuleProvider : IProvider<EntityPanelModule> {

      private readonly CaravanPostFragment _caravanPostFragment;

      public EntityPanelModuleProvider(CaravanPostFragment caravanPostFragment) {
        _caravanPostFragment = caravanPostFragment;
      }

      public EntityPanelModule Get() {
        var builder = new EntityPanelModule.Builder();
        builder.AddMiddleFragment(_caravanPostFragment);
        return builder.Build();
      }

    }

  }
}
