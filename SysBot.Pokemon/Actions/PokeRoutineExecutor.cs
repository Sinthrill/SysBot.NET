using PKHeX.Core;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;

namespace SysBot.Pokemon
{
    public abstract class PokeRoutineExecutor<T> : PokeRoutineExecutorBase where T : PKM, new()
    {
        protected PokeRoutineExecutor(IConsoleBotManaged<IConsoleConnection, IConsoleConnectionAsync> cfg) : base(cfg)
        {
        }

        public abstract Task<T> ReadPokemon(ulong offset, CancellationToken token);
        public abstract Task<T> ReadPokemon(ulong offset, int size, CancellationToken token);
        public abstract Task<T> ReadPokemonPointer(IEnumerable<long> jumps, int size, CancellationToken token);
        public abstract Task<T> ReadBoxPokemon(int box, int slot, CancellationToken token);

        public async Task<T?> ReadUntilPresent(ulong offset, int waitms, int waitInterval, int size, CancellationToken token)
        {
            int msWaited = 0;
            while (msWaited < waitms)
            {
                var pk = await ReadPokemon(offset, size, token).ConfigureAwait(false);
                if (pk.Species != 0 && pk.ChecksumValid)
                    return pk;
                await Task.Delay(waitInterval, token).ConfigureAwait(false);
                msWaited += waitInterval;
            }
            return null;
        }

        public async Task<T?> ReadUntilPresentPointer(IReadOnlyList<long> jumps, int waitms, int waitInterval, int size, CancellationToken token)
        {
            int msWaited = 0;
            while (msWaited < waitms)
            {
                var pk = await ReadPokemonPointer(jumps, size, token).ConfigureAwait(false);
                if (pk.Species != 0 && pk.ChecksumValid)
                    return pk;
                await Task.Delay(waitInterval, token).ConfigureAwait(false);
                msWaited += waitInterval;
            }
            return null;
        }

        protected async Task<(bool, ulong)> ValidatePointerAll(IEnumerable<long> jumps, CancellationToken token)
        {
            var solved = await SwitchConnection.PointerAll(jumps, token).ConfigureAwait(false);
            return (solved != 0, solved);
        }

        public static void DumpPokemon(string folder, string subfolder, T pk)
        {
            if (!Directory.Exists(folder))
                return;
            var dir = Path.Combine(folder, subfolder);
            Directory.CreateDirectory(dir);
            var fn = Path.Combine(dir, Util.CleanFileName(pk.FileName));
            File.WriteAllBytes(fn, pk.DecryptedPartyData);
            LogUtil.LogInfo($"Saved file: {fn}", "Dump");
        }

        public async Task<bool> TryReconnect(int attempts, int extraDelay, SwitchProtocol protocol, CancellationToken token)
        {
            // USB can have several reasons for connection loss, some of which is not recoverable (power loss, sleep). Only deal with WiFi for now.
            if (protocol is SwitchProtocol.WiFi)
            {
                // If ReconnectAttempts is set to -1, this should allow it to reconnect (essentially) indefinitely.
                for (int i = 0; i < (uint)attempts; i++)
                {
                    LogUtil.LogInfo($"Trying to reconnect... ({i + 1})", Connection.Label);
                    Connection.Reset();
                    if (Connection.Connected)
                        break;

                    await Task.Delay(30_000 + extraDelay, token).ConfigureAwait(false);
                }
            }
            return Connection.Connected;
        }

        public async Task VerifyBotbaseVersion(CancellationToken token)
        {
            var data = await SwitchConnection.GetBotbaseVersion(token).ConfigureAwait(false);
            var version = decimal.TryParse(data, CultureInfo.InvariantCulture, out var v) ? v : 0;
            if (version < BotbaseVersion)
            {
                var protocol = Config.Connection.Protocol;
                var msg = protocol is SwitchProtocol.WiFi ? "sys-botbase" : "usb-botbase";
                msg += $" version is not supported. Expected version {BotbaseVersion} or greater, and current version is {version}. Please download the latest version from: ";
                if (protocol is SwitchProtocol.WiFi)
                    msg += "https://github.com/olliz0r/sys-botbase/releases/latest";
                else
                    msg += "https://github.com/Koi-3088/usb-botbase/releases/latest";
                throw new Exception(msg);
            }
        }

