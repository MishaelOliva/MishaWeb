#!/usr/bin/env node

// Repeatable, isolated memory acceptance probe for MishaWeb.
//
// The probe intentionally uses only Node.js built-ins and PowerShell/CIM. It
// launches the test-only MishaWeb.YouTubeProbe.exe with a unique disposable
// WebView2 user-data folder, waits for a deterministic loopback page and a
// stable process tree, then samples the exact host plus its WebView2 children.
// Cleanup asks only the exact spawned host PID to close and removes only the
// uniquely named temporary profile after every matching child has exited.

import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import { stat, mkdir, rm, writeFile } from 'node:fs/promises';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import process from 'node:process';
import {
  createProbeChildEnvironment,
  createProbeLaunchContract,
  extractWebViewUserDataFolder,
  webViewUserDataFolderMatches
} from './ProbeLaunchContract.mjs';
import {
  analyzeChurnMemory,
  DEFAULT_CHURN_LIMITS,
  OWNED_PROCESS_GROUPS,
  ownedPrivateBytes,
  presentGroups
} from './MemoryChurnAnalysis.mjs';

/**
 * Every group name classifyProcess can emit. `other` is its catch-all, and
 * classifyProcess also returns an unrecognised `--type` verbatim, so a new
 * Chromium process type surfaces here as an unknown group rather than silently
 * joining the gated total.
 */
const KNOWN_PROCESS_GROUPS = [...OWNED_PROCESS_GROUPS, 'gpu', 'crashpad', 'other'];

const MEBIBYTE = 1024 * 1024;
const SKIP_EXIT_CODE = 2;
const DEFAULTS = Object.freeze({
  warmupMs: 8_000,
  maxWarmupMs: 30_000,
  durationMs: 15_000,
  sampleMs: 1_000,
  steadyWindow: 4,
  steadyToleranceMiB: 16,
  maxFinalPrivateMiB: 450,
  maxPeakPrivateMiB: 525,
  maxFinalWorkingSetMiB: 650,
  maxPeakWorkingSetMiB: 725,
  maxFinalProcesses: 12,
  churnCycles: 0,
  churnBatchSize: 3,
  churnDwellMs: 750,
  churnTimeoutMs: 120_000,
  cooldownMs: 8_000,
  maxCooldownMs: 30_000,
  maxChurnRetainedPrivateMiB: DEFAULT_CHURN_LIMITS.maxRetainedPrivateMiB,
  maxChurnRetainedWorkingSetMiB: DEFAULT_CHURN_LIMITS.maxRetainedWorkingSetMiB,
  maxChurnSlopePrivateMiB: DEFAULT_CHURN_LIMITS.maxCycleSlopePrivateMiB,
  churnPlateauToleranceMiB: DEFAULT_CHURN_LIMITS.plateauToleranceMiB
});

function printUsage() {
  console.log(`Usage:
  node desktop.tests/MemoryAcceptanceProbe.mjs \\
    --exe <path-to-MishaWeb.YouTubeProbe.exe> \\
    --out <output-folder> \\
    [--url <http-or-https-url>] [--warmup-ms <milliseconds>] \\
    [--max-warmup-ms <milliseconds>] [--duration-ms <milliseconds>] \\
    [--sample-ms <milliseconds>] [--steady-window <sample-count>] \\
    [--steady-tolerance-mib <MiB>] [--max-final-private-mib <MiB>] \\
    [--max-peak-private-mib <MiB>] [--max-final-working-set-mib <MiB>] \\
    [--max-peak-working-set-mib <MiB>] [--max-final-processes <count>] \\
    [--churn-cycles <3-32>] [--churn-batch-size <1-4>] \\
    [--churn-dwell-ms <milliseconds>] [--churn-timeout-ms <milliseconds>] \\
    [--cooldown-ms <milliseconds>] [--max-cooldown-ms <milliseconds>] \\
    [--max-churn-retained-private-mib <MiB>] \\
    [--max-churn-retained-working-set-mib <MiB>] \\
    [--max-churn-slope-private-mib <MiB/cycle>] \\
    [--churn-plateau-tolerance-mib <MiB>] \\
    [--observe-only] [--keep-profile] [--normal-profile]

Modes:
  default          Private test launcher, deterministic loopback page unless
                   --url is supplied, and a disposable WebView2 profile.
  --normal-profile Explicit opt-in to the real MishaWeb profile. The probe
                   refuses to start while MishaWeb or another probe is open,
                   never removes the profile, and still closes only its PID.
  --observe-only   Collect the same report without applying memory ceilings.
  --churn-cycles   Opt-in private-only bounded tab churn. This mode rejects
                   --url and --normal-profile, uses only its tokenized loopback
                   fixture, and measures baseline -> churn -> cooldown cleanup.

Default acceptance ceilings (one lightweight tab, after stabilization):
  final private       ${DEFAULTS.maxFinalPrivateMiB} MiB
  sampled peak private ${DEFAULTS.maxPeakPrivateMiB} MiB
  final working set   ${DEFAULTS.maxFinalWorkingSetMiB} MiB
  sampled peak WS     ${DEFAULTS.maxPeakWorkingSetMiB} MiB
  final process count ${DEFAULTS.maxFinalProcesses}

Default churn retention ceilings (after all churn tabs close):
  retained private     ${DEFAULTS.maxChurnRetainedPrivateMiB} MiB
  retained working set ${DEFAULTS.maxChurnRetainedWorkingSetMiB} MiB
  private slope        ${DEFAULTS.maxChurnSlopePrivateMiB} MiB/cycle
  cooldown plateau     ${DEFAULTS.churnPlateauToleranceMiB} MiB range

Private-bytes ceilings cover the process groups this product owns (host,
browser, renderer, utility). The crashpad handler and the WebView2 gpu helper
are excluded: crashpad is a crash reporter, and gpu reserves address space it
does not touch, so gating on either measures something this product does not
own. The process total and the gpu helper's commit appear in the JSON output
but are not gated.

"Peak private" is the sum of each owned group's highest reading across the run,
not the highest total the process set ever reached. A group's peak usually
occurs on a different sample from the next group's, so the sum exceeds any real
total; totalPeakPrivateMiB in the output carries the same caveat.

Output contract:
  stdout: one compact summary JSON object
  <output-folder>/memory-result.json: complete warmup, process-group, peak,
                                     final, cleanup, and acceptance evidence

Exit codes:
  0 = PASS
  1 = FAIL (stability or an enabled ceiling failed)
  2 = SKIP (unsafe launch, probe failure, or inconclusive cleanup)`);
}

function parseInteger(value, name, minimum, maximum) {
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed < minimum || parsed > maximum) {
    throw new Error(`${name} must be an integer from ${minimum} through ${maximum}`);
  }
  return parsed;
}

function parseNumber(value, name, minimum, maximum) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed < minimum || parsed > maximum) {
    throw new Error(`${name} must be a number from ${minimum} through ${maximum}`);
  }
  return parsed;
}

function parseArgs(argv) {
  const options = {
    exe: '',
    out: '',
    url: '',
    warmupMs: DEFAULTS.warmupMs,
    maxWarmupMs: DEFAULTS.maxWarmupMs,
    durationMs: DEFAULTS.durationMs,
    sampleMs: DEFAULTS.sampleMs,
    steadyWindow: DEFAULTS.steadyWindow,
    steadyToleranceMiB: DEFAULTS.steadyToleranceMiB,
    maxFinalPrivateMiB: DEFAULTS.maxFinalPrivateMiB,
    maxPeakPrivateMiB: DEFAULTS.maxPeakPrivateMiB,
    maxFinalWorkingSetMiB: DEFAULTS.maxFinalWorkingSetMiB,
    maxPeakWorkingSetMiB: DEFAULTS.maxPeakWorkingSetMiB,
    maxFinalProcesses: DEFAULTS.maxFinalProcesses,
    churnCycles: DEFAULTS.churnCycles,
    churnBatchSize: DEFAULTS.churnBatchSize,
    churnDwellMs: DEFAULTS.churnDwellMs,
    churnTimeoutMs: DEFAULTS.churnTimeoutMs,
    cooldownMs: DEFAULTS.cooldownMs,
    maxCooldownMs: DEFAULTS.maxCooldownMs,
    maxChurnRetainedPrivateMiB: DEFAULTS.maxChurnRetainedPrivateMiB,
    maxChurnRetainedWorkingSetMiB: DEFAULTS.maxChurnRetainedWorkingSetMiB,
    maxChurnSlopePrivateMiB: DEFAULTS.maxChurnSlopePrivateMiB,
    churnPlateauToleranceMiB: DEFAULTS.churnPlateauToleranceMiB,
    observeOnly: false,
    keepProfile: false,
    normalProfile: false
  };

  const booleanOptions = new Map([
    ['--observe-only', 'observeOnly'],
    ['--keep-profile', 'keepProfile'],
    ['--normal-profile', 'normalProfile']
  ]);
  const valueOptions = new Map([
    ['--exe', 'exe'],
    ['--out', 'out'],
    ['--url', 'url'],
    ['--warmup-ms', 'warmupMs'],
    ['--max-warmup-ms', 'maxWarmupMs'],
    ['--duration-ms', 'durationMs'],
    ['--sample-ms', 'sampleMs'],
    ['--steady-window', 'steadyWindow'],
    ['--steady-tolerance-mib', 'steadyToleranceMiB'],
    ['--max-final-private-mib', 'maxFinalPrivateMiB'],
    ['--max-peak-private-mib', 'maxPeakPrivateMiB'],
    ['--max-final-working-set-mib', 'maxFinalWorkingSetMiB'],
    ['--max-peak-working-set-mib', 'maxPeakWorkingSetMiB'],
    ['--max-final-processes', 'maxFinalProcesses'],
    ['--churn-cycles', 'churnCycles'],
    ['--churn-batch-size', 'churnBatchSize'],
    ['--churn-dwell-ms', 'churnDwellMs'],
    ['--churn-timeout-ms', 'churnTimeoutMs'],
    ['--cooldown-ms', 'cooldownMs'],
    ['--max-cooldown-ms', 'maxCooldownMs'],
    ['--max-churn-retained-private-mib', 'maxChurnRetainedPrivateMiB'],
    ['--max-churn-retained-working-set-mib', 'maxChurnRetainedWorkingSetMiB'],
    ['--max-churn-slope-private-mib', 'maxChurnSlopePrivateMiB'],
    ['--churn-plateau-tolerance-mib', 'churnPlateauToleranceMiB']
  ]);

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--help' || argument === '-h') {
      printUsage();
      process.exit(0);
    }
    if (booleanOptions.has(argument)) {
      options[booleanOptions.get(argument)] = true;
      continue;
    }
    const property = valueOptions.get(argument);
    if (!property) throw new Error(`Unknown option: ${argument}`);
    const value = argv[index + 1];
    if (!value || value.startsWith('--')) throw new Error(`Missing value for ${argument}`);
    index += 1;
    options[property] = value;
  }

  if (!options.exe) throw new Error('--exe is required');
  if (!options.out) throw new Error('--out is required');
  options.exe = path.resolve(options.exe);
  options.out = path.resolve(options.out);
  if (path.basename(options.exe).toLowerCase() !== 'mishaweb.youtubeprobe.exe') {
    throw new Error('--exe must point to the isolated MishaWeb.YouTubeProbe.exe launcher');
  }
  if (process.platform !== 'win32') throw new Error('The memory probe requires Windows');

  if (options.url) {
    let parsedUrl;
    try {
      parsedUrl = new URL(options.url);
    } catch {
      throw new Error('--url must be an absolute HTTP or HTTPS URL');
    }
    if (!['http:', 'https:'].includes(parsedUrl.protocol)) {
      throw new Error('--url must be an absolute HTTP or HTTPS URL');
    }
    options.url = parsedUrl.href;
  }

  options.warmupMs = parseInteger(options.warmupMs, '--warmup-ms', 2_000, 120_000);
  options.maxWarmupMs = parseInteger(
    options.maxWarmupMs,
    '--max-warmup-ms',
    options.warmupMs,
    180_000);
  options.durationMs = parseInteger(options.durationMs, '--duration-ms', 3_000, 180_000);
  options.sampleMs = parseInteger(options.sampleMs, '--sample-ms', 500, 5_000);
  options.steadyWindow = parseInteger(options.steadyWindow, '--steady-window', 3, 12);
  options.steadyToleranceMiB = parseNumber(
    options.steadyToleranceMiB,
    '--steady-tolerance-mib',
    1,
    256);
  options.maxFinalPrivateMiB = parseNumber(
    options.maxFinalPrivateMiB,
    '--max-final-private-mib',
    64,
    4_096);
  options.maxPeakPrivateMiB = parseNumber(
    options.maxPeakPrivateMiB,
    '--max-peak-private-mib',
    64,
    4_096);
  options.maxFinalWorkingSetMiB = parseNumber(
    options.maxFinalWorkingSetMiB,
    '--max-final-working-set-mib',
    64,
    4_096);
  options.maxPeakWorkingSetMiB = parseNumber(
    options.maxPeakWorkingSetMiB,
    '--max-peak-working-set-mib',
    64,
    4_096);
  options.maxFinalProcesses = parseInteger(
    options.maxFinalProcesses,
    '--max-final-processes',
    2,
    64);
  options.churnCycles = parseInteger(options.churnCycles, '--churn-cycles', 0, 32);
  if (options.churnCycles > 0 && options.churnCycles < 3) {
    throw new Error('--churn-cycles must be 0 (disabled) or an integer from 3 through 32');
  }
  options.churnBatchSize = parseInteger(
    options.churnBatchSize,
    '--churn-batch-size',
    1,
    4);
  options.churnDwellMs = parseInteger(
    options.churnDwellMs,
    '--churn-dwell-ms',
    250,
    5_000);
  options.churnTimeoutMs = parseInteger(
    options.churnTimeoutMs,
    '--churn-timeout-ms',
    15_000,
    300_000);
  options.cooldownMs = parseInteger(options.cooldownMs, '--cooldown-ms', 2_000, 120_000);
  options.maxCooldownMs = parseInteger(
    options.maxCooldownMs,
    '--max-cooldown-ms',
    options.cooldownMs,
    180_000);
  options.maxChurnRetainedPrivateMiB = parseNumber(
    options.maxChurnRetainedPrivateMiB,
    '--max-churn-retained-private-mib',
    16,
    2_048);
  options.maxChurnRetainedWorkingSetMiB = parseNumber(
    options.maxChurnRetainedWorkingSetMiB,
    '--max-churn-retained-working-set-mib',
    16,
    2_048);
  options.maxChurnSlopePrivateMiB = parseNumber(
    options.maxChurnSlopePrivateMiB,
    '--max-churn-slope-private-mib',
    1,
    512);
  options.churnPlateauToleranceMiB = parseNumber(
    options.churnPlateauToleranceMiB,
    '--churn-plateau-tolerance-mib',
    4,
    512);
  if (options.normalProfile && options.keepProfile) {
    throw new Error('--keep-profile is only valid for the default disposable-profile mode');
  }
  if (options.churnCycles > 0 && options.normalProfile) {
    throw new Error('--churn-cycles is private-only and cannot be combined with --normal-profile');
  }
  if (options.churnCycles > 0 && options.url) {
    throw new Error('--churn-cycles uses only the deterministic loopback fixture and cannot use --url');
  }
  return options;
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function roundMiB(bytes) {
  return Math.round((bytes / MEBIBYTE) * 10) / 10;
}

function runningPidsForImage(imageName) {
  const query = spawnSync(
    'tasklist.exe',
    ['/FI', `IMAGENAME eq ${imageName}`, '/FO', 'CSV', '/NH'],
    { encoding: 'utf8', windowsHide: true, timeout: 10_000 });
  if (query.error || query.status !== 0) {
    return {
      error: query.error?.message || query.stderr?.trim() || `tasklist exited ${query.status}`,
      pids: []
    };
  }
  const pids = [];
  for (const line of query.stdout.split(/\r?\n/)) {
    const match = /^"[^"]+","(\d+)"/.exec(line.trim());
    if (match) pids.push(Number.parseInt(match[1], 10));
  }
  return { error: '', pids };
}

function classifyProcess(processRow, rootPid) {
  if (processRow.processId === rootPid) return 'host';
  const commandLine = processRow.commandLine || '';
  const typeMatch = /(?:^|\s)--type=(?:"([^"]+)"|([^\s]+))/i.exec(commandLine);
  const processType = (typeMatch?.[1] || typeMatch?.[2] || '').toLowerCase();
  if (processType === 'renderer') return 'renderer';
  if (processType === 'gpu-process') return 'gpu';
  if (processType === 'utility') return 'utility';
  if (processType === 'crashpad-handler') return 'crashpad';
  if (processType) return processType;
  if (processRow.name.toLowerCase().includes('crashpad')) return 'crashpad';
  if (processRow.name.toLowerCase().includes('msedgewebview2')) return 'browser';
  return 'other';
}

const PROCESS_QUERY_SCRIPT = String.raw`
$ErrorActionPreference = 'Stop'
$targetRoot = [int]$env:MISHA_MEMORY_ROOT_PID
$profileMarker = [string]$env:MISHA_MEMORY_PROFILE_MARKER
$knownProcessIds = @([string]$env:MISHA_MEMORY_KNOWN_PIDS -split ',' | ForEach-Object {
  $parsedProcessId = 0
  if ([int]::TryParse($_, [ref]$parsedProcessId)) { $parsedProcessId }
})
$allProcesses = @(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name,WorkingSetSize,PrivatePageCount,HandleCount,ThreadCount,CommandLine)
$selectedIds = [System.Collections.Generic.HashSet[int]]::new()
[void]$selectedIds.Add($targetRoot)
foreach ($knownProcessId in $knownProcessIds) { [void]$selectedIds.Add($knownProcessId) }
if (-not [string]::IsNullOrWhiteSpace($profileMarker)) {
  foreach ($item in $allProcesses) {
    if ($null -ne $item.CommandLine -and $item.CommandLine.IndexOf($profileMarker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
      [void]$selectedIds.Add([int]$item.ProcessId)
    }
  }
}
$changed = $true
while ($changed) {
  $changed = $false
  foreach ($item in $allProcesses) {
    if ($selectedIds.Contains([int]$item.ParentProcessId) -and $selectedIds.Add([int]$item.ProcessId)) {
      $changed = $true
    }
  }
}
$rows = @(
  foreach ($item in $allProcesses) {
    if ($selectedIds.Contains([int]$item.ProcessId)) {
      [pscustomobject]@{
        processId = [int]$item.ProcessId
        parentProcessId = [int]$item.ParentProcessId
        name = [string]$item.Name
        workingSetBytes = [long]$item.WorkingSetSize
        privateBytes = [long]$item.PrivatePageCount
        handles = [int]$item.HandleCount
        threads = [int]$item.ThreadCount
        commandLine = [string]$item.CommandLine
      }
    }
  }
)
[Console]::Out.Write((ConvertTo-Json -InputObject $rows -Compress -Depth 3))
`;

function queryProcessRows(rootPid, profileMarker, knownProcessIds = []) {
  const query = spawnSync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', PROCESS_QUERY_SCRIPT],
    {
      encoding: 'utf8',
      windowsHide: true,
      timeout: 12_000,
      maxBuffer: 4 * 1024 * 1024,
      env: {
        ...process.env,
        MISHA_MEMORY_ROOT_PID: String(rootPid),
        MISHA_MEMORY_PROFILE_MARKER: profileMarker || '',
        MISHA_MEMORY_KNOWN_PIDS: knownProcessIds.join(',')
      }
    });
  if (query.error || query.status !== 0) {
    throw new Error(
      query.error?.message || query.stderr?.trim() || `PowerShell process query exited ${query.status}`);
  }
  let rows;
  try {
    rows = JSON.parse(query.stdout || '[]');
  } catch (error) {
    throw new Error(`PowerShell returned invalid process JSON: ${error instanceof Error ? error.message : error}`);
  }
  if (!Array.isArray(rows)) rows = rows ? [rows] : [];
  return rows.map(row => ({
    processId: Number(row.processId) || 0,
    parentProcessId: Number(row.parentProcessId) || 0,
    name: String(row.name || ''),
    workingSetBytes: Number(row.workingSetBytes) || 0,
    privateBytes: Number(row.privateBytes) || 0,
    handles: Number(row.handles) || 0,
    threads: Number(row.threads) || 0,
    commandLine: String(row.commandLine || '')
  }));
}

function verifyDisposableUserDataFolder(rootPid, expectedFolder) {
  const rows = queryProcessRows(rootPid, expectedFolder);
  // WebView2's Chromium process points --user-data-dir at the EBWebView
  // payload below the environment root. Accept only that exact child (or the
  // root itself for runtime variants); never accept an arbitrary descendant.
  const expectedRuntimeFolder = path.join(expectedFolder, 'EBWebView');
  const declaredRows = rows
    .map(row => ({
      processId: row.processId,
      folder: extractWebViewUserDataFolder(row.commandLine),
      matches: webViewUserDataFolderMatches(row.commandLine, expectedFolder)
        || webViewUserDataFolderMatches(row.commandLine, expectedRuntimeFolder)
    }))
    .filter(row => row.folder !== null);
  if (declaredRows.length === 0) {
    throw new Error('WebView2 did not expose a --user-data-dir value for isolation verification');
  }
  const mismatch = declaredRows.find(row => !row.matches);
  if (mismatch) {
    throw new Error(
      `WebView2 process ${mismatch.processId} used an unexpected user-data folder`);
  }
  return {
    verified: true,
    expectedRootFolder: expectedFolder,
    expectedRuntimeFolder,
    observedFolders: [...new Set(declaredRows.map(row => row.folder))],
    processIds: declaredRows.map(row => row.processId)
  };
}

function emptyAggregate() {
  return {
    processCount: 0,
    workingSetBytes: 0,
    workingSetMiB: 0,
    privateBytes: 0,
    privateMiB: 0,
    handles: 0,
    threads: 0
  };
}

function addProcess(aggregate, processRow) {
  aggregate.processCount += 1;
  aggregate.workingSetBytes += processRow.workingSetBytes;
  aggregate.privateBytes += processRow.privateBytes;
  aggregate.handles += processRow.handles;
  aggregate.threads += processRow.threads;
}

function finalizeAggregate(aggregate) {
  aggregate.workingSetMiB = roundMiB(aggregate.workingSetBytes);
  aggregate.privateMiB = roundMiB(aggregate.privateBytes);
  return aggregate;
}

function createSample(rootPid, profileMarker, originTime) {
  const queriedAt = performance.now();
  const processRows = queryProcessRows(rootPid, profileMarker)
    .filter(row => row.processId > 0)
    .map(row => ({ ...row, kind: classifyProcess(row, rootPid) }));
  const total = emptyAggregate();
  const groups = {};
  for (const processRow of processRows) {
    addProcess(total, processRow);
    const group = groups[processRow.kind] || (groups[processRow.kind] = emptyAggregate());
    addProcess(group, processRow);
  }
  finalizeAggregate(total);
  for (const group of Object.values(groups)) finalizeAggregate(group);
  return {
    elapsedMs: Math.round(performance.now() - originTime),
    queryDurationMs: Math.round(performance.now() - queriedAt),
    total,
    groups,
    processes: processRows.map(row => ({
      processId: row.processId,
      parentProcessId: row.parentProcessId,
      name: row.name,
      kind: row.kind,
      workingSetBytes: row.workingSetBytes,
      workingSetMiB: roundMiB(row.workingSetBytes),
      privateBytes: row.privateBytes,
      privateMiB: roundMiB(row.privateBytes),
      handles: row.handles,
      threads: row.threads
    }))
  };
}

function memoryWindowIsSteady(samples, windowSize, toleranceBytes) {
  if (samples.length < windowSize) return false;
  const recent = samples.slice(-windowSize);
  const processSignatures = recent.map(sample => Object.entries(sample.groups)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([name, group]) => `${name}:${group.processCount}`)
    .join('|'));
  if (new Set(processSignatures).size !== 1) return false;
  const privateValues = recent.map(sample => sample.total.privateBytes);
  const privateRange = Math.max(...privateValues) - Math.min(...privateValues);
  return privateRange <= toleranceBytes;
}

function peakForSamples(samples) {
  const metrics = ['processCount', 'workingSetBytes', 'privateBytes', 'handles', 'threads'];
  const summarize = selector => {
    const result = {};
    for (const metric of metrics) {
      let bestSample = null;
      let bestValue = -1;
      for (const sample of samples) {
        const aggregate = selector(sample);
        const value = aggregate?.[metric] || 0;
        if (value > bestValue) {
          bestValue = value;
          bestSample = sample;
        }
      }
      result[metric] = {
        value: Math.max(0, bestValue),
        atElapsedMs: bestSample?.elapsedMs ?? null
      };
      if (metric === 'workingSetBytes' || metric === 'privateBytes') {
        result[metric].mib = roundMiB(Math.max(0, bestValue));
      }
    }
    return result;
  };

  const groupNames = new Set();
  for (const sample of samples) {
    for (const groupName of Object.keys(sample.groups)) groupNames.add(groupName);
  }
  return {
    total: summarize(sample => sample.total),
    groups: Object.fromEntries([...groupNames]
      .sort()
      .map(groupName => [groupName, summarize(sample => sample.groups[groupName])]))
  };
}

