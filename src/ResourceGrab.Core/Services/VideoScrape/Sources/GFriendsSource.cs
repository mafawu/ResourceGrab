namespace ResourceGrab.Core.Services.VideoScrape.Sources;

/// <summary>GFriends — GitHub 开源头像库。</summary>
public sealed class GFriendsSource : IVideoActorSource
{
    public string Id => "gfriends";
    public string DisplayName => "GFriends";

    public async ValueTask<ActorSourceFetchResult> FetchAsync(string actorName, CancellationToken ct)
    {
        await Task.CompletedTask;
        return ActorSourceFetchResult.Failure(Id, ActorSourceOutcome.Disabled, "Not yet implemented");
    }
}
