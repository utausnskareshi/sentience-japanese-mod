/**
 * 配布ペイロードを生成する CLI。
 *
 * 使い方: npm run build
 * 1. ゲーム内プラグイン（src/runtime）を esbuild で 1 ファイルに結合（Chromium 59 向けに変換）
 * 2. translation/ja/*.json（原文ハッシュ → 訳文）を 1 つの訳文バンドルにまとめる
 * 3. ファイル一覧とハッシュ値を manifest.json に書き出す（パッチツールが埋め込んで使う）
 *
 * 出力物に原文は含まれない（訳文とハッシュ値のみ）。
 */
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, rmSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { build } from 'esbuild';
import { BUNDLE_FORMAT, EMPTY_OVERLAYS, parseOverlays, type TranslationBundle } from '../shared/bundle.ts';
import { NAMESPACES, type Namespace } from '../shared/text.ts';
import { asArray, asRecord, asString, readJson, writeJson } from './lib/json.ts';
import { BUNDLE_RELATIVE_PATH, PAYLOAD_DIR, PAYLOAD_GAME_DIR, PLUGIN_ENTRY, PLUGIN_RELATIVE_PATH } from './lib/payload.ts';
import { SOURCE_CODES_PATH } from './lib/checks.ts';
import { assertControlCodesMatch, findChromium59Incompatibilities, formatProblemList, parseTranslationTable } from './lib/build-lib.ts';
import { parseCliArgs } from './lib/cli.ts';

/** 使い方（引数の誤りのときに表示する） */
const USAGE = 'npm run build（引数はありません）';

/** package.json のバージョン（パッチのバージョン） */
const readPackageVersion = (): string => asString(asRecord(readJson('package.json'), 'package.json')['version'], 'package.json.version');

/** 訳文データのバージョン（translation/meta.json） */
const readTranslationVersion = (): string => {
  const path = join('translation', 'meta.json');
  return existsSync(path) ? asString(asRecord(readJson(path), path)['version'], `${path}.version`) : 'dev';
};

/** RPGツクールMV のプラグインヘッダ（プラグイン管理画面で表示される説明） */
const pluginHeader = (version: string): string =>
  [
    '//=============================================================================',
    `// SentienceJP.js v${version}`,
    '// Sentience: The Android\'s Tale 非公式日本語化プラグイン',
    '//=============================================================================',
    '/*:',
    ' * @plugindesc Sentience: The Android\'s Tale 非公式日本語化プラグイン（ゲームのファイルは書き換えません）',
    ' * @author Sentience JP',
    ' * @help',
    ' * 日本語化パッチツールが自動で配置・登録します。手動での編集は不要です。',
    ' * 元に戻すときは、日本語化パッチツールの「元に戻す」を使ってください。',
    ' * 本プラグインは開発元とは無関係の非公式なものです。',
    ' */',
  ].join('\n');

/** ゲーム内プラグインを結合して書き出す */
const buildRuntime = async (version: string): Promise<void> => {
  const outfile = join(PAYLOAD_GAME_DIR, PLUGIN_RELATIVE_PATH);
  const result = await build({
    entryPoints: [join('src', 'runtime', 'main.ts')],
    outfile,
    bundle: true,
    format: 'iife',
    target: 'chrome59',
    platform: 'browser',
    charset: 'utf8',
    legalComments: 'none',
    banner: { js: pluginHeader(version) },
    define: { __PLUGIN_VERSION__: JSON.stringify(version) },
    logLevel: 'warning',
  });
  if (result.errors.length > 0) {
    throw new Error(`ゲーム内プラグインのビルドに失敗しました（${result.errors.length} 件）`);
  }
  // esbuild は target: 'chrome59' でも、Chromium 59 で動かない正規表現を new RegExp("…") に変えて黙って通し、
  // 新しい API（globalThis など）もそのまま出力するため、出力を検査して配布前に止める（build-lib.ts。単体テストあり）
  const problems = findChromium59Incompatibilities(readFileSync(outfile, 'utf8'));
  if (problems.length > 0) {
    throw new Error(
      `ゲーム内プラグイン（${outfile}）に Chromium 59（NW.js 0.23.7）で動かない書き方が ${problems.length} 件あります:\n${formatProblemList(problems)}`,
    );
  }
};

/**
 * translation/ja/<ns>.json を読み込んで検証する（キーの形式・空の訳文・改行以外の C0 制御文字・U+FFFD。
 * 規則は build-lib.ts の parseTranslationTable。単体テストあり）
 */
