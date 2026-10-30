using System.Net;
using System.Net.Sockets;
using Ludo.Application.Abstractions;
using Ludo.Application.Common;
using Ludo.Application.Protocol;
using Ludo.Application.Session;
using Ludo.Infrastructure.Crypto;
using Ludo.Infrastructure.Networking;

namespace Ludo.Application.Tests;

/// <summary>End-to-end: real TCP peers on loopback play whole games driven by bots.</summary>
public sealed class PeerSessionTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Any_member_starts_a_game_that_players_finish_and_spectators_follow(int playerCount)
    {
        // One extra member declines and watches; a member who is not the host runs the game.
        var peers = Enumerable.Range(0, playerCount + 1).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            var initiator = peers[1];
            var spectator = peers[0];
            var players = peers.Where(p => p != spectator).ToArray();

            await initiator.App.ProposeGame.ExecuteAsync();
            await WaitUntil(() => peers.All(p => p.Latest.Match?.Phase == MatchPhaseView.Inviting), "invite did not reach everyone");
            foreach (var player in players.Where(p => p != initiator))
            {
                await player.App.AcceptInvite.ExecuteAsync(player.Latest.Match!.Id);
            }

            await spectator.App.DeclineInvite.ExecuteAsync(spectator.Latest.Match!.Id);
            await WaitUntil(
                () => initiator.Latest.Match!.CanStart
                    && initiator.Latest.Match.Invitees.Count(i => i.Answer == InviteAnswer.Accepted) == playerCount,
                "answers never arrived");
            await initiator.App.StartGame.ExecuteAsync();
            await WaitUntil(() => players.All(p => p.Latest.Match is { IsPlaying: true, Game: not null }), "not every player started");
            Assert.False(spectator.Latest.Match!.IsPlaying);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await Task.WhenAll(players.Select(p => p.PlayAsync(timeout.Token)));
            await WaitUntil(() => spectator.Latest.Match?.Phase == MatchPhaseView.Finished, "spectator never saw the end");

            var results = peers.Select(p => Describe(p.Latest.Match!.Game!)).ToArray();
            Assert.All(results, r => Assert.Equal(results[0], r));
            Assert.All(peers, p => Assert.DoesNotContain(p.Latest.Match!.Log, l => l.Contains("Warning", StringComparison.Ordinal)));
            Assert.Equal(playerCount, peers[0].Latest.Match!.Game!.Seats.Count(s => s.Rank is not null));

            // The lobby outlives the game, so anyone can start the next one.
            Assert.All(peers, p => Assert.True(p.Latest.CanProposeGame));
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task Someone_rejoining_before_their_old_link_drops_keeps_one_lobby_entry()
    {
        var id = Ludo.Domain.Players.PlayerId.New();
        var peers = new[] { new TestPeer("Host"), new TestPeer("Other"), new TestPeer("Gone", localId: id) };
        var back = new TestPeer("Back", localId: id);
        try
        {
            var port = await FormLobbyAsync(peers);
            await StartGameAsync(initiator: peers[0], peers);

            // The first copy is still connected: to the others, this is a link that hasn't timed out yet.
            await back.App.JoinLobby.ExecuteAsync(back.Name, "127.0.0.1", port);

            await WaitUntil(
                () => peers[..2].All(p => p.Latest.Members.Count == 3 && p.Latest.Members.Any(m => m.Name == "Back")),
                "the returning player never replaced their old entry");
            Assert.All(peers[..2], p => Assert.DoesNotContain(p.Latest.Members, m => m.Name == "Gone"));

            // Their seat is gone, so the game carries on and they can only watch.
            await WaitUntil(
                () => peers[0].Latest.Match?.Game?.Seats.Single(s => s.Name == "Gone").Standing == SeatStanding.Left,
                "the old seat still holds up the game");
            await WaitUntil(() => back.Latest.Match is { Phase: MatchPhaseView.Playing, IsPlaying: false }, "they never got the game");
        }
        finally
        {
            await back.App.DisposeAsync();
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task A_player_leaving_the_game_stays_in_the_lobby_and_watches()
    {
        var peers = Enumerable.Range(0, 3).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            await StartGameAsync(initiator: peers[0], peers);

            await peers[1].App.LeaveGame.ExecuteAsync();

            // Seats follow join order, which depends on whose greeting reached the host first.
            await WaitUntil(
                () => peers.All(p => p.Latest.Match?.Game?.Seats.Single(s => s.Name == "P1").Standing == SeatStanding.Left),
                "leaver was not dropped everywhere");
            Assert.False(peers[1].Latest.Match!.IsPlaying);
            Assert.Equal(3, peers[0].Latest.Members.Count);
            await WaitUntil(() => !peers[0].Latest.Members.Single(m => m.Name == "P1").InGame, "leaver still shown as playing");

            // The leaver keeps following the game between the other two.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await Task.WhenAll(peers[0].PlayAsync(timeout.Token), peers[2].PlayAsync(timeout.Token));
            await WaitUntil(() => peers[1].Latest.Match?.Phase == MatchPhaseView.Finished, "leaver stopped following");
            Assert.Equal(Describe(peers[0].Latest.Match!.Game!), Describe(peers[1].Latest.Match!.Game!));
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task The_initiator_can_stop_the_game_for_everyone()
    {
        var peers = Enumerable.Range(0, 3).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            await StartGameAsync(initiator: peers[2], peers);

            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.StopGame.ExecuteAsync());
            await peers[2].App.StopGame.ExecuteAsync();

            await WaitUntil(() => peers.All(p => p.Latest.Match?.Phase == MatchPhaseView.Stopped), "stop did not reach everyone");
            Assert.All(peers, p => Assert.Equal(SessionStage.Lobby, p.Latest.Stage));
            Assert.All(peers, p => Assert.True(p.Latest.CanProposeGame));
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task The_lobby_hands_over_to_a_new_host_and_the_game_carries_on()
    {
        var peers = Enumerable.Range(0, 3).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            await StartGameAsync(initiator: peers[1], peers);

            // The host, a player but not the initiator, quits the app.
            await peers[0].App.LeaveSession.ExecuteAsync();

            // The initiator sits first; the others follow join order, so find the old host by name.
            await WaitUntil(
                () => peers.Skip(1).All(p => p.Latest.Match?.Game?.Seats.Single(s => s.Name == "P0").Standing == SeatStanding.Left),
                "remaining peers did not drop the host");
            await WaitUntil(
                () => peers.Skip(1).Count(p => p.Latest.IsHost) == 1
                    && peers.Skip(1).All(p => p.Latest.Members.Count == 2 && p.Latest.Members.Count(m => m.IsHost) == 1),
                "no new host took over");
            Assert.All(peers.Skip(1), p => Assert.Equal(MatchPhaseView.Playing, p.Latest.Match!.Phase));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await Task.WhenAll(peers[1].PlayAsync(timeout.Token), peers[2].PlayAsync(timeout.Token));
            Assert.Equal(Describe(peers[1].Latest.Match!.Game!), Describe(peers[2].Latest.Match!.Game!));
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task Someone_joining_mid_game_can_watch_it()
    {
        var peers = Enumerable.Range(0, 2).Select(i => new TestPeer($"P{i}")).ToArray();
        var latecomer = new TestPeer("Late");
        try
        {
            var port = await FormLobbyAsync(peers);
            await StartGameAsync(initiator: peers[1], peers);
            await peers[1].App.RollDice.ExecuteAsync();
            await WaitUntil(() => peers[1].Latest.Match?.Game?.RollCount == 1, "roll never completed");

            await latecomer.App.JoinLobby.ExecuteAsync(latecomer.Name, "127.0.0.1", port);

            await WaitUntil(
                () => latecomer.Latest.Match is { Phase: MatchPhaseView.Playing, Game.RollCount: >= 1 },
                "latecomer never got the game");
            Assert.False(latecomer.Latest.Match!.IsPlaying);
            Assert.False(latecomer.Latest.CanProposeGame);
        }
        finally
        {
            await latecomer.App.DisposeAsync();
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task An_invite_sent_while_a_game_runs_does_not_disturb_it()
    {
        var peers = Enumerable.Range(0, 3).Select(i => new TestPeer($"P{i}")).ToArray();
        var latecomer = new TestPeer("Late");
        try
        {
            var port = await FormLobbyAsync(peers);
            await StartGameAsync(initiator: peers[1], peers);
            var gameId = peers[1].Latest.Match!.Id;

            // Fire an invite the moment we are in, before the running game has reached us.
            await latecomer.App.JoinLobby.ExecuteAsync(latecomer.Name, "127.0.0.1", port);
            await WaitUntil(() => latecomer.Latest.Members.Count >= 2, "latecomer never got the member list");
            try
            {
                await latecomer.App.ProposeGame.ExecuteAsync();
            }
            catch (SessionException)
            {
                // The running game got here first, which is fine too.
            }

            await WaitUntil(
                () => latecomer.Latest.Matches.Any(m => m is { Phase: MatchPhaseView.Playing, IsPlaying: false } && m.Id == gameId),
                "latecomer never ended up watching the running game");
            Assert.All(peers, p => Assert.Equal(gameId, p.Latest.Match!.Id));
            Assert.All(peers, p => Assert.True(p.Latest.Match!.IsPlaying));
            Assert.False(latecomer.Latest.CanProposeGame);

            await peers[1].App.RollDice.ExecuteAsync();
            await WaitUntil(
                () => peers.All(p => p.Latest.Match?.Game?.RollCount == 1)
                    && latecomer.Latest.Matches.Single(m => m.Id == gameId).Game?.RollCount == 1,
                "the game stalled after the stray invite");
        }
        finally
        {
            await latecomer.App.DisposeAsync();
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task A_host_cannot_boot_a_seated_player_out_of_a_running_game()
    {
        var networks = Enumerable.Range(0, 3).Select(_ => new FilteredNetwork(new TcpPeerNetwork())).ToArray();
        var peers = networks.Select((n, i) => new TestPeer($"P{i}", network: n)).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            await StartGameAsync(initiator: peers[1], peers);
            var host = SessionContext.FindByKey(peers[0].Latest.LocalKey)!.Value;

            networks[2].Inject(host, new LobbyClosedMessage());
            networks[2].Inject(host, new JoinRejectedMessage("go away"));
            await peers[1].App.RollDice.ExecuteAsync();

            await WaitUntil(() => peers.All(p => p.Latest.Match?.Game?.RollCount == 1), "the game stalled after the host's messages");
            Assert.True(peers[2].Latest.Match!.IsPlaying);
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task A_forged_replay_cannot_cancel_an_invite_or_pose_as_someone_elses_game()
    {
        var networks = Enumerable.Range(0, 3).Select(_ => new FilteredNetwork(new TcpPeerNetwork())).ToArray();
        var peers = networks.Select((n, i) => new TestPeer($"P{i}", network: n)).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            var spectator = SessionContext.FindByKey(peers[0].Latest.LocalKey)!.Value;
            var other = SessionContext.FindByKey(peers[1].Latest.LocalKey)!.Value;

            // Idle: a replay whose seat 0 is not its sender is not that sender's game to show.
            networks[2].Inject(spectator, ReplayWithFirstSeat(other));
            await Task.Delay(200);
            Assert.Null(peers[2].Latest.Match);

            await peers[1].App.ProposeGame.ExecuteAsync();
            await WaitUntil(() => peers.All(p => p.Latest.Match?.Phase == MatchPhaseView.Inviting), "invite did not reach everyone");
            var inviteId = peers[1].Latest.Match!.Id;

            networks[2].Inject(spectator, ReplayWithFirstSeat(spectator));
            await Task.Delay(200);

            Assert.All(peers, p => Assert.Equal(inviteId, p.Latest.Match!.Id));
            Assert.All(peers, p => Assert.Equal(MatchPhaseView.Inviting, p.Latest.Match!.Phase));
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    private static MatchReplayMessage ReplayWithFirstSeat(Ludo.Domain.Players.PlayerId seat) =>
        new(Guid.NewGuid(), GameCatalog.Ludo.Id, Ludo.Domain.Gameplay.GameRules.Classic, [new SeatAssignment(seat, "Fake", new byte[32])], []);

    [Fact]
    public async Task Crossing_invites_both_stay_open()
    {
        var peers = Enumerable.Range(0, 3).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);

            await Task.WhenAll(peers[1].App.ProposeGame.ExecuteAsync(), peers[2].App.ProposeGame.ExecuteAsync());

            await WaitUntil(
                () => peers.All(p => p.Latest.Matches.Count(m => m.Phase == MatchPhaseView.Inviting) == 2),
                "an invite went missing");
            Assert.False(peers[1].Latest.CanProposeGame);
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task Two_games_run_side_by_side_without_touching_each_other()
    {
        // P0 stays out and watches both.
        var peers = Enumerable.Range(0, 5).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            var first = await StartPairAsync(peers[1], peers[2], peers);
            var second = await StartPairAsync(peers[3], peers[4], peers);
            Assert.NotEqual(first, second);

            // Only the watcher is left, so nobody can be invited any more, and those playing can't start another.
            await WaitUntil(() => peers.Skip(1).All(p => p.Latest.Match?.IsPlaying == true && !p.Latest.CanProposeGame), "players still look free");
            await WaitUntil(() => !peers[0].Latest.CanProposeGame, "the watcher still sees someone free");
            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.ProposeGame.ExecuteAsync());

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await Task.WhenAll(peers.Skip(1).Select(p => p.PlayAsync(timeout.Token)));
            await WaitUntil(
                () => peers.All(p => p.Latest.Matches.Count(m => m.Phase == MatchPhaseView.Finished) == 2),
                "someone never saw both games end");

            foreach (var id in new[] { first, second })
            {
                var results = peers.Select(p => Describe(p.Latest.Matches.Single(m => m.Id == id).Game!)).ToArray();
                Assert.All(results, r => Assert.Equal(results[0], r));
            }

            // Each game has its own board and record, whoever is looking.
            var firstLog = peers[0].Latest.Matches.Single(m => m.Id == first).Log;
            Assert.NotEmpty(firstLog);
            Assert.DoesNotContain(firstLog, l => l.Contains("P3", StringComparison.Ordinal) || l.Contains("P4", StringComparison.Ordinal));
            Assert.All(peers, p => Assert.DoesNotContain(p.Latest.Matches.SelectMany(m => m.Log), l => l.Contains("Warning", StringComparison.Ordinal)));
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task Eight_players_split_into_two_boards_of_four()
    {
        var peers = Enumerable.Range(0, 8).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            var first = await StartGroupAsync(peers[0], peers[1..4], peers);
            var second = await StartGroupAsync(peers[4], peers[5..8], peers);

            // Lobby chat still reaches everyone while both games run.
            await peers[1].App.SendChat.ExecuteAsync("good luck");
            await WaitUntil(() => peers.All(p => p.Latest.Chat.Count == 1), "chat did not cross the two games");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            await Task.WhenAll(peers.Select(p => p.PlayAsync(timeout.Token)));
            await WaitUntil(
                () => peers.All(p => p.Latest.Matches.Count(m => m.Phase == MatchPhaseView.Finished) == 2),
                "someone never saw both games end");

            foreach (var id in new[] { first, second })
            {
                var results = peers.Select(p => Describe(p.Latest.Matches.Single(m => m.Id == id).Game!)).ToArray();
                Assert.All(results, r => Assert.Equal(results[0], r));
                Assert.Equal(4, peers[0].Latest.Matches.Single(m => m.Id == id).Game!.Seats.Count);
            }
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task A_new_game_leaves_out_whoever_is_already_playing()
    {
        var peers = Enumerable.Range(0, 4).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            await StartPairAsync(peers[0], peers[1], peers);
            await WaitUntil(() => peers[2].Latest.Members.Count(m => m.InGame) == 2, "players not shown as busy");

            // Inviting everyone means everyone free; naming only busy players is refused.
            await Assert.ThrowsAsync<SessionException>(() => peers[2].App.ProposeGame.ExecuteAsync(GameCatalog.Ludo.Id, [peers[1].Latest.LocalKey]));
            await peers[2].App.ProposeGame.ExecuteAsync();
            var invite = peers[2].Latest.Match!;
            Assert.Equal(["P2", "P3"], invite.Invitees.Select(i => i.Name).Order());

            await WaitUntil(() => peers[1].Latest.Matches.Any(m => m.Id == invite.Id), "invite never reached a player");
            Assert.False(peers[1].Latest.Matches.Single(m => m.Id == invite.Id).CanAnswer);
            await Assert.ThrowsAsync<SessionException>(() => peers[1].App.AcceptInvite.ExecuteAsync(invite.Id));
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task Saying_yes_to_one_invite_takes_back_a_yes_to_the_other_and_withdraws_your_own()
    {
        var peers = Enumerable.Range(0, 4).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            await peers[0].App.ProposeGame.ExecuteAsync();
            await peers[2].App.ProposeGame.ExecuteAsync();
            var a = peers[0].Latest.Match!.Id;
            var b = peers[2].Latest.Match!.Id;
            await WaitUntil(() => peers.All(p => p.Latest.Matches.Count(m => m.Phase == MatchPhaseView.Inviting) == 2), "invites did not reach everyone");

            await peers[1].App.AcceptInvite.ExecuteAsync(a);
            await WaitUntil(() => peers[0].Latest.Match!.CanStart, "first yes never arrived");

            await peers[1].App.AcceptInvite.ExecuteAsync(b);
            await WaitUntil(
                () => peers[0].Latest.Matches.Single(m => m.Id == a).Invitees.Single(i => i.Name == "P1").Answer == InviteAnswer.Declined
                    && peers[2].Latest.Matches.Single(m => m.Id == b).CanStart,
                "the first yes was not taken back");
            Assert.False(peers[0].Latest.Match!.CanStart);

            // Joining someone else's game withdraws the invite we had open ourselves.
            await peers[2].App.AcceptInvite.ExecuteAsync(a);
            await WaitUntil(
                () => peers.All(p => p.Latest.Matches.Single(m => m.Id == b).Phase == MatchPhaseView.Stopped),
                "the joiner's own invite stayed open");
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task A_start_that_seats_someone_who_moved_on_only_loses_that_seat()
    {
        var networks = Enumerable.Range(0, 3).Select(_ => new FilteredNetwork(new TcpPeerNetwork())).ToArray();
        var peers = networks.Select((n, i) => new TestPeer($"P{i}", network: n)).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            await peers[0].App.ProposeGame.ExecuteAsync();
            await peers[2].App.ProposeGame.ExecuteAsync();
            var a = peers[0].Latest.Match!.Id;
            var b = peers[2].Latest.Match!.Id;
            await WaitUntil(() => peers.All(p => p.Latest.Matches.Count(m => m.Phase == MatchPhaseView.Inviting) == 2), "invites did not reach everyone");
            await peers[1].App.AcceptInvite.ExecuteAsync(a);
            await WaitUntil(() => peers[0].Latest.Match!.CanStart, "yes never arrived");

            // P0 never hears P1 change their mind, so it seats P1 anyway.
            networks[0].Mute(SessionContext.FindByKey(peers[1].Latest.LocalKey)!.Value);
            await peers[1].App.AcceptInvite.ExecuteAsync(b);
            await WaitUntil(() => peers[2].Latest.Match!.CanStart, "second yes never arrived");
            await peers[0].App.StartGame.ExecuteAsync();
            await WaitUntil(() => peers[1].Latest.Matches.Single(m => m.Id == a).Game is not null, "the first game never reached P1");

            var seen = peers[1].Latest.Matches.Single(m => m.Id == a);
            Assert.False(seen.IsPlaying);
            Assert.Equal(SeatStanding.Left, seen.Game!.Seats.Single(s => s.Name == "P1").Standing);

            await peers[2].App.StartGame.ExecuteAsync();
            await WaitUntil(() => peers[1].Latest.Match is { IsPlaying: true } m && m.Id == b, "P1 never got to play the game they chose");
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task Host_falls_back_to_a_free_port_when_its_port_is_taken()
    {
        var squatter = new TcpListener(IPAddress.IPv6Any, 0) { ExclusiveAddressUse = true };
        squatter.Server.DualMode = true;
        squatter.Start();
        var takenPort = ((IPEndPoint)squatter.LocalEndpoint).Port;
        var host = new TestPeer("Host");
        try
        {
            await host.App.HostLobby.ExecuteAsync(host.Name, takenPort);

            Assert.Equal(SessionStage.Lobby, host.Latest.Stage);
            Assert.DoesNotContain(host.Latest.JoinAddresses, a => a.EndsWith($":{takenPort}", StringComparison.Ordinal));
        }
        finally
        {
            squatter.Stop();
            await host.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task Auto_lobby_joins_a_discovered_lobby()
    {
        var discovery = new ScriptedDiscovery();
        var host = new TestPeer("Host");
        var guest = new TestPeer("Guest", discovery);
        try
        {
            var port = FreePort();
            await host.App.HostLobby.ExecuteAsync(host.Name, port);

            guest.App.AutoLobby.Start(guest.Name);
            discovery.Announce(new DiscoveredLobby(Guid.NewGuid(), host.Name, "127.0.0.1", port, 1, 8, ProtocolInfo.Version));

            await WaitUntil(() => host.Latest.Members.Count == 2, "guest never auto-joined");
            Assert.False(guest.Latest.IsHost);
        }
        finally
        {
            await guest.App.DisposeAsync();
            await host.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task Auto_lobby_warns_about_a_different_version_instead_of_joining_it()
    {
        var discovery = new ScriptedDiscovery();
        var guest = new TestPeer("Guest", discovery);
        try
        {
            guest.App.AutoLobby.Start(guest.Name);
            discovery.Announce(new DiscoveredLobby(Guid.NewGuid(), "OldPc", "10.0.0.9", 47777, 1, 8, ProtocolInfo.Version - 1));

            await WaitUntil(() => guest.Notices.Any(n => n.Contains("older version", StringComparison.Ordinal)), "no version warning");
            await WaitUntil(() => guest.Latest.IsHost, "should have opened its own lobby");
        }
        finally
        {
            await guest.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task Auto_lobby_opens_a_lobby_when_none_is_found()
    {
        var loner = new TestPeer("Loner", new ScriptedDiscovery());
        try
        {
            loner.App.AutoLobby.Start(loner.Name);

            await WaitUntil(() => loner.Latest.Stage == SessionStage.Lobby, "never opened a lobby");
            Assert.True(loner.Latest.IsHost);
        }
        finally
        {
            await loner.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_host_moves_to_a_lobby_it_outranks_when_that_lobby_cannot_hear_it()
    {
        var discovery = new ScriptedDiscovery();
        var deaf = new TestPeer("Deaf");
        var loner = new TestPeer("Loner", discovery);
        using var announcing = new CancellationTokenSource();
        try
        {
            var port = FreePort();
            await deaf.App.HostLobby.ExecuteAsync(deaf.Name, port);
            loner.App.AutoLobby.Start(loner.Name);
            await WaitUntil(() => loner.Latest.IsHost, "loner never opened its own lobby");

            // Same size, highest possible id: the loner outranks it, but it never hears the loner's beacons.
            var deafLobby = new DiscoveredLobby(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), deaf.Name, "127.0.0.1", port, 1, 8, ProtocolInfo.Version);
            _ = Task.Run(async () =>
            {
                while (!announcing.IsCancellationRequested)
                {
                    discovery.Announce(deafLobby);
                    await Task.Delay(500);
                }
            });

            await WaitUntil(() => deaf.Latest.Members.Count == 2, "loner never went to the lobby that could not hear it");
            Assert.False(loner.Latest.IsHost);
        }
        finally
        {
            announcing.Cancel();
            await loner.App.DisposeAsync();
            await deaf.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_lone_host_merges_into_a_bigger_lobby()
    {
        var discovery = new ScriptedDiscovery();
        var host = new TestPeer("Host");
        var member = new TestPeer("Member");
        var loner = new TestPeer("Loner", discovery);
        try
        {
            var port = FreePort();
            await host.App.HostLobby.ExecuteAsync(host.Name, port);
            await member.App.JoinLobby.ExecuteAsync(member.Name, "127.0.0.1", port);
            await WaitUntil(() => host.Latest.Members.Count == 2, "member never joined");

            loner.App.AutoLobby.Start(loner.Name);
            await WaitUntil(() => loner.Latest.IsHost, "loner never opened its own lobby");

            discovery.Announce(new DiscoveredLobby(Guid.NewGuid(), host.Name, "127.0.0.1", port, 2, 8, ProtocolInfo.Version));

            await WaitUntil(() => host.Latest.Members.Count == 3, "loner never moved to the bigger lobby");
            Assert.False(loner.Latest.IsHost);
        }
        finally
        {
            await loner.App.DisposeAsync();
            await member.App.DisposeAsync();
            await host.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lobby_members_rename_and_chat_with_everyone()
    {
        var peers = Enumerable.Range(0, 3).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);

            await peers[2].App.RenamePlayer.ExecuteAsync("  Zed ");
            await WaitUntil(
                () => peers.All(p => p.Latest.Members.Any(m => m.Name == "Zed") && p.Latest.Members.All(m => m.Name != "P2")),
                "rename did not reach everyone");
            Assert.Equal("Zed", peers[2].Latest.LocalName);

            await peers[2].App.SendChat.ExecuteAsync("hello\nall");
            await peers[1].App.SendChat.ExecuteAsync("hi Zed");
            await WaitUntil(() => peers.All(p => p.Latest.Chat.Count == 2), "chat did not reach everyone");

            Assert.All(peers, p => Assert.Contains(p.Latest.Chat, l => l is { Sender: "Zed", Text: "hello all" }));
            Assert.All(peers, p => Assert.Contains(p.Latest.Chat, l => l is { Sender: "P1", Text: "hi Zed" }));
            Assert.True(peers[2].Latest.Chat.Single(l => l.Sender == "Zed").IsLocal);
            Assert.False(peers[0].Latest.Chat.Single(l => l.Sender == "Zed").IsLocal);
            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.SendChat.ExecuteAsync("   "));
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task Members_share_avatars_message_privately_and_nudge()
    {
        var peers = Enumerable.Range(0, 3).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            // An avatar set before joining goes out in the Hello; a later change is broadcast.
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
            await peers[1].App.UpdateProfile.ExecuteAsync(5, png);
            await FormLobbyAsync(peers);
            await WaitUntil(
                () => peers.All(p => p.Latest.Members.Single(m => m.Name == "P1").Profile is { Color: 5, Photo.Length: 7 }),
                "avatar did not reach everyone");
            await peers[1].App.UpdateProfile.ExecuteAsync(2, null);
            await WaitUntil(
                () => peers.All(p => p.Latest.Members.Single(m => m.Name == "P1").Profile is { Color: 2, Photo: null }),
                "avatar change did not reach everyone");
            await Assert.ThrowsAsync<SessionException>(() => peers[1].App.UpdateProfile.ExecuteAsync(1, [1, 2, 3]));

            var p0 = peers[0].Latest.LocalKey;
            var p2 = peers[2].Latest.LocalKey;
            await peers[2].App.SendDirectChat.ExecuteAsync(p0, "just us");
            await WaitUntil(() => peers[0].Latest.DirectChats.Any(d => d.PeerKey == p2 && d.Lines.Count == 1), "private line never arrived");
            Assert.Equal("just us", peers[0].Latest.DirectChats.Single().Lines[0].Text);
            Assert.Equal(p0, peers[2].Latest.DirectChats.Single().PeerKey);
            Assert.True(peers[2].Latest.DirectChats.Single().Lines[0].IsLocal);
            Assert.Empty(peers[1].Latest.DirectChats);
            Assert.All(peers, p => Assert.Empty(p.Latest.Chat));

            await peers[0].App.Nudge.ExecuteAsync(p2);
            await WaitUntil(() => peers[2].Nudges.Count == 1, "nudge never arrived");
            Assert.Equal("P0", peers[2].Nudges.Single().From);
            await WaitUntil(
                () => peers[2].Latest.DirectChats.Single(d => d.PeerKey == p0).Lines.Any(l => l.Kind == ChatLineKind.Nudge),
                "nudge not shown in the conversation");
            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.Nudge.ExecuteAsync(p2));
            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.Nudge.ExecuteAsync(peers[0].Latest.LocalKey));
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task Only_invited_members_can_join_a_game_and_the_rest_can_watch()
    {
        var peers = Enumerable.Range(0, 3).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            var invited = peers[2].Latest.LocalKey;
            await peers[0].App.ProposeGame.ExecuteAsync(GameCatalog.Ludo.Id, [invited]);
            await WaitUntil(() => peers.All(p => p.Latest.Match?.Phase == MatchPhaseView.Inviting), "invite did not reach everyone");

            Assert.True(peers[2].Latest.Match!.IsInvited);
            Assert.False(peers[1].Latest.Match!.IsInvited);
            Assert.Equal(GameCatalog.Ludo, peers[1].Latest.Match!.GameType);
            Assert.Equal(["P0", "P2"], peers[0].Latest.Match!.Invitees.Select(i => i.Name).Order());
            await Assert.ThrowsAsync<SessionException>(() => peers[1].App.AcceptInvite.ExecuteAsync(peers[1].Latest.Match!.Id));

            await peers[2].App.AcceptInvite.ExecuteAsync(peers[2].Latest.Match!.Id);
            await WaitUntil(() => peers[0].Latest.Match!.CanStart, "answer never arrived");
            await peers[0].App.StartGame.ExecuteAsync();
            await WaitUntil(() => peers.All(p => p.Latest.Match?.Game is not null), "game did not reach everyone");
            Assert.Equal(2, peers[1].Latest.Match!.Game!.Seats.Count);
            Assert.False(peers[1].Latest.Match!.IsPlaying);
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task A_notification_reaches_only_those_picked_and_everyone_involved_sees_each_answer()
    {
        var peers = Enumerable.Range(0, 4).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            var involved = peers.Take(3).ToArray();
            await peers[0].App.SendPoll.ExecuteAsync("Pizza tonight?", ["Yes", "No"], [peers[1].Latest.LocalKey, peers[2].Latest.LocalKey]);
            await WaitUntil(() => involved.All(p => p.Latest.Polls.Count == 1), "notification did not reach those picked");
            var id = peers[0].Latest.Polls.Single().Id;
            Assert.True(peers[0].Latest.Polls.Single().IsMine);
            Assert.False(peers[1].Latest.Polls.Single().IsMine);
            Assert.Equal("P0", peers[1].PollActivity.Single().From);

            await peers[1].App.VotePoll.ExecuteAsync(id, 0);
            await WaitUntil(
                () => involved.All(p => p.Latest.Polls.Single().Recipients.Single(r => r.Name == "P1").Choice == 0),
                "answer did not reach everyone involved");

            // Changing an answer replaces it everywhere.
            await peers[1].App.VotePoll.ExecuteAsync(id, 1);
            await peers[2].App.VotePoll.ExecuteAsync(id, 0);
            await WaitUntil(
                () => involved.All(p => p.Latest.Polls.Single().Recipients.Select(r => r.Choice).Order().SequenceEqual([0, 1])
                    && p.Latest.Polls.Single().Recipients.Single(r => r.Name == "P1").Choice == 1),
                "changed answer did not reach everyone involved");

            // The sender follows the answers, and whoever was left out hears nothing at all.
            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.VotePoll.ExecuteAsync(id, 0));
            await Assert.ThrowsAsync<SessionException>(() => peers[3].App.VotePoll.ExecuteAsync(id, 0));
            Assert.Empty(peers[3].Latest.Polls);
            Assert.True(peers[3].PollActivity.IsEmpty);

            // Someone leaving keeps their answer and their name.
            await peers[1].App.LeaveSession.ExecuteAsync();
            await WaitUntil(
                () => !peers[0].Latest.Polls.Single().Recipients.Single(r => r.Name == "P1").IsPresent,
                "leaver still shown as present");
            Assert.Equal(1, peers[0].Latest.Polls.Single().Recipients.Single(r => r.Name == "P1").Choice);
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task An_answer_that_overtakes_its_notification_is_kept_and_a_stranger_cannot_answer()
    {
        var networks = Enumerable.Range(0, 4).Select(_ => new FilteredNetwork(new TcpPeerNetwork())).ToArray();
        var peers = networks.Select((n, i) => new TestPeer($"P{i}", network: n)).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            var ids = peers.Select(p => SessionContext.FindByKey(p.Latest.LocalKey)!.Value).ToArray();
            var poll = Guid.NewGuid();

            networks[2].Inject(ids[1], new PollVoteMessage(poll, 1));
            networks[2].Inject(ids[0], new PollMessage(poll, "Pizza?", ["Yes", "No"], [ids[1], ids[2]]));
            await WaitUntil(
                () => peers[2].Latest.Polls.SingleOrDefault()?.Recipients.Single(r => r.Name == "P1").Choice == 1,
                "early answer was lost");

            // P3 was not asked, and answers that are not on the list mean nothing.
            networks[2].Inject(ids[3], new PollVoteMessage(poll, 0));
            networks[2].Inject(ids[1], new PollVoteMessage(poll, 7));
            await peers[2].App.VotePoll.ExecuteAsync(poll, 0);
            var view = peers[2].Latest.Polls.Single();
            Assert.DoesNotContain(view.Recipients, r => r.Name == "P3");
            Assert.Equal(1, view.Recipients.Single(r => r.Name == "P1").Choice);
            Assert.Equal(0, view.Recipients.Single(r => r.IsLocal).Choice);

            // A notification that does not list us, or breaks the limits, is dropped.
            networks[3].Inject(ids[0], new PollMessage(Guid.NewGuid(), "Not for you", ["Yes", "No"], [ids[1]]));
            networks[3].Inject(ids[0], new PollMessage(Guid.NewGuid(), "Too few", ["Yes"], [ids[3]]));
            networks[3].Inject(ids[0], new PollMessage(Guid.NewGuid(), "Same twice", ["Yes", "yes"], [ids[3]]));
            await peers[3].App.UpdateProfile.ExecuteAsync(1, null);
            Assert.Empty(peers[3].Latest.Polls);
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task Only_the_sender_can_take_a_notification_back_and_it_goes_for_everyone()
    {
        var networks = Enumerable.Range(0, 3).Select(_ => new FilteredNetwork(new TcpPeerNetwork())).ToArray();
        var peers = networks.Select((n, i) => new TestPeer($"P{i}", network: n)).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            var ids = peers.Select(p => SessionContext.FindByKey(p.Latest.LocalKey)!.Value).ToArray();
            await peers[0].App.SendPoll.ExecuteAsync("Pizza tonight?", ["Yes", "No"], [peers[1].Latest.LocalKey, peers[2].Latest.LocalKey]);
            await WaitUntil(() => peers.All(p => p.Latest.Polls.Count == 1), "notification did not arrive");
            var id = peers[0].Latest.Polls.Single().Id;
            await peers[1].App.VotePoll.ExecuteAsync(id, 0);
            await WaitUntil(
                () => peers.All(p => p.Latest.Polls.Single().Recipients.Single(r => r.Name == "P1").Choice == 0),
                "answer did not arrive");

            // A recipient cannot take it back, here or over the wire.
            await Assert.ThrowsAsync<SessionException>(() => peers[1].App.RetractPoll.ExecuteAsync(id));
            networks[2].Inject(ids[1], new PollRetractMessage(id));
            await peers[2].App.UpdateProfile.ExecuteAsync(1, null);
            Assert.Single(peers[2].Latest.Polls);

            await peers[0].App.RetractPoll.ExecuteAsync(id);
            Assert.Empty(peers[0].Latest.Polls);
            await WaitUntil(() => peers.All(p => p.Latest.Polls.Count == 0), "notification was not taken back");
            Assert.Contains(peers[2].PollActivity, a => a.IsRetracted && a.From == "P0" && a.Text == "Pizza tonight?");

            // It cannot be answered any more, and nothing still on its way brings it back.
            await Assert.ThrowsAsync<SessionException>(() => peers[1].App.VotePoll.ExecuteAsync(id, 1));
            networks[2].Inject(ids[0], new PollMessage(id, "Pizza tonight?", ["Yes", "No"], [ids[1], ids[2]]));
            networks[2].Inject(ids[1], new PollVoteMessage(id, 1));
            await peers[2].App.UpdateProfile.ExecuteAsync(2, null);
            Assert.Empty(peers[2].Latest.Polls);
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    [Fact]
    public async Task A_notification_breaking_the_limits_is_refused_before_it_is_sent()
    {
        var peers = Enumerable.Range(0, 2).Select(i => new TestPeer($"P{i}")).ToArray();
        try
        {
            await FormLobbyAsync(peers);
            var other = peers[1].Latest.LocalKey;

            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.SendPoll.ExecuteAsync(" ", ["Yes", "No"], [other]));
            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.SendPoll.ExecuteAsync("Ok?", ["Yes"], [other]));
            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.SendPoll.ExecuteAsync("Ok?", ["Yes", " yes "], [other]));
            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.SendPoll.ExecuteAsync("Ok?", ["Yes", "No"], []));
            await Assert.ThrowsAsync<SessionException>(() => peers[0].App.SendPoll.ExecuteAsync("Ok?", ["Yes", "No"], [peers[0].Latest.LocalKey]));
            Assert.Empty(peers[0].Latest.Polls);
        }
        finally
        {
            await DisposeAllAsync(peers);
        }
    }

    /// <summary>Peer 0 hosts and the rest join; returns the port once everyone is connected to everyone.</summary>
    private static async Task<int> FormLobbyAsync(TestPeer[] peers)
    {
        var port = FreePort();
        await peers[0].App.HostLobby.ExecuteAsync(peers[0].Name, port);
        foreach (var joiner in peers.Skip(1))
        {
            await joiner.App.JoinLobby.ExecuteAsync(joiner.Name, "127.0.0.1", port);
        }

        await WaitUntil(
            () => peers.All(p => p.Latest.Members.Count == peers.Length && p.Latest.Members.All(m => m.IsReady)),
            "mesh never became ready");
        return port;
    }

    /// <summary>The initiator invites everyone, everyone accepts, and the game starts.</summary>
    private static async Task StartGameAsync(TestPeer initiator, TestPeer[] peers)
    {
        await initiator.App.ProposeGame.ExecuteAsync();
        await WaitUntil(() => peers.All(p => p.Latest.Match?.Phase == MatchPhaseView.Inviting), "invite did not reach everyone");
        foreach (var peer in peers.Where(p => p != initiator))
        {
            await peer.App.AcceptInvite.ExecuteAsync(peer.Latest.Match!.Id);
        }

        await WaitUntil(
            () => initiator.Latest.Match!.Invitees.All(i => i.Answer == InviteAnswer.Accepted) && initiator.Latest.Match.CanStart,
            "answers never arrived");
        await initiator.App.StartGame.ExecuteAsync();
        await WaitUntil(() => peers.All(p => p.Latest.Match is { IsPlaying: true, Game: not null }), "not everyone started");
    }

    /// <summary>The initiator invites only the partner, who accepts; returns the id of the game they then start.</summary>
    private static Task<Guid> StartPairAsync(TestPeer initiator, TestPeer partner, TestPeer[] peers) =>
        StartGroupAsync(initiator, [partner], peers);

    /// <summary>The initiator invites only these partners, who accept; returns the id of the game they then start.</summary>
    private static async Task<Guid> StartGroupAsync(TestPeer initiator, TestPeer[] partners, TestPeer[] peers)
    {
        await initiator.App.ProposeGame.ExecuteAsync(GameCatalog.Ludo.Id, partners.Select(p => p.Latest.LocalKey).ToArray());
        var id = initiator.Latest.Match!.Id;
        await WaitUntil(() => peers.All(p => p.Latest.Matches.Any(m => m.Id == id)), "invite did not reach everyone");
        foreach (var partner in partners)
        {
            await partner.App.AcceptInvite.ExecuteAsync(id);
        }

        await WaitUntil(
            () => initiator.Latest.Match!.CanStart && initiator.Latest.Match.Invitees.All(i => i.Answer == InviteAnswer.Accepted),
            "answers never arrived");
        await initiator.App.StartGame.ExecuteAsync();
        await WaitUntil(() => partners.All(p => p.Latest.Match is { IsPlaying: true, Game: not null } m && m.Id == id), "a partner never started");
        return id;
    }

    private static async Task DisposeAllAsync(IEnumerable<TestPeer> peers)
    {
        foreach (var peer in peers)
        {
            await peer.App.DisposeAsync();
        }
    }

    private static string Describe(GameStateView game) => string.Join(
        " / ",
        game.Seats.Select(s => $"{s.Name}:{s.Standing}:{s.Rank}:" +
            string.Join(",", s.Tokens.Select(t => $"{t.Location}{t.Progress}"))));

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WaitUntil(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(20);
        }
    }

    private sealed class TestPeer : ISessionPresenter
    {
        private volatile SessionSnapshot _latest = SessionSnapshot.Empty;

        public TestPeer(string name, ILobbyDiscovery? discovery = null, IPeerNetwork? network = null, Ludo.Domain.Players.PlayerId? localId = null)
        {
            Name = name;
            App = new LudoApplication(
                network ?? new TcpPeerNetwork(),
                discovery ?? new NullDiscovery(),
                new Sha256CryptoProvider(),
                this,
                TimeProvider.System,
                localId: localId);
        }

        public string Name { get; }

        public LudoApplication App { get; }

        public SessionSnapshot Latest => _latest;

        public System.Collections.Concurrent.ConcurrentQueue<string> Notices { get; } = new();

        public void PresentSession(SessionSnapshot snapshot) => _latest = snapshot;

        public void PresentNotice(string message) => Notices.Enqueue(message);

        public void PresentChat(ChatLineView line)
        {
        }

        public void PresentInvite(InviteView invite)
        {
        }

        public System.Collections.Concurrent.ConcurrentQueue<NudgeView> Nudges { get; } = new();

        public void PresentNudge(NudgeView nudge) => Nudges.Enqueue(nudge);

        public System.Collections.Concurrent.ConcurrentQueue<PollActivityView> PollActivity { get; } = new();

        public void PresentPollActivity(PollActivityView activity) => PollActivity.Enqueue(activity);

        /// <summary>A bot: rolls on its turn and always moves its first movable token, until the game ends for it.</summary>
        public async Task PlayAsync(CancellationToken cancellation)
        {
            while (_latest.Match is { Phase: MatchPhaseView.Playing, IsPlaying: true, Game: { } game })
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    if (game.IsLocalTurn && game.Phase == GamePhaseView.WaitingForRoll)
                    {
                        await App.RollDice.ExecuteAsync();
                        continue;
                    }

                    if (game.IsLocalTurn && game.Phase == GamePhaseView.WaitingForMove)
                    {
                        var token = game.Seats.Single(s => s.IsLocal).Tokens.First(t => t.CanMove);
                        await App.MoveToken.ExecuteAsync(token.Index);
                        continue;
                    }
                }
                catch (SessionException)
                {
                    // Snapshot was stale; look again.
                }

                await Task.Delay(1, cancellation);
            }
        }
    }

    /// <summary>Drops game messages from chosen peers, as if their link only carried the lobby.</summary>
    private sealed class FilteredNetwork(IPeerNetwork inner) : IPeerNetwork
    {
        private EventHandler<IncomingMessage>? _received;

        private readonly HashSet<Ludo.Domain.Players.PlayerId> _muted = [];

        /// <summary>Delivers a message as if the peer had sent it, the way a modified client could.</summary>
        public void Inject(Ludo.Domain.Players.PlayerId from, PeerMessage message) =>
            _received?.Invoke(this, new IncomingMessage(from, message));

        public void Mute(Ludo.Domain.Players.PlayerId peer)
        {
            lock (_muted)
            {
                _muted.Add(peer);
            }
        }

        public event EventHandler<PeerConnection>? PeerConnected
        {
            add => inner.PeerConnected += value;
            remove => inner.PeerConnected -= value;
        }

        public event EventHandler<Ludo.Domain.Players.PlayerId>? PeerDisconnected
        {
            add => inner.PeerDisconnected += value;
            remove => inner.PeerDisconnected -= value;
        }

        public event EventHandler<IncomingMessage>? MessageReceived
        {
            add
            {
                if (_received is null)
                {
                    inner.MessageReceived += OnReceived;
                }

                _received += value;
            }
            remove => _received -= value;
        }

        public int ListenPort => inner.ListenPort;

        public Task StartAsync(Ludo.Domain.Players.PlayerId localId, int port, CancellationToken cancellationToken = default) =>
            inner.StartAsync(localId, port, cancellationToken);

        public Task<Ludo.Domain.Players.PlayerId> ConnectAsync(string host, int port, CancellationToken cancellationToken = default) =>
            inner.ConnectAsync(host, port, cancellationToken);

        public void Send(Ludo.Domain.Players.PlayerId peer, PeerMessage message) => inner.Send(peer, message);

        public void Broadcast(PeerMessage message) => inner.Broadcast(message);

        public void Disconnect(Ludo.Domain.Players.PlayerId peer) => inner.Disconnect(peer);

        public Task StopAsync() => inner.StopAsync();

        public IReadOnlyList<string> GetLocalAddresses() => inner.GetLocalAddresses();

        private void OnReceived(object? sender, IncomingMessage incoming)
        {
            var isMatch = incoming.Message.GetType().Name.StartsWith("Match", StringComparison.Ordinal)
                || incoming.Message is GameStartMessage;
            lock (_muted)
            {
                if (isMatch && _muted.Contains(incoming.From))
                {
                    return;
                }
            }

            _received?.Invoke(sender, incoming);
        }
    }

    private sealed class NullDiscovery : ILobbyDiscovery
    {
        public event EventHandler<DiscoveredLobby>? LobbyDiscovered
        {
            add { }
            remove { }
        }

        public void Advertise(LobbyAdvertisement advertisement)
        {
        }

        public void StopAdvertising()
        {
        }

        public void StartBrowsing()
        {
        }

        public void StopBrowsing()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedDiscovery : ILobbyDiscovery
    {
        public event EventHandler<DiscoveredLobby>? LobbyDiscovered;

        public void Announce(DiscoveredLobby lobby) => LobbyDiscovered?.Invoke(this, lobby);

        public void Advertise(LobbyAdvertisement advertisement)
        {
        }

        public void StopAdvertising()
        {
        }

        public void StartBrowsing()
        {
        }

        public void StopBrowsing()
        {
        }

        public void Dispose()
        {
        }
    }
}
