// Verbatim spans of the original app; make-golden.mjs checks each one against its recorded sha256.

// golden-span src/hooks/usePokerSession.ts 52-82 7f48a79 sha256:fb4e478589b096552af433688522ccffd5e93b1f77db4685ab902fbdc37535b9
export function getWinningVoteFromSelections(selections: PlayerSelection[]): WinningPoints {
  const participating = selections.filter((s) => s.points !== -1);
  if (participating.length === 0) return { kind: 'single', points: 0 };

  const weightByPoints = new Map<number, number>();
  const addWeight = (pt: number, w: number) => {
    weightByPoints.set(pt, (weightByPoints.get(pt) || 0) + w);
  };

  for (const s of participating) {
    if (s.betweenHighPoints != null) {
      const low = Math.min(s.points, s.betweenHighPoints);
      const high = Math.max(s.points, s.betweenHighPoints);
      addWeight(low, 1);
      addWeight(high, 1);
    } else {
      addWeight(s.points, 1);
    }
  }

  let bestPoints = 0;
  let bestWeight = -1;
  for (const [points, weight] of weightByPoints) {
    if (weight > bestWeight || (weight === bestWeight && points > bestPoints)) {
      bestWeight = weight;
      bestPoints = points;
    }
  }

  return { kind: 'single', points: bestPoints };
}
// golden-span-end
