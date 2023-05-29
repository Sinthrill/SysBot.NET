using PKHeX.Core;
using SysBot.Base;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;

namespace SysBot.Pokemon
{
    public class CloneSettings : ISynchronizationSetting
    {
        private const string Clone = nameof(Clone);
        private const string Synchronize = nameof(Synchronize);
        public override string ToString() => "Clone Trade Settings";

        private int _showdownSetLog;

        // Distribute

        [Category(Clone), Description("When enabled, idle LinkTrade bots will randomly clone PKM files from other trainers.")]
        public bool CloneWhileIdle { get; set; } = false;

        [Category(Clone), Description("Number of trades a user is allowed to perform per encounter for idle modes.")]
        public int TradesPerEncounter { get; set; } = 1;

        [Category(Clone), Description("Clone Trade Link Code.")]
        public int TradeCode { get; set; } = 7196;

        [Category(Clone), Description("Clone Trade Link Code uses the Min and Max range rather than the fixed trade code.")]
        public bool RandomCode { get; set; }

        [Category(Clone), Description("Held item used to trigger Held Item Swaps.")]
        public LegalHeld9 ItemSwapItem { get; set; } = LegalHeld9.Potion;

        [Category(Clone), Description("Held item used to trigger Nickname removal.")]
        public LegalHeld9 NickSwapItem { get; set; } = LegalHeld9.PokéDoll;

        [Category(Clone), Description("Held item used to trigger Distributions.")]
        public LegalHeld9 DistroSwapItem { get; set; } = LegalHeld9.FullHeal;

        [Category(Clone), Description("Held item used to trigger Genning.")]
        public LegalHeld9 GennedSwapItem { get; set; } = LegalHeld9.FreshWater;

        [Category(Clone), Description("Held item used to trigger OT swaps.")]
        public LegalHeld9 OTSwapItem { get; set; } = LegalHeld9.BurnHeal;

        [Category(Clone), Description("Counter for genned set logging.")]
        public int SetLogCount
        {
            get => _showdownSetLog;
            set => _showdownSetLog = value;
        }

        public int AddGennedSetLog() => Interlocked.Increment(ref _showdownSetLog);

        // Synchronize

        [Category(Synchronize), Description("Link Trade: Using multiple distribution bots -- all bots will confirm their trade code at the same time. When Local, the bots will continue when all are at the barrier. When Remote, something else must signal the bots to continue.")]
        public BotSyncOption SynchronizeBots { get; set; } = BotSyncOption.LocalSync;

        [Category(Synchronize), Description("Link Trade: Using multiple distribution bots -- once all bots are ready to confirm trade code, the Hub will wait X milliseconds before releasing all bots.")]
        public int SynchronizeDelayBarrier { get; set; }

        [Category(Synchronize), Description("Link Trade: Using multiple distribution bots -- how long (seconds) a bot will wait for synchronization before continuing anyways.")]
        public double SynchronizeTimeout { get; set; } = 90;

    }
}