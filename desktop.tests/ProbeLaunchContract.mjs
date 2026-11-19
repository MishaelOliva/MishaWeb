import path from 'node:path';

export const ISOLATED_PROBE_IMAGE_NAME = 'MishaWeb.YouTubeProbe.exe';
export const PACKAGED_BROWSER_IMAGE_NAME = 'MishaWeb.exe';
export const PRIVATE_PROBE_ARGUMENT = '--private-probe';
export const PRIVATE_CHURN_PROBE_ARGUMENT = '--private-churn-probe';
export const NORMAL_PROFILE_ARGUMENT = '--normal-profile';
export const MULTI_VIEW_ARGUMENT = '--multi-view';

const CHURN_BOUNDS = Object.freeze({
  minimumCycles: 3,
  maximumCycles: 32,
  minimumBatchSize: 1,
  maximumBatchSize: 4,
  minimumDwellMs: 250,
  maximumDwellMs: 5_000
});

function requireNonEmptyString(value, name) {
  if (typeof value !== 'string' || !value.trim()) {
    throw new TypeError(`${name} must be a non-empty string`);
  }
  return value;
}

function requireBoundedInteger(value, name, minimum, maximum) {
  if (!Number.isInteger(value) || value < minimum || value > maximum) {
    throw new RangeError(`${name} must be an integer from ${minimum} through ${maximum}`);
  }
  return value;
}

function normalizeWindowsFolder(value) {
  return path.win32
    .normalize(path.win32.resolve(requireNonEmptyString(value, 'userDataFolder')))
    .replace(/[\\/]+$/, '')
    .toLocaleLowerCase('en-US');
}

