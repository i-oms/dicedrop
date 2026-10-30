using Ludo.Application.Abstractions;
using Ludo.Application.Protocol;
using Ludo.Application.Session;
using Ludo.Domain.Players;

namespace Ludo.Application.Sync;

/// <summary>
/// Forms the lobby and the peer mesh. The host only keeps the member list and answers newcomers; games
/// are run by whoever starts them. If the host leaves, the longest-standing member takes over, so the
/// lobby and any game in it carry on.
/// </summary>
internal sealed class LobbyProtocol(SessionContext context, MatchProtocol matches)
{
    public void OnPeerConnected(PeerConnection connection)
    {
        if (context.Stage != SessionStage.Lobby)
        {
            context.Network.Disconnect(connection.Peer);
            return;
        }

        context.Peers[connection.Peer] = new PeerInfo(connection.Peer, connection.Host, connection.ListenPort);
        context.Network.Send(
            connection.Peer,
            new HelloMessage(ProtocolInfo.Version, context.LocalName, context.LocalProfile));
    }

    public DispatchResult OnHello(PlayerId from, HelloMessage hello)
    {
        if (hello.ProtocolVersion != ProtocolInfo.Version)
        {
            return Reject(from, "Your game version is incompatible with this lobby.");
        }

        if (context.Stage != SessionStage.Lobby)
        {
            return Reject(from, "The game has already started.");
        }

        if (!context.Peers.TryGetValue(from, out var peer) || peer.IsGreeted)
        {
            return Reject(from, "Invalid greeting.");
        }

        string name;
        try
        {
            name = SessionContext.ValidateName(hello.Name);
        }
        catch (Common.SessionException)
        {
            return Reject(from, "Invalid player name.");
        }

        if (context.IsHost && context.Roster.Count >= SessionContext.MaxPlayers && !context.Roster.Exists(r => r.Id == from))
        {
            return Reject(from, "The lobby is full.");
        }

        peer.Name = name;
        peer.Profile = SessionContext.ValidateProfile(hello.Profile);
        matches.OnMemberGreeted(from);

        if (!context.IsHost)
        {
            ReportConnectivity();
            return DispatchResult.Handled;
        }

        if (!context.Roster.Exists(r => r.Id == from))
        {
            context.Roster.Add(new RosterSeat(from, name));
            context.AddLog($"{name} joined.");
        }

        var others = context.Peers.Values
            .Where(p => p.Id != from && context.Roster.Exists(r => r.Id == p.Id))
            .Select(p => new PeerAddress(p.Id, p.Host, p.ListenPort))
            .ToArray();
        context.Network.Send(from, new PeerDirectoryMessage(others));
        BroadcastRoster();
        return DispatchResult.Handled;
    }

    public DispatchResult OnPeerDirectory(PlayerId from, PeerDirectoryMessage directory, Action<PeerAddress> connect)
    {
        if (context.Stage != SessionStage.Lobby || from != context.HostId)
        {
            return DispatchResult.Ignored;
        }

        foreach (var address in directory.Peers)
        {
            if (address.Id != context.LocalId && !context.Peers.ContainsKey(address.Id))
            {
                connect(address);
            }
        }

        return DispatchResult.Handled;
    }

    public DispatchResult OnPeerStatus(PlayerId from, PeerStatusMessage status)
    {
        if (context.Stage != SessionStage.Lobby || !context.IsHost
            || context.Roster.Find(r => r.Id == from) is not { } seat)
        {
            return DispatchResult.Ignored;
        }

        seat.ConnectedTo.Clear();
        seat.ConnectedTo.UnionWith(status.ConnectedPeers);
        BroadcastRoster();
        return DispatchResult.Handled;
    }

