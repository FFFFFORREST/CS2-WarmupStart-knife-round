using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace WarmupStartVote;

/// <summary>
/// Warmup -> chat vote -> knife round -> winning team picks stay/swap -> live MR12.
/// Independent of RockTheVote-CSS. Built against the server's own CSS API DLL.
/// </summary>
public sealed class WarmupStartVotePlugin : BasePlugin
{
    public override string ModuleName => "Warmup Start Vote";
    public override string ModuleVersion => "1.2.2";
    public override string ModuleAuthor => "CS2 community server";
    public override string ModuleDescription => "Free warmup, start vote, knife round, winning-team side pick, live match";

    private const int MinimumPlayers = 1;
    private const double ApprovalFraction = 0.50;
    private const float StartVoteSeconds = 25f;
    private const int StartVoteCooldownSeconds = 30;
    private const float SidePickSeconds = 30f;
    // Longer than RockTheVote's DelayToChangeInTheEnd (default 6s) so its map change goes first.
    private const float MatchEndReloadSeconds = 15f;
    private const string ResetPermission = "@css/generic";
    private const int DropKnifeCount = 5;
    // Warmup is one endless round, so outside a live match .drop uses a time cooldown.
    private const int DropCooldownSeconds = 30;

    // RoundEndReason.GameCommencing: round_end fired by a restart, not by a real round result.
    private const int GameCommencingReason = 16;

    private enum MatchPhase { Warmup, Voting, KnifePending, KnifeActive, SidePick, Live }
    private MatchPhase _phase = MatchPhase.Warmup;
    private readonly HashSet<ulong> _yes = new();
    private readonly HashSet<ulong> _no = new();
    private readonly HashSet<ulong> _knifeWinners = new();
    private int _knifeWinnerTeam;
    private int _voteSerial;
    private int _sideSerial;
    private DateTimeOffset _lastVoteFailed = DateTimeOffset.MinValue;
    private int _roundSerial;
    private readonly Dictionary<ulong, int> _dropRound = new();
    private readonly Dictionary<ulong, DateTimeOffset> _dropTime = new();

    public override void Load(bool hotReload)
    {
        AddCommand("css_votestart", "Vote to start a knife round then a live match", StartVote);
        AddCommand("css_yes", "Vote YES to start", VoteYes);
        AddCommand("css_no", "Vote NO to start", VoteNo);
        AddCommand("css_startyes", "Vote YES to start", VoteYes);
        AddCommand("css_startno", "Vote NO to start", VoteNo);
        AddCommand("css_stay", "Knife winner keeps their current side", Stay);
        AddCommand("css_swap", "Knife winner swaps CT and T sides", Swap);
        AddCommand("css_startstatus", "Show match phase / vote status", Status);
        AddCommand("css_startreset", "Admin: abort vote/knife/match and restore free warmup", ForceReset);
        AddCommandListener("say", OnChat);
        AddCommandListener("say_team", OnChat);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        // Restart the cs2 systemd service after deploying, rather than hot-reloading
        // into a live match; this plugin doesn't guess the state of an existing match.
    }

    private static bool Human(CCSPlayerController? p) =>
        p is { IsValid: true } && !p.IsBot && !p.IsHLTV && p.SteamID != 0;

    private static List<CCSPlayerController> Humans(CCSPlayerController? except = null) =>
        Utilities.GetPlayers()
            .Where(x => Human(x) && (except == null || x.SteamID != except.SteamID))
            .ToList();

    // Everyone on a playing side, bots included, so a swap moves whole teams.
    private static List<CCSPlayerController> TeamPlayers() =>
        Utilities.GetPlayers()
            .Where(x => x is { IsValid: true } && !x.IsHLTV
                && (x.Team == CsTeam.Terrorist || x.Team == CsTeam.CounterTerrorist))
            .ToList();

    private static bool InWarmup()
    {
        try
        {
            var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            return rules?.WarmupPeriod ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static int NeededYes(int count) =>
        Math.Max(1, (int)Math.Ceiling(count * ApprovalFraction));

    private void ClearBallots()
    {
        _yes.Clear();
        _no.Clear();
        ++_voteSerial;
    }

    // Drops ballots of players who left; returns the number of humans still online.
    private int PruneBallots(CCSPlayerController? leaving = null)
    {
        var online = Humans(leaving).Select(x => x.SteamID).ToHashSet();
        _yes.RemoveWhere(x => !online.Contains(x));
        _no.RemoveWhere(x => !online.Contains(x));
        return online.Count;
    }

    private void StartVote(CCSPlayerController? p, CommandInfo info)
    {
        if (!Human(p)) return;
        if (_phase != MatchPhase.Warmup)
        {
            p!.PrintToChat("[StartVote] Only available during free warmup. !startstatus");
            return;
        }

        var online = Humans();
        if (online.Count < MinimumPlayers)
        {
            p!.PrintToChat($"[StartVote] Need at least {MinimumPlayers} human player(s).");
            return;
        }
        // Cooldown counts from when the previous vote failed, not from when it started.
        var remaining = StartVoteCooldownSeconds - (DateTimeOffset.UtcNow - _lastVoteFailed).TotalSeconds;
        if (remaining > 0)
        {
            p!.PrintToChat($"[StartVote] Wait {Math.Ceiling(remaining)}s before trying again.");
            return;
        }

        _phase = MatchPhase.Voting;
        ClearBallots();
        var serial = _voteSerial;

        Server.PrintToChatAll($"[StartVote] {p!.PlayerName} started a match vote! {NeededYes(online.Count)}/{online.Count} YES needed (50%).");
        Server.PrintToChatAll($"[StartVote] Type !yes or !no within {(int)StartVoteSeconds} seconds. Proposer must also vote.");
        AddTimer(StartVoteSeconds, () =>
        {
            if (_phase != MatchPhase.Voting || serial != _voteSerial) return;
            FailVote("Time ran out. Free warmup continues.");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void VoteYes(CCSPlayerController? p, CommandInfo info) => RecordVote(p, true);
    private void VoteNo(CCSPlayerController? p, CommandInfo info) => RecordVote(p, false);

    private void RecordVote(CCSPlayerController? p, bool yes)
    {
        if (!Human(p)) return;
        if (_phase != MatchPhase.Voting)
        {
            p!.PrintToChat("[StartVote] No open start vote. Use !votestart in warmup.");
            return;
        }

        var id = p!.SteamID;
        if (_yes.Contains(id) || _no.Contains(id))
        {
            p.PrintToChat("[StartVote] Your vote is already counted.");
            return;
        }
        if (yes) _yes.Add(id); else _no.Add(id);

        int online = PruneBallots();
        Server.PrintToChatAll($"[StartVote] YES {_yes.Count}, NO {_no.Count} | {NeededYes(online)} YES needed ({online} online).");
        EvaluateVote(online);
    }

    private void EvaluateVote(int online)
    {
        int needed = NeededYes(online);
        if (_yes.Count >= needed)
        {
            ClearBallots();
            BeginKnifeOrSoloLive();
        }
        else if (_yes.Count + (online - _yes.Count - _no.Count) < needed)
        {
            FailVote("Not enough remaining YES votes. Free warmup continues.");
        }
    }

    private void FailVote(string message)
    {
        ClearBallots();
        _phase = MatchPhase.Warmup;
        _lastVoteFailed = DateTimeOffset.UtcNow;
        Server.PrintToChatAll($"[StartVote] {message}");
    }

    private void BeginKnifeOrSoloLive()
    {
        var teams = Humans();
        bool hasT = teams.Any(x => x.Team == CsTeam.Terrorist);
        bool hasCT = teams.Any(x => x.Team == CsTeam.CounterTerrorist);
        if (!hasT || !hasCT)
        {
            // Can't determine a winner with one side empty. Preserve the
            // one-person testing workflow and begin live without a knife round.
            _phase = MatchPhase.Live;
            Server.PrintToChatAll("[StartVote] Vote passed! No human opponents on both sides: skipping knife round.");
            Server.ExecuteCommand("exec live.cfg");
            return;
        }

        _phase = MatchPhase.KnifePending;
        _knifeWinnerTeam = 0;
        _knifeWinners.Clear();
        Server.PrintToChatAll("[StartVote] Vote passed! KNIFE ROUND: knives only. Winner chooses !stay or !swap.");
        Server.ExecuteCommand("exec knife.cfg");
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart e, GameEventInfo info)
    {
        ++_roundSerial;
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd e, GameEventInfo info)
    {
        // knife.cfg's mp_warmup_end / mp_restartgame fire extra round_start and
        // round_end events. Only a round that gets past freeze time is the real fight.
        if (_phase == MatchPhase.KnifePending && !InWarmup())
        {
            _phase = MatchPhase.KnifeActive;
            Server.PrintToChatAll("[Knife] Fight! Knives only. Winning team picks sides after this round.");
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e, GameEventInfo info)
    {
        var p = e.Userid;
        if (!Human(p) || (_phase != MatchPhase.KnifePending && _phase != MatchPhase.KnifeActive))
            return HookResult.Continue;
        var id = p!.SteamID;
        // Wait until the engine has assigned default equipment before stripping it.
        AddTimer(0.2f, () =>
        {
            if (_phase != MatchPhase.KnifePending && _phase != MatchPhase.KnifeActive) return;
            var current = Humans().FirstOrDefault(x => x.SteamID == id);
            if (current == null || (current.Team != CsTeam.Terrorist && current.Team != CsTeam.CounterTerrorist)) return;
            try
            {
                current.RemoveWeapons();
                current.GiveNamedItem("weapon_knife");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WarmupStartVote] Knife weapon assignment failed for {id}: {ex}");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd e, GameEventInfo info)
    {
        if (_phase != MatchPhase.KnifeActive) return HookResult.Continue;

        if (e.Reason == GameCommencingReason)
        {
            // Someone restarted the game mid-knife; wait for the restarted round's freeze end.
            _phase = MatchPhase.KnifePending;
            return HookResult.Continue;
        }

        if (e.Winner != (int)CsTeam.Terrorist && e.Winner != (int)CsTeam.CounterTerrorist)
        {
            _phase = MatchPhase.KnifePending;
            Server.PrintToChatAll("[Knife] Draw. Replaying knife round.");
            AddTimer(2f, () =>
            {
                if (_phase == MatchPhase.KnifePending) Server.ExecuteCommand("exec knife.cfg");
            }, TimerFlags.STOP_ON_MAPCHANGE);
            return HookResult.Continue;
        }

        _knifeWinnerTeam = e.Winner;
        _knifeWinners.Clear();
        foreach (var player in Humans().Where(x => (int)x.Team == _knifeWinnerTeam))
            _knifeWinners.Add(player.SteamID);
        _phase = MatchPhase.SidePick;
        int serial = ++_sideSerial;
        string side = _knifeWinnerTeam == (int)CsTeam.CounterTerrorist ? "CT" : "T";
        Server.PrintToChatAll($"[Knife] {side} won! A player from the winning team must type !stay or !swap.");
        Server.PrintToChatAll($"[Knife] {SidePickSeconds:0}s to choose; timeout defaults to STAY.");

        // Keep the server in warmup while waiting for the choice, instead
        // of inadvertently playing a second knife round.
        AddTimer(1f, () =>
        {
            if (_phase == MatchPhase.SidePick && _sideSerial == serial)
                Server.ExecuteCommand("exec practice.cfg");
        }, TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(SidePickSeconds, () =>
        {
            if (_phase == MatchPhase.SidePick && _sideSerial == serial)
            {
                Server.PrintToChatAll("[Knife] Side choice timeout. Keeping sides.");
                StartLive(swap: false);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private void Stay(CCSPlayerController? p, CommandInfo info) => ChooseSide(p, swap: false);
    private void Swap(CCSPlayerController? p, CommandInfo info) => ChooseSide(p, swap: true);

    private void ChooseSide(CCSPlayerController? p, bool swap)
    {
        if (!Human(p)) return;
        if (_phase != MatchPhase.SidePick)
        {
            p!.PrintToChat("[Knife] Side choice is not currently available.");
            return;
        }
        if (!_knifeWinners.Contains(p!.SteamID))
        {
            p.PrintToChat("[Knife] Only a member of the knife-winning team may choose sides.");
            return;
        }
        StartLive(swap);
    }

    private void StartLive(bool swap)
    {
        if (_phase != MatchPhase.SidePick) return;
        _phase = MatchPhase.Live;
        ++_sideSerial;
        _knifeWinners.Clear();
        if (swap)
        {
            // Capture teams before moving anyone; SwitchTeam changes Team in-place.
            var players = TeamPlayers().Select(x => (Player: x, OriginalTeam: x.Team)).ToList();
            foreach (var (player, originalTeam) in players)
            {
                if (!player.IsValid) continue;
                try
                {
                    player.SwitchTeam(originalTeam == CsTeam.Terrorist
                        ? CsTeam.CounterTerrorist : CsTeam.Terrorist);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WarmupStartVote] Team swap failed for {player.PlayerName}: {ex}");
                }
            }
        }
        Server.PrintToChatAll($"[Knife] {(swap ? "SWAP" : "STAY")} selected. Starting LIVE MR12 now!");
        // live.cfg also resets round scores, removes warmup equipment rules,
        // restores economy and respawn rules, and restarts the match.
        Server.ExecuteCommand("exec live.cfg");
    }

    private void Status(CCSPlayerController? p, CommandInfo info)
    {
        if (!Human(p)) return;
        var count = _phase == MatchPhase.Voting ? PruneBallots() : Humans().Count;
        var details = _phase switch
        {
            MatchPhase.Warmup => $"Free warmup ({count} online). Type !votestart.",
            MatchPhase.Voting => $"Voting: {_yes.Count} YES, {_no.Count} NO. {NeededYes(count)} YES needed.",
            MatchPhase.KnifePending => "Knife round is starting.",
            MatchPhase.KnifeActive => "Knife round active.",
            MatchPhase.SidePick => "Knife winner must choose !stay or !swap.",
            MatchPhase.Live => "Competitive match is LIVE.",
            _ => "Unknown"
        };
        p!.PrintToChat("[StartVote] " + details);
    }

    private void ForceReset(CCSPlayerController? p, CommandInfo info)
    {
        // p == null is the server console / RCON.
        if (p != null && !AdminManager.PlayerHasPermissions(p, ResetPermission))
        {
            info.ReplyToCommand($"[StartVote] You need {ResetPermission} to use this.");
            return;
        }
        ReturnToWarmup("An admin reset the match. Free warmup restored.");
        info.ReplyToCommand("[StartVote] Reset to free warmup.");
    }

    // ".drop" in chat: drop copies of the player's own knife at their feet for others to pick up.
    private HookResult OnChat(CCSPlayerController? p, CommandInfo info)
    {
        var text = info.ArgString.Trim().Trim('"').Trim();
        if (!Human(p) || !text.Equals(".drop", StringComparison.OrdinalIgnoreCase))
            return HookResult.Continue;
        var id = p!.SteamID;
        Server.NextFrame(() => DropKnives(id));
        return HookResult.Continue;
    }

    private static bool HasKnife(CCSPlayerPawn pawn)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return false;
        foreach (var handle in weapons)
        {
            var name = handle.Value?.DesignerName;
            if (name != null && (name.Contains("knife") || name.Contains("bayonet"))) return true;
        }
        return false;
    }

    private void DropKnives(ulong id)
    {
        var p = Humans().FirstOrDefault(x => x.SteamID == id);
        if (p == null) return;
        if (_phase == MatchPhase.KnifePending || _phase == MatchPhase.KnifeActive)
        {
            p.PrintToChat("[Drop] Not available during the knife round.");
            return;
        }
        var pawn = p.PlayerPawn.Value;
        var origin = pawn?.AbsOrigin;
        if (pawn == null || origin == null || !p.PawnIsAlive
            || (p.Team != CsTeam.Terrorist && p.Team != CsTeam.CounterTerrorist))
        {
            p.PrintToChat("[Drop] You must be alive on a team to use .drop.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_phase == MatchPhase.Live)
        {
            if (_dropRound.TryGetValue(id, out var round) && round == _roundSerial)
            {
                p.PrintToChat("[Drop] .drop can be used once per round.");
                return;
            }
        }
        else if (_dropTime.TryGetValue(id, out var last))
        {
            var remaining = DropCooldownSeconds - (now - last).TotalSeconds;
            if (remaining > 0)
            {
                p.PrintToChat($"[Drop] Wait {Math.Ceiling(remaining)}s before using .drop again.");
                return;
            }
        }

        // The player keeps their own knife, so each extra copy can't be picked up and stays on the ground.
        if (!HasKnife(pawn)) p.GiveNamedItem("weapon_knife");

        float x = origin.X, y = origin.Y, z = origin.Z;
        int dropped = 0;
        for (int i = 0; i < DropKnifeCount; i++)
        {
            try
            {
                var knife = p.GiveNamedItem<CBasePlayerWeapon>("weapon_knife");
                if (knife == null || !knife.IsValid || knife.OwnerEntity.IsValid) continue;
                // Small ring around the feet so the copies don't spawn inside each other; zero velocity = straight down.
                double angle = 2 * Math.PI * i / DropKnifeCount;
                knife.Teleport(
                    new Vector(x + (float)(Math.Cos(angle) * 12), y + (float)(Math.Sin(angle) * 12), z + 32),
                    new QAngle(0, 0, 0),
                    new Vector(0, 0, 0));
                ++dropped;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WarmupStartVote] Knife drop failed for {id}: {ex}");
            }
        }
        if (dropped == 0)
        {
            p.PrintToChat("[Drop] Could not drop knives right now.");
            return;
        }

        _dropRound[id] = _roundSerial;
        _dropTime[id] = now;
        Server.PrintToChatAll($"[Drop] {p.PlayerName} dropped {dropped} knives. Press E on one to pick it up (or drop yours with G first).");
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect e, GameEventInfo info)
    {
        var leaving = e.Userid;
        if (!Human(leaving)) return HookResult.Continue;

        // Checked synchronously: an empty server may hibernate and not run timers.
        if (_phase == MatchPhase.Voting)
        {
            EvaluateVote(PruneBallots(leaving));
        }
        else if (_phase != MatchPhase.Warmup && Humans(leaving).Count == 0)
        {
            ReturnToWarmup("All players left. Free warmup restored.");
        }
        return HookResult.Continue;
    }

    private void OnMapStart(string mapName)
    {
        ResetForMap();
        AddTimer(3f, () =>
        {
            if (_phase != MatchPhase.Warmup) return;
            Server.ExecuteCommand("exec practice.cfg");
            Server.PrintToChatAll("[StartVote] Free warmup. !votestart -> knife round -> side pick -> live.");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void OnMapEnd() => ResetForMap();

    private void ResetForMap()
    {
        _phase = MatchPhase.Warmup;
        ClearBallots();
        ++_sideSerial;
        _knifeWinners.Clear();
        _knifeWinnerTeam = 0;
        _lastVoteFailed = DateTimeOffset.MinValue;
        _dropRound.Clear();
        _dropTime.Clear();
    }

    private void ReturnToWarmup(string message)
    {
        ResetForMap();
        Server.ExecuteCommand("exec practice.cfg");
        Server.PrintToChatAll($"[StartVote] {message}");
    }

    [GameEventHandler]
    public HookResult OnMatchWinPanel(EventCsWinPanelMatch e, GameEventInfo info)
    {
        // Running practice.cfg can't leave the game-over screen, and without a
        // map cycle CS2 stays there; reload the map so OnMapStart restores warmup.
        // The timer dies on map change, so a map picked by RockTheVote wins.
        var map = Server.MapName;
        Server.PrintToChatAll($"[StartVote] Match over. Reloading the map in {MatchEndReloadSeconds:0}s for free warmup.");
        AddTimer(MatchEndReloadSeconds, () =>
        {
            if (Server.MapName != map) return;
            ReloadMap(map);
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private static void ReloadMap(string map)
    {
        // Workshop maps aren't valid changelevel targets; ds_workshop_changelevel
        // handles maps from the server's hosted workshop collection.
        Server.ExecuteCommand(Server.IsMapValid(map)
            ? $"changelevel {map}"
            : $"ds_workshop_changelevel {map}");
    }
}
