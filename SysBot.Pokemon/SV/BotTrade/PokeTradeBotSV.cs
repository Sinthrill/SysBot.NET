﻿using System.Linq;
using PKHeX.Core;
using PKHeX.Core.Searching;
using SysBot.Base;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.PokeDataOffsetsSV;
using System.Numerics;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using PKHeX.Core.AutoMod;

namespace SysBot.Pokemon
{
    // ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
    public class PokeTradeBotSV : PokeRoutineExecutor9SV, ICountBot
    {
        private readonly PokeTradeHub<PK9> Hub;
        private readonly TradeSettings TradeSettings;
        private readonly TradeAbuseSettings AbuseSettings;

        public ICountSettings Counts => TradeSettings;

        private static readonly TrackedUserLog PreviousUsers = new();
        private static readonly TrackedUserLog PreviousUsersDistribution = new();
        private static readonly TrackedUserLog EncounteredUsers = new();
        private static readonly Random rnd = new();

        /// <summary>
        /// Folder to dump received trade data to.
        /// </summary>
        /// <remarks>If null, will skip dumping.</remarks>
        private readonly IDumper DumpSetting;

        /// <summary>
        /// Synchronized start for multiple bots.
        /// </summary>
        public bool ShouldWaitAtBarrier { get; private set; }

        /// <summary>
        /// Tracks failed synchronized starts to attempt to re-sync.
        /// </summary>
        public int FailedBarrier { get; private set; }

        public PokeTradeBotSV(PokeTradeHub<PK9> hub, PokeBotState cfg) : base(cfg)
        {
            Hub = hub;
            TradeSettings = hub.Config.Trade;
            AbuseSettings = hub.Config.TradeAbuse;
            DumpSetting = hub.Config.Folder;
            lastOffered = new byte[8];
        }

        // Cached offsets that stay the same per session.
        private ulong BoxStartOffset;
        private ulong OverworldOffset;
        private ulong PortalOffset;
        private ulong ConnectedOffset;
        private ulong TradePartnerNIDOffset;
        private ulong TradePartnerOfferedOffset;

        // Store the current save's OT and TID/SID for comparison.
        private string OT = string.Empty;
        private uint DisplaySID;
        private uint DisplayTID;

        // Stores whether we returned all the way to the overworld, which repositions the cursor.
        private bool StartFromOverworld = true;
        // Stores whether the last trade was Distribution with fixed code, in which case we don't need to re-enter the code.
        private bool LastTradeDistributionFixed;

        // Track the last Pokémon we were offered since it persists between trades.
        private byte[] lastOffered;

        public override async Task MainLoop(CancellationToken token)
        {
            try
            {
                await InitializeHardware(Hub.Config.Trade, token).ConfigureAwait(false);

                Log("Identifying trainer data of the host console.");
                var sav = await IdentifyTrainer(token).ConfigureAwait(false);
                OT = sav.OT;
                DisplaySID = sav.DisplaySID;
                DisplayTID = sav.DisplayTID;
                RecentTrainerCache.SetRecentTrainer(sav);
                await InitializeSessionOffsets(token).ConfigureAwait(false);

                // Force the bot to go through all the motions again on its first pass.
                StartFromOverworld = true;
                LastTradeDistributionFixed = false;

                Log($"Starting main {nameof(PokeTradeBotSV)} loop.");
                await InnerLoop(sav, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(e.Message);
            }

            Log($"Ending {nameof(PokeTradeBotSV)} loop.");
            await HardStop().ConfigureAwait(false);
        }

        public override async Task HardStop()
        {
            UpdateBarrier(false);
            await CleanExit(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task InnerLoop(SAV9SV sav, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Config.IterateNextRoutine();
                var task = Config.CurrentRoutineType switch
                {
                    PokeRoutineType.Idle => DoNothing(token),
                    _ => DoTrades(sav, token),
                };
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    if (e.StackTrace != null)
                        Connection.LogError(e.StackTrace);
                    var attempts = Hub.Config.Timings.ReconnectAttempts;
                    var delay = Hub.Config.Timings.ExtraReconnectDelay;
                    var protocol = Config.Connection.Protocol;
                    if (!await TryReconnect(attempts, delay, protocol, token).ConfigureAwait(false))
                        return;
                }
            }
        }

        private async Task DoNothing(CancellationToken token)
        {
            Log("No task assigned. Waiting for new task assignment.");
            while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.Idle)
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }

        private async Task DoTrades(SAV9SV sav, CancellationToken token)
        {
            var type = Config.CurrentRoutineType;
            int waitCounter = 0;
            await SetCurrentBox(0, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested && Config.NextRoutineType == type)
            {
                var (detail, priority) = GetTradeData(type);
                if (detail is null)
                {
                    await WaitForQueueStep(waitCounter++, token).ConfigureAwait(false);
                    continue;
                }
                waitCounter = 0;

                detail.IsProcessing = true;
                string tradetype = $" ({detail.Type})";
                Log($"Starting next {type}{tradetype} Bot Trade. Getting data...");
                Hub.Config.Stream.StartTrade(this, detail, Hub);
                Hub.Queues.StartTrade(this, detail);

                await PerformTrade(sav, detail, type, priority, token).ConfigureAwait(false);
            }
        }

        private async Task WaitForQueueStep(int waitCounter, CancellationToken token)
        {
            if (waitCounter == 0)
            {
                // Updates the assets.
                Hub.Config.Stream.IdleAssets(this);
                Log("Nothing to check, waiting for new users...");
            }

            await Task.Delay(1_000, token).ConfigureAwait(false);
        }

        protected virtual (PokeTradeDetail<PK9>? detail, uint priority) GetTradeData(PokeRoutineType type)
        {
            if (Hub.Queues.TryDequeue(type, out var detail, out var priority))
                return (detail, priority);
            if (Hub.Queues.TryDequeueClone(out detail))
                return (detail, PokeTradePriorities.TierFree);
            if (Hub.Queues.TryDequeueLedy(out detail))
                return (detail, PokeTradePriorities.TierFree);
            return (null, PokeTradePriorities.TierFree);
        }

        private async Task PerformTrade(SAV9SV sav, PokeTradeDetail<PK9> detail, PokeRoutineType type, uint priority, CancellationToken token)
        {
            PokeTradeResult result;
            try
            {
                result = await PerformLinkCodeTrade(sav, detail, token).ConfigureAwait(false);
                if (result == PokeTradeResult.Success)
                    return;
            }
            catch (SocketException socket)
            {
                Log(socket.Message);
                result = PokeTradeResult.ExceptionConnection;
                HandleAbortedTrade(detail, type, priority, result);
                throw; // let this interrupt the trade loop. re-entering the trade loop will recheck the connection.
            }
            catch (Exception e)
            {
                Log(e.Message);
                result = PokeTradeResult.ExceptionInternal;
            }

            HandleAbortedTrade(detail, type, priority, result);
        }