    public DispatchResult OnLobbyState(PlayerId from, LobbyStateMessage state)
    {
        if (context.Stage != SessionStage.Lobby || from != context.HostId)
        {
            return DispatchResult.Ignored;
        }

        context.Roster.Clear();
        foreach (var entry in state.Roster.Take(SessionContext.MaxPlayers))
        {
            context.Roster.Add(new RosterSeat(entry.Id, entry.Name) { Ready = entry.Ready });
        }

        return DispatchResult.Handled;
    }

    public void RenameLocal(string newName)
    {
        RequireLobby();
        var name = SessionContext.ValidateName(newName);
        if (name == context.LocalName)
        {
            return;
        }

        // Links that are not greeted yet get the new name in our Hello, which is sent after this.
        context.Rename(name);
        context.Network.Broadcast(new RenameMessage(name));
        ApplyRename(context.LocalId, name);
    }

    public DispatchResult OnRename(PlayerId from, RenameMessage rename)
    {
        if (context.Stage != SessionStage.Lobby || !context.Peers.TryGetValue(from, out var peer) || !peer.IsGreeted)
        {
            return DispatchResult.Ignored;
        }

        string name;
        try
        {
            name = SessionContext.ValidateName(rename.Name);
        }
        catch (Common.SessionException)
        {
            return DispatchResult.Ignored;
        }

        peer.Name = name;
        ApplyRename(from, name);
        return DispatchResult.Handled;
    }

    public void ChatLocal(string text)
    {
        RequireLobby();
        var line = SessionContext.NormalizeChat(text)
            ?? throw new Common.SessionException($"Messages must be 1 to {SessionLimits.MaxChatLength} characters.");
        context.Network.Broadcast(new ChatMessage(line));
        context.AddChat(context.LocalId, line, isLocal: true);
    }

    public DispatchResult OnChat(PlayerId from, ChatMessage chat)
    {
        if (context.Stage != SessionStage.Lobby
            || !context.Peers.TryGetValue(from, out var peer) || !peer.IsGreeted
            || SessionContext.NormalizeChat(chat.Text) is not { } line)
        {
            return DispatchResult.Ignored;
        }

        context.AddChat(from, line, isLocal: false);
        return DispatchResult.Handled;
    }

    public void DirectChatLocal(string peerKey, string text)
    {
        RequireLobby();
        var peer = RequirePeer(peerKey);
        var line = SessionContext.NormalizeChat(text)
            ?? throw new Common.SessionException($"Messages must be 1 to {SessionLimits.MaxChatLength} characters.");
        context.Network.Send(peer, new DirectChatMessage(line));
        context.AddDirect(peer, context.LocalId, line, ChatLineKind.Message);
    }

    public DispatchResult OnDirectChat(PlayerId from, DirectChatMessage chat)
    {
        if (context.Stage != SessionStage.Lobby
            || !context.Peers.TryGetValue(from, out var peer) || !peer.IsGreeted
            || SessionContext.NormalizeChat(chat.Text) is not { } line)
        {
            return DispatchResult.Ignored;
        }

        context.AddDirect(from, from, line, ChatLineKind.Message);
        return DispatchResult.Handled;
    }

    public void NudgeLocal(string peerKey)
    {
        RequireLobby();
        var target = RequirePeer(peerKey);
        var peer = context.Peers[target];
        var now = context.Time.GetUtcNow();
        if (peer.LastNudgeOut is { } last && now - last < SessionLimits.NudgeCooldown)
        {
            var wait = Math.Ceiling((SessionLimits.NudgeCooldown - (now - last)).TotalSeconds);
            throw new Common.SessionException($"You can nudge {peer.Name} again in {wait} s.");
        }

        peer.LastNudgeOut = now;
        context.Network.Send(target, new NudgeMessage());
        context.AddDirect(target, context.LocalId, $"You nudged {peer.Name}.", ChatLineKind.Nudge);
    }