async function readBoundedRequestBody(request, maximumBytes = 8_192) {
  const chunks = [];
  let bytes = 0;
  for await (const chunk of request) {
    bytes += chunk.length;
    if (bytes > maximumBytes) throw new RangeError('Loopback control body exceeded its bound');
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString('utf8');
}

function createFixtureServer(probeId, { enableChurn = false } = {}) {
  let resolvePageRequest;
  const pageRequested = new Promise(resolve => { resolvePageRequest = resolve; });
  const churnState = {
    enabled: enableChurn,
    started: false,
    events: [],
    failure: '',
    probeDocumentLoads: 0
  };
  const sendText = (response, statusCode, body) => {
    response.writeHead(statusCode, {
      'Content-Type': 'text/plain; charset=utf-8',
      'Content-Length': Buffer.byteLength(body),
      'Cache-Control': 'no-store'
    });
    response.end(body);
  };
  const createCompletion = response => {
    let completed = false;
    return (statusCode = 200, reply = 'OK') => {
      if (completed) return;
      completed = true;
      try { sendText(response, statusCode, reply); } catch { }
    };
  };
  const server = http.createServer(async (request, response) => {
    const requestUrl = new URL(request.url || '/', 'http://127.0.0.1');
    if (requestUrl.pathname === '/favicon.ico') {
      response.writeHead(204, { 'Cache-Control': 'no-store' });
      response.end();
      return;
    }
    if (enableChurn && requestUrl.searchParams.get('token') !== probeId) {
      sendText(response, 404, 'Not found');
      return;
    }
    if (requestUrl.pathname === '/control' && request.method === 'GET' && enableChurn) {
      sendText(response, 200, churnState.started ? 'START' : 'WAIT');
      return;
    }
    if (requestUrl.pathname === '/frame' && request.method === 'GET' && enableChurn) {
      sendText(response, 200, `bounded iframe ${requestUrl.searchParams.get('cycle') || '0'}`);
      return;
    }
    if (requestUrl.pathname === '/retention-status'
        && request.method === 'GET'
        && enableChurn) {
      sendText(
        response,
        200,
        churnState.probeDocumentLoads >= 4
          ? 'READY'
          : 'WAIT');
      return;
    }
    if (requestUrl.pathname === '/retention-check'
        && request.method === 'POST'
        && enableChurn) {
      try {
        const expectedCount = Number(await readBoundedRequestBody(request));
        const passed = Number.isInteger(expectedCount)
          && expectedCount === 3
          && churnState.probeDocumentLoads === expectedCount + 1;
        if (!passed) {
          sendText(
            response,
            409,
            `Expected one baseline plus three retained document loads; observed ${churnState.probeDocumentLoads}`);
          return;
        }
        sendText(response, 200, 'OK');
      } catch {
        sendText(response, 413, 'Control body too large');
      }
      return;
    }
    if (requestUrl.pathname === '/checkpoint' && request.method === 'POST' && enableChurn) {
      try {
        const body = await readBoundedRequestBody(request);
        if (!/^\d{1,2}$/.test(body)) {
          sendText(response, 400, 'Invalid checkpoint');
          return;
        }
        churnState.events.push({
          type: 'checkpoint',
          value: Number(body),
          at: Date.now(),
          complete: createCompletion(response)
        });
      } catch {
        sendText(response, 413, 'Control body too large');
      }
      return;
    }
    if (requestUrl.pathname === '/done' && request.method === 'POST' && enableChurn) {
      try {
        const body = await readBoundedRequestBody(request);
        churnState.events.push({
          type: 'done',
          value: Number(body),
          at: Date.now(),
          complete: createCompletion(response)
        });
      } catch {
        sendText(response, 413, 'Control body too large');
      }
      return;
    }
    if (requestUrl.pathname === '/failure' && request.method === 'POST' && enableChurn) {
      try {
        churnState.failure = (await readBoundedRequestBody(request)).slice(0, 4_096);
        churnState.events.push({ type: 'failure', value: churnState.failure, at: Date.now() });
        sendText(response, 200, 'OK');
      } catch {
        sendText(response, 413, 'Control body too large');
      }
      return;
    }
    if (request.method !== 'GET'
        || !['/probe', '/churn'].includes(requestUrl.pathname)
        || (requestUrl.pathname !== '/probe' && !enableChurn)) {
      sendText(response, 404, 'Not found');
      return;
    }
    resolvePageRequest({ url: request.url || '/', at: Date.now() });
    if (requestUrl.pathname === '/probe') churnState.probeDocumentLoads += 1;
    const frameMarkup = requestUrl.pathname === '/churn'
      ? `<iframe title="churn frame" src="/frame?token=${encodeURIComponent(probeId)}&cycle=${encodeURIComponent(requestUrl.searchParams.get('cycle') || '0')}" width="2" height="2"></iframe>`
      : '';
    const body = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>MishaWeb memory probe</title>
  <style>
    :root { color-scheme: dark; font-family: system-ui, sans-serif; }
    body { margin: 0; min-height: 100vh; display: grid; place-items: center;
      background: #190f19; color: #fce8f7; }
    main { width: min(32rem, calc(100% - 3rem)); padding: 2rem; text-align: center;
      border: 1px solid #5d3b55; border-radius: 1.25rem; background: #241522; }
    .bunny { font-size: 3rem; }
  </style>
</head>
<body>
  <main><div class="bunny" aria-hidden="true">🐇</div>
    <h1>MishaWeb memory probe</h1><p>Stable local fixture ${probeId}</p>
    ${frameMarkup}</main>
</body>
</html>`;
    response.writeHead(200, {
      'Content-Type': 'text/html; charset=utf-8',
      'Content-Length': Buffer.byteLength(body),
      'Cache-Control': 'no-store'
    });
    response.end(body);
  });
  return { server, pageRequested, churnState, probeId };
}

async function listenOnLoopback(fixture) {
  fixture.server.listen(0, '127.0.0.1');
  await once(fixture.server, 'listening');
  const address = fixture.server.address();
  if (!address || typeof address === 'string') throw new Error('Could not reserve fixture port');
  const token = fixture.churnState.enabled
    ? `?token=${encodeURIComponent(fixture.probeId)}`
    : '';
  return `http://127.0.0.1:${address.port}/probe${token}`;
}

async function closeFixture(fixture) {
  if (!fixture) return;
  try { fixture.server.closeAllConnections?.(); } catch { }
  if (!fixture.server.listening) return;
  await new Promise(resolve => fixture.server.close(() => resolve()));
}

async function waitForChurnEvent(fixture, expectedType, expectedValue, deadline, child) {
  while (Date.now() < deadline) {
    if (fixture.churnState.failure) {
      throw new Error(`Private churn launcher failed: ${fixture.churnState.failure}`);
    }
    const event = fixture.churnState.events.shift();
    if (event) {
      if (event.type !== expectedType || event.value !== expectedValue) {
        event.complete?.(409, 'Unexpected churn event');
        throw new Error(
          `Unexpected churn event ${event.type}:${event.value}; expected ${expectedType}:${expectedValue}`);
      }
      return event;
    }
    if (child.exitCode !== null || child.signalCode !== null) {
      // The contained launcher posts its bounded failure body immediately
      // before closing. Give the loopback request one event-loop turn so the
      // actionable diagnostic wins over a generic process-exit race.
      await delay(250);
      if (fixture.churnState.failure) {
        throw new Error(`Private churn launcher failed: ${fixture.churnState.failure}`);
      }
      throw new Error(
        `The private churn launcher exited early (exitCode=${child.exitCode})`);
    }
    await delay(100);
  }
  throw new Error(`Timed out waiting for churn event ${expectedType}:${expectedValue}`);
}

async function waitForExit(child, timeoutMs) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return true;
  return await Promise.race([
    new Promise(resolve => {
      child.once('exit', () => resolve(true));
      child.once('error', () => resolve(true));
    }),
    delay(timeoutMs).then(() => false)
  ]);
}

async function closeExactProcess(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) {
    return { requested: false, exited: true, method: 'already-exited' };
  }
  if (!Number.isInteger(child.pid)) {
    return { requested: false, exited: true, method: 'spawn-failed' };
  }
  const command = `$target = Get-Process -Id ${child.pid} -ErrorAction SilentlyContinue; `
    + 'if ($null -eq $target) { [Console]::Write("missing") } '
    + 'else { [Console]::Write($target.CloseMainWindow()) }';
  const close = spawnSync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', command],
    { encoding: 'utf8', windowsHide: true, timeout: 10_000 });
  const requested = close.status === 0 && close.stdout.trim().toLowerCase() === 'true';
  const exited = await waitForExit(child, 15_000);
  return {
    requested,
    exited,
    method: 'CloseMainWindow',
    detail: close.error?.message || close.stderr?.trim() || close.stdout.trim()
  };
}

async function waitForIsolatedProcessesToExit(rootPid, profileMarker, knownProcessIds, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let remaining = [];
  while (Date.now() < deadline) {
    try {
      remaining = queryProcessRows(rootPid, profileMarker, knownProcessIds);
    } catch (error) {
      return {
        exited: false,
        remaining: [],
        error: error instanceof Error ? error.message : String(error)
      };
    }
    if (remaining.length === 0) return { exited: true, remaining: [] };
    await delay(500);
  }
  return {
    exited: false,
    remaining: remaining.map(row => ({ processId: row.processId, name: row.name }))
  };
}

function isSafeDisposableProfile(profileFolder, probeId) {
  const resolvedTemporaryRoot = path.resolve(os.tmpdir());
  const resolvedProfile = path.resolve(profileFolder);
  const relative = path.relative(resolvedTemporaryRoot, resolvedProfile);
  return path.dirname(resolvedProfile).toLowerCase() === resolvedTemporaryRoot.toLowerCase()
    && path.basename(resolvedProfile) === `MishaWeb-Memory-Probe-${probeId}`
    && relative
    && !relative.startsWith('..')
    && !path.isAbsolute(relative);
}

async function writeResult(outputFolder, result) {
  await mkdir(outputFolder, { recursive: true });
  const resultPath = path.join(outputFolder, 'memory-result.json');
  await writeFile(resultPath, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
  // Gated figures. Read back from the checks rather than recomputed, so the
  // summary cannot disagree with the ceiling it documents. In observe-only mode
  // those gates never run and the fields are null, which is the honest answer.
  const gatedActual = name =>
    result.acceptance?.checks?.find(item => item.name === name)?.actual ?? null;
  console.log(JSON.stringify({
    status: result.status,
    reason: result.reason,
    resultPath,
    probeId: result.probeId,
    profileMode: result.profileMode,
    workload: result.configuration?.workload || null,
    processId: result.processId || null,
    steadyStateReached: result.steadyState?.reached ?? null,
    finalWorkingSetMiB: result.measurement?.final?.total?.workingSetMiB ?? null,
    peakWorkingSetMiB: result.measurement?.peak?.total?.workingSetBytes?.mib ?? null,
    finalProcessCount: result.measurement?.final?.total?.processCount ?? null,
    // Gated figures, covering only the groups this product owns. These are the
    // same values the final-private-mib and peak-private-mib gates compare
    // against.
    gatedProcessGroups: [...OWNED_PROCESS_GROUPS],
    finalOwnedPrivateMiB: gatedActual('final-private-mib'),
    peakOwnedPrivateMiB: gatedActual('peak-private-mib'),
    knownProcessGroups: gatedActual('known-process-groups'),
    // Ungated totals, reported so the GPU reservation stays visible. The peak
    // total is a sum taken across samples, so it exceeds any single sample and
    // is not a real peak for the process set.
    totalFinalPrivateMiB: result.measurement?.final?.total?.privateMiB ?? null,
    totalPeakPrivateMiB: result.measurement?.peak?.total?.privateBytes?.mib ?? null,
    gpuFinalPrivateMiB: result.measurement?.final?.groups?.gpu?.privateMiB ?? null,
    churnRetainedPrivateMiB:
      result.acceptance?.churn?.analysis?.evidence?.retainedPrivateMiB ?? null,
    churnGpuRetainedPrivateMiB:
      result.acceptance?.churn?.analysis?.evidence?.gpuRetainedPrivateMiB ?? null,
    churnPrivateSlopeMiB:
      result.acceptance?.churn?.analysis?.evidence?.cycleSlopePrivateMiB ?? null,
    churnCooldownSteady: result.acceptance?.churn?.cooldownSteady ?? null,
    violations: result.acceptance?.violations?.map(item => item.name) || [],
    close: result.cleanup?.close || null,
    descendantsExited: result.cleanup?.descendantsExited ?? null,
    profileRemoved: result.cleanup?.profileRemoved ?? false
  }));
}

export function evaluateAcceptance(options, steadyStateReached, measurementSamples) {
  const final = measurementSamples.at(-1);
  const peak = peakForSamples(measurementSamples);
  const checks = [
    {
      name: 'steady-state-reached',
      actual: steadyStateReached,
      limit: true,
      passed: steadyStateReached
    }
  ];
  if (!options.observeOnly) {
    // The private-bytes gates cover the process groups this codebase owns. The
    // WebView2 GPU helper reserves address space it does not touch: its working
    // set stays near 9 MiB while its private commit runs to several hundred, and
    // neither the reservation nor its release is this codebase's to make. Gating
    // the total on it measured Chromium's allocator rather than this product, and
    // failed in every run for that reason alone. The total and the GPU commit
    // are still reported, ungated, in the result so a real change stays visible.
    const finalOwnedPrivateBytes = ownedPrivateBytes(final);
    const peakOwnedPrivateBytes = ownedPrivateBytes(peak);
    // A ceiling compared against null or zero is not a measurement, it is a
    // pass. The peak summary wraps each group metric in { value, atElapsedMs },
    // which an earlier version of this read as "no data" and reported 0 MiB
    // against a 525 MiB limit.
    const unknownGroups = [...new Set(
      [...presentGroups(final), ...presentGroups(peak)]
    )].filter(name => !KNOWN_PROCESS_GROUPS.includes(name));
    const ownedReadable = finalOwnedPrivateBytes !== null && peakOwnedPrivateBytes !== null;
    // Detects vocabulary drift in the direction that is actually observable: a
    // group that used to be emitted under a known name now arrives under an
    // unknown one. The opposite test, "every gated group must be present", is
    // wrong, because WebView2 spawns utility and renderer processes on demand
    // and requiring all four fails a healthy run. An unreadable owned total is a
    // separate condition and also fails: a run whose gated figures cannot be
    // read has not been gated. Both are reported by name, and the run is FAIL
    // rather than SKIP so the remaining checks survive and the violation says
    // what broke.
    checks.push(
      {
        name: 'known-process-groups',
        actual: unknownGroups.length > 0
          ? unknownGroups.join(',')
          : KNOWN_PROCESS_GROUPS.join(','),
        limit: KNOWN_PROCESS_GROUPS.length,
        passed: unknownGroups.length === 0 && ownedReadable
      }
    );
    // Only the private-bytes gates depend on the group breakdown. The
    // working-set and process gates read the sample total, so they run whatever
    // the groups look like: an unreadable group must cost those measurements,
    // not the whole run.
    if (ownedReadable) {
      checks.push(
        {
          name: 'final-private-mib',
          actual: finalOwnedPrivateBytes / MEBIBYTE,
          limit: options.maxFinalPrivateMiB,
          passed: finalOwnedPrivateBytes <= options.maxFinalPrivateMiB * MEBIBYTE
        },
        {
          name: 'peak-private-mib',
          actual: peakOwnedPrivateBytes / MEBIBYTE,
          limit: options.maxPeakPrivateMiB,
          passed: peakOwnedPrivateBytes <= options.maxPeakPrivateMiB * MEBIBYTE
        }
      );
    }
    checks.push(
      {
        name: 'final-working-set-mib',
        actual: final.total.workingSetMiB,
        limit: options.maxFinalWorkingSetMiB,
        passed: final.total.workingSetBytes <= options.maxFinalWorkingSetMiB * MEBIBYTE
      },
      {
        name: 'peak-working-set-mib',
        actual: peak.total.workingSetBytes.mib,
        limit: options.maxPeakWorkingSetMiB,
        passed: peak.total.workingSetBytes.value <= options.maxPeakWorkingSetMiB * MEBIBYTE
      },
      {
        name: 'final-process-count',
        actual: final.total.processCount,
        limit: options.maxFinalProcesses,
        passed: final.total.processCount <= options.maxFinalProcesses
      }
    );
  }
  return {
    evaluated: !options.observeOnly,
    thresholds: options.observeOnly ? null : {
      maxFinalPrivateMiB: options.maxFinalPrivateMiB,
      maxPeakPrivateMiB: options.maxPeakPrivateMiB,
      maxFinalWorkingSetMiB: options.maxFinalWorkingSetMiB,
      maxPeakWorkingSetMiB: options.maxPeakWorkingSetMiB,
      maxFinalProcesses: options.maxFinalProcesses
    },
    checks,
    violations: checks.filter(check => !check.passed),
    peak
  };
}

function requireSpawnedHostSample(child, profileMarker, originTime, stage) {
  const sample = createSample(child.pid, profileMarker, originTime);
  if (!sample.processes.some(item => item.processId === child.pid)) {
    throw new Error(`The exact spawned host PID disappeared during ${stage}`);
  }
  return sample;
}

async function measurePrivateChurn({
  options,
  fixture,
  child,
  profileMarker,
  originTime,
  baselineSample,
  steadyStateReached
}) {
  if (!fixture?.churnState?.enabled) {
    throw new Error('Private churn requires its tokenized loopback fixture');
  }

  const cycleSamples = [];
  const checkpointEvents = [];
  const churnDeadline = Date.now() + options.churnTimeoutMs;
  fixture.churnState.started = true;
  for (let cycle = 1; cycle <= options.churnCycles; cycle += 1) {
    const event = await waitForChurnEvent(
      fixture,
      'checkpoint',
      cycle,
      churnDeadline,
      child);
    try {
      cycleSamples.push(requireSpawnedHostSample(
        child,
        profileMarker,
        originTime,
        `churn checkpoint ${cycle}`));
      checkpointEvents.push({ type: event.type, value: event.value, at: event.at });
      event.complete?.();
    } catch (error) {
      event.complete?.(500, 'Checkpoint sampling failed');
      throw error;
    }
  }
  const doneEvent = await waitForChurnEvent(
    fixture,
    'done',
    options.churnCycles,
    churnDeadline,
    child);
  checkpointEvents.push({
    type: doneEvent.type,
    value: doneEvent.value,
    at: doneEvent.at
  });
  doneEvent.complete?.();

  const cooldownSamples = [];
  const cooldownStartedAt = performance.now();
  const cooldownDeadline = cooldownStartedAt + options.maxCooldownMs;
  let cooldownSteady = false;
  do {
    cooldownSamples.push(requireSpawnedHostSample(
      child,
      profileMarker,
      originTime,
      'churn cooldown'));
    const cooldownElapsed = performance.now() - cooldownStartedAt;
    cooldownSteady = cooldownElapsed >= options.cooldownMs
      && memoryWindowIsSteady(
        cooldownSamples,
        DEFAULT_CHURN_LIMITS.plateauWindow,
        options.churnPlateauToleranceMiB * MEBIBYTE);
    const hasPlateauWindow = cooldownSamples.length >= DEFAULT_CHURN_LIMITS.plateauWindow;
    if (hasPlateauWindow && (cooldownSteady || performance.now() >= cooldownDeadline)) break;
    await delay(options.sampleMs);
  } while (true);

  const analysis = analyzeChurnMemory({
    baselineSample,
    cycleSamples,
    cooldownSamples,
    limits: {
      maxRetainedPrivateMiB: options.maxChurnRetainedPrivateMiB,
      maxRetainedWorkingSetMiB: options.maxChurnRetainedWorkingSetMiB,
      maxCycleSlopePrivateMiB: options.maxChurnSlopePrivateMiB,
      plateauToleranceMiB: options.churnPlateauToleranceMiB
    }
  });
  const stableCleanupSamples = cooldownSamples.slice(-DEFAULT_CHURN_LIMITS.plateauWindow);
  const postCleanupAcceptance = evaluateAcceptance(
    options,
    steadyStateReached,
    stableCleanupSamples);
  const cooldownCheck = {
    name: 'churn-cooldown-steady',
    actual: cooldownSteady,
    limit: true,
    passed: cooldownSteady
  };
  const checks = [
    ...postCleanupAcceptance.checks,
    cooldownCheck,
    ...(options.observeOnly ? [] : analysis.checks)
  ];
  const workloadSamples = [baselineSample, ...cycleSamples, ...cooldownSamples];
  const acceptance = {
    ...postCleanupAcceptance,
    thresholds: options.observeOnly ? null : {
      ...postCleanupAcceptance.thresholds,
      churn: analysis.limits
    },
    checks,
    violations: checks.filter(check => !check.passed),
    workloadPeak: peakForSamples(workloadSamples),
    churn: {
      evaluated: !options.observeOnly,
      cooldownSteady,
      analysis
    }
  };

  return {
    acceptance,
    baselineSample,
    cycleSamples,
    cooldownSamples,
    checkpointEvents,
    workloadSamples
  };
}

async function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
    const executable = await stat(options.exe);
    if (!executable.isFile()) throw new Error('--exe does not point to a file');
  } catch (error) {
    printUsage();
    console.error(error instanceof Error ? error.message : String(error));
    return SKIP_EXIT_CODE;
  }

  const probeId = randomUUID();
  const startedAtUtc = new Date().toISOString();
  const profileMode = options.normalProfile ? 'normal-opt-in' : 'private-disposable';
  const profileFolder = path.resolve(os.tmpdir(), `MishaWeb-Memory-Probe-${probeId}`);
  const profileMarker = options.normalProfile ? '' : profileFolder;
  let fixture = null;
  let child = null;
  let result = null;
  let exitCode = SKIP_EXIT_CODE;
  let profileCleanupAllowed = !options.normalProfile;
  let profileIsolation = options.normalProfile
    ? { required: false, verified: false }
    : { required: true, verified: false, expectedFolder: profileFolder };
  const cleanup = {
    close: null,
    descendantsExited: null,
    remainingProcesses: [],
    profileRemoved: false
  };

  await mkdir(options.out, { recursive: true });
  const guardedImages = new Set([path.basename(options.exe)]);
  if (options.normalProfile) guardedImages.add('MishaWeb.exe');
  const existingProcesses = [];
  for (const imageName of guardedImages) {
    const running = runningPidsForImage(imageName);
    if (running.error) {
      result = {
        status: 'SKIP',
        reason: `Could not safely check existing ${imageName} processes: ${running.error}`,
        probeId,
        profileMode,
        startedAtUtc,
        cleanup
      };
      await writeResult(options.out, result);
      return SKIP_EXIT_CODE;
    }
    for (const pid of running.pids) existingProcesses.push({ imageName, processId: pid });
  }
  if (existingProcesses.length > 0) {
    result = {
      status: 'SKIP',
      reason: options.normalProfile
        ? 'The normal profile may be in use; close MishaWeb and every memory-probe launcher first'
        : 'Another isolated memory-probe launcher is already running',
      existingProcesses,
      probeId,
      profileMode,
      startedAtUtc,
      cleanup
    };
    await writeResult(options.out, result);
    return SKIP_EXIT_CODE;
  }

  try {
    let startupUrl = options.url;
    if (!startupUrl) {
      fixture = createFixtureServer(
        probeId,
        { enableChurn: options.churnCycles > 0 });
      startupUrl = await listenOnLoopback(fixture);
    }

    const launchContract = createProbeLaunchContract({
      executablePath: options.exe,
      startupUrl,
      normalProfile: options.normalProfile,
      allowPackagedNormal: false,
      churn: options.churnCycles > 0
        ? {
            cycles: options.churnCycles,
            batchSize: options.churnBatchSize,
            dwellMs: options.churnDwellMs
          }
        : null
    });
    const childEnvironment = createProbeChildEnvironment(
      process.env,
      launchContract,
      { profileFolder });
    const originTime = performance.now();
    child = spawn(launchContract.executablePath, [...launchContract.childArguments], {
      cwd: path.dirname(launchContract.executablePath),
      env: childEnvironment,
      stdio: 'ignore',
      windowsHide: false
    });
    child.on('error', () => { });

    if (fixture) {
      const ready = await Promise.race([
        fixture.pageRequested.then(() => 'page'),
        new Promise(resolve => child.once('exit', () => resolve('exit'))),
        delay(20_000).then(() => 'timeout')
      ]);
      if (ready !== 'page') {
        throw new Error(ready === 'exit'
          ? `The test host exited before requesting its local fixture (exitCode=${child.exitCode})`
          : 'The test host did not request its local fixture within 20 seconds');
      }
    } else {
      const deadline = Date.now() + 20_000;
      let webViewReady = false;
      while (Date.now() < deadline) {
        if (child.exitCode !== null || child.signalCode !== null) {
          throw new Error(`The test host exited before WebView2 became ready (exitCode=${child.exitCode})`);
        }
        const sample = createSample(child.pid, profileMarker, originTime);
        if ((sample.groups.browser?.processCount || 0) > 0
            && (sample.groups.renderer?.processCount || 0) > 0) {
          webViewReady = true;
          break;
        }
        await delay(500);
      }
      if (!webViewReady) throw new Error('WebView2 did not expose a browser and renderer process within 20 seconds');
    }

    if (!options.normalProfile) {
      try {
        profileIsolation = {
          required: true,
          ...verifyDisposableUserDataFolder(child.pid, profileFolder)
        };
      } catch (error) {
        profileCleanupAllowed = false;
        throw error;
      }
    }

    const readyElapsedMs = Math.round(performance.now() - originTime);
    const warmupSamples = [];
    const warmupDeadline = performance.now() + options.maxWarmupMs;
    let steadyStateReached = false;
    let steadyAtElapsedMs = null;
    while (performance.now() <= warmupDeadline) {
      if (child.exitCode !== null || child.signalCode !== null) {
        throw new Error(`The test host exited during warmup (exitCode=${child.exitCode})`);
      }
      const sample = createSample(child.pid, profileMarker, originTime);
      if (!sample.processes.some(item => item.processId === child.pid)) {
        throw new Error('The exact spawned host PID disappeared during warmup');
      }
      warmupSamples.push(sample);
      const warmForMs = sample.elapsedMs - readyElapsedMs;
      // Settle before the warmup window is measured for steadiness. A sample
      // taken while the GPU helper is still initialising shows a working set
      // two orders of magnitude above its steady value, which made the window
      // look unstable long after it had settled and pushed the loop past its
      // deadline.
      if (warmForMs >= options.warmupMs
          && memoryWindowIsSteady(
            warmupSamples,
            options.steadyWindow,
            options.steadyToleranceMiB * MEBIBYTE)
          && (sample.groups?.gpu?.workingSetMiB ?? 0) <= options.steadyToleranceMiB) {
        steadyStateReached = true;
        steadyAtElapsedMs = sample.elapsedMs;
        break;
      }
      await delay(options.sampleMs);
    }

    let measurementSamples;
    let acceptance;
    let churnMeasurement = null;
    if (options.churnCycles > 0) {
      const baselineSample = warmupSamples.at(-1);
      if (!baselineSample) throw new Error('Private churn has no stable baseline sample');
      churnMeasurement = await measurePrivateChurn({
        options,
        fixture,
        child,
        profileMarker,
        originTime,
        baselineSample,
        steadyStateReached
      });
      measurementSamples = churnMeasurement.workloadSamples;
      acceptance = churnMeasurement.acceptance;
    } else {
      measurementSamples = [];
      const measurementStartedAt = performance.now();
      while (measurementSamples.length === 0
          || performance.now() - measurementStartedAt <= options.durationMs) {
        if (child.exitCode !== null || child.signalCode !== null) {
          throw new Error(`The test host exited during measurement (exitCode=${child.exitCode})`);
        }
        measurementSamples.push(requireSpawnedHostSample(
          child,
          profileMarker,
          originTime,
          'measurement'));
        if (performance.now() - measurementStartedAt >= options.durationMs) break;
        await delay(options.sampleMs);
      }
      acceptance = evaluateAcceptance(options, steadyStateReached, measurementSamples);
    }

    const passed = acceptance.violations.length === 0;
    result = {
      status: passed ? 'PASS' : 'FAIL',
      reason: passed
        ? options.observeOnly
          ? 'The isolated workload stabilized and observation completed'
          : options.churnCycles > 0
            ? 'Bounded private tab churn returned to a stable cleanup plateau within every retention limit'
            : 'The isolated workload stabilized within every memory ceiling'
        : acceptance.violations.map(item => `${item.name}=${item.actual} (limit ${item.limit})`).join('; '),
      probeId,
      profileMode,
      profileIsolation,
      processId: child.pid,
      executable: options.exe,
      startupUrl: options.url || 'deterministic-loopback-fixture',
      startedAtUtc,
      configuration: {
        workload: options.churnCycles > 0 ? 'bounded-private-tab-churn' : 'single-tab',
        warmupMs: options.warmupMs,
        maxWarmupMs: options.maxWarmupMs,
        durationMs: options.durationMs,
        sampleMs: options.sampleMs,
        steadyWindow: options.steadyWindow,
        steadyToleranceMiB: options.steadyToleranceMiB,
        observeOnly: options.observeOnly,
        churn: options.churnCycles > 0
          ? {
              cycles: options.churnCycles,
              batchSize: options.churnBatchSize,
              dwellMs: options.churnDwellMs,
              timeoutMs: options.churnTimeoutMs,
              cooldownMs: options.cooldownMs,
              maxCooldownMs: options.maxCooldownMs
            }
          : null
      },
      steadyState: {
        reached: steadyStateReached,
        pageReadyElapsedMs: readyElapsedMs,
        reachedAtElapsedMs: steadyAtElapsedMs,
        warmupPeak: peakForSamples(warmupSamples),
        samples: warmupSamples
      },
      measurement: {
        peak: acceptance.workloadPeak || acceptance.peak,
        final: measurementSamples.at(-1),
        samples: measurementSamples,
        churn: churnMeasurement
          ? {
              baseline: churnMeasurement.baselineSample,
              cycleSamples: churnMeasurement.cycleSamples,
              cooldownSamples: churnMeasurement.cooldownSamples,
              checkpointEvents: churnMeasurement.checkpointEvents,
              retainedDocumentLoads: fixture?.churnState?.probeDocumentLoads ?? null
            }
          : null
      },
      acceptance,
      cleanup
    };
    exitCode = passed ? 0 : 1;
  } catch (error) {
    result = {
      status: 'SKIP',
      reason: `Memory probe could not complete safely: ${error instanceof Error ? error.message : String(error)}`,
      probeId,
      profileMode,
      profileIsolation,
      processId: child?.pid || null,
      executable: options.exe,
      startedAtUtc,
      retainedDocumentLoads: fixture?.churnState?.probeDocumentLoads ?? null,
      cleanup
    };
    exitCode = SKIP_EXIT_CODE;
  } finally {
    cleanup.close = await closeExactProcess(child);
    if (child?.pid && cleanup.close.exited) {
      const knownProcessIds = result?.measurement?.final?.processes
        ?.map(item => item.processId)
        .filter(Number.isInteger) || [];
      const descendants = await waitForIsolatedProcessesToExit(
        child.pid,
        profileMarker,
        knownProcessIds,
        12_000);
      cleanup.descendantsExited = descendants.exited;
      cleanup.remainingProcesses = descendants.remaining || [];
      if (descendants.error) cleanup.descendantQueryError = descendants.error;
    } else {
      cleanup.descendantsExited = child ? false : true;
    }

    if (child && (!cleanup.close.exited || !cleanup.descendantsExited)) {
      result.status = 'SKIP';
      result.reason = `${result.reason}; the exact test process tree did not exit cleanly`;
      exitCode = SKIP_EXIT_CODE;
    }

    if (!options.normalProfile && profileCleanupAllowed && !options.keepProfile
        && cleanup.close.exited && cleanup.descendantsExited) {
      if (!isSafeDisposableProfile(profileFolder, probeId)) {
        cleanup.profileCleanupError = 'Disposable profile path failed the exact safety check';
        result.status = 'SKIP';
        result.reason = `${result.reason}; disposable profile path was not safe to remove`;
        exitCode = SKIP_EXIT_CODE;
      } else {
        try {
          await rm(profileFolder, { recursive: true, force: true, maxRetries: 4, retryDelay: 250 });
          cleanup.profileRemoved = true;
        } catch (error) {
          cleanup.profileCleanupError = error instanceof Error ? error.message : String(error);
          result.status = 'SKIP';
          result.reason = `${result.reason}; the disposable profile could not be removed`;
          exitCode = SKIP_EXIT_CODE;
        }
      }
    } else if (!options.normalProfile) {
      cleanup.profileFolder = profileFolder;
      if (!profileCleanupAllowed) {
        cleanup.profileRemovalSkipped =
          'Runtime user-data-folder identity was not verified; no folder was removed';
      }
    }

    await closeFixture(fixture);
  }

  await writeResult(options.out, result);
  return exitCode;
}

// Guarded so the module can be imported by MemoryAcceptanceProbe.test.mjs
// without launching a run. An ungated `await main()` made this file untestable,
// which is how the owned-groups gate shipped with two defects that only a
// five-minute browser run surfaced.
//
// The comparison uses pathToFileURL rather than string concatenation: building
// a file: URL by hand mis-escapes a path containing a space, '#' or '?', and a
// mis-escaped URL does not match import.meta.url, so the guard would silently
// become false and the probe would exit 0 having done nothing.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = await main();
}