export function extractWebViewUserDataFolder(commandLine) {
  if (typeof commandLine !== 'string' || !commandLine) return null;
  const match = /(?:^|\s)"--user-data-dir=([^"]+)"/i.exec(commandLine)
    || /(?:^|\s)--user-data-dir="([^"]+)"/i.exec(commandLine)
    || /(?:^|\s)--user-data-dir=([^\s"]+)/i.exec(commandLine);
  return match?.[1] || null;
}

export function webViewUserDataFolderMatches(commandLine, expectedFolder) {
  const actualFolder = extractWebViewUserDataFolder(commandLine);
  return actualFolder !== null
    && normalizeWindowsFolder(actualFolder) === normalizeWindowsFolder(expectedFolder);
}

function validatePrivateChurn(startupUrl, churn) {
  if (!churn) return null;
  if (typeof churn !== 'object') throw new TypeError('churn must be an object');

  let parsedUrl;
  try {
    parsedUrl = new URL(startupUrl);
  } catch {
    throw new Error('Private churn requires an absolute loopback fixture URL');
  }
  const token = parsedUrl.searchParams.get('token') || '';
  if (parsedUrl.protocol !== 'http:'
      || parsedUrl.hostname !== '127.0.0.1'
      || parsedUrl.pathname !== '/probe'
      || parsedUrl.username
      || parsedUrl.password
      || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(token)) {
    throw new Error(
      'Private churn requires tokenized http://127.0.0.1 loopback fixture URL');
  }

  return Object.freeze({
    cycles: requireBoundedInteger(
      churn.cycles,
      'churn.cycles',
      CHURN_BOUNDS.minimumCycles,
      CHURN_BOUNDS.maximumCycles),
    batchSize: requireBoundedInteger(
      churn.batchSize,
      'churn.batchSize',
      CHURN_BOUNDS.minimumBatchSize,
      CHURN_BOUNDS.maximumBatchSize),
    dwellMs: requireBoundedInteger(
      churn.dwellMs,
      'churn.dwellMs',
      CHURN_BOUNDS.minimumDwellMs,
      CHURN_BOUNDS.maximumDwellMs)
  });
}

/**
 * Creates the only supported child-process argument contract for live probes.
 *
 * Production MishaWeb.exe does not expose a private-window command-line mode.
 * A disposable WebView2 UDF alone therefore does not protect MishaWeb's normal
 * application settings. Default probes must use the test launcher, whose
 * --private-probe switch constructs MainForm with BrowserMode.Private.
 */
export function createProbeLaunchContract({
  executablePath,
  startupUrl,
  normalProfile = false,
  allowPackagedNormal = true,
  churn = null,
  multiView = null
}) {
  const resolvedExecutablePath = path.resolve(requireNonEmptyString(executablePath, 'executablePath'));
  const normalizedStartupUrl = requireNonEmptyString(startupUrl, 'startupUrl');
  const imageName = path.basename(resolvedExecutablePath);
  const normalizedImageName = imageName.toLowerCase();
  const isIsolatedLauncher = normalizedImageName === ISOLATED_PROBE_IMAGE_NAME.toLowerCase();
  const isPackagedBrowser = normalizedImageName === PACKAGED_BROWSER_IMAGE_NAME.toLowerCase();
  const privateChurn = validatePrivateChurn(normalizedStartupUrl, churn);
  const validatedMultiView = multiView === null || multiView === undefined
    ? null
    : requireBoundedInteger(multiView, 'multiView', 2, 4);

  if (normalProfile && privateChurn) {
    throw new Error('Private churn cannot run against the normal profile');
  }
  if (privateChurn && !isIsolatedLauncher) {
    throw new Error(`Private churn requires ${ISOLATED_PROBE_IMAGE_NAME}`);
  }
  if (validatedMultiView !== null && !isIsolatedLauncher) {
    throw new Error(`Multi-view probe requires ${ISOLATED_PROBE_IMAGE_NAME}`);
  }

  if (!normalProfile && !isIsolatedLauncher) {
    throw new Error(
      `Default probe mode requires ${ISOLATED_PROBE_IMAGE_NAME}; ${imageName} has no private-browser launch contract. `
      + `Build/use the isolated probe launcher instead of ${PACKAGED_BROWSER_IMAGE_NAME}.`);
  }
  if (normalProfile && !isIsolatedLauncher && !(allowPackagedNormal && isPackagedBrowser)) {
    throw new Error(
      `Normal-profile mode requires ${ISOLATED_PROBE_IMAGE_NAME}`
      + (allowPackagedNormal ? ` or ${PACKAGED_BROWSER_IMAGE_NAME}` : ''));
  }

  const baseArguments = privateChurn
    ? [
        PRIVATE_CHURN_PROBE_ARGUMENT,
        normalizedStartupUrl,
        String(privateChurn.cycles),
        String(privateChurn.batchSize),
        String(privateChurn.dwellMs)
      ]
    : normalProfile
    ? isIsolatedLauncher
      ? [NORMAL_PROFILE_ARGUMENT, normalizedStartupUrl]
      : [normalizedStartupUrl]
    : [PRIVATE_PROBE_ARGUMENT, normalizedStartupUrl];
  const childArguments = validatedMultiView === null
    ? baseArguments
    : [...baseArguments, MULTI_VIEW_ARGUMENT, String(validatedMultiView)];
  const workload = validatedMultiView !== null
    ? (privateChurn ? 'multi-view-bounded-tab-churn' : `multi-view-${validatedMultiView}`)
    : (privateChurn ? 'bounded-tab-churn' : 'single-tab');

  return Object.freeze({
    executablePath: resolvedExecutablePath,
    imageName,
    executableKind: isIsolatedLauncher ? 'isolated-probe-launcher' : 'packaged-browser',
    browserMode: normalProfile ? 'normal' : 'private',
    profileMode: normalProfile ? 'normal-opt-in' : 'private-disposable',
    workload,
    multiView: validatedMultiView,
    churn: privateChurn,
    usesDisposableUserDataFolder: !normalProfile,
    childArguments: Object.freeze(childArguments)
  });
}

/**
 * Removes inherited WebView2 overrides before applying the launch contract.
 * This keeps private probes on their unique UDF and keeps explicit normal-mode
 * probes from accidentally inheriting a supposedly disposable UDF.
 */
export function createProbeChildEnvironment(
  baseEnvironment,
  launchContract,
  { profileFolder = '', additionalBrowserArguments = '' } = {}) {
  if (!baseEnvironment || typeof baseEnvironment !== 'object') {
    throw new TypeError('baseEnvironment must be an object');
  }
  if (!launchContract || typeof launchContract !== 'object') {
    throw new TypeError('launchContract must be an object');
  }

  const childEnvironment = { ...baseEnvironment };
  for (const key of Object.keys(childEnvironment)) {
    const normalizedKey = key.toUpperCase();
    if (normalizedKey === 'WEBVIEW2_USER_DATA_FOLDER'
        || normalizedKey === 'WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS') {
      delete childEnvironment[key];
    }
  }

  if (launchContract.usesDisposableUserDataFolder) {
    const resolvedProfileFolder = path.resolve(requireNonEmptyString(profileFolder, 'profileFolder'));
    if (!path.isAbsolute(resolvedProfileFolder)) {
      throw new Error('profileFolder must resolve to an absolute path');
    }
    childEnvironment.WEBVIEW2_USER_DATA_FOLDER = resolvedProfileFolder;
  }
  if (additionalBrowserArguments) {
    childEnvironment.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS =
      requireNonEmptyString(additionalBrowserArguments, 'additionalBrowserArguments');
  }

  return childEnvironment;
}