        private void HandleAbortedTrade(PokeTradeDetail<PK9> detail, PokeRoutineType type, uint priority, PokeTradeResult result)
        {
            detail.IsProcessing = false;
            if (result.ShouldAttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry)
            {
                detail.IsRetry = true;
                Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradePriorities.Tier2));
                detail.SendNotification(this, "Oops! Something happened. I'll requeue you for another attempt.");
            }
            else
            {
                detail.SendNotification(this, $"Oops! Something happened. Canceling the trade: {result}.");
                detail.TradeCanceled(this, result);
            }
        }

        private async Task<PokeTradeResult> PerformLinkCodeTrade(SAV9SV sav, PokeTradeDetail<PK9> poke, CancellationToken token)
        {
            // Update Barrier Settings
            UpdateBarrier(poke.IsSynchronized);
            poke.TradeInitialize(this);
            Hub.Config.Stream.EndEnterCode(this);

            // StartFromOverworld can be true on first pass or if something went wrong last trade.
            if (StartFromOverworld && !await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                await RecoverToOverworld(token).ConfigureAwait(false);

            // Handles getting into the portal. Will retry this until successful.
            // if we're not starting from overworld, then ensure we're online before opening link trade -- will break the bot otherwise.
            // If we're starting from overworld, then ensure we're online before opening the portal.
            if (!StartFromOverworld && !await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                await RecoverToOverworld(token).ConfigureAwait(false);
                if (!await ConnectAndEnterPortal(Hub.Config, token).ConfigureAwait(false))
                {
                    await RecoverToOverworld(token).ConfigureAwait(false);
                    return PokeTradeResult.RecoverStart;
                }
            }
            else if (StartFromOverworld && !await ConnectAndEnterPortal(Hub.Config, token).ConfigureAwait(false))
            {
                await RecoverToOverworld(token).ConfigureAwait(false);
                return PokeTradeResult.RecoverStart;
            }

            var toSend = poke.TradeData;
            if (toSend.Species != 0)
                await SetBoxPokemonAbsolute(BoxStartOffset, toSend, token, sav).ConfigureAwait(false);

            // Assumes we're freshly in the Portal and the cursor is over Link Trade.
            Log("Selecting Link Trade.");

            await Click(A, 1_500, token).ConfigureAwait(false);
            // Make sure we clear any Link Codes if we're not in Distribution with fixed code, and it wasn't entered last round.
            if (!LastTradeDistributionFixed)
            {
                await Click(X, 1_000, token).ConfigureAwait(false);
                await Click(PLUS, 1_000, token).ConfigureAwait(false);

                // Loading code entry.
                if (poke.Type != PokeTradeType.Random | (poke.Type != PokeTradeType.Clone && Hub.Config.Clone.CloneWhileIdle))
                    Hub.Config.Stream.StartEnterCode(this);
                await Task.Delay(Hub.Config.Timings.ExtraTimeOpenCodeEntry, token).ConfigureAwait(false);

                var code = poke.Code;
                Log($"Entering Link Trade code: {code:0000 0000}...");
                await EnterLinkCode(code, Hub.Config, token).ConfigureAwait(false);

                await Click(PLUS, 3_000, token).ConfigureAwait(false);
                StartFromOverworld = false;
            }

            if ((poke.Type == PokeTradeType.Random) || (poke.Type == PokeTradeType.Clone && Hub.Config.Clone.CloneWhileIdle))
            {
                LastTradeDistributionFixed = !Hub.Config.Distribution.RandomCode;
            }

            // Search for a trade partner for a Link Trade.
            await Click(A, 1_000, token).ConfigureAwait(false);

            // Clear it so we can detect it loading.
            await ClearTradePartnerNID(TradePartnerNIDOffset, token).ConfigureAwait(false);

            // Wait for Barrier to trigger all bots simultaneously.
            WaitAtBarrierIfApplicable(token);
            await Click(A, 1_000, token).ConfigureAwait(false);

            poke.TradeSearching(this);

            // Wait for a Trainer...
            var partnerFound = await WaitForTradePartner(token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                StartFromOverworld = true;
                LastTradeDistributionFixed = false;
                await ExitTradeToPortal(false, token).ConfigureAwait(false);
                return PokeTradeResult.RoutineCancel;
            }
            if (!partnerFound)
            {
                if (!await RecoverToPortal(token).ConfigureAwait(false))
                {
                    Log("Failed to recover to portal.");
                    await RecoverToOverworld(token).ConfigureAwait(false);
                }
                return PokeTradeResult.NoTrainerFound;
            }

            Hub.Config.Stream.EndEnterCode(this);

            // Wait until we get into the box.
            var cnt = 0;
            while (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(0_500, token).ConfigureAwait(false);
                if (++cnt > 20) // Didn't make it in after 10 seconds.
                {
                    await Click(A, 1_000, token).ConfigureAwait(false); // Ensures we dismiss a popup.
                    if (!await RecoverToPortal(token).ConfigureAwait(false))
                    {
                        Log("Failed to recover to portal.");
                        await RecoverToOverworld(token).ConfigureAwait(false);
                    }
                    return PokeTradeResult.RecoverOpenBox;
                }
            }
            await Task.Delay(3_000 + Hub.Config.Timings.ExtraTimeOpenBox, token).ConfigureAwait(false);

            var tradePartner = await GetTradePartnerInfo(token).ConfigureAwait(false);
            var trainerNID = await GetTradePartnerNID(TradePartnerNIDOffset, token).ConfigureAwait(false);
            RecordUtil<PokeTradeBot>.Record($"Initiating\t{trainerNID:X16}\t{tradePartner.TrainerName}\t{poke.Trainer.TrainerName}\t{poke.Trainer.ID}\t{poke.ID}\t{toSend.EncryptionConstant:X8}");
            Log($"Found Link Trade partner: {tradePartner.TrainerName}-{tradePartner.TID7} (ID: {trainerNID})");

            var partnerCheck = CheckPartnerReputation(poke, trainerNID, tradePartner.TrainerName);
            if (partnerCheck != PokeTradeResult.Success)
            {
                await Click(A, 1_000, token).ConfigureAwait(false); // Ensures we dismiss a popup.
                await ExitTradeToPortal(false, token).ConfigureAwait(false);
                return partnerCheck;
            }

            bool isDistribution = false;
            if (poke.Type == PokeTradeType.Random || poke.Type == PokeTradeType.Clone)
                isDistribution = true;
            var list = isDistribution ? PreviousUsersDistribution : PreviousUsers;

            poke.SendNotification(this, $"Found Link Trade partner: {tradePartner.TrainerName}. Waiting for a Pokémon...");

            int multiTrade = 0;
            while (multiTrade < Hub.Config.Clone.TradesPerEncounter)
            {

                if (multiTrade > 0)
                {
                    list.TryRegister(trainerNID, tradePartner.TrainerName);
                }

                // Hard check to verify that the offset changed from the last thing offered from the previous trade.
                // This is because box opening times can vary per person, the offset persists between trades, and can also change offset between trades.
                
                var tradeOffered = await ReadUntilChanged(TradePartnerOfferedOffset, lastOffered, 10_000, 0_500, false, true, token).ConfigureAwait(false);
                Log($"Trade partner offered offset is {TradePartnerOfferedOffset}");
                if (!tradeOffered)
                {
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return PokeTradeResult.TrainerTooSlow;
                }

                if (poke.Type == PokeTradeType.Dump)
                {
                    var result = await ProcessDumpTradeAsync(poke, token).ConfigureAwait(false);
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return result;
                }

                // Wait for user input...
                var offered = await ReadUntilPresent(TradePartnerOfferedOffset, 25_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
                var oldEC = await SwitchConnection.ReadBytesAbsoluteAsync(TradePartnerOfferedOffset, 8, token).ConfigureAwait(false);
                if (offered == null || offered.Species < 1 || !offered.ChecksumValid)
                {
                    Log("Trade ended because a valid Pokémon was not offered.");
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return PokeTradeResult.TrainerTooSlow;
                }

                PokeTradeResult update;
                var trainer = new PartnerDataHolder(0, tradePartner.TrainerName, tradePartner.TID7);
                (toSend, update) = await GetEntityToSend(sav, poke, offered, oldEC, toSend, trainer, token).ConfigureAwait(false);
                if (update != PokeTradeResult.Success)
                {
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return update;
                }

                Log("Confirming trade.");
                var tradeResult = await ConfirmAndStartTrading(poke, token).ConfigureAwait(false);
                if (tradeResult != PokeTradeResult.Success)
                {
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return tradeResult;
                }

                if (token.IsCancellationRequested)
                {
                    StartFromOverworld = true;
                    LastTradeDistributionFixed = false;
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return PokeTradeResult.RoutineCancel;
                }

                // Trade was Successful!
                var received = await ReadPokemon(BoxStartOffset, BoxFormatSlotSize, token).ConfigureAwait(false);
                // Pokémon in b1s1 is same as the one they were supposed to receive (was never sent).
                if (SearchUtil.HashByDetails(received) == SearchUtil.HashByDetails(toSend) && received.Checksum == toSend.Checksum)
                {
                    Log("User did not complete the trade.");
                    await ExitTradeToPortal(false, token).ConfigureAwait(false);
                    return PokeTradeResult.TrainerTooSlow;
                }

                // As long as we got rid of our inject in b1s1, assume the trade went through.
                Log("User completed the trade.");
                poke.TradeFinished(this, received);

                // Only log if we completed the trade.
                UpdateCountsAndExport(poke, received, toSend);

                // Sometimes they offered another mon, so store that immediately upon leaving Union Room.
                lastOffered = await SwitchConnection.ReadBytesAbsoluteAsync(TradePartnerOfferedOffset, 8, token).ConfigureAwait(false);

                if (poke.Type == PokeTradeType.Random || poke.Type == PokeTradeType.Clone)
                {
                    multiTrade++;
                } else
                {
                    multiTrade = Hub.Config.Clone.TradesPerEncounter;
                }

                if (multiTrade < Hub.Config.Clone.TradesPerEncounter)
                {
                    await Task.Delay(Hub.Config.Timings.ExtraTimeMultiTrade, token).ConfigureAwait(false);
                }
            }

            list.TryRegister(trainerNID, tradePartner.TrainerName);

            await ExitTradeToPortal(false, token).ConfigureAwait(false);
            return PokeTradeResult.Success;
        }

        private void UpdateCountsAndExport(PokeTradeDetail<PK9> poke, PK9 received, PK9 toSend)
        {
            var counts = TradeSettings;
            if (poke.Type == PokeTradeType.Random)
                counts.AddCompletedDistribution();
            else if (poke.Type == PokeTradeType.Clone)
                counts.AddCompletedClones();
            else
                counts.AddCompletedTrade();

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
            {
                var subfolder = poke.Type.ToString().ToLower();
                DumpPokemon(DumpSetting.DumpFolder, subfolder, received); // received by bot
                if (poke.Type is PokeTradeType.Specific or PokeTradeType.Clone)
                    DumpPokemon(DumpSetting.DumpFolder, "traded", toSend); // sent to partner
            }
        }

        private async Task<PokeTradeResult> ConfirmAndStartTrading(PokeTradeDetail<PK9> detail, CancellationToken token)
        {
            // We'll keep watching B1S1 for a change to indicate a trade started -> should try quitting at that point.
            var oldEC = await SwitchConnection.ReadBytesAbsoluteAsync(BoxStartOffset, 8, token).ConfigureAwait(false);

            await Click(A, 3_000, token).ConfigureAwait(false);
            for (int i = 0; i < Hub.Config.Trade.MaxTradeConfirmTime; i++)
            {
                if (await IsUserBeingShifty(detail, token).ConfigureAwait(false))
                    return PokeTradeResult.SuspiciousActivity;
                await Click(A, 1_000, token).ConfigureAwait(false);

                // EC is detectable at the start of the animation.
                var newEC = await SwitchConnection.ReadBytesAbsoluteAsync(BoxStartOffset, 8, token).ConfigureAwait(false);
                if (!newEC.SequenceEqual(oldEC))
                {
                    await Task.Delay(25_000, token).ConfigureAwait(false);
                    return PokeTradeResult.Success;
                }
            }
            // If we don't detect a B1S1 change, the trade didn't go through in that time.
            return PokeTradeResult.TrainerTooSlow;
        }

        // Upon connecting, their Nintendo ID will instantly update.
        protected virtual async Task<bool> WaitForTradePartner(CancellationToken token)
        {
            Log("Waiting for trainer...");
            int ctr = (Hub.Config.Trade.TradeWaitTime * 1_000) - 2_000;
            await Task.Delay(2_000, token).ConfigureAwait(false);
            while (ctr > 0)
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                ctr -= 1_000;
                var newNID = await GetTradePartnerNID(TradePartnerNIDOffset, token).ConfigureAwait(false);
                if (newNID != 0)
                {
                    TradePartnerOfferedOffset = await SwitchConnection.PointerAll(Offsets.LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
                    return true;
                }

                // Fully load into the box.
                await Task.Delay(1_000, token).ConfigureAwait(false);
            }
            return false;
        }

        // If we can't manually recover to overworld, reset the game.
        // Try to avoid pressing A which can put us back in the portal with the long load time.
        private async Task<bool> RecoverToOverworld(CancellationToken token)
        {
            if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                return true;

            Log("Attempting to recover to overworld.");
            var attempts = 0;
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                attempts++;
                if (attempts >= 30)
                    break;

                await Click(B, 1_300, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;

                await Click(B, 2_000, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;

                await Click(A, 1_300, token).ConfigureAwait(false);
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;
            }

            // We didn't make it for some reason.
            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                Log("Failed to recover to overworld, rebooting the game.");
                await RestartGameSV(token).ConfigureAwait(false);
            }
            await Task.Delay(1_000, token).ConfigureAwait(false);

            // Force the bot to go through all the motions again on its first pass.
            StartFromOverworld = true;
            LastTradeDistributionFixed = false;
            return true;
        }

        // If we didn't find a trainer, we're still in the portal but there can be 
        // different numbers of pop-ups we have to dismiss to get back to when we can trade.
        // Rather than resetting to overworld, try to reset out of portal and immediately go back in.
        private async Task<bool> RecoverToPortal(CancellationToken token)
        {
            Log("Reorienting to Poké Portal.");
            var attempts = 0;
            while (await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
            {
                await Click(B, 1_500, token).ConfigureAwait(false);
                if (++attempts >= 30)
                {
                    Log("Failed to recover to Poké Portal.");
                    return false;
                }
            }

            // Should be in the X menu hovered over Poké Portal.
            await Click(A, 1_000, token).ConfigureAwait(false);

            return await SetUpPortalCursor(token).ConfigureAwait(false);
        }

        // Should be used from the overworld. Opens X menu, attempts to connect online, and enters the Portal.
        // The cursor should be positioned over Link Trade.
        private async Task<bool> ConnectAndEnterPortal(PokeTradeHubConfig config, CancellationToken token)
        {
            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                await RecoverToOverworld(token).ConfigureAwait(false);

            Log("Opening the Poké Portal.");

            // Open the X Menu.
            await Click(X, 1_000, token).ConfigureAwait(false);

            // Connect online if not already.
            if (!await ConnectToOnline(config, token).ConfigureAwait(false))
            {
                Log("Failed to connect to online.");
                return false; // Failed, either due to connection or softban.
            }

            // Make sure we're at the bottom of the Main Menu.
            await Click(DRIGHT, 0_300, token).ConfigureAwait(false);
            await PressAndHold(DDOWN, 1_000, 1_000, token).ConfigureAwait(false);
            await Click(DUP, 0_200, token).ConfigureAwait(false);
            await Click(DUP, 0_200, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);

            return await SetUpPortalCursor(token).ConfigureAwait(false);
        }

        // Waits for the Portal to load (slow) and then moves the cursor down to link trade.
        private async Task<bool> SetUpPortalCursor(CancellationToken token)
        {
            // Wait for the portal to load.
            var attempts = 0;
            while (!await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(0_500, token).ConfigureAwait(false);
                if (++attempts > 20)
                {
                    Log("Failed to load the Poké Portal.");
                    return false;
                }
            }
            await Task.Delay(2_000 + Hub.Config.Timings.ExtraTimeLoadPortal, token).ConfigureAwait(false);

            // Handle the news popping up.
            if (await SwitchConnection.IsProgramRunning(LibAppletWeID, token).ConfigureAwait(false))
            {
                Log("News detected, will close once it's loaded!");
                await Task.Delay(5_000, token).ConfigureAwait(false);
                await Click(B, 2_000 + Hub.Config.Timings.ExtraTimeLoadPortal, token).ConfigureAwait(false);
            }

            Log("Adjusting the cursor in the Portal.");
            // Move down to Link Trade.
            await Click(DDOWN, 0_300, token).ConfigureAwait(false);
            await Click(DDOWN, 0_300, token).ConfigureAwait(false);
            return true;
        }

        // Connects online if not already. Assumes the user to be in the X menu to avoid a news screen.
        private async Task<bool> ConnectToOnline(PokeTradeHubConfig config, CancellationToken token)
        {
            if (await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
                return true;

            await Click(L, 1_000, token).ConfigureAwait(false);
            await Click(A, 4_000, token).ConfigureAwait(false);

            var wait = 0;
            while (!await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(0_500, token).ConfigureAwait(false);
                if (++wait > 30) // More than 15 seconds without a connection.
                    return false;
            }

            // There are several seconds after connection is established before we can dismiss the menu.
            await Task.Delay(3_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
            return true;
        }

        private async Task ExitTradeToPortal(bool unexpected, CancellationToken token)
        {
            if (await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
                return;

            if (unexpected)
                Log("Unexpected behavior, recovering to Portal.");

            // Ensure we're not in the box first.
            // Takes a long time for the Portal to load up, so once we exit the box, wait 5 seconds.
            Log("Leaving the box...");
            var attempts = 0;
            while (await IsInBox(PortalOffset, token).ConfigureAwait(false))
            {
                await Click(B, 1_000, token).ConfigureAwait(false);
                if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                {
                    await Task.Delay(5_000, token).ConfigureAwait(false);
                    break;
                }

                await Click(A, 1_000, token).ConfigureAwait(false);
                if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                {
                    await Task.Delay(5_000, token).ConfigureAwait(false);
                    break;
                }

                await Click(B, 1_000, token).ConfigureAwait(false);
                if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                {
                    await Task.Delay(5_000, token).ConfigureAwait(false);
                    break;
                }

                // Didn't make it out of the box for some reason.
                if (++attempts > 20)
                {
                    Log("Failed to exit box, rebooting the game.");
                    if (!await RecoverToOverworld(token).ConfigureAwait(false))
                        await RestartGameSV(token).ConfigureAwait(false);
                    await ConnectAndEnterPortal(Hub.Config, token).ConfigureAwait(false);
                    return;
                }
            }

            // Wait for the portal to load.
            Log("Waiting on the portal to load...");
            attempts = 0;
            while (!await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                if (await IsInPokePortal(PortalOffset, token).ConfigureAwait(false))
                    break;

                // Didn't make it into the portal for some reason.
                if (++attempts > 40)
                {
                    Log("Failed to load the portal, rebooting the game.");
                    if (!await RecoverToOverworld(token).ConfigureAwait(false))
                        await RestartGameSV(token).ConfigureAwait(false);
                    await ConnectAndEnterPortal(Hub.Config, token).ConfigureAwait(false);
                    return;
                }
            }
            await Task.Delay(2_000, token).ConfigureAwait(false);
        }

        // These don't change per session and we access them frequently, so set these each time we start.
        private async Task InitializeSessionOffsets(CancellationToken token)
        {
            Log("Caching session offsets...");
            BoxStartOffset = await SwitchConnection.PointerAll(Offsets.BoxStartPokemonPointer, token).ConfigureAwait(false);
            OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
            PortalOffset = await SwitchConnection.PointerAll(Offsets.PortalBoxStatusPointer, token).ConfigureAwait(false);
            ConnectedOffset = await SwitchConnection.PointerAll(Offsets.IsConnectedPointer, token).ConfigureAwait(false);
            TradePartnerNIDOffset = await SwitchConnection.PointerAll(Offsets.LinkTradePartnerNIDPointer, token).ConfigureAwait(false);
        }

        // todo: future
        protected virtual async Task<bool> IsUserBeingShifty(PokeTradeDetail<PK9> detail, CancellationToken token)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return false;
        }

        private async Task RestartGameSV(CancellationToken token)
        {
            await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
            await InitializeSessionOffsets(token).ConfigureAwait(false);
        }

        private async Task<PokeTradeResult> ProcessDumpTradeAsync(PokeTradeDetail<PK9> detail, CancellationToken token)
        {
            int ctr = 0;
            var time = TimeSpan.FromSeconds(Hub.Config.Trade.MaxDumpTradeTime);
            var start = DateTime.Now;

            var pkprev = new PK9();
            var bctr = 0;
            while (ctr < Hub.Config.Trade.MaxDumpsPerTrade && DateTime.Now - start < time)
            {
                if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
                    break;
                if (bctr++ % 3 == 0)
                    await Click(B, 0_100, token).ConfigureAwait(false);

                // Wait for user input... Needs to be different from the previously offered Pokémon.
                var pk = await ReadUntilPresent(TradePartnerOfferedOffset, 3_000, 0_050, BoxFormatSlotSize, token).ConfigureAwait(false);
                if (pk == null || pk.Species < 1 || !pk.ChecksumValid || SearchUtil.HashByDetails(pk) == SearchUtil.HashByDetails(pkprev))
                    continue;

                // Save the new Pokémon for comparison next round.
                pkprev = pk;

                // Send results from separate thread; the bot doesn't need to wait for things to be calculated.
                if (DumpSetting.Dump)
                {
                    var subfolder = detail.Type.ToString().ToLower();
                    DumpPokemon(DumpSetting.DumpFolder, subfolder, pk); // received
                }

                var la = new LegalityAnalysis(pk);
                var verbose = $"```{la.Report(true)}```";
                Log($"Shown Pokémon is: {(la.Valid ? "Valid" : "Invalid")}.");

                ctr++;
                var msg = Hub.Config.Trade.DumpTradeLegalityCheck ? verbose : $"File {ctr}";

                // Extra information about trainer data for people requesting with their own trainer data.
                var ot = pk.OT_Name;
                var ot_gender = pk.OT_Gender == 0 ? "Male" : "Female";
                var tid = pk.GetDisplayTID().ToString(pk.GetTrainerIDFormat().GetTrainerIDFormatStringTID());
                var sid = pk.GetDisplaySID().ToString(pk.GetTrainerIDFormat().GetTrainerIDFormatStringSID());
                msg += $"\n**Trainer Data**\n```OT: {ot}\nOTGender: {ot_gender}\nTID: {tid}\nSID: {sid}```";

                // Extra information for shiny eggs, because of people dumping to skip hatching.
                var eggstring = pk.IsEgg ? "Egg " : string.Empty;
                msg += pk.IsShiny ? $"\n**This Pokémon {eggstring}is shiny!**" : string.Empty;
                detail.SendNotification(this, pk, msg);
            }

            Log($"Ended Dump loop after processing {ctr} Pokémon.");
            if (ctr == 0)
                return PokeTradeResult.TrainerTooSlow;

            TradeSettings.AddCompletedDumps();
            detail.Notifier.SendNotification(this, detail, $"Dumped {ctr} Pokémon.");
            detail.Notifier.TradeFinished(this, detail, detail.TradeData); // blank PK9
            return PokeTradeResult.Success;
        }

        private async Task<TradePartnerSV> GetTradePartnerInfo(CancellationToken token)
        {
            // We're able to see both users' MyStatus, but one of them will be ourselves.
            var trader_info = await GetTradePartnerMyStatus(Offsets.Trader1MyStatusPointer, token).ConfigureAwait(false);
            if (trader_info.OT == OT && trader_info.DisplaySID == DisplaySID && trader_info.DisplayTID == DisplayTID) // This one matches ourselves.
                trader_info = await GetTradePartnerMyStatus(Offsets.Trader2MyStatusPointer, token).ConfigureAwait(false);
            return new TradePartnerSV(trader_info);
        }

        protected virtual async Task<(PK9 toSend, PokeTradeResult check)> GetEntityToSend(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, byte[] oldEC, PK9 toSend, PartnerDataHolder partnerID, CancellationToken token)
        {
            return poke.Type switch
            {
                PokeTradeType.Random => await HandleRandomLedy(sav, poke, offered, toSend, partnerID, token).ConfigureAwait(false),
                PokeTradeType.Clone => await HandleClone(sav, poke, offered, oldEC, partnerID, token).ConfigureAwait(false),
                _ => (toSend, PokeTradeResult.Success),
            };
        }

        private (PK9 clone, string tradeType, string swap1, string swap2, PokeTradeResult check) GetCloneSwapInfo(PK9 clone, PK9 offered)
        {
            var config = Hub.Config.Clone;
            string swap = "";
            string info = "";
            bool evNickname = offered.Nickname.All(c => "M0SA".Contains(c)) && offered.Nickname.Length == 6;
            bool evHexNickname = offered.Nickname.All(c => "0123456789ABCDEFSN".Contains(c)) && offered.Nickname.Length == 12;
            bool evReset = offered.Nickname == "Reset";
            string item = GameInfo.GetStrings(1).Item[offered.HeldItem];
            if (offered.HeldItem == (int)config.ItemSwapItem)
            {
                swap = "Item";
            }
            if (offered.HeldItem != 0 && offered.HeldItem != 17 && swap == "")
            {
                string[] itemString = item.Split(' ');
                if (itemString.Length > 1)
                {
                    swap = itemString[1];
                    if (swap != "Ball" && swap != "Tera")
                        swap = "";                       
                }
                
                info = itemString[0];
            }
            if (offered.HeldItem == (int)config.NickSwapItem)
            {
                swap = "Name";
            }
            if (offered.HeldItem == (int)config.DistroSwapItem)
            {
                swap = "Distro";
            }
            if (offered.HeldItem == (int)config.GennedSwapItem && swap == "")
            {
                bool genNickname = offered.Nickname.All(c => "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ".Contains(c));
                bool genNickLength = offered.Nickname.Length is 5 or 6 or 11 or 12;
                if (genNickname && genNickLength)
                    swap = "Genned";
            }
            if (evNickname || evHexNickname || evReset)
            {
                if (swap == "")
                {                    
                    swap = "EV";
                }
            }
            switch (swap)
            {
                case "Ball":
                    Log($"Requesting {offered.Nickname} be in {StringsUtil.UseAnOrNot(info)} Ball.");
                    break;
                case "Tera":
                    Log($"Requesting {offered.Nickname} be changed to {info} Tera.");
                    break;
                case "EV":
                    Log($"{GameInfo.GetStrings(1).Species[offered.Species]} requesting an EV spread of {offered.Nickname}.");
                    break;
                case "Name":
                    Log($"{GameInfo.GetStrings(1).Species[offered.Species]} requesting a Nickname removal.");
                    break;
                case "Item":
                    Log($"{GameInfo.GetStrings(1).Species[offered.Species]} requesting a held item.");
                    break;
                case "Distro":
                    Log($"Requesting Distribution Pokémon {offered.Nickname}.");
                    break;
                case "Genned":
                    Log($"Requesting a fresh Genned Pokémon with {offered.Nickname}");
                    break;
                default:
                    break;
            }


            string info2 = "";
            string swap2 = "";
            string swap1 = "";
            if (offered.IsNicknamed && swap != "")
                (swap1, info2, swap2, swap) = CheckDoubleSwap(swap, offered.Nickname);
            return swap switch
            {
                "Name" => HandleNameRemove(clone, offered),
                "Tera" => HandleTeraSwap(info, clone, offered),
                "Ball" => HandleBallSwap(info, clone, offered),
                "Double" => HandleDoubleSwap(info, swap1, info2, swap2, clone, offered),
                "Item" => HandleItemSwap(clone, offered),
                "EV" => HandleEVSwap(clone, offered),
                "Distro" => HandleDistroSwap(clone, offered),
                "Genned" => HandleGennedSwap(offered),
                _ => (clone, swap, swap1, swap2, PokeTradeResult.Success),
            };
        }

        private (string swap1, string info2, string swap2, string swap) CheckDoubleSwap(string swap, string nickname)
        {
            if (swap is "Ball")
            {
                bool EVSwap = nickname.All(c => "M0SA".Contains(c)) && nickname.Length == 6;
                bool EVHexSwap = nickname.All(c => "0123456789ABCDEFSN".Contains(c)) && nickname.Length == 12;
                bool EVReset = nickname == "Reset";
                if (EVSwap || EVHexSwap || EVReset)
                {
                    Log($"Requesting EV spread {nickname} as well.");
                    return (swap, nickname, "EV", "Double");
                }

                if (nickname.All(c => "0123456789".Contains(c)))
                    return (swap, "None", "None", swap);

                MoveType a;
                try
                {
                    if (nickname == "Any")
                        return (swap, "None", "None", swap);
                    a = (MoveType)Enum.Parse(typeof(MoveType), nickname);
                    Log($"Requesting {nickname} Tera as well.");
                    return (swap, nickname, "Tera", "Double");
                }
                catch (Exception e)
                {
                    Log(e.Message);
                    return (swap, "None", "None", swap);
                }
            } else if (swap is "Tera")
            {
                bool EVSwap = nickname.All(c => "M0SA".Contains(c)) && nickname.Length == 6;
                bool EVHexSwap = nickname.All(c => "0123456789ABCDEFSN".Contains(c)) && nickname.Length == 12;
                bool EVReset = nickname == "Reset";
                if (EVSwap || EVHexSwap || EVReset)
                {
                    Log($"Requesting EV spread {nickname} as well.");
                    return (swap, nickname, "EV", "Double");
                }

                if (nickname.All(c => "0123456789".Contains(c)))
                    return (swap, "None", "None", swap);

                Ball b;
                if (nickname == "Poké")
                    nickname = "Poke";

                try
                {
                    if (nickname == "None")
                        return(swap, "None", "None", swap);
                    b = (Ball)Enum.Parse(typeof(Ball), nickname);
                    Log($"Requesting to be in {StringsUtil.UseAnOrNot(nickname)} Ball as well.");
                    return (swap, nickname, "Ball", "Double");
                }
                catch (Exception e)
                {
                    Log(e.Message);
                    return (swap, "None", "None", swap);
                }
            } else
            {
                return (swap, "None", "None", swap);
            }
        }

        private (PK9 clone, string tradeType, string swap1, string swap2, PokeTradeResult check) HandleDoubleSwap(string info, string swap1, string info2, string swap2, PK9 clone, PK9 offered)
        {
            int i = 0;
            string[] multiSwap = new string[2] { swap1, swap2 };
            string[] multiInfo = new string[2] { info, info2 };
            PokeTradeResult update = new();
            foreach (var pair in multiSwap)
            {
                switch (pair)
                {
                    case "Tera":
                        (clone, _, _, _, update) = HandleTeraSwap(multiInfo[i++], clone, offered);
                        break;

                    case "Ball":
                        (clone, _, _, _, update) = HandleBallSwap(multiInfo[i++], clone, offered);
                        break;

                    case "EV":
                        (clone, _, _, _, update) = HandleEVSwap(clone, offered);
                        i++;
                        break;
                }
            }
            if (clone.IsNicknamed)
            {
                clone.SetDefaultNickname();
                clone.RefreshChecksum();
            }
            return (clone, "Double", swap1, swap2, update);
        }

        private (PK9 clone, string tradeType, string swap1, string swap2, PokeTradeResult check) HandleTeraSwap(string type, PK9 clone, PK9 offered)
        {
            string swap = "Tera", swap1 = "", swap2 = "";
            MoveType a;
            try
            {
                a = (MoveType)Enum.Parse(typeof(MoveType), type);
                clone.TeraTypeOverride = a;
                clone.RefreshChecksum();
                return (clone, swap, swap1, swap2, PokeTradeResult.Success);
            }
            catch (Exception e)
            {
                Log(e.Message);
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
            }
        }

        private (PK9 clone, string tradeType, string swap1, string swap2, PokeTradeResult check) HandleBallSwap(string ball, PK9 clone, PK9 offered)
        {
            string swap = "Ball", swap1 = "", swap2 = "";
            Ball b;
            if (offered.Species > 905 && offered.Species < 915)
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);

            //Handle Cherish Balls not being available
            if (ball is "Cherish")
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);

            //Handle items with Ball as second word that aren't actually Balls
            if (offered.FatefulEncounter || ball is "Smoke" or "Iron" or "Light")
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);

            //Handle Balls that aren't released yet in SV
            if (ball is "Sport" or "Safari")
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);

            //Handle LA Balls until Home support
            if (ball is "LAPoke" or "LAGreat" or "LAUltra" or "LAFeather" or "LAWing" or "LAJet" or "LAHeavy" or "LALeaden" or "LAGigaton" or "LAOrigin")
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);

            //In-game trades from NPCs can't have Balls swapped
            if (offered.Met_Location is 30001)
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);

            //GMeowth from Salvatore can't have Ball swapped
            if (offered.Met_Location is 130 or 131)
                if (offered.Met_Level is 5)
                    return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);

            //Master balls don't breed down
            if (offered.WasEgg)
            {
                if (ball is "Master")
                    return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
            }

            if (ball is "Poké")
                ball = "Poke";

            try
            {
                b = (Ball)Enum.Parse(typeof(Ball), ball);
                clone.Ball = (int)b;
                clone.RefreshChecksum();
                return (clone, swap, swap1, swap2, PokeTradeResult.Success);
            }
            catch (Exception e)
            {
                Log(e.Message);
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
            }
        }

        private static (PK9 clone, string tradeType, string swap1, string swap2, PokeTradeResult check) HandleNameRemove(PK9 clone, PK9 offered)
        {
            string swap = "Name", swap1 = "", swap2 = "";
            if (offered.Met_Location == 30001)
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
            clone.SetDefaultNickname();
            clone.RefreshChecksum();
            return (clone, swap, swap1, swap2, PokeTradeResult.Success);
        }

        private static (PK9 clone, string tradeType, string swap1, string swap2, PokeTradeResult check) HandleEVSwap(PK9 clone, PK9 offered)
        {
            string swap = "EV", swap1 = "", swap2 = "";
            int[] spread = new int[] { 0, 0, 0, 0, 0, 0 };
            if (offered.Nickname is "Reset")
            {
                clone.SetEVs(spread);
                clone.SetDefaultNickname();
                clone.RefreshChecksum();
                return (clone, swap, swap1, swap2, PokeTradeResult.Success);
            } 
            int i = 0, j = 0;
            int maxEV = 510;
            if (offered.Nickname.Length == 6)
            {
                List<int> splitEV = new();
                char[] nickChars = offered.Nickname.ToCharArray();
                foreach (char f in nickChars)
                {
                    if (f is 'M')
                    {
                        spread[i++] = 252;
                    }
                    else if (f is '0')
                    {
                        spread[i++] = 0;
                    }
                    else if (f is 'S')
                    {
                        splitEV.Add(i++);
                    }
                    else if (f is 'A')
                    {
                        spread[i] = offered.GetEV(i++);
                    }
                    else
                    {
                        return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
                    }
                }

                if (spread.Sum() > maxEV)
                    return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);

                if (splitEV.Count != 0)
                {
                    int split = (maxEV - spread.Sum()) / splitEV.Count;
                    if (split > 252)
                        split = 252;
                    foreach (int e in splitEV)
                    {
                        spread[e] = split;
                    }
                }

                if (spread.Sum() > maxEV)
                    return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);

                clone.SetEVs(spread);
                clone.SetDefaultNickname();
                clone.RefreshChecksum();
                return (clone, swap, swap1, swap2, PokeTradeResult.Success);
            } else if (offered.Nickname.Length == 12)
            {
                List<string> nickHexValues = new();
                for (i = 0; i < 12; i += 2)
                {
                    nickHexValues.Add(offered.Nickname.Substring(i, 2));
                }
                foreach (string f in nickHexValues)
                {
                    if (f is "NN")
                        spread[j++] = 0;
                    else if (f is "SS")
                        spread[j] = offered.GetEV(j++);
                    else
                    {
                        int EVValue = Convert.ToInt32(f, 16);
                        if (EVValue > 252)
                            EVValue = 252;
                        spread[j++] = EVValue;
                    }
                }

                if (spread.Sum() > maxEV)
                    return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);

                clone.SetEVs(spread);
                clone.SetDefaultNickname();
                clone.RefreshChecksum();
                return (clone, swap, swap1, swap2, PokeTradeResult.Success);
            } else
            {
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
            }
        }

        private static (PK9 clone, PokeTradeResult check) HandleSecondEVSwap(PK9 clone, PK9 pk2)
        {
            int i; int j = 0;
            int[] spread = new int[] { 0, 0, 0, 0, 0, 0 };
            int maxEV = 510;
            if (pk2.Nickname.Length == 12)
            {
                List<string> nickHexValues = new();
                for (i = 0; i < 12; i += 2)
                {
                    nickHexValues.Add(pk2.Nickname.Substring(i, 2));
                }
                foreach (string f in nickHexValues)
                {
                    if (f == "NN")
                        spread[j++] = 0;
                    else if (f == "SS")
                        spread[j] = clone.GetEV(j++);
                    else
                    {
                        int EVValue = Convert.ToInt32(f, 16);
                        if (EVValue > 252)
                            EVValue = 252;
                        spread[j++] = EVValue;
                    }
                }

                if (spread.Sum() > maxEV)
                    return (pk2, PokeTradeResult.TrainerRequestBad);

                clone.SetEVs(spread);
                clone.SetDefaultNickname();
                clone.RefreshChecksum();
                return (clone, PokeTradeResult.Success);
            }
            else
            {
                return (pk2, PokeTradeResult.TrainerRequestBad);
            }

        }

        private (PK9 clone, string tradeType, string swap1, string swap2, PokeTradeResult check) HandleItemSwap(PK9 clone, PK9 offered)
        {
            string swap = "Item", swap1 = "", swap2 = "";
            bool isParsable = short.TryParse(offered.Nickname, out short itemID);
            if (!isParsable)
            {
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
            }
            itemID -= 1;
            Log($"Requesting item {GameInfo.GetStrings(1).Item[itemID]}");
            bool canHold = ItemRestrictions.IsHeldItemAllowed(itemID, offered.Context);
            if (!canHold)
            {
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
            }
            clone.HeldItem = itemID;
            clone.RefreshChecksum();
            return (clone, swap, swap1, swap2, PokeTradeResult.Success);

        }

        private (PK9 clone, string tradeType, string swap1, string swap2, PokeTradeResult check) HandleDistroSwap(PK9 clone, PK9 offered)
        {
            string swap = "Distro", swap1 = "", swap2 = "";
            ulong dummyID = 0;
            var trade = Hub.Ledy.GetLedyTrade(offered, dummyID);
            if (trade != null)
            {
                clone = trade.Receive;
                return (clone, swap, swap1, swap2, PokeTradeResult.Success);
            }
            return (clone, "None", swap1, swap2, PokeTradeResult.Success);
        }

        private static ushort Base36ToUShort(string convert)
        {
            int tbase = 36;
            ushort b10 = (ushort)convert
                        .Select(d => d >= '0' && d <= '9' ? d - '0' : 10 + char.ToUpper(d) - 'A')
                        .Aggregate(0, (pos, d) => pos * tbase + d);
            return b10;
        }

        // Working on full genning support via nickname, commented until completed.
        private (PK9 pk, string tradeType, string swap1, string swap2, PokeTradeResult check) HandleGennedSwap(PK9 offered)
        {
            string swap = "Genned", swap1 = "", swap2 = "", nature, abiName, formName = "";
            int ability;
            ushort abiIndex, natureIndex;
            byte form;
            bool shiny, hasForm = false;
            char abiChar, genderChar;
            var s = GameInfo.Strings;
            string genInput = offered.Nickname;
            ushort species = Base36ToUShort(genInput[..2]);
            string specName = GameInfo.GetStrings(1).Species[species];           
            if (species >= (int)Species.MAX_COUNT)
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
            PersonalInfo9SV formInfo;
            PersonalInfo9SV speciesInfo = formInfo = PersonalTable.SV[species];

            // Handle Species with multiple Forms and Parse set info
            string[] AvailForms = species == (int)Species.Alcremie ? FormConverter.GetAlcremieFormList(s.forms) : FormConverter.GetFormList(species, s.types, s.forms, GameInfo.GenderSymbolASCII, EntityContext.Gen9);
            bool formNick = offered.Nickname.Length is 6 or 12;
            if (formNick)
            {
                form = (byte)Base36ToUShort(genInput.Substring(2, 1));
                hasForm = speciesInfo.IsFormWithinRange(form);
                if (hasForm)
                {
                    formName = AvailForms[form];
                    formInfo = PersonalTable.SV.GetFormEntry(species, form);
                }
                /* Commenting Ability information for potential future re-use.
                abiChar = genInput[4];
                abiIndex = abiChar switch
                {
                    'F' => 0,
                    'S' => 1,
                    'H' => 2,
                    _ => 0,
                };
                bool hasForm = PersonalTable.SV[species].IsFormWithinRange(form);
                if (hasForm)
                {
                    formName = AvailForms[form];
                    ability = PersonalTable.SV[species, form].GetAbilityAtIndex(abiIndex);
                }
                else
                {
                    ability = PersonalTable.SV[species].GetAbilityAtIndex(abiIndex);
                }
                abiName = GameInfo.GetStrings(1).Ability[ability];
                */
                shiny = genInput.Substring(3, 1) == "S";
                genderChar = genInput[4];
                natureIndex = Base36ToUShort(genInput.Substring(5, 1));
                if (natureIndex > 24)
                    natureIndex = (ushort)rnd.Next(0, 24);
                nature = GameInfo.GetStrings(1).Natures[natureIndex];
            } else
            {
                shiny = genInput.Substring(2, 1) == "S";
                genderChar = genInput[3];
                /*
                abiChar = genInput[3];
                abiIndex = abiChar switch
                {
                    'F' => 0,
                    'S' => 1,
                    'H' => 2,
                    _ => 0,
                };
                abiName = GameInfo.GetStrings(1).Ability[PersonalTable.SV[species].GetAbilityAtIndex(abiIndex)];
                */
                natureIndex = Base36ToUShort(genInput.Substring(4, 1));
                if (natureIndex > 24)
                    natureIndex = (ushort)rnd.Next(0, 24);
                nature = GameInfo.GetStrings(1).Natures[natureIndex];
            }

            int validGender = hasForm ? formInfo.Gender : speciesInfo.Gender;

            var reqGender = validGender switch
            {
                PersonalInfo.RatioMagicGenderless => 2,
                PersonalInfo.RatioMagicFemale => 1,
                PersonalInfo.RatioMagicMale => 0,
                _ => genderChar == 'M' ? 0 : 1,
            };

            string? genderLog = Enum.GetName(typeof(Gender), reqGender);
            if (genderLog is null)
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);            
            string genderSet = reqGender switch
            {
                1 => " (F)\r\n",
                0 => " (M)\r\n",
                _ => "\r\n",
            };
            string shinyLog = shiny ? "Shiny " : "";
            string formLog = formName is not "" ? "-" + formName : "";
            if (formLog.Contains(" ("))
            {
                formLog = formLog.Replace(" (", "-");
                formLog = formLog.Replace(")", "");
            }
            Log($"Request is for {shinyLog}{specName}{formLog} with Gender: {genderLog} and {nature} Nature.");

            // Handle specific IV requests
            string ReqIVs = "";
            string[] ivTitles = { "HP", "Atk", "Def", "Spe", "SpA", "SpD" };
            if (genInput.Length is 11 or 12)
            {
                string IVSpread = genInput[^6..];
                int i = 0; int j;
                foreach (char c in IVSpread)
                {
                    if (c is 'W' or 'X' or 'Y' or 'Z')
                        j = 0;
                    else
                        j = Base36ToUShort(c.ToString());
                    if (j < 31)
                        ReqIVs += j + " " + ivTitles[i] + " ";
                    i++;
                }
            }

            var sav = TrainerSettings.GetSavedTrainerData(GameVersion.SV, 9);

            // Generate basic Showdown Set information
            string showdownSet = "";
            showdownSet += specName;
            if (formName is not "")
                showdownSet += formLog;
            showdownSet += genderSet;            
            // showdownSet += "Ability: " + abiName + "\r\n";
            if (shiny)
                showdownSet += "Shiny: Yes\r\n";
            showdownSet += nature + " Nature\r\n";
            if (ReqIVs != "")
                showdownSet += "IVs: " + ReqIVs + "\r\n";
            showdownSet += "Language: " + Enum.GetName(typeof(LanguageID), offered.Language) + "\r\n";
            showdownSet += "OT: " + offered.OT_Name + "\r\n";
            showdownSet += "TID: " + offered.DisplayTID + "\r\n";
            showdownSet += "SID: " + offered.DisplaySID + "\r\n";
            showdownSet += "OTGender: " + offered.OT_Gender + "\r\n";
            string boxVersionCheck = species switch
            {
                998 => ".Version=50\r\n",
                999 => ".Version=51\r\n",
                _ => ".Version=" + offered.Version + "\r\n",
            };
            showdownSet += boxVersionCheck;
            showdownSet += "~=Generation=9\r\n";
            showdownSet += ".Moves=$suggest";
            showdownSet = showdownSet.Replace("`\n", "").Replace("\n`", "").Replace("`", "").Trim();
            var set = new ShowdownSet(showdownSet);
            var template = AutoLegalityWrapper.GetTemplate(set);
            File.WriteAllText("ShowdownSetText.txt", showdownSet);
            if (set.InvalidLines.Count != 0)
            {
                Log($"Unable to parse Showdown Set:\n{string.Join("\n", set.InvalidLines)}");
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
            }

            // Handle set legality checking and preparing to send
            var pkm = sav.GetLegal(template, out var result);
            var la = new LegalityAnalysis(pkm);
            pkm = EntityConverter.ConvertToType(pkm, typeof(PK9), out _) ?? pkm;
            PK9? dumpPKM = pkm as PK9;
            if (dumpPKM is not null)
                DumpPokemon(DumpSetting.DumpFolder, "genToConvert", dumpPKM);
            if (pkm is not PK9 pk || !la.Valid)
            {
                var reason = result == "Timeout" ? $"That {specName} set took too long to generate." : $"I wasn't able to create a {specName} from that set.";
                Log(reason);
                return (offered, swap, swap1, swap2, PokeTradeResult.TrainerRequestBad);
            }
            pk.HyperTrainClear();
            pk.ResetPartyStats();
            pk.MarkValue = 0;
            pk.HT_Name = "Sinthrill";
            pk.HT_Language = 2;
            pk.HT_Gender = 1;
            pk.HT_Friendship = 50;
            pk.RefreshChecksum();
            return (pk, swap, swap1, swap2, PokeTradeResult.Success);
        }

        private async Task<(PK9 toSend, PokeTradeResult check)> HandleClone(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, byte[] oldEC, PartnerDataHolder partner, CancellationToken token)
        {
            if (Hub.Config.Discord.ReturnPKMs)
                poke.SendNotification(this, offered, "Here's what you showed me!");

            var la = new LegalityAnalysis(offered);
            if (!la.Valid)
            {
                Log($"Clone request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {GameInfo.GetStrings(1).Species[offered.Species]}.");
                if (DumpSetting.Dump)
                    DumpPokemon(DumpSetting.DumpFolder, "hacked", offered);

                var report = la.Report();
                Log(report);
                poke.SendNotification(this, "This Pokémon is not legal per PKHeX's legality checks. I am forbidden from cloning this. Exiting trade.");
                poke.SendNotification(this, report);

                return (offered, PokeTradeResult.IllegalTrade);
            }

            var clone = offered.Clone();
            if (Hub.Config.Legality.ResetHOMETracker)
                clone.Tracker = 0;

            PokeTradeResult update;
            string tradeType, swap1, swap2;
            (clone, tradeType, swap1, swap2, update)  = GetCloneSwapInfo(clone, offered);
            if (update != PokeTradeResult.Success)
                return (offered, PokeTradeResult.TrainerRequestBad);

            if (tradeType != "Distro" && tradeType != "Genned")
            {
                poke.SendNotification(this, $"**Cloned your {GameInfo.GetStrings(1).Species[clone.Species]}!**\nNow press B to cancel your offer and trade me a Pokémon you don't want.");
                Log($"Cloned a {GameInfo.GetStrings(1).Species[clone.Species]}. Waiting for user to change their Pokémon...");
            }
            else if (tradeType == "Distro")
            {
                poke.SendNotification(this, $"Injecting the requested Pokémon {clone.Nickname}.");
            }
            else if (tradeType == "Genned")
            {
                poke.SendNotification(this, $"Genned your requested Pokémon {clone.Nickname}.");
            }

            // Separate this out from WaitForPokemonChanged since we compare to old EC from original read.
            var partnerFound = await ReadUntilChanged(TradePartnerOfferedOffset, oldEC, 15_000, 0_200, false, true, token).ConfigureAwait(false);
            if (!partnerFound)
            {
                poke.SendNotification(this, "**HEY CHANGE IT NOW OR I AM LEAVING!!!**");
                // They get one more chance.
                partnerFound = await ReadUntilChanged(TradePartnerOfferedOffset, oldEC, 15_000, 0_200, false, true, token).ConfigureAwait(false);
            }

            var pk2 = await ReadUntilPresent(TradePartnerOfferedOffset, 25_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
            if (!partnerFound || pk2 is null || SearchUtil.HashByDetails(pk2) == SearchUtil.HashByDetails(offered))
            {
                Log("Trade partner did not change their Pokémon.");
                return (offered, PokeTradeResult.TrainerTooSlow);
            }

            if (swap1 == "EV" || tradeType == "EV" || swap2 == "EV")
            {
                bool doubleEV = offered.Nickname.All(c => "0123456789ABCDEFSN".Contains(c)) && offered.Nickname.Length == 12 && pk2.Nickname.All(c => "0123456789ABCDEFSN".Contains(c)) && pk2.Nickname.Length == 12;
                if (doubleEV)
                {
                    (clone, update) = HandleSecondEVSwap(clone, pk2);
                    if (update != PokeTradeResult.Success)
                        return (offered, PokeTradeResult.TrainerRequestBad);
                }
            }

            poke.TradeData = clone;

            await Click(A, 0_800, token).ConfigureAwait(false);
            await SetBoxPokemonAbsolute(BoxStartOffset, clone, token, sav).ConfigureAwait(false);

            if (tradeType == "Ball")
                TradeSettings.AddCompletedBallSwaps();
            if (tradeType == "Tera")
                TradeSettings.AddCompletedTeraSwaps();
            if (tradeType == "Item")
                TradeSettings.AddCompletedItemSwaps();
            if (tradeType == "EV")
                TradeSettings.AddCompletedEVSwaps();
            if (tradeType == "Name")
                TradeSettings.AddCompletedNameRemoves();
            if (tradeType == "Distro")
                TradeSettings.AddCompletedDistroSwaps();
            if (tradeType == "Genned")
                TradeSettings.AddCompletedGennedSwaps();
            if (tradeType == "Double")
            {
                TradeSettings.AddCompletedDoubleSwaps();

                switch (swap1)
                {
                    case "Tera":
                        TradeSettings.AddCompletedTeraSwaps();
                        break;

                    case "Ball":
                        TradeSettings.AddCompletedBallSwaps();
                        break;

                    case "EV":
                        TradeSettings.AddCompletedEVSwaps();
                        break;
                }

                switch (swap2)
                {
                    case "Tera":
                        TradeSettings.AddCompletedTeraSwaps();
                        break;

                    case "Ball":
                        TradeSettings.AddCompletedBallSwaps();
                        break;

                    case "EV":
                        TradeSettings.AddCompletedEVSwaps();
                        break;
                }
            }
            return (clone, PokeTradeResult.Success);
        }

        private async Task<(PK9 toSend, PokeTradeResult check)> HandleRandomLedy(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, PK9 toSend, PartnerDataHolder partner, CancellationToken token)
        {
            // Allow the trade partner to do a Ledy swap.
            var config = Hub.Config.Distribution;
            var trade = Hub.Ledy.GetLedyTrade(offered, partner.TrainerOnlineID, config.LedySpecies);
            if (trade != null)
            {
                if (trade.Type == LedyResponseType.AbuseDetected)
                {
                    var msg = $"Found {partner.TrainerName} has been detected for abusing Ledy trades.";
                    if (AbuseSettings.EchoNintendoOnlineIDLedy)
                        msg += $"\nID: {partner.TrainerOnlineID}";
                    if (!string.IsNullOrWhiteSpace(AbuseSettings.LedyAbuseEchoMention))
                        msg = $"{AbuseSettings.LedyAbuseEchoMention} {msg}";
                    EchoUtil.Echo(msg);

                    return (toSend, PokeTradeResult.SuspiciousActivity);
                }

                toSend = trade.Receive;
                poke.TradeData = toSend;

                poke.SendNotification(this, $"Injecting the requested Pokémon {toSend.Nickname}.");
                await SetBoxPokemonAbsolute(BoxStartOffset, toSend, token, sav).ConfigureAwait(false);
            }
            else if (config.LedyQuitIfNoMatch)
            {
                return (toSend, PokeTradeResult.TrainerRequestBad);
            }

            return (toSend, PokeTradeResult.Success);
        }

        private void WaitAtBarrierIfApplicable(CancellationToken token)
        {
            if (!ShouldWaitAtBarrier)
                return;
            var opt = Hub.Config.Distribution.SynchronizeBots;
            if (opt == BotSyncOption.NoSync)
                return;

            var timeoutAfter = Hub.Config.Distribution.SynchronizeTimeout;
            if (FailedBarrier == 1) // failed last iteration
                timeoutAfter *= 2; // try to re-sync in the event things are too slow.

            var result = Hub.BotSync.Barrier.SignalAndWait(TimeSpan.FromSeconds(timeoutAfter), token);

            if (result)
            {
                FailedBarrier = 0;
                return;
            }

            FailedBarrier++;
            Log($"Barrier sync timed out after {timeoutAfter} seconds. Continuing.");
        }

        /// <summary>
        /// Checks if the barrier needs to get updated to consider this bot.
        /// If it should be considered, it adds it to the barrier if it is not already added.
        /// If it should not be considered, it removes it from the barrier if not already removed.
        /// </summary>
        private void UpdateBarrier(bool shouldWait)
        {
            if (ShouldWaitAtBarrier == shouldWait)
                return; // no change required

            ShouldWaitAtBarrier = shouldWait;
            if (shouldWait)
            {
                Hub.BotSync.Barrier.AddParticipant();
                Log($"Joined the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
            else
            {
                Hub.BotSync.Barrier.RemoveParticipant();
                Log($"Left the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
        }

        private PokeTradeResult CheckPartnerReputation(PokeTradeDetail<PK9> poke, ulong TrainerNID, string TrainerName)
        {
            bool quit = false;
            var user = poke.Trainer;
            bool isDistribution = false;
            string msg = "";
            if (poke.Type == PokeTradeType.Random || poke.Type == PokeTradeType.Clone)
                isDistribution = true;
            var useridmsg = isDistribution ? "" : $" ({user.ID})";
            var list = isDistribution ? PreviousUsersDistribution : PreviousUsers;

            var cooldown = list.TryGetPrevious(TrainerNID);
            if (cooldown != null)
            {
                var delta = DateTime.Now - cooldown.Time;
                Log($"Last saw {TrainerName} {delta.TotalMinutes:F1} minutes ago (OT: {TrainerName}).");

                var cd = AbuseSettings.TradeCooldown;
                if (cd != 0 && TimeSpan.FromMinutes(cd) > delta)
                {
                    poke.Notifier.SendNotification(this, poke, "You have ignored the trade cooldown set by the bot owner. The owner has been notified.");
                    msg = $"Found {TrainerName}{useridmsg} ignoring the {cd} minute trade cooldown. Last encountered {delta.TotalMinutes:F1} minutes ago.";
                    list.TryRegister(TrainerNID, TrainerName);
                    if (AbuseSettings.EchoNintendoOnlineIDCooldown)
                        msg += $"\nID: {TrainerNID}";
                    if (!string.IsNullOrWhiteSpace(AbuseSettings.CooldownAbuseEchoMention))
                        msg = $"{AbuseSettings.CooldownAbuseEchoMention} {msg}";
                    EchoUtil.Echo(msg);
                    quit = true;
                }
            }

            if (!isDistribution)
            {
                var previousEncounter = EncounteredUsers.TryRegister(poke.Trainer.ID, TrainerName, poke.Trainer.ID);
                if (previousEncounter != null && previousEncounter.Name != TrainerName)
                {
                    if (AbuseSettings.TradeAbuseAction != TradeAbuseAction.Ignore)
                    {
                        if (AbuseSettings.TradeAbuseAction == TradeAbuseAction.BlockAndQuit)
                        {
                            AbuseSettings.BannedIDs.AddIfNew(new[] { GetReference(TrainerName, TrainerNID, "in-game block for sending to multiple in-game players") });
                            Log($"Added {TrainerNID} to the BannedIDs list.");
                        }
                        quit = true;
                    }

                    msg = $"Found {user.TrainerName}{useridmsg} sending to multiple in-game players. Previous OT: {previousEncounter.Name}, Current OT: {TrainerName}";
                    if (AbuseSettings.EchoNintendoOnlineIDMultiRecipients)
                        msg += $"\nID: {TrainerNID}";
                    if (!string.IsNullOrWhiteSpace(AbuseSettings.MultiRecipientEchoMention))
                        msg = $"{AbuseSettings.MultiRecipientEchoMention} {msg}";
                    EchoUtil.Echo(msg);
                }
            }

            if (quit)
                return PokeTradeResult.SuspiciousActivity;

            // Try registering the partner in our list of recently seen.
            // Get back the details of their previous interaction.
            var previous = list.TryGetPrevious(TrainerNID);
            if (previous != null && previous.NetworkID != TrainerNID && !isDistribution)
            {
                var delta = DateTime.Now - previous.Time;
                if (delta < TimeSpan.FromMinutes(AbuseSettings.TradeAbuseExpiration) && AbuseSettings.TradeAbuseAction != TradeAbuseAction.Ignore)
                {
                    if (AbuseSettings.TradeAbuseAction == TradeAbuseAction.BlockAndQuit)
                    {
                        AbuseSettings.BannedIDs.AddIfNew(new[] { GetReference(TrainerName, TrainerNID, "in-game block for multiple accounts") });
                        Log($"Added {TrainerNID} to the BannedIDs list.");
                    }
                    quit = true;
                }

                msg = $"Found {user.TrainerName}{useridmsg} using multiple accounts.\nPreviously encountered {previous.Name} ({previous.RemoteID}) {delta.TotalMinutes:F1} minutes ago on OT: {TrainerName}.";
                if (AbuseSettings.EchoNintendoOnlineIDMulti)
                    msg += $"\nID: {TrainerNID}";
                if (!string.IsNullOrWhiteSpace(AbuseSettings.MultiAbuseEchoMention))
                    msg = $"{AbuseSettings.MultiAbuseEchoMention} {msg}";
                EchoUtil.Echo(msg);
            }

            if (quit)
                return PokeTradeResult.SuspiciousActivity;

            var entry = AbuseSettings.BannedIDs.List.Find(z => z.ID == TrainerNID);
            if (entry != null)
            {
                msg = $"{user.TrainerName}{useridmsg} is a banned user, and was encountered in-game using OT: {TrainerName}.";
                if (!string.IsNullOrWhiteSpace(entry.Comment))
                    msg += $"\nUser was banned for: {entry.Comment}";
                if (!string.IsNullOrWhiteSpace(AbuseSettings.BannedIDMatchEchoMention))
                    msg = $"{AbuseSettings.BannedIDMatchEchoMention} {msg}";
                EchoUtil.Echo(msg);
                return PokeTradeResult.SuspiciousActivity;
            }

            return PokeTradeResult.Success;
        }

        private static RemoteControlAccess GetReference(string name, ulong id, string comment) => new()
        {
            ID = id,
            Name = name,
            Comment = $"Added automatically on {DateTime.Now:yyyy.MM.dd-hh:mm:ss} ({comment})",
        };
    }
}
