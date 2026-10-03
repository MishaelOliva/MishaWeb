#!/usr/bin/env node

import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  createProbeChildEnvironment,
  createProbeLaunchContract,
  extractWebViewUserDataFolder,
  ISOLATED_PROBE_IMAGE_NAME,
  NORMAL_PROFILE_ARGUMENT,
  PACKAGED_BROWSER_IMAGE_NAME,
  PRIVATE_CHURN_PROBE_ARGUMENT,
  PRIVATE_PROBE_ARGUMENT,
  webViewUserDataFolderMatches
} from './ProbeLaunchContract.mjs';

const startupUrl = 'https://www.youtube.com/watch?v=contract-test';
const isolatedExecutable = path.resolve('fixtures', ISOLATED_PROBE_IMAGE_NAME);
const packagedExecutable = path.resolve('fixtures', PACKAGED_BROWSER_IMAGE_NAME);
const profileFolder = path.resolve('fixtures', 'disposable-profile');

const privateLaunch = createProbeLaunchContract({
  executablePath: isolatedExecutable,
  startupUrl
});
assert.equal(privateLaunch.browserMode, 'private');
assert.equal(privateLaunch.profileMode, 'private-disposable');
assert.equal(privateLaunch.executableKind, 'isolated-probe-launcher');
assert.equal(privateLaunch.usesDisposableUserDataFolder, true);
assert.deepEqual(privateLaunch.childArguments, [PRIVATE_PROBE_ARGUMENT, startupUrl]);

const churnUrl = 'http://127.0.0.1:43127/probe?token=7f42a9a4-e5bc-47c9-8ab4-a66f63509b11';
const privateChurnLaunch = createProbeLaunchContract({
  executablePath: isolatedExecutable,
  startupUrl: churnUrl,
  churn: { cycles: 12, batchSize: 3, dwellMs: 750 }
});
assert.equal(privateChurnLaunch.browserMode, 'private');
assert.equal(privateChurnLaunch.workload, 'bounded-tab-churn');
assert.equal(privateChurnLaunch.usesDisposableUserDataFolder, true);
assert.deepEqual(privateChurnLaunch.childArguments, [
  PRIVATE_CHURN_PROBE_ARGUMENT,
  churnUrl,
  '12',
  '3',
  '750'
]);
assert.throws(
  () => createProbeLaunchContract({
    executablePath: isolatedExecutable,
    startupUrl: 'https://example.test/probe?token=7f42a9a4-e5bc-47c9-8ab4-a66f63509b11',
    churn: { cycles: 12, batchSize: 3, dwellMs: 750 }
  }),
  /tokenized http:\/\/127\.0\.0\.1/);
assert.throws(
  () => createProbeLaunchContract({
    executablePath: isolatedExecutable,
    startupUrl: churnUrl,
    normalProfile: true,
    churn: { cycles: 12, batchSize: 3, dwellMs: 750 }
  }),
  /cannot run against the normal profile/);
assert.throws(
  () => createProbeLaunchContract({
    executablePath: isolatedExecutable,
    startupUrl: churnUrl,
    churn: { cycles: 33, batchSize: 3, dwellMs: 750 }
  }),
  /churn\.cycles must be an integer from 3 through 32/);
assert.throws(
  () => createProbeLaunchContract({
    executablePath: packagedExecutable,
    startupUrl: churnUrl,
    churn: { cycles: 12, batchSize: 3, dwellMs: 750 }
  }),
  /Private churn requires MishaWeb\.YouTubeProbe\.exe/);
assert.throws(
  () => createProbeLaunchContract({
    executablePath: isolatedExecutable,
    startupUrl: churnUrl.replace('/probe', '/not-the-probe'),
    churn: { cycles: 12, batchSize: 3, dwellMs: 750 }
  }),
  /tokenized http:\/\/127\.0\.0\.1/);

const privateEnvironment = createProbeChildEnvironment(
  {
    webview2_user_data_folder: 'unsafe-inherited-profile',
    WebView2_Additional_Browser_Arguments: '--unsafe-inherited-switch',
    PROBE_SENTINEL: 'preserved'
  },
  privateLaunch,
  {
    profileFolder,
    additionalBrowserArguments: '--remote-debugging-address=127.0.0.1'
  });
assert.equal(privateEnvironment.WEBVIEW2_USER_DATA_FOLDER, profileFolder);
assert.equal(
  privateEnvironment.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS,
  '--remote-debugging-address=127.0.0.1');
assert.equal(privateEnvironment.PROBE_SENTINEL, 'preserved');
assert.equal(Object.hasOwn(privateEnvironment, 'webview2_user_data_folder'), false);
assert.equal(Object.hasOwn(privateEnvironment, 'WebView2_Additional_Browser_Arguments'), false);
const quotedProfileCommand =
  `"msedgewebview2.exe" --user-data-dir="${profileFolder}" --type=renderer`;