        // Check if either Tesla or dmnt are active if the sanity check for Trainer Data fails, as these are common culprits.
        private const ulong ovlloaderID = 0x420000000007e51a; // Tesla Menu
        private const ulong dmntID = 0x010000000000000d;      // dmnt used for cheats

        public async Task CheckForRAMShiftingApps(CancellationToken token)
        {
            Log("Trainer data is not valid.");

            bool found = false;
            var msg = "";
            if (await SwitchConnection.IsProgramRunning(ovlloaderID, token).ConfigureAwait(false))
            {
                msg += "Found Tesla Menu";
                found = true;
            }

            if (await SwitchConnection.IsProgramRunning(dmntID, token).ConfigureAwait(false))
            {
                if (found)
                    msg += " and ";
                msg += "dmnt (cheat codes?)";
                found = true;
            }
            if (found)
            {
                msg += ".";
                Log(msg);
                Log("Please remove interfering applications and reboot the Switch.");
            }
        }

        protected async Task<PokeTradeResult> CheckPartnerReputation(PokeRoutineExecutor<T> bot, PokeTradeDetail<T> poke, ulong TrainerNID, string TrainerName,
            TradeAbuseSettings AbuseSettings, CancellationToken token)
        {
            bool quit = false;
            var user = poke.Trainer;
            bool isDistribution = false;
            string msg = "";
            if (poke.Type == PokeTradeType.Random || (poke.Type == PokeTradeType.Clone && Config.InitialRoutine == PokeRoutineType.FlexTrade))
                isDistribution = true;
            var useridmsg = isDistribution ? "" : $" ({user.ID})";
            var list = isDistribution ? PreviousUsersDistribution : PreviousUsers;
            int attempts;
            var listCool = UserCooldowns;

            CheckExpiration(TrainerNID, AbuseSettings);

            var whitelisted = AbuseSettings.WhitelistIDs.List.Find(z => z.ID == TrainerNID);
            if (whitelisted != null)
            {
                Log($"Encountered whitelisted user {whitelisted.Name} with ID {TrainerNID})");
                return PokeTradeResult.Success;
            }

            var entry = AbuseSettings.BannedIDs.List.Find(z => z.ID == TrainerNID);
            if (entry != null)
            {
                if (AbuseSettings.BlockDetectedBannedUser && bot is PokeRoutineExecutor8SWSH)
                    await BlockUser(token).ConfigureAwait(false);
                msg = $"{user.TrainerName}{useridmsg} is a banned user, and was encountered in-game using OT: {TrainerName}.";
                if (!string.IsNullOrWhiteSpace(entry.Comment))
                    msg += $"\nUser was banned for: {entry.Comment}";
                if (!string.IsNullOrWhiteSpace(AbuseSettings.BannedIDMatchEchoMention))
                    msg = $"{AbuseSettings.BannedIDMatchEchoMention} {msg}";
                EchoUtil.Echo(msg);
                return PokeTradeResult.SuspiciousActivity;
            }

            // Check within the trade type (distribution or non-Distribution).
            var previous = list.TryGetPreviousNID(TrainerNID);
            if (previous != null)
            {
                var delta = DateTime.Now - previous.Time; // Time that has passed since last trade.
                var coolDelta = DateTime.Now - DateTime.ParseExact(AbuseSettings.CooldownUpdate, "yyyy.MM.dd - HH:mm:ss", CultureInfo.InvariantCulture);
                Log($"Last traded with {user.TrainerName} {delta.TotalMinutes:F1} minutes ago (OT: {TrainerName}).");

                // Allows setting a cooldown for repeat trades. If the same user is encountered within the cooldown period for the same trade type, the user is warned and the trade will be ignored.
                var cd = AbuseSettings.TradeCooldown;     // Time they must wait before trading again.
                if (cd != 0 && TimeSpan.FromMinutes(cd) > delta)
                {
                    var wait = TimeSpan.FromMinutes(cd) - delta;
                    poke.Notifier.SendNotification(bot, poke, $"You are still on trade cooldown, and cannot trade for another {wait.TotalMinutes:F1} minute(s).");
                    msg = $"Found {TrainerName}{useridmsg} ignoring the {cd} minute trade cooldown. Last encountered {delta.TotalMinutes:F1} minutes ago.";
                    _ = isDistribution ? list.TryRegister(TrainerNID, TrainerName) : list.TryRegister(TrainerNID, TrainerName, poke.Trainer.ID);
                    if (AbuseSettings.AutoBanCooldown && TimeSpan.FromMinutes(60) < coolDelta)
                    {
                        attempts = listCool.TryInsert(TrainerNID, TrainerName);
                        msg += $"\nConnection during cooldown number {attempts} for {TrainerName}.";
                        if (AbuseSettings.EchoNintendoOnlineIDCooldown)
                            msg += $"\nID: {TrainerNID}";
                        if (!string.IsNullOrWhiteSpace(AbuseSettings.CooldownAbuseEchoMention))
                            msg = $"{AbuseSettings.CooldownAbuseEchoMention} {msg}";
                        EchoUtil.Echo(msg);
                        if (attempts >= AbuseSettings.RepeatConnections)
                        {
                            DateTime expires = DateTime.Now.AddDays(2);
                            string expiration = $"{expires:yyyy.MM.dd} - 23:59:59";
                            AbuseSettings.BannedIDs.AddIfNew(new[] { GetReference(TrainerName, TrainerNID, "Cooldown Abuse Ban", expiration) });
                            string banMsg = $"Added {TrainerName}-{TrainerNID} to the BannedIDs list for cooldown abuse.";
                            Log(banMsg);
                            EchoUtil.Echo(banMsg);
                        }
                    }
                    return PokeTradeResult.SuspiciousActivity;
                }

                // For non-Distribution trades, flag users using multiple Discord/Twitch accounts to send to the same in-game player within a time limit.
                // This is usually to evade a ban or a trade cooldown.
                if (!isDistribution && previous.NetworkID == TrainerNID && previous.RemoteID != user.ID)
                {
                    if (delta < TimeSpan.FromMinutes(AbuseSettings.TradeAbuseExpiration) && AbuseSettings.TradeAbuseAction != TradeAbuseAction.Ignore)
                    {
                        if (AbuseSettings.TradeAbuseAction == TradeAbuseAction.BlockAndQuit)
                        {
                            await BlockUser(token).ConfigureAwait(false);
                            if (AbuseSettings.BanIDWhenBlockingUser || bot is not PokeRoutineExecutor8SWSH) // Only ban ID if blocking in SWSH, always in other games.
                            {
                                AbuseSettings.BannedIDs.AddIfNew(new[] { GetReference(TrainerName, TrainerNID, "in-game block for multiple accounts") });
                                Log($"Added {TrainerNID} to the BannedIDs list.");
                            }
                        }
                        quit = true;
                    }

                    msg = $"Found {user.TrainerName}{useridmsg} using multiple accounts.\nPreviously traded with {previous.Name} ({previous.RemoteID}) {delta.TotalMinutes:F1} minutes ago on OT: {TrainerName}.";
                    if (AbuseSettings.EchoNintendoOnlineIDMulti)
                        msg += $"\nID: {TrainerNID}";
                    if (!string.IsNullOrWhiteSpace(AbuseSettings.MultiAbuseEchoMention))
                        msg = $"{AbuseSettings.MultiAbuseEchoMention} {msg}";
                    EchoUtil.Echo(msg);
                }
            }

            // For non-Distribution trades, we can optionally flag users sending to multiple in-game players.
            // Can trigger if the user gets sniped, but can also catch abusers sending to many people.
            if (!isDistribution)
            {
                var previous_remote = PreviousUsers.TryGetPreviousRemoteID(poke.Trainer.ID);
                if (previous_remote != null && previous_remote.Name != TrainerName)
                {
                    if (AbuseSettings.TradeAbuseAction != TradeAbuseAction.Ignore)
                    {
                        if (AbuseSettings.TradeAbuseAction == TradeAbuseAction.BlockAndQuit)
                        {
                            await BlockUser(token).ConfigureAwait(false);
                            if (AbuseSettings.BanIDWhenBlockingUser || bot is not PokeRoutineExecutor8SWSH) // Only ban ID if blocking in SWSH, always in other games.
                            {
                                AbuseSettings.BannedIDs.AddIfNew(new[] { GetReference(TrainerName, TrainerNID, "in-game block for sending to multiple in-game players") });
                                Log($"Added {TrainerNID} to the BannedIDs list.");
                            }
                        }
                        quit = true;
                    }

                    msg = $"Found {user.TrainerName}{useridmsg} sending to multiple in-game players. Previous OT: {previous_remote.Name}, Current OT: {TrainerName}";
                    if (AbuseSettings.EchoNintendoOnlineIDMultiRecipients)
                        msg += $"\nID: {TrainerNID}";
                    if (!string.IsNullOrWhiteSpace(AbuseSettings.MultiRecipientEchoMention))
                        msg = $"{AbuseSettings.MultiRecipientEchoMention} {msg}";
                    EchoUtil.Echo(msg);
                }
            }

            if (quit)
                return PokeTradeResult.SuspiciousActivity;

            return PokeTradeResult.Success;
        }

