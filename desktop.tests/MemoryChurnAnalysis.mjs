const MEBIBYTE = 1024 * 1024;

export const DEFAULT_CHURN_LIMITS = Object.freeze({
  maxRetainedPrivateMiB: 96,
  maxRetainedWorkingSetMiB: 160,
  maxCycleSlopePrivateMiB: 24,
  monotonicNoiseMiB: 4,
  maxMonotonicRun: 4,
  monotonicGrowthFloorMiB: 64,
  plateauWindow: 4,
  plateauToleranceMiB: 48,
  maxFinalProcessDelta: 2,
  maxFinalHandleDelta: 512,
  maxFinalThreadDelta: 32
});

function finiteNumber(value, name) {
  const number = Number(value);
  if (!Number.isFinite(number)) throw new TypeError(`${name} must be finite`);
  return number;
}

function sampleMetric(sample, metric, name) {
  if (!sample || typeof sample !== 'object' || !sample.total || typeof sample.total !== 'object') {
    throw new TypeError(`${name} must contain a total aggregate`);
  }
  return finiteNumber(sample.total[metric], `${name}.total.${metric}`);
}

function normalizeLimits(overrides = {}) {
  const limits = { ...DEFAULT_CHURN_LIMITS, ...overrides };
  for (const [name, value] of Object.entries(limits)) {
    limits[name] = finiteNumber(value, name);
    if (limits[name] < 0) throw new RangeError(`${name} cannot be negative`);
  }
  limits.plateauWindow = Math.max(2, Math.trunc(limits.plateauWindow));
  limits.maxMonotonicRun = Math.max(2, Math.trunc(limits.maxMonotonicRun));
  return limits;
}

/**
 * Process groups MishaWeb is responsible for. The `gpu` group is a Chromium
 * helper that reserves address space it does not touch: its working set stays
 * around 9 MiB while its private commit reaches several hundred, and that
 * reservation is neither allocated nor released by this codebase. Gating
 * retention on it measured Chromium's allocator rather than a leak.
 *
 * The GPU commit is still recorded and reported, just not gated. See
 * `gpuPrivateMiB` in the result.
 */
export const OWNED_PROCESS_GROUPS = Object.freeze(['host', 'browser', 'renderer', 'utility']);

/**
 * Reads a private-bytes metric that may be a raw number or the `{ value,
 * atElapsedMs, mib }` wrapper the peak summary uses. Returns null when neither
 * shape is present.
 *
 * This distinction matters: reading only numbers made the peak gate compute 0
 * for a real sample, so it passed for any total the app could reach.
 */
function privateBytesOf(metric) {
  if (typeof metric === 'number' && Number.isFinite(metric)) return metric;
  if (metric && typeof metric === 'object') {
    const value = metric.value;
    if (typeof value === 'number' && Number.isFinite(value)) return value;
  }
  return null;
}

/**
 * Private bytes across the process groups the product owns, excluding the GPU
 * helper.
 *
 * Returns null rather than 0 when no owned group carries a readable metric. A
 * zero is indistinguishable from an app that really used nothing, so a sample
 * shape or group-vocabulary change has to surface as null and be rejected by the
 * caller rather than silently zeroing a ceiling.
 */
export function ownedPrivateBytes(sample) {
  if (!sample || typeof sample !== 'object' || !sample.groups) return null;
  let total = 0;
  let counted = 0;
  for (const group of OWNED_PROCESS_GROUPS) {
    const bytes = privateBytesOf(sample.groups[group]?.privateBytes);
    if (bytes === null) continue;
    total += bytes;
    counted += 1;
  }
  return counted === 0 ? null : total;
}

export function gpuPrivateBytes(sample) {
  const bytes = privateBytesOf(sample?.groups?.gpu?.privateBytes);
  return bytes === null ? 0 : bytes;
}

/**
 * Owned private bytes for a sample that a gate depends on, rejecting a sample
 * whose owned groups cannot be read. Aborting is the point: a null here means a
 * group name or metric shape changed, and every ceiling downstream would
 * otherwise compare against a figure that silently lost its largest terms.
 */
function requireOwnedPrivate(sample, label) {
  const bytes = ownedPrivateBytes(sample);
  if (bytes === null) {
    throw new RangeError(
      `${label} carries no readable private bytes for ${OWNED_PROCESS_GROUPS.join(', ')}`);
  }
  return bytes;
}

export function linearSlope(values) {
  if (!Array.isArray(values) || values.length < 2) return 0;
  const normalized = values.map((value, index) => finiteNumber(value, `values[${index}]`));
  const meanX = (normalized.length - 1) / 2;
  const meanY = normalized.reduce((sum, value) => sum + value, 0) / normalized.length;
  let numerator = 0;
  let denominator = 0;
  for (let index = 0; index < normalized.length; index += 1) {
    const x = index - meanX;
    numerator += x * (normalized[index] - meanY);
    denominator += x * x;
  }
  return denominator === 0 ? 0 : numerator / denominator;
}

export function longestIncreasingRun(values, noiseFloor = 0) {
  if (!Array.isArray(values) || values.length < 2) return 0;
  const noise = Math.max(0, finiteNumber(noiseFloor, 'noiseFloor'));
  let longest = 0;
  let current = 0;
  for (let index = 1; index < values.length; index += 1) {
    const previous = finiteNumber(values[index - 1], `values[${index - 1}]`);
    const next = finiteNumber(values[index], `values[${index}]`);
    if (next > previous + noise) {
      current += 1;
      longest = Math.max(longest, current);
    } else {
      current = 0;
    }
  }
  return longest;
}

function asMiB(bytes) {
  return Math.round((bytes / MEBIBYTE) * 10) / 10;
}

function check(name, actual, limit, passed) {
  return { name, actual, limit, passed: Boolean(passed) };
}

/**
 * Evaluates a completed baseline -> bounded tab churn -> cooldown trace.
 *
 * The limits are deliberately generous because Chromium keeps reusable heaps
 * and processes. Regressions fail on retained post-cleanup growth, sustained
 * per-cycle slope, a suspicious monotonic run, missing cooldown plateau, or
 * process/handle/thread resources that do not return near baseline.
 */
export function analyzeChurnMemory({
  baselineSample,
  cycleSamples,
  cooldownSamples,
  limits: limitOverrides = {}
}) {
  if (!Array.isArray(cycleSamples) || cycleSamples.length < 3) {
    throw new RangeError('cycleSamples must contain at least three completed cycles');
  }
  if (!Array.isArray(cooldownSamples) || cooldownSamples.length < 2) {
    throw new RangeError('cooldownSamples must contain at least two samples');
  }

  const limits = normalizeLimits(limitOverrides);
  const baselinePrivate = sampleMetric(baselineSample, 'privateBytes', 'baselineSample');
  const baselineWorkingSet = sampleMetric(baselineSample, 'workingSetBytes', 'baselineSample');
  const baselineProcesses = sampleMetric(baselineSample, 'processCount', 'baselineSample');
  const baselineHandles = sampleMetric(baselineSample, 'handles', 'baselineSample');
  const baselineThreads = sampleMetric(baselineSample, 'threads', 'baselineSample');
  const cyclePrivate = cycleSamples.map((sample, index) =>
    sampleMetric(sample, 'privateBytes', `cycleSamples[${index}]`));
  const finalSample = cooldownSamples.at(-1);
  const finalPrivate = sampleMetric(finalSample, 'privateBytes', 'finalCooldownSample');
  const finalWorkingSet = sampleMetric(finalSample, 'workingSetBytes', 'finalCooldownSample');
  const finalProcesses = sampleMetric(finalSample, 'processCount', 'finalCooldownSample');
  const finalHandles = sampleMetric(finalSample, 'handles', 'finalCooldownSample');
  const finalThreads = sampleMetric(finalSample, 'threads', 'finalCooldownSample');

  const plateauSamples = cooldownSamples.slice(-Math.min(limits.plateauWindow, cooldownSamples.length));
  const plateauPrivate = plateauSamples.map((sample, index) =>
    sampleMetric(sample, 'privateBytes', `plateauSamples[${index}]`));
  const plateauRange = Math.max(...plateauPrivate) - Math.min(...plateauPrivate);
  const plateauSlope = linearSlope(plateauPrivate);
  const cycleSlope = linearSlope(cyclePrivate);
  const retainedWorkingSet = finalWorkingSet - baselineWorkingSet;

  // Every private-bytes gate is measured over the process groups this codebase
  // owns. Gating the totals included the GPU helper, whose private commit is a
  // reservation Chromium makes and reclaims on its own schedule: it dominated
  // the figure while its working set stayed near 9 MiB, so a leak and that
  // reservation were indistinguishable. The per-sample totals swing by hundreds
  // of MiB between cycles while the owned series stays flat, so a monotonic-run
  // or plateau gate left on the totals measures the helper's churn rather than
  // this product's. The GPU commit is reported below as an ungated measurement
  // so a real change in it stays visible.
  const baselineOwnedPrivate = requireOwnedPrivate(baselineSample, 'baselineSample');
  const cycleOwnedPrivate = cycleSamples.map((sample, index) =>
    requireOwnedPrivate(sample ?? baselineSample, `cycleSamples[${index}]`));
  const finalOwnedPrivate = requireOwnedPrivate(finalSample, 'finalCooldownSample');
  const cooldownOwnedPrivate = cooldownSamples.map((sample, index) =>
    requireOwnedPrivate(sample ?? finalSample, `cooldownSamples[${index}]`));
  const plateauOwnedPrivate = cooldownOwnedPrivate
    .slice(-Math.min(limits.plateauWindow, cooldownOwnedPrivate.length));
  const retainedOwnedPrivate = finalOwnedPrivate - baselineOwnedPrivate;
  const ownedCycleSlope = linearSlope(cycleOwnedPrivate);
  const monotonicRun = longestIncreasingRun(cycleOwnedPrivate, limits.monotonicNoiseMiB * MEBIBYTE);
  const cycleGrowth = cycleOwnedPrivate.at(-1) - cycleOwnedPrivate[0];
  const plateauOwnedRange = Math.max(...plateauOwnedPrivate) - Math.min(...plateauOwnedPrivate);
  const finalGpuPrivate = gpuPrivateBytes(finalSample);
  const baselineGpuPrivate = gpuPrivateBytes(baselineSample);
  const suspiciousMonotonicGrowth = monotonicRun >= limits.maxMonotonicRun
    && cycleGrowth > limits.monotonicGrowthFloorMiB * MEBIBYTE;

  const checks = [
    check(
      'churn-retained-private-mib',
      asMiB(retainedOwnedPrivate),
      limits.maxRetainedPrivateMiB,
      retainedOwnedPrivate <= limits.maxRetainedPrivateMiB * MEBIBYTE),
    check(
      'churn-retained-working-set-mib',
      asMiB(retainedWorkingSet),
      limits.maxRetainedWorkingSetMiB,
      retainedWorkingSet <= limits.maxRetainedWorkingSetMiB * MEBIBYTE),
    check(
      'churn-private-slope-mib-per-cycle',
      asMiB(ownedCycleSlope),
      limits.maxCycleSlopePrivateMiB,
      ownedCycleSlope <= limits.maxCycleSlopePrivateMiB * MEBIBYTE),
    check(
      'churn-monotonic-growth-run',
      monotonicRun,
      `< ${limits.maxMonotonicRun} when growth exceeds ${limits.monotonicGrowthFloorMiB} MiB`,
      !suspiciousMonotonicGrowth),
    check(
      'churn-cooldown-plateau-range-mib',
      asMiB(plateauOwnedRange),
      limits.plateauToleranceMiB,
      plateauOwnedRange <= limits.plateauToleranceMiB * MEBIBYTE),
    check(
      'churn-final-process-delta',
      finalProcesses - baselineProcesses,
      limits.maxFinalProcessDelta,
      finalProcesses - baselineProcesses <= limits.maxFinalProcessDelta),
    check(
      'churn-final-handle-delta',
      finalHandles - baselineHandles,
      limits.maxFinalHandleDelta,
      finalHandles - baselineHandles <= limits.maxFinalHandleDelta),
    check(
      'churn-final-thread-delta',
      finalThreads - baselineThreads,
      limits.maxFinalThreadDelta,
      finalThreads - baselineThreads <= limits.maxFinalThreadDelta)
  ];

  return {
    limits,
    checks,
    violations: checks.filter(item => !item.passed),
    passed: checks.every(item => item.passed),
    evidence: {
      cycles: cycleSamples.length,
      cooldownSamples: cooldownSamples.length,
      // Gated figures, over the process groups this codebase owns.
      retainedPrivateMiB: asMiB(retainedOwnedPrivate),
      cycleSlopePrivateMiB: asMiB(ownedCycleSlope),
      gatedProcessGroups: [...OWNED_PROCESS_GROUPS],
      // Ungated measurements, reported so a real change stays visible.
      totalRetainedPrivateMiB: asMiB(finalPrivate - baselinePrivate),
      totalCycleSlopePrivateMiB: asMiB(cycleSlope),
      totalCycleGrowthPrivateMiB: asMiB(cyclePrivate.at(-1) - cyclePrivate[0]),
      totalPlateauRangePrivateMiB: asMiB(plateauRange),
      gpuBaselinePrivateMiB: asMiB(baselineGpuPrivate),
      gpuFinalPrivateMiB: asMiB(finalGpuPrivate),
      gpuRetainedPrivateMiB: asMiB(finalGpuPrivate - baselineGpuPrivate),
      retainedWorkingSetMiB: asMiB(retainedWorkingSet),
      cycleGrowthPrivateMiB: asMiB(cycleGrowth),
      longestMonotonicGrowthRun: monotonicRun,
      suspiciousMonotonicGrowth,
      plateauWindow: plateauOwnedPrivate.length,
      plateauRangePrivateMiB: asMiB(plateauOwnedRange),
      plateauSlopePrivateMiB: asMiB(plateauSlope),
      finalProcessDelta: finalProcesses - baselineProcesses,
      finalHandleDelta: finalHandles - baselineHandles,
      finalThreadDelta: finalThreads - baselineThreads
    }
  };
}