const wholeQuotedProfileCommand =
  `"msedgewebview2.exe" "--user-data-dir=${profileFolder}" --type=gpu-process`;
assert.equal(extractWebViewUserDataFolder(quotedProfileCommand), profileFolder);
assert.equal(webViewUserDataFolderMatches(quotedProfileCommand, profileFolder), true);
assert.equal(webViewUserDataFolderMatches(wholeQuotedProfileCommand, profileFolder), true);
assert.equal(
  webViewUserDataFolderMatches(
    'msedgewebview2.exe --user-data-dir=C:\\unsafe-profile',
    profileFolder),
  false);

assert.throws(
  () => createProbeLaunchContract({ executablePath: packagedExecutable, startupUrl }),
  /has no private-browser launch contract/);

const packagedNormalLaunch = createProbeLaunchContract({
  executablePath: packagedExecutable,
  startupUrl,
  normalProfile: true
});
assert.equal(packagedNormalLaunch.browserMode, 'normal');
assert.equal(packagedNormalLaunch.profileMode, 'normal-opt-in');
assert.equal(packagedNormalLaunch.usesDisposableUserDataFolder, false);
assert.deepEqual(packagedNormalLaunch.childArguments, [startupUrl]);

const normalEnvironment = createProbeChildEnvironment(
  {
    WEBVIEW2_USER_DATA_FOLDER: 'must-not-survive',
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: 'must-not-survive'
  },
  packagedNormalLaunch);
assert.equal(
  Object.keys(normalEnvironment).some(key => key.toUpperCase() === 'WEBVIEW2_USER_DATA_FOLDER'),
  false);
assert.equal(
  Object.keys(normalEnvironment).some(key => key.toUpperCase() === 'WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'),
  false);

const isolatedNormalLaunch = createProbeLaunchContract({
  executablePath: isolatedExecutable,
  startupUrl,
  normalProfile: true
});
assert.deepEqual(isolatedNormalLaunch.childArguments, [NORMAL_PROFILE_ARGUMENT, startupUrl]);
assert.throws(
  () => createProbeLaunchContract({
    executablePath: path.resolve('fixtures', 'renamed-browser.exe'),
    startupUrl,
    normalProfile: true
  }),
  /Normal-profile mode requires/);

// Exercise the actual CLI path that previously mislabeled a production
// MishaWeb.exe launch as private-disposable. It must reject before creating an
// output folder, enumerating processes, or spawning any executable.
const rejectedOutput = path.join(
  os.tmpdir(),
  `MishaWeb-Probe-Contract-Rejection-${process.pid}-${Date.now()}`);
const youtubeProbeScript = path.resolve('desktop.tests', 'YouTubeFirstPaintProbe.mjs');
if (existsSync(youtubeProbeScript)) {
  const rejectedPackagedLaunch = spawnSync(
    process.execPath,
    [
      youtubeProbeScript,
      '--exe', packagedExecutable,
      '--out', rejectedOutput,
      '--url', startupUrl,
      '--no-screenshots'
    ],
    { encoding: 'utf8', windowsHide: true });
  assert.equal(rejectedPackagedLaunch.status, 2);
  assert.match(rejectedPackagedLaunch.stderr, /has no private-browser launch contract/);
  assert.equal(existsSync(rejectedOutput), false);
}

const memoryProbeScript = path.resolve('desktop.tests', 'MemoryAcceptanceProbe.mjs');
const rejectedChurnOutput = path.join(
  os.tmpdir(),
  `MishaWeb-Churn-Contract-Rejection-${process.pid}-${Date.now()}`);
const rejectedExternalChurn = spawnSync(
  process.execPath,
  [
    memoryProbeScript,
    '--exe', isolatedExecutable,
    '--out', rejectedChurnOutput,
    '--churn-cycles', '6',
    '--url', startupUrl
  ],
  { encoding: 'utf8', windowsHide: true });
assert.equal(rejectedExternalChurn.status, 2);
assert.match(rejectedExternalChurn.stderr, /uses only the deterministic loopback fixture/);
assert.equal(existsSync(rejectedChurnOutput), false);

const rejectedNormalChurn = spawnSync(
  process.execPath,
  [
    memoryProbeScript,
    '--exe', isolatedExecutable,
    '--out', rejectedChurnOutput,
    '--churn-cycles', '6',
    '--normal-profile'
  ],
  { encoding: 'utf8', windowsHide: true });
assert.equal(rejectedNormalChurn.status, 2);
assert.match(rejectedNormalChurn.stderr, /private-only/);
assert.equal(existsSync(rejectedChurnOutput), false);

console.log(JSON.stringify({
  status: 'PASS',
  contract: 'private probes require the isolated BrowserMode.Private launcher and disposable UDF'
}));