    public DispatchResult OnNudge(PlayerId from)
    {
        if (context.Stage != SessionStage.Lobby || !context.Peers.TryGetValue(from, out var peer) || !peer.IsGreeted)
        {
            return DispatchResult.Ignored;
        }

        // A little slack under the sender's own cooldown, for clock and delivery jitter.
        var now = context.Time.GetUtcNow();
        if (peer.LastNudgeIn is { } last && now - last < SessionLimits.NudgeCooldown / 2)
        {
            return DispatchResult.Ignored;
        }

        peer.LastNudgeIn = now;
        context.AddDirect(from, from, $"{peer.Name} nudged you.", ChatLineKind.Nudge);
        context.Presenter.PresentNudge(new NudgeView(SessionContext.KeyOf(from), peer.Name!));
        return DispatchResult.Handled;
    }

    public void CloseEveryoneLocal()
    {
        RequireLobby();
        context.Network.Broadcast(new CloseAppMessage());
    }

    public DispatchResult OnCloseApp(PlayerId from)
    {
        if (context.Stage != SessionStage.Lobby || !context.Peers.TryGetValue(from, out var peer) || !peer.IsGreeted)
        {
            return DispatchResult.Ignored;
        }

        context.AddLog($"{peer.Name} closed the window for everyone.");
        context.Presenter.PresentCloseRequest(peer.Name!);
        return DispatchResult.Handled;
    }

    /// <summary>Sets our avatar. Outside a lobby it is just kept, and goes out in the next Hello.</summary>
    public void ProfileLocal(PlayerProfile profile)
    {
        var valid = SessionContext.ValidateProfile(profile) ?? throw new Common.SessionException("That avatar is not valid.");
        if (profile.Photo is not null && valid.Photo is null)
        {
            throw new Common.SessionException($"Avatar photos must be PNG or JPEG and at most {SessionLimits.MaxPhotoBytes / 1024} KB.");
        }

        context.LocalProfile = valid;
        if (context.Stage == SessionStage.Lobby)
        {
            context.Network.Broadcast(new ProfileMessage(context.LocalProfile));
        }
    }

    public DispatchResult OnProfile(PlayerId from, ProfileMessage message)
    {
        if (context.Stage != SessionStage.Lobby || !context.Peers.TryGetValue(from, out var peer) || !peer.IsGreeted
            || SessionContext.ValidateProfile(message.Profile) is not { } profile)
        {
            return DispatchResult.Ignored;
        }

        peer.Profile = profile;
        return DispatchResult.Handled;
    }

    public async Task<DispatchResult> OnJoinRejectedAsync(PlayerId from, JoinRejectedMessage rejected)
    {
        if (context.Stage != SessionStage.Lobby || from != context.HostId || IsSeatedInRunningGame)
        {
            return DispatchResult.Ignored;
        }

        context.Notify($"Could not join: {rejected.Reason}");
        await context.TeardownAsync().ConfigureAwait(false);
        return DispatchResult.Handled;
    }

    public async Task<DispatchResult> OnLobbyClosedAsync(PlayerId from)
    {
        if (context.Stage != SessionStage.Lobby || from != context.HostId || IsSeatedInRunningGame)
        {
            return DispatchResult.Ignored;
        }

        // No notice: going idle sends us straight on to the lobby our host moved to.
        await context.TeardownAsync().ConfigureAwait(false);
        return DispatchResult.Handled;
    }

    public async Task OnPeerGoneAsync(PlayerId peer)
    {
        if (!context.Peers.Remove(peer) || context.Stage != SessionStage.Lobby)
        {
            return;
        }

        var seat = context.Roster.Find(r => r.Id == peer);
        if (peer == context.HostId)
        {
            await HandOverHostAsync(seat).ConfigureAwait(false);
            return;
        }

        if (context.IsHost)
        {
            if (seat is not null)
            {
                context.Roster.Remove(seat);
                context.AddLog($"{seat.Name} left the lobby.");
            }

            BroadcastRoster();
        }
        else
        {
            ReportConnectivity();
        }
    }

