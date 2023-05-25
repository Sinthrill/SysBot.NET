using PKHeX.Core;
using SysBot.Base;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;

namespace SysBot.Pokemon
{
    public class TradeSettings : IBotStateSettings, ICountSettings
    {
        private const string TradeCode = nameof(TradeCode);
        private const string TradeConfig = nameof(TradeConfig);
        private const string Dumping = nameof(Dumping);
        private const string Counts = nameof(Counts);
        public override string ToString() => "Trade Bot Settings";

        [Category(TradeConfig), Description("Time to wait for a trade partner in seconds.")]
        public int TradeWaitTime { get; set; } = 30;

        [Category(TradeConfig), Description("Max amount of time in seconds pressing A to wait for a trade to process.")]
        public int MaxTradeConfirmTime { get; set; } = 25;

        [Category(TradeCode), Description("Minimum Link Code.")]
        public int MinTradeCode { get; set; } = 8180;

        [Category(TradeCode), Description("Maximum Link Code.")]
        public int MaxTradeCode { get; set; } = 8199;

        [Category(Dumping), Description("Dump Trade: Dumping routine will stop after a maximum number of dumps from a single user.")]
        public int MaxDumpsPerTrade { get; set; } = 20;

        [Category(Dumping), Description("Dump Trade: Dumping routine will stop after spending x seconds in trade.")]
        public int MaxDumpTradeTime { get; set; } = 180;

        [Category(Dumping), Description("Dump Trade: If enabled, Dumping routine will output legality check information to the user.")]
        public bool DumpTradeLegalityCheck { get; set; } = true;

        [Category(TradeConfig), Description("When enabled, the screen will be turned off during normal bot loop operation to save power.")]
        public bool ScreenOff { get; set; }

        /// <summary>
        /// Gets a random trade code based on the range settings.
        /// </summary>
        public int GetRandomTradeCode() => Util.Rand.Next(MinTradeCode, MaxTradeCode + 1);

        private int _completedSurprise;
        private int _completedDistribution;
        private int _completedTrades;
        private int _completedSeedChecks;
        private int _completedClones;
        private int _completedDumps;
        private int _completedTeraSwaps;
        private int _completedBallSwaps;
        private int _completedDoubleSwaps;
        private int _completedItemSwaps;
        private int _completedEVSwaps;
        private int _completedNameRemoves;
        private int _completedDistroSwaps;
        private int _completedGennedSwaps;

        [Category(Counts), Description("Completed Surprise Trades")]
        public int CompletedSurprise
        {
            get => _completedSurprise;
            set => _completedSurprise = value;
        }

        [Category(Counts), Description("Completed Link Trades (Distribution)")]
        public int CompletedDistribution
        {
            get => _completedDistribution;
            set => _completedDistribution = value;
        }

        [Category(Counts), Description("Completed Link Trades (Specific User)")]
        public int CompletedTrades
        {
            get => _completedTrades;
            set => _completedTrades = value;
        }

        [Category(Counts), Description("Completed Seed Check Trades")]
        public int CompletedSeedChecks
        {
            get => _completedSeedChecks;
            set => _completedSeedChecks = value;
        }

        [Category(Counts), Description("Completed Clone Trades (Specific User)")]
        public int CompletedClones
        {
            get => _completedClones;
            set => _completedClones = value;
        }

        [Category(Counts), Description("Completed Dump Trades (Specific User)")]
        public int CompletedDumps
        {
            get => _completedDumps;
            set => _completedDumps = value;
        }

        [Category(Counts), Description("Completed Item Swap Trades")]
        public int CompletedItemSwaps
        {
            get => _completedItemSwaps;
            set => _completedItemSwaps = value;
        }

        [Category(Counts), Description("Completed Name Remove Trades")]
        public int CompletedNameRemoves
        {
            get => _completedNameRemoves;
            set => _completedNameRemoves = value;
        }

        [Category(Counts), Description("Completed Double Swap Trades")]
        public int CompletedDoubleSwaps
        {
            get => _completedDoubleSwaps;
            set => _completedDoubleSwaps = value;
        }

