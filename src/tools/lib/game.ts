/**
 * 作業用CLIから、ローカルにインストールされたゲームの場所を探す。
 *
 * 優先順位:
 *   1. コマンドライン引数 --game <path>（各 CLI が lib/cli.ts で解析して値を渡す）
 *   2. 環境変数 SENTIENCE_GAME_DIR
 *   3. Steam のインストール先（レジストリ → 既定の場所）→ libraryfolders.vdf → appmanifest_635850.acf
 * 見つからなかった場合は、調べた場所と失敗理由をまとめてエラーにする。
 */
import { existsSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';

/** Steam の AppID */
export const APP_ID = '635850';

/** 見つかったゲームの情報 */
export interface GameLocation {
  /** ゲームのルートフォルダ（nw.exe がある場所） */
  readonly root: string;
  /** www フォルダ */
  readonly www: string;
  /** appmanifest から読んだビルドID（不明なら undefined） */
  readonly buildId: string | undefined;
}

/** Steam のインストール先が記録されているレジストリ値（PowerShell のパス表記） */
const REGISTRY_CANDIDATES: ReadonlyArray<readonly [key: string, name: string]> = [
  ['HKCU:\\Software\\Valve\\Steam', 'SteamPath'],
  ['HKLM:\\SOFTWARE\\WOW6432Node\\Valve\\Steam', 'InstallPath'],
  ['HKLM:\\SOFTWARE\\Valve\\Steam', 'InstallPath'],
];

/** レジストリで見つからない場合に調べる、Steam の既定のインストール先 */
const DEFAULT_STEAM_DIRS: readonly string[] = ['C:\\Program Files (x86)\\Steam', 'C:\\Program Files\\Steam'];

/** ゲームフォルダとして成立しているか（必須ファイルの存在確認） */
export const isGameRoot = (root: string): boolean =>
  ['nw.exe', 'www/data/System.json', 'www/js/plugins.js'].every((p) => existsSync(join(root, p)));

/** KeyValues（vdf/acf）形式から "key" "value" の組をすべて取り出す（単純な抽出で十分な用途向け） */
export const readKeyValues = (text: string, key: string): string[] => {
  const regex = new RegExp(`"${key}"\\s+"((?:[^"\\\\]|\\\\.)*)"`, 'g');
  const values: string[] = [];
  for (const match of text.matchAll(regex)) {
    // vdf ではバックスラッシュが \\ とエスケープされている
    values.push((match[1] ?? '').replace(/\\\\/g, '\\'));
  }
  return values;
};

/** 例外の内容を 1 行で説明する（探索経過の表示用） */
const describeError = (error: unknown): string => {
  const message = error instanceof Error ? error.message : String(error);
  return message.split(/\r?\n/).find((line) => line.trim() !== '')?.trim() ?? '不明なエラー';
};

/**
 * レジストリ値を読む。reg.exe は日本語環境で CP932 を出力し、日本語を含むパスが文字化けするため、
 * PowerShell で出力の文字コードを UTF-8 に指定して読む。値が無ければ例外を投げる。
 * キーと値の名前は上の定数のみで、外部からの入力は渡さない。
 */
const readRegistryValue = (key: string, name: string): string => {
  const script = [
    '[Console]::OutputEncoding = [System.Text.Encoding]::UTF8',
    `(Get-ItemProperty -LiteralPath '${key}' -Name '${name}' -ErrorAction Stop).'${name}'`,
  ].join('; ');
  const output = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  }).trim();
  if (output === '') {
    throw new Error('値が空です');
  }
  return output.replace(/\//g, '\\');
};

/** Steam のインストール先の候補（レジストリ → 既定の場所。重複は除く）を、調べた経過とともに返す */
const steamDirCandidates = (trace: string[]): string[] => {
  const fromRegistry =
    process.platform === 'win32'
      ? REGISTRY_CANDIDATES.flatMap(([key, name]) => {
          try {
            return [readRegistryValue(key, name)];
          } catch (error) {
            trace.push(`レジストリ ${key}\\${name}: 読み取れませんでした（${describeError(error)}）`);
            return [];
          }
        })
      : [];
  if (process.platform !== 'win32') {
    trace.push('Windows 以外のため、レジストリは調べませんでした');
  }
  const all = [...fromRegistry, ...DEFAULT_STEAM_DIRS];
  return all.filter((dir, index) => all.findIndex((other) => other.toLowerCase() === dir.toLowerCase()) === index);
};

/** 1 つの Steam フォルダのライブラリから appmanifest を探してゲームの場所を求める */
const locateInSteamDir = (steam: string, trace: string[]): GameLocation | undefined => {
  if (!existsSync(steam)) {
    trace.push(`${steam}: フォルダがありません`);
    return undefined;
  }
  const vdfPath = join(steam, 'steamapps', 'libraryfolders.vdf');
  const libraries = existsSync(vdfPath) ? readKeyValues(readFileSync(vdfPath, 'utf8'), 'path') : [];
  if (!existsSync(vdfPath)) {
    trace.push(`${vdfPath}: ありません（Steam フォルダのみ調べます）`);
  }
  for (const lib of [steam, ...libraries]) {
    const manifest = join(lib, 'steamapps', `appmanifest_${APP_ID}.acf`);
    if (!existsSync(manifest)) {
      continue;
    }
    const acf = readFileSync(manifest, 'utf8');
    const installDir = readKeyValues(acf, 'installdir')[0];
    if (installDir === undefined) {
      trace.push(`${manifest}: installdir がありません`);
      continue;
    }
    const root = join(lib, 'steamapps', 'common', installDir);
    if (isGameRoot(root)) {
      return { root, www: join(root, 'www'), buildId: readKeyValues(acf, 'buildid')[0] };
    }
    trace.push(`${root}: ゲームのファイル（nw.exe・www/data/System.json・www/js/plugins.js）が揃っていません`);
  }
  trace.push(`${steam}: どのライブラリにも appmanifest_${APP_ID}.acf がありません`);
  return undefined;
};

/**
 * ゲームの場所を求める。見つからなければ、調べた場所と理由を含む例外を投げる。
 *
 * @param gameOption コマンドライン引数 --game の値（指定が無ければ undefined）。
 *   値の無い --game は、各 CLI の引数の解析（lib/cli.ts）で使い方を表示して拒否するため、ここには来ない
 *   （以前は値の無い --game が「未指定」と同じ扱いになり、黙って環境変数・自動検出に切り替わっていた）。
 */
export const locateGame = (gameOption: string | undefined): GameLocation => {
  const explicit = gameOption ?? process.env['SENTIENCE_GAME_DIR'];
  if (explicit !== undefined) {
    if (!isGameRoot(explicit)) {
      throw new Error(`指定されたフォルダは Sentience のインストール先ではありません: ${explicit}`);
    }
    return { root: explicit, www: join(explicit, 'www'), buildId: undefined };
  }
  const trace: string[] = [];
  for (const steam of steamDirCandidates(trace)) {
    const found = locateInSteamDir(steam, trace);
    if (found !== undefined) {
      return found;
    }
  }
  throw new Error(
    [
      'Sentience: The Android\'s Tale のインストール先が見つかりません。--game <フォルダ> または環境変数 SENTIENCE_GAME_DIR で指定してください。',
      '調べた場所と結果:',
      ...trace.map((line) => `  - ${line}`),
    ].join('\n'),
  );
};
