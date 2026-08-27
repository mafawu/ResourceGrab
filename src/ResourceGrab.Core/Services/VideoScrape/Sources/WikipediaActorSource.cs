namespace ResourceGrab.Core.Services.VideoScrape.Sources;

/// <summary>Wikipedia — 演员百科信息、别名。</summary>
public sealed class WikipediaActorSource : IVideoActorSource
{
    public string Id => "wikipedia";
    public string DisplayName => "Wikipedia";

    public async ValueTask<ActorSourceFetchResult> FetchAsync(string actorName, CancellationToken ct)
    {
        await Task.CompletedTask;
        return ActorSourceFetchResult.Failure(Id, ActorSourceOutcome.Disabled, "Not yet implemented");
    }
}