        [Category(Counts), Description("Completed Tera Swap Trades")]
        public int CompletedTeraSwaps
        {
            get => _completedTeraSwaps;
            set => _completedTeraSwaps = value;
        }

        [Category(Counts), Description("Completed Ball Swap Trades")]
        public int CompletedBallSwaps
        {
            get => _completedBallSwaps;
            set => _completedBallSwaps = value;
        }

        [Category(Counts), Description("Completed EV Swap Trades")]
        public int CompletedEVSwaps
        {
            get => _completedEVSwaps;
            set => _completedEVSwaps = value;
        }

        [Category(Counts), Description("Completed Distribution Swap Trades")]
        public int CompletedDistroSwaps
        {
            get => _completedDistroSwaps;
            set =>_completedDistroSwaps = value;
        }

        [Category(Counts), Description("Completed Genned Swap Trades")]
        public int CompletedGennedSwaps
        {
            get => _completedGennedSwaps;
            set => _completedGennedSwaps = value;
        }

        [Category(Counts), Description("When enabled, the counts will be emitted when a status check is requested.")]
        public bool EmitCountsOnStatusCheck { get; set; }

        public void AddCompletedTrade() => Interlocked.Increment(ref _completedTrades);
        public void AddCompletedSeedCheck() => Interlocked.Increment(ref _completedSeedChecks);
        public void AddCompletedSurprise() => Interlocked.Increment(ref _completedSurprise);
        public void AddCompletedDistribution() => Interlocked.Increment(ref _completedDistribution);
        public void AddCompletedDumps() => Interlocked.Increment(ref _completedDumps);
        public void AddCompletedClones() => Interlocked.Increment(ref _completedClones);
        public void AddCompletedTeraSwaps() => Interlocked.Increment(ref _completedTeraSwaps);
        public void AddCompletedBallSwaps() => Interlocked.Increment(ref _completedBallSwaps);
        public void AddCompletedDoubleSwaps() => Interlocked.Increment(ref _completedDoubleSwaps);
        public void AddCompletedItemSwaps() => Interlocked.Increment(ref _completedItemSwaps);
        public void AddCompletedEVSwaps() => Interlocked.Increment(ref _completedEVSwaps);
        public void AddCompletedNameRemoves() => Interlocked.Increment(ref _completedNameRemoves);
        public void AddCompletedDistroSwaps() => Interlocked.Increment(ref _completedDistroSwaps);
        public void AddCompletedGennedSwaps() => Interlocked.Increment(ref _completedGennedSwaps);

        public IEnumerable<string> GetNonZeroCounts()
        {
            if (!EmitCountsOnStatusCheck)
                yield break;
            if (CompletedSeedChecks != 0)
                yield return $"Seed Check Trades: {CompletedSeedChecks}";
            if (CompletedClones != 0)
                yield return $"Clone Trades: {CompletedClones}";
            if (CompletedDumps != 0)
                yield return $"Dump Trades: {CompletedDumps}";
            if (CompletedTrades != 0)
                yield return $"Link Trades: {CompletedTrades}";
            if (CompletedDistribution != 0)
                yield return $"Distribution Trades: {CompletedDistribution}";
            if (CompletedSurprise != 0)
                yield return $"Surprise Trades: {CompletedSurprise}";
            if (CompletedItemSwaps != 0)
                yield return $"Item Swaps: {CompletedItemSwaps}";
            if (CompletedNameRemoves != 0)
                yield return $"Name Removes: {CompletedNameRemoves}";
            if (CompletedDistroSwaps != 0)
                yield return $"Distro Swaps: {CompletedDistroSwaps}";
            if (CompletedGennedSwaps != 0)
                yield return $"Genned Swaps: {CompletedGennedSwaps}";
            if (CompletedDoubleSwaps != 0)
                yield return $"Double Swaps: {CompletedDoubleSwaps}";
            if (CompletedTeraSwaps!= 0)
                yield return $"Tera Swaps: {CompletedTeraSwaps}";
            if (CompletedBallSwaps != 0)
                yield return $"Ball Swaps: {CompletedBallSwaps}";
            if (CompletedEVSwaps != 0)
                yield return $"EV Swaps: {CompletedEVSwaps}";
        }
    }
}
