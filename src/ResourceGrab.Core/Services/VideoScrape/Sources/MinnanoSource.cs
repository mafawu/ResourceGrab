namespace ResourceGrab.Core.Services.VideoScrape.Sources;

/// <summary>Minnano AV — 日文原名、生日、身高、三围。</summary>
public sealed class MinnanoSource : IVideoActorSource
{
    public string Id => "minnano";
    public string DisplayName => "Minnano AV";

    public async ValueTask<ActorSourceFetchResult> FetchAsync(string actorName, CancellationToken ct)
    {
        await Task.CompletedTask;
        return ActorSourceFetchResult.Failure(Id, ActorSourceOutcome.Disabled, "Not yet implemented");
    }
}
