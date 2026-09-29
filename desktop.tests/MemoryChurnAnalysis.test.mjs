#!/usr/bin/env node

import assert from 'node:assert/strict';
import {
  analyzeChurnMemory,
  linearSlope,
  longestIncreasingRun,
  ownedPrivateBytes,
  OWNED_PROCESS_GROUPS
} from './MemoryChurnAnalysis.mjs';

const MEBIBYTE = 1024 * 1024;
// gpuMiB is split out so a test can put growth in the Chromium helper, which is
// reported but not gated, and growth in the groups the product owns, which is.
const sample = ({
  privateMiB,
  gpuMiB = 0,
  workingSetMiB = privateMiB + 30,
  processCount = 6,
  handles = 320,
  threads = 72
}) => {
  const owned = (privateMiB - gpuMiB) * MEBIBYTE;
  return {
    total: {
      privateBytes: privateMiB * MEBIBYTE,
      workingSetBytes: workingSetMiB * MEBIBYTE,
      processCount,
      handles,
      threads
    },
    groups: {
      host: { privateBytes: owned * 0.5 },
      browser: { privateBytes: owned * 0.25 },
      renderer: { privateBytes: owned * 0.15 },
      utility: { privateBytes: owned * 0.1 },
      gpu: { privateBytes: gpuMiB * MEBIBYTE }
    }
  };
};

assert.equal(linearSlope([10, 20, 30, 40]), 10);
assert.equal(linearSlope([40, 30, 20, 10]), -10);
assert.equal(longestIncreasingRun([10, 20, 30, 25, 40, 50], 1), 2);

const plateau = analyzeChurnMemory({
  baselineSample: sample({ privateMiB: 220, handles: 300, threads: 70 }),
  cycleSamples: [245, 270, 282, 276, 284, 281].map(privateMiB => sample({
    privateMiB,
    handles: 390,
    threads: 86
  })),
  cooldownSamples: [260, 246, 240, 238, 241].map(privateMiB => sample({
    privateMiB,
    handles: 326,
    threads: 74
  }))
});
assert.equal(plateau.passed, true);
assert.equal(plateau.violations.length, 0);
assert.equal(plateau.evidence.suspiciousMonotonicGrowth, false);
assert.ok(plateau.evidence.retainedPrivateMiB <= 96);

const leak = analyzeChurnMemory({
  baselineSample: sample({ privateMiB: 200, handles: 250, threads: 60 }),
  cycleSamples: [225, 255, 290, 330, 375, 425].map((privateMiB, index) => sample({
    privateMiB,
    handles: 300 + (index * 120),
    threads: 70 + (index * 8)
  })),
  cooldownSamples: [410, 420, 435, 450].map((privateMiB, index) => sample({
    privateMiB,
    processCount: 10,
    handles: 850 + (index * 20),
    threads: 120
  })),
  limits: {
    maxRetainedPrivateMiB: 80,
    maxRetainedWorkingSetMiB: 100,
    maxCycleSlopePrivateMiB: 20,
    monotonicNoiseMiB: 2,
    maxMonotonicRun: 4,
    monotonicGrowthFloorMiB: 50,
    plateauToleranceMiB: 20,
    maxFinalProcessDelta: 1,
    maxFinalHandleDelta: 200,
    maxFinalThreadDelta: 20
  }
});
assert.equal(leak.passed, false);
assert.equal(leak.evidence.suspiciousMonotonicGrowth, true);
assert.ok(leak.violations.some(item => item.name === 'churn-retained-private-mib'));
assert.ok(leak.violations.some(item => item.name === 'churn-private-slope-mib-per-cycle'));
assert.ok(leak.violations.some(item => item.name === 'churn-monotonic-growth-run'));
assert.ok(leak.violations.some(item => item.name === 'churn-cooldown-plateau-range-mib'));
assert.ok(leak.violations.some(item => item.name === 'churn-final-process-delta'));

const noisyCleanup = analyzeChurnMemory({
  baselineSample: sample({ privateMiB: 180 }),
  cycleSamples: [220, 235, 228, 242, 231, 239].map(privateMiB => sample({ privateMiB })),
  cooldownSamples: [230, 205, 194, 198].map(privateMiB => sample({ privateMiB })),
  limits: { plateauToleranceMiB: 40 }
});
assert.equal(noisyCleanup.passed, true);

assert.throws(
  () => analyzeChurnMemory({
    baselineSample: sample({ privateMiB: 100 }),
    cycleSamples: [sample({ privateMiB: 110 }), sample({ privateMiB: 120 })],
    cooldownSamples: [sample({ privateMiB: 105 }), sample({ privateMiB: 104 })]
  }),
  /at least three completed cycles/);
assert.throws(
  () => analyzeChurnMemory({
    baselineSample: sample({ privateMiB: 100 }),
    cycleSamples: [110, 115, 120].map(privateMiB => sample({ privateMiB })),
    cooldownSamples: []
  }),
  /at least two samples/);

