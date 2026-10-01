//+------------------------------------------------------------------+
//|                                   VerifyTickRangeExact.mq5       |
//|  Counts ticks between two exact unix-ms boundaries.              |
//|  Uses CopyTicksRange — same underlying data as CopyTicks.        |
//+------------------------------------------------------------------+
#property copyright "backs2"
#property version   "1.00"
#property script_show_inputs

// Defaults = the exact boundaries from the 2026-10-01 19:11 fetch log:
//   fromMsc = 1790788308907
//   last next_from_msc = 1790881969140
// CopyTicksRange's `to_msc` is exclusive, so we add 1 to include the
// final tick the fetch ended on.
input long InpFromMsc = 1790790532338;
input long InpToMsc   = 1790884177142;

void OnStart()
{
   MqlTick ticks[];
   int count = CopyTicksRange(_Symbol, ticks, COPY_TICKS_ALL,
                              (ulong)InpFromMsc, (ulong)InpToMsc);
   int err = GetLastError();

   if(count <= 0)
   {
      PrintFormat("CopyTicksRange returned %d, error %d", count, err);
      return;
   }

   long firstMsc = (long)ticks[0].time_msc;
   long lastMsc  = (long)ticks[count - 1].time_msc;

   PrintFormat("Count: %d", count);
   PrintFormat("First time_msc: %I64d  (requested from %I64d, delta %I64d ms)",
               firstMsc, InpFromMsc, firstMsc - InpFromMsc);
   PrintFormat("Last  time_msc: %I64d  (requested to   %I64d, delta %I64d ms)",
               lastMsc, InpToMsc, InpToMsc - lastMsc);
}