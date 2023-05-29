using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SysBot.Pokemon.TradeHub
{
    public enum CloneSwapType
    {
        None,
        EVSpread,
        BallSwap,
        TeraSwap,
        NicknameClear,
        GennedRequest,
        DistroRequest,
        ItemRequest,
        OTSwap,
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class CloneSwapInfo
    {
        public CloneSwapType SwapType { get; set; }
        public string RequestMon { get; set; } = string.Empty;
        public string SwapInfo { get; set; } = string.Empty;
        public override string ToString() => $"Swap Type: {SwapType} // Request Mon: {RequestMon} // Swap Info: {SwapInfo}";
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class CloneSwapInfoList
    {
        public List<CloneSwapInfo> List { get; set; } = new();

        public bool Contains(CloneSwapType swapType) => List.Any(z => z.SwapType == swapType);

        public void Update(CloneSwapInfo swapInfo) 
        {
            var index = List.FindIndex(z => z.SwapType == swapInfo.SwapType);
            if (!Contains(swapInfo.SwapType))
            {
                List.Add(swapInfo);
                return;
            }

            List[index] = swapInfo;
        }

        public IEnumerator<CloneSwapInfo> GetEnumerator() => List.GetEnumerator();

        public IEnumerable<string> Summarize() => List.Select(z => z.ToString());
    }
}