// A GPU helper that reserves several hundred MiB is reported, not gated. This
// is the shape the real probe produced: the owned groups are flat, the Chromium
// GPU commit moves, and no threshold is moved to accommodate it.
const gpuReservationOnly = analyzeChurnMemory({
  baselineSample: sample({ privateMiB: 344, gpuMiB: 151 }),
  cycleSamples: [353, 500, 470, 520, 495, 510].map(privateMiB => sample({
    privateMiB,
    gpuMiB: privateMiB - 193
  })),
  cooldownSamples: [520, 505, 502, 500].map(privateMiB => sample({
    privateMiB,
    gpuMiB: privateMiB - 195
  }))
});
assert.equal(gpuReservationOnly.passed, true, JSON.stringify(gpuReservationOnly.violations));
assert.ok(gpuReservationOnly.evidence.gpuRetainedPrivateMiB > 150);
assert.ok(gpuReservationOnly.evidence.retainedPrivateMiB <= 16);
assert.deepEqual(
  gpuReservationOnly.evidence.gatedProcessGroups,
  ['host', 'browser', 'renderer', 'utility']);

// The same growth in a group the product owns is still a failure, so excluding
// the GPU helper has not weakened the gate.
const ownedGroupLeak = analyzeChurnMemory({
  baselineSample: sample({ privateMiB: 344, gpuMiB: 151 }),
  cycleSamples: [400, 430, 460, 490, 520, 550].map(privateMiB => sample({
    privateMiB,
    gpuMiB: 151
  })),
  cooldownSamples: [545, 550, 552, 554].map(privateMiB => sample({
    privateMiB,
    gpuMiB: 151
  }))
});
assert.equal(ownedGroupLeak.passed, false);
assert.ok(ownedGroupLeak.violations.some(item => item.name === 'churn-retained-private-mib'));

// The peak summary wraps every group metric in { value, atElapsedMs, mib }. A
// reader that accepts only raw numbers skips every group, returns 0, and the
// ceiling passes for any real total. This is the shape that shipped.
const peakShapedSample = {
  total: { privateBytes: 690 * MEBIBYTE },
  groups: Object.fromEntries(
    [...OWNED_PROCESS_GROUPS, 'gpu'].map(group => [
      group,
      { privateBytes: { value: 40 * MEBIBYTE, atElapsedMs: 45701, mib: 40 } }
    ])
  )
};
const peakOwned = ownedPrivateBytes(peakShapedSample);
assert.equal(peakOwned, 4 * 40 * MEBIBYTE, 'a peak-shaped sample must be summed, not zeroed');

// An unreadable shape has to be distinguishable from a genuinely small figure,
// otherwise a shape change is indistinguishable from an app that used nothing.
assert.equal(ownedPrivateBytes({ total: { privateBytes: 100 } }), null);
assert.equal(ownedPrivateBytes({ groups: {} }), null);
assert.equal(
  ownedPrivateBytes({
    groups: Object.fromEntries(OWNED_PROCESS_GROUPS.map(group => [group, {}]))
  }),
  null,
  'owned groups present but carrying no private bytes is not a measurement'
);
assert.throws(
  () => analyzeChurnMemory({
    baselineSample: { total: { privateBytes: 200 * MEBIBYTE, workingSetBytes: 0, processCount: 6, handles: 300, threads: 70 } },
    cycleSamples: Array.from({ length: 3 }, () => ({ total: { privateBytes: 200 * MEBIBYTE, workingSetBytes: 0, processCount: 6, handles: 300, threads: 70 } })),
    cooldownSamples: Array.from({ length: 2 }, () => ({ total: { privateBytes: 200 * MEBIBYTE, workingSetBytes: 0, processCount: 6, handles: 300, threads: 70 } }))
  }),
  /no readable private bytes/,
  'a churn trace with no owned groups must abort rather than gate against zero'
);

// A trace whose owned groups are flat while the GPU helper oscillates by hundreds
// of MiB must not read as monotonic growth. Gating the run length on the totals
// made this fire: the totals grew 81 MiB across the cycles and produced a run of
// 4 against a limit of 4, on series whose owned half never moved.
const gpuChurnOnly = analyzeChurnMemory({
  baselineSample: sample({ privateMiB: 344, gpuMiB: 151 }),
  cycleSamples: [420, 337, 601, 402, 659, 480].map(privateMiB => sample({
    privateMiB,
    gpuMiB: privateMiB - 190
  })),
  cooldownSamples: [505, 501, 500, 502].map(privateMiB => sample({
    privateMiB,
    gpuMiB: privateMiB - 195
  }))
});
assert.ok(
  gpuChurnOnly.evidence.longestMonotonicGrowthRun < gpuChurnOnly.limits.maxMonotonicRun,
  `GPU-only churn must not read as a monotonic run of ${gpuChurnOnly.evidence.longestMonotonicGrowthRun}`
);
assert.equal(gpuChurnOnly.evidence.suspiciousMonotonicGrowth, false);
assert.ok(
  gpuChurnOnly.evidence.totalCycleGrowthPrivateMiB > gpuChurnOnly.evidence.cycleGrowthPrivateMiB,
  'the total series must still show the growth the owned series does not'
);

console.log(JSON.stringify({
  status: 'PASS',
  contract: 'bounded churn detects retained monotonic growth and accepts cleanup plateaus'
}));

