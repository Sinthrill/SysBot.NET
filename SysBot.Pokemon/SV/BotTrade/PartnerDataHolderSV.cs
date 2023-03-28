using PKHeX.Core;

namespace SysBot.Pokemon
{
    public class PartnerDataHolderSV
    {
        public readonly ulong TrainerOnlineID;
        public readonly TradePartnerSV partner;
        public readonly string TID7;
        public readonly string SID7;
        public readonly string TrainerName;
        public readonly int Game;
        public readonly int Gender;
        public readonly int Language;

        public PartnerDataHolderSV(ulong trainerNid, TradePartnerSV info)
        {
            TrainerOnlineID = trainerNid;
            partner = info;
            TID7 = info.TID7;
            SID7 = info.SID7;
            TrainerName = info.TrainerName;
            Game = info.Game;
            Gender = info.Gender;
            Language = info.Language;
        }
    }
}

