using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CrossSourceMerge;

/// <summary>
/// Registers the task and the schedule with the server.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<Tasks.MergeTask>();
        serviceCollection.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<Tasks.MergeTask>());
        serviceCollection.AddHostedService<Takt>();
    }
}