const readTranslationTable = (ns: Namespace): Record<string, string> => {
  const path = join('translation', 'ja', `${ns}.json`);
  return existsSync(path) ? parseTranslationTable(readJson(path), path) : {};
};

/**
 * 訳文の制御文字（文字色・変数表示など）が原文と一致するかを translation/source-codes.json で照合する
 * （照合の規則は build-lib.ts の assertControlCodesMatch。単体テストあり）。
 */
const validateControlCodes = (entries: Readonly<Record<Namespace, Readonly<Record<string, string>>>>): void =>
  assertControlCodesMatch(entries, existsSync(SOURCE_CODES_PATH) ? readJson(SOURCE_CODES_PATH) : undefined);

/** すべての訳文表を読み込み、制御文字を照合する（不正なら例外） */
const readValidatedEntries = (): Record<Namespace, Record<string, string>> => {
  const entries = NAMESPACES.reduce(
    (acc, ns) => {
      acc[ns] = readTranslationTable(ns);
      return acc;
    },
    {} as Record<Namespace, Record<string, string>>,
  );
  validateControlCodes(entries);
  return entries;
};

/** 画像に重ねる日本語ラベル（translation/overlays.json。無ければ空） */
const readOverlays = (): TranslationBundle['overlays'] => {
  const overlaysPath = join('translation', 'overlays.json');
  return existsSync(overlaysPath) ? parseOverlays(readJson(overlaysPath), overlaysPath) : EMPTY_OVERLAYS;
};

/** 訳文バンドルを書き出し、件数を返す */
const buildBundle = (
  translationVersion: string,
  entries: Readonly<Record<Namespace, Record<string, string>>>,
  overlays: TranslationBundle['overlays'],
): Record<Namespace, number> => {
  const bundle: TranslationBundle = { format: BUNDLE_FORMAT, version: translationVersion, entries, overlays };
  const path = join(PAYLOAD_GAME_DIR, BUNDLE_RELATIVE_PATH);
  mkdirSync(dirname(path), { recursive: true });
  // 配布物は改行・インデントなしで小さくする
  writeFileSync(path, JSON.stringify(bundle), 'utf8');
  return NAMESPACES.reduce(
    (acc, ns) => {
      acc[ns] = Object.keys(entries[ns]).length;
      return acc;
    },
    {} as Record<Namespace, number>,
  );
};

/** ファイルの SHA-256（16 進小文字） */
const sha256 = (path: string): string => createHash('sha256').update(readFileSync(path)).digest('hex');

/** manifest.json を書き出す */
const writeManifest = (patchVersion: string, translationVersion: string): void => {
  const known = asRecord(readJson(join('config', 'known-game.json')), 'config/known-game.json');
  const files = [PLUGIN_RELATIVE_PATH, BUNDLE_RELATIVE_PATH].map((relative) => {
    const path = join(PAYLOAD_GAME_DIR, relative);
    return { path: relative, sha256: sha256(path), size: readFileSync(path).length };
  });
  writeJson(join(PAYLOAD_DIR, 'manifest.json'), {
    schema: 1,
    patchVersion,
    translationVersion,
    pluginEntry: PLUGIN_ENTRY,
    files,
    knownPluginsJsSha256: asArray(known['knownPluginsJsSha256'], 'knownPluginsJsSha256').map((v, i) => asString(v, `knownPluginsJsSha256[${i}]`)),
    knownBuildIds: asArray(known['knownBuildIds'], 'knownBuildIds').map((v, i) => asString(v, `knownBuildIds[${i}]`)),
  });
};

const main = async (): Promise<void> => {
  // 引数は受け付けない（打ち間違えたオプションを黙って無視しないよう、あれば使い方を表示して終了する）
  parseCliArgs(process.argv.slice(2), { usage: USAGE, options: {}, allowPositionals: false });
  const patchVersion = readPackageVersion();
  const translationVersion = readTranslationVersion();
  // 入力の検査を先に済ませる（不正な訳文で既存のペイロードを壊さないため）
  const entries = readValidatedEntries();
  const overlays = readOverlays();
  // 古い生成物が混ざらないよう、毎回作り直す
  rmSync(PAYLOAD_DIR, { recursive: true, force: true });
  await buildRuntime(patchVersion);
  const counts = buildBundle(translationVersion, entries, overlays);
  writeManifest(patchVersion, translationVersion);
  console.log(`ペイロードを生成しました: ${PAYLOAD_DIR}（パッチ v${patchVersion} / 訳文 ${translationVersion}）`);
  NAMESPACES.forEach((ns) => console.log(`  ${ns.padEnd(6)} ${String(counts[ns]).padStart(6)} 件`));
};

main().catch((error: unknown) => {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
});
