#!/usr/bin/env node

import assert from 'node:assert/strict';
import {
  analyzeChurnMemory,
  linearSlope,
  longestIncreasingRun
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

console.log(JSON.stringify({
  status: 'PASS',
  contract: 'bounded churn detects retained monotonic growth and accepts cleanup plateaus'
}));

