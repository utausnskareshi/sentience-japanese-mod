/**
 * 訳文データ全体を検証する CLI。
 *
 * 使い方: npm run verify -- [--game <path>] [--strict] [--allow-untranslated]
 * - 名前空間ごとの翻訳率（--strict では未訳も問題として数える。--allow-untranslated で除外）
 * - 制御文字の一致・半角カナ（エラー）、未翻訳の疑い・長すぎる文章（警告）
 * - ゲーム同梱の日本語フォント（M+ 1m）に無い文字（訳文と画像ラベル translation/overlays.json の両方）
 * - 原文に存在しないキー（ゲーム更新で不要になった訳文）
 * - 原文の制御文字一覧（translation/source-codes.json）が抽出結果と一致するか
 * --strict 指定時は問題があれば終了コード 1 を返す（リリース前の確認用）。
 */
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { NAMESPACES } from '../shared/text.ts';
import { parseOverlays } from '../shared/bundle.ts';
import { locateGame } from './lib/game.ts';
import { readJson } from './lib/json.ts';
import { readSourceFile, unitId } from './lib/units.ts';
import { readTables } from './lib/translations.ts';
import { SOURCE_CODES_PATH, checkTranslation, codeList } from './lib/checks.ts';
import { readFontCodepoints } from './lib/font.ts';
import { parseCliArgs } from './lib/cli.ts';
import { summarizeVerification } from './lib/verify-lib.ts';

/** フォントに無くても問題ない文字（制御・空白・ASCII は英字フォント側で描画される） */
const isIgnorableChar = (cp: number): boolean => cp < 0x80 || cp === 0x3000;

/** 画像ラベルの定義ファイル */
const OVERLAYS_PATH = join('translation', 'overlays.json');

/** 使い方（引数の誤りのときに表示する） */
const USAGE = 'npm run verify -- [--game <ゲームのフォルダ>] [--strict] [--allow-untranslated]';