        public void LogSuccessfulTrades(PokeTradeDetail<T> poke, ulong TrainerNID, string TrainerName)
        {
            // All users who traded, tracked by whether it was a targeted trade or distribution.
            if (poke.Type == PokeTradeType.Random || (poke.Type == PokeTradeType.Clone && Config.InitialRoutine == PokeRoutineType.FlexTrade))
                PreviousUsersDistribution.TryRegister(TrainerNID, TrainerName);
            else
                PreviousUsers.TryRegister(TrainerNID, TrainerName, poke.Trainer.ID);

            _ = UserCooldowns.TryInsert(TrainerNID, TrainerName, true);
        }

        private void CheckExpiration(ulong trainerNID, TradeAbuseSettings AbuseSettings)
        {
            var isWhitelisted = AbuseSettings.WhitelistIDs.List.Find(z => z.ID == trainerNID);
            var isBlacklisted = AbuseSettings.BannedIDs.List.Find(z => z.ID == trainerNID);

            if (isWhitelisted != null)
            {
                bool hasExpired = DateTime.Now > DateTime.ParseExact(isWhitelisted.Expiration, "yyyy.MM.dd - HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                if (hasExpired)
                {
                    AbuseSettings.WhitelistIDs.List.Remove(isWhitelisted);
                    Log($"{isWhitelisted.Name}({isWhitelisted.ID}) whitelist has expired.");
                }
            }

            if (isBlacklisted != null)
            {
                bool hasExpired = DateTime.Now > DateTime.ParseExact(isBlacklisted.Expiration, "yyyy.MM.dd - HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                if (hasExpired)
                {
                    AbuseSettings.BannedIDs.List.Remove(isBlacklisted);
                    Log($"{isBlacklisted.Name}({isBlacklisted.ID}) blacklist has expired.");
                }
            }
        }

        private static RemoteControlAccess GetReference(string name, ulong id, string comment, string expiration = "9999.12.31 - 23:59:59") => new()
        {
            ID = id,
            Name = name,
            Expiration = expiration,
            Comment = $"Added automatically on {DateTime.Now:yyyy.MM.dd - HH:mm:ss} ({comment})",
        };

        // Blocks a user from the box during in-game trades (SWSH).
        private async Task BlockUser(CancellationToken token)
        {
            Log("Blocking user in-game...");
            await PressAndHold(RSTICK, 0_750, 0, token).ConfigureAwait(false);
            await Click(DUP, 0_300, token).ConfigureAwait(false);
            await Click(A, 1_300, token).ConfigureAwait(false);
            await Click(A, 1_300, token).ConfigureAwait(false);
            await Click(DUP, 0_300, token).ConfigureAwait(false);
            await Click(A, 1_100, token).ConfigureAwait(false);
            await Click(A, 1_100, token).ConfigureAwait(false);
        }
    }
}