    /// <summary>Host side: recomputes who is fully meshed, then shares the roster and refreshes the LAN beacon.</summary>
    public void BroadcastRoster()
    {
        var memberIds = context.Roster.Select(r => r.Id).ToArray();
        var hostLinks = context.Peers.Values.Where(p => p.IsGreeted).Select(p => p.Id).ToHashSet();
        foreach (var seat in context.Roster)
        {
            var isLocal = seat.Id == context.LocalId;
            HashSet<PlayerId> links = isLocal ? hostLinks : [.. seat.ConnectedTo, context.LocalId];
            var meshed = memberIds.All(id => id == seat.Id || links.Contains(id));
            seat.Ready = meshed && (isLocal || hostLinks.Contains(seat.Id));
        }

        context.Network.Broadcast(new LobbyStateMessage(
            context.Roster.Select(r => new RosterEntry(r.Id, r.Name, r.Ready)).ToArray()));
        context.Discovery.Advertise(new LobbyAdvertisement(
            context.LobbyId,
            context.LocalName,
            context.Network.ListenPort,
            context.Roster.Count,
            SessionContext.MaxPlayers,
            ProtocolInfo.Version));
    }

    /// <summary>
    /// The host left: the first remaining member in join order takes over. Everyone holds the same roster,
    /// so everyone picks the same successor, and the mesh is already complete, so nobody reconnects.
    /// </summary>
    private async Task HandOverHostAsync(RosterSeat? oldHost)
    {
        if (oldHost is not null)
        {
            context.Roster.Remove(oldHost);
        }

        var successor = context.Roster.Find(r =>
            r.Id == context.LocalId || (context.Peers.TryGetValue(r.Id, out var p) && p.IsGreeted));
        if (successor is null)
        {
            // We never got the member list, so there is nothing to keep: find a lobby again.
            await context.TeardownAsync().ConfigureAwait(false);
            return;
        }

        context.HostId = successor.Id;
        context.AddLog($"{oldHost?.Name ?? "The host"} left the lobby. {(context.IsHost ? "You are" : $"{successor.Name} is")} the host now.");
        if (context.IsHost)
        {
            foreach (var member in context.Roster)
            {
                member.ConnectedTo.Clear();
            }

            BroadcastRoster();
        }
        else
        {
            ReportConnectivity();
        }
    }

    private void ReportConnectivity()
    {
        if (context.HostId is { } host)
        {
            var linked = context.Peers.Values.Where(p => p.IsGreeted).Select(p => p.Id).ToArray();
            context.Network.Send(host, new PeerStatusMessage(linked));
        }
    }

    /// <summary>Everyone relabels the seat at once; the host then confirms it to all with a fresh roster.</summary>
    private void ApplyRename(PlayerId player, string name)
    {
        if (context.Roster.Find(r => r.Id == player) is { } seat && seat.Name != name)
        {
            context.AddLog($"{seat.Name} is now {name}.");
            seat.Name = name;
        }

        if (context.IsHost)
        {
            BroadcastRoster();
        }
    }

    /// <summary>The host has no say in a game: if it really goes away, the lost link hands the lobby over and play goes on.</summary>
    private bool IsSeatedInRunningGame => context.SeatedMatch is not null;

    private PlayerId RequirePeer(string key) =>
        SessionContext.FindByKey(key) is { } id && id != context.LocalId
            && context.Peers.TryGetValue(id, out var peer) && peer.IsGreeted
            ? id
            : throw new Common.SessionException("That person is no longer in the lobby.");

    private void RequireLobby()
    {
        if (context.Stage != SessionStage.Lobby)
        {
            throw new Common.SessionException("That is only possible in the lobby.");
        }
    }

    private DispatchResult Reject(PlayerId peer, string reason)
    {
        context.Network.Send(peer, new JoinRejectedMessage(reason));
        context.Network.Disconnect(peer);
        context.Peers.Remove(peer);
        return DispatchResult.Ignored;
    }
}