const main = (): void => {
  // 未知のオプション（--strcit などの打ち間違い）・値の無い --game は使い方を表示して終了する（lib/cli.ts）
  const { values } = parseCliArgs(process.argv.slice(2), {
    usage: USAGE,
    options: { game: 'string', strict: 'boolean', 'allow-untranslated': 'boolean' },
    allowPositionals: false,
  });
  const strict = values.strict;
  const allowUntranslated = values['allow-untranslated'];
  const game = locateGame(values.game);
  const source = readSourceFile(join('work', 'source', 'units.json'));
  const tables = readTables();
  const font = readFontCodepoints(join(game.www, 'fonts', 'mplus-1m-regular.ttf'));

  const errors: string[] = [];
  const warnings: string[] = [];
  const missingGlyphs = new Map<string, number>();
  const knownKeys = new Set(source.units.map((u) => unitId(u)));

  /** フォントに無い文字を数える */
  const collectMissingGlyphs = (text: string): void => {
    Array.from(text).forEach((ch) => {
      const cp = ch.codePointAt(0) ?? 0;
      if (!isIgnorableChar(cp) && !font.has(cp)) {
        missingGlyphs.set(ch, (missingGlyphs.get(ch) ?? 0) + 1);
      }
    });
  };

  // 翻訳率（未訳の件数は --strict の判定に使う）
  console.log('翻訳率:');
  let untranslated = 0;
  NAMESPACES.forEach((ns) => {
    const units = source.units.filter((u) => u.ns === ns);
    const done = units.filter((u) => tables[ns][u.key] !== undefined).length;
    untranslated += units.length - done;
    const percent = units.length === 0 ? 100 : Math.floor((done / units.length) * 1000) / 10;
    console.log(`  ${ns.padEnd(6)} ${String(done).padStart(5)} / ${String(units.length).padStart(5)}（${percent}%）`);
  });

  // 個々の訳文のチェック
  source.units.forEach((unit) => {
    const translation = tables[unit.ns][unit.key];
    if (translation === undefined) {
      return;
    }
    checkTranslation(unit, translation).forEach((issue) =>
      (issue.severity === 'error' ? errors : warnings).push(`${unitId(unit)}: ${issue.message}`),
    );
    collectMissingGlyphs(translation);
  });

  // 画像ラベルの文字もフォントと半角カナを確認する
  if (existsSync(OVERLAYS_PATH)) {
    const overlays = parseOverlays(readJson(OVERLAYS_PATH), OVERLAYS_PATH);
    [overlays.pictures, overlays.parallaxes, overlays.titles1].forEach((group) => {
      Object.keys(group).forEach((name) => {
        (group[name] ?? []).forEach((spec) => {
          collectMissingGlyphs(spec.text);
          if (/[｡-ﾟ]/.test(spec.text)) {
            errors.push(`overlays.${name}: 半角カナが含まれています`);
          }
        });
      });
    });
  }

  // 原文の制御文字一覧が、いまの抽出結果と一致しているか（build はこの一覧で照合するため）
  if (existsSync(SOURCE_CODES_PATH)) {
    const recorded = readJson(SOURCE_CODES_PATH);
    const expected = source.units
      .map((u) => [unitId(u), codeList(u.source)] as const)
      .filter(([, codes]) => codes.length > 0);
    const recordedRecord = typeof recorded === 'object' && recorded !== null ? (recorded as Record<string, unknown>) : {};
    const mismatched = expected.filter(([id, codes]) => JSON.stringify(recordedRecord[id]) !== JSON.stringify(codes));
    if (mismatched.length > 0 || Object.keys(recordedRecord).length !== expected.length) {
      errors.push(`${SOURCE_CODES_PATH} が抽出結果と一致しません（npm run extract で作り直してください）`);
    }
  } else {
    errors.push(`${SOURCE_CODES_PATH} がありません（npm run extract で作成してください）`);
  }

  // 原文に無いキー（ゲーム更新で不要になった、または誤ったキー）
  const orphans = NAMESPACES.flatMap((ns) =>
    Object.keys(tables[ns])
      .filter((key) => !knownKeys.has(`${ns}:${key}`))
      .map((key) => `${ns}:${key}`),
  );

  if (errors.length > 0) {
    console.log(`\nエラー ${errors.length} 件:`);
    errors.slice(0, 300).forEach((i) => console.log(`  ${i}`));
  }
  if (warnings.length > 0) {
    console.log(`\n警告 ${warnings.length} 件:`);
    warnings.slice(0, 300).forEach((i) => console.log(`  ${i}`));
  }
  if (missingGlyphs.size > 0) {
    const list = Array.from(missingGlyphs.entries()).sort((a, b) => b[1] - a[1]);
    console.log(`\nフォントに無い文字 ${list.length} 種: ${list.map(([ch, n]) => `${ch}(${n})`).join(' ')}`);
  }
  if (orphans.length > 0) {
    console.log(`\n原文に存在しないキー ${orphans.length} 件: ${orphans.slice(0, 20).join(', ')}`);
  }
  // 問題の総数と終了コードの規則は verify-lib.ts（単体テストあり）
  const summary = summarizeVerification(
    { errors: errors.length, warnings: warnings.length, missingGlyphKinds: missingGlyphs.size, orphans: orphans.length, untranslated },
    { strict, allowUntranslated },
  );
  if (summary.untranslatedProblem > 0) {
    console.log(`\n未訳 ${untranslated} 件（--allow-untranslated で許容）`);
  }
  console.log(summary.problemCount === 0 ? '\n問題は見つかりませんでした' : `\n問題 ${summary.problemCount} 件`);
  // 終了コードは判定の結果そのまま（--strict で問題があれば 1）。ここで条件分岐を持たず、規則は verify-lib.ts に集める
  process.exitCode = summary.exitCode;
};

try {
  main();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
}
