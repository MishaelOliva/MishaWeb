#!/usr/bin/env node

// Covers evaluateAcceptance, the gate set the memory probe runs. It was
// untestable before: the module ended in `process.exitCode = await main()`, so
// importing it launched a probe. Two defects shipped in the owned-groups gate
// because of that.

import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import { OWNED_PROCESS_GROUPS } from './MemoryChurnAnalysis.mjs';

const MEBIBYTE = 1024 * 1024;
const { evaluateAcceptance } = await import('./MemoryAcceptanceProbe.mjs');

const OPTIONS = {
  observeOnly: false,
  maxFinalPrivateMiB: 450,
  maxPeakPrivateMiB: 525,
  maxFinalWorkingSetMiB: 650,
  maxPeakWorkingSetMiB: 725,
  maxFinalProcesses: 12
};

// Groups as classifyProcess emits them on a real run: host 98.8, browser 59.9,
// renderer 24.4, utility 22.2, gpu 325.7, crashpad 3.4, total 534.3 MiB.
const GROUP_MIB = { host: 98.8, browser: 59.9, renderer: 24.4, utility: 22.2, gpu: 325.7, crashpad: 3.4 };
const TOTAL_MIB = 534.3;

function realSample(overrides = {}) {
  const groups = Object.fromEntries(
    Object.entries({ ...GROUP_MIB, ...overrides }).map(([name, mib]) => [
      name,
      {
        privateBytes: mib * MEBIBYTE,
        privateMiB: mib,
        workingSetBytes: mib * 0.05 * MEBIBYTE,
        workingSetMiB: Number((mib * 0.05).toFixed(1)),
        processCount: 1,
        handles: 100,
        threads: 10
      }
    ])
  );
  const total = Object.values(groups).reduce((sum, group) => sum + group.privateBytes, 0);
  return {
    total: {
      privateBytes: total,
      privateMiB: TOTAL_MIB,
      workingSetBytes: total * 0.05,
      workingSetMiB: 50.9,
      processCount: 7,
      handles: 700,
      threads: 90
    },
    groups
  };
}

function peakOf(sample) {
  return {
    total: {
      privateBytes: { value: sample.total.privateBytes, mib: TOTAL_MIB },
      workingSetBytes: { value: sample.total.workingSetBytes, mib: 76.1 },
      processCount: { value: 7 },
      handles: { value: 700 },
      threads: { value: 90 }
    },
    groups: Object.fromEntries(
      Object.entries(sample.groups).map(([name, group]) => [
        name,
        { privateBytes: { value: group.privateBytes, mib: group.privateMiB } }
      ])
    )
  };
}

const checkNamed = (result, name) => result.checks.find(item => item.name === name);

// A real run: every owned group readable, every ceiling inside its limit.
const healthy = evaluateAcceptance(OPTIONS, true, [realSample()]);
assert.ok(checkNamed(healthy, 'owned-groups-readable').passed);
assert.equal(checkNamed(healthy, 'owned-groups-readable').actual, OWNED_PROCESS_GROUPS.join(','));
assert.ok(checkNamed(healthy, 'final-private-mib').passed);
assert.equal(
  Math.round(checkNamed(healthy, 'final-private-mib').actual),
  Math.round(OWNED_PROCESS_GROUPS.reduce((sum, name) => sum + GROUP_MIB[name], 0)),
  'the gated figure must be the sum of the owned groups'
);

// The peak summary wraps each metric, and it must be summed rather than read as
// zero. This gate reported actual 0 against a 525 MiB limit for one commit.
const healthyPeak = checkNamed(healthy, 'peak-private-mib');
assert.ok(healthyPeak.actual > 190 && healthyPeak.actual < 215, `peak reads ${healthyPeak.actual}`);
assert.ok(healthyPeak.passed);

// The case a ratio could not see. Renaming browser removes 59.9 MiB from every
// gated ceiling while still leaving a large owned total, so a share-of-total
// floor passed it. Naming the missing group catches all four renames.
for (const renamed of OWNED_PROCESS_GROUPS) {
  const broken = evaluateAcceptance(OPTIONS, true, [realSample({ [renamed]: undefined })]);
  const readable = checkNamed(broken, 'owned-groups-readable');
  assert.equal(readable.passed, false, `renaming ${renamed} must fail the presence check`);
  assert.ok(
    readable.actual.split(',').includes(renamed),
    `the check must name ${renamed}, reported "${readable.actual}"`
  );
  assert.ok(
    broken.violations.some(item => item.name === 'owned-groups-readable'),
    `renaming ${renamed} must fail the run`
  );
  // The evidence that still exists has to survive: a SKIP with every check
  // discarded is the failure mode this replaced.
  assert.ok(
    broken.checks.some(item => item.name === 'final-working-set-mib'),
    'an unreadable group must not discard the other checks'
  );
  assert.ok(broken.checks.length > 1, 'the other gates must still be reported');
}

// Observe-only runs no ceilings, so the presence check must not claim to gate.
const observing = evaluateAcceptance({ ...OPTIONS, observeOnly: true }, true, [realSample()]);
assert.ok(observing.checks.some(item => item.name === 'steady-state-reached'));
assert.equal(observing.checks.some(item => item.name === 'final-private-mib'), false);

console.log(JSON.stringify({
  status: 'PASS',
  contract: 'acceptance gates sum owned groups, read peak wrappers, and name a missing group'
}));
