using System;
using System.Collections.Generic;
using System.Threading;
using backs2.Core.Overview;

namespace backs2.Core.Computing;

public static class BatchDeterminismCheck
{
public static int Run(IComputeDispatcher dispatcher, IReadOnlyList<MonteCarloDispatchRequest> requests, int seed)
{
var perItem = new MonteCarloDispatchResult[requests.Count];
for (int i = 0; i < requests.Count; i++)
perItem[i] = dispatcher.DispatchMonteCarloAsync(requests[i], CancellationToken.None).GetAwaiter().GetResult();

var batched = dispatcher.DispatchMonteCarloBatchAsync(requests, CancellationToken.None).GetAwaiter().GetResult();

int mismatches = 0;
if (batched.Length != perItem.Length) return -1;
for (int i = 0; i < perItem.Length; i++)
{
var a = perItem[i];
var b = batched[i];
if (a.Count != b.Count) { mismatches++; continue; }
for (int k = 0; k < a.Count; k++)
{
if (a.TerminalBalances[k] != b.TerminalBalances[k]) { mismatches++; break; }
if (a.Outcomes[k] != b.Outcomes[k]) { mismatches++; break; }
if (a.SlicesElapsed[k] != b.SlicesElapsed[k]) { mismatches++; break; }
if (a.MaxDrawdowns[k] != b.MaxDrawdowns[k]) { mismatches++; break; }
if (a.BestDayRatios[k] != b.BestDayRatios[k]) { mismatches++; break; }
if (a.LongestLosingStreaks[k] != b.LongestLosingStreaks[k]) { mismatches++; break; }
if (a.LongestFlatStretches[k] != b.LongestFlatStretches[k]) { mismatches++; break; }
if (a.RuinCauses[k] != b.RuinCauses[k]) { mismatches++; break; }
}
}
return mismatches;
}
}