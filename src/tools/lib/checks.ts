/**
 * 訳文の機械的なチェック（統合時・検証時・ビルド時に共通で使う）。
 *
 * 指摘には重大度を付ける。
 * - error: そのままゲームに入れると表示が壊れるもの（制御文字の不一致、半角カナ、改行以外の C0 制御文字、
 *   文字化けの痕跡 U+FFFD、1 行で表示される項目の改行）。統合を拒否する。
 * - warning: 確認を促すもの（未翻訳の疑い、長すぎる吹き出し、改ページが入る長さ、原文に無い空行）。統合は行う。
 */
import { join } from 'node:path';
import { listEscapeCodes, normalizeEscapeCode } from '../../shared/text.ts';
import { estimateWidth, wrapText } from '../../shared/wrap.ts';
import { POP_MAX_CHARS, POP_MAX_LINES, WINDOW_MAX_CHARS, WINDOW_PAGE_LINES } from '../../shared/layout.ts';
import type { SourceUnit } from './units.ts';

/**
 * 原文の制御文字の一覧ファイル（"<名前空間>:<キー>" → 正規化した制御文字の配列）。
 * 原文の文章は含まない。抽出時に作り、ゲーム本体の無い環境（CI）でのビルド時の照合に使う。
 * 一覧に無いキーは「原文に制御文字が無い」ことを表す。
 */
export const SOURCE_CODES_PATH = join('translation', 'source-codes.json');

/** 指摘の重大度 */
export type Severity = 'error' | 'warning';

/** 指摘 1 件 */
export interface Issue {
  readonly severity: Severity;
  readonly message: string;
}

/** 文字列中の制御文字を比較用に正規化して取り出す */
export const codeList = (text: string): string[] => listEscapeCodes(text).map(normalizeEscapeCode);

/** 制御文字の出現回数表 */
const countCodes = (codes: readonly string[]): Map<string, number> =>
  codes.reduce((map, code) => map.set(code, (map.get(code) ?? 0) + 1), new Map<string, number>());

/**
 * 原文と訳文の制御文字の差分を説明する（一致なら空配列）。順序は問わず、種類ごとの個数で比べる。
 * 吹き出し指定（\pop）は原文の先頭から切り出して引き継ぐため、原文の本文には含まれない。
 * 訳文に \pop が入っていれば不一致として検出する。
 */
export const diffCodes = (sourceCodes: readonly string[], translation: string): string[] => {
  const source = countCodes(sourceCodes);
  const target = countCodes(codeList(translation));
  const keys = new Set([...source.keys(), ...target.keys()]);
  return Array.from(keys)
    .filter((k) => (source.get(k) ?? 0) !== (target.get(k) ?? 0))
    .map((k) => `${k}（原文 ${source.get(k) ?? 0} / 訳文 ${target.get(k) ?? 0}）`);
};

/** 日本語（かな・漢字）を含むか */
const hasJapanese = (text: string): boolean => /[぀-ヿ㐀-鿿]/.test(text);

/** 推定の行数（全角 1・半角 0.55 の概算幅で折り返した行数） */
const estimateLines = (text: string, maxChars: number): number =>
  wrapText(text, { maxWidth: maxChars, measure: estimateWidth, expandCode: () => '' }).length;

/**
 * 改行（\n）以外の C0 制御文字と DEL。
 * JSON で「\\fb」と書くべきところを「\fb」と書くと、JSON.parse で改ページ（U+000C）＋「b」になる。
 * ツクールMV は「\」を ESC（U+001B）に置き換えてから 1 文字ずつ処理するため（rpg_windows.js の
 * convertEscapeCharacters・processCharacter）、訳文に入った \f は改ページ（入力待ち）、ESC は制御文字の開始として
 * 解釈されてしまう。いずれも訳文には不要な文字なので、すべて拒否する。
 */
const FORBIDDEN_CONTROL_CHARS = /[\x00-\x09\x0b-\x1f\x7f]/g;

/**
 * 置換文字 U+FFFD。文字コードの変換に失敗した痕跡（Shift_JIS で保存したファイルを UTF-8 として読んだ場合など）で、
 * ゲームのフォントにも字形が無い。原文（英語）には含まれない。
 */
const REPLACEMENT_CHARACTER = String.fromCharCode(0xfffd);

/** 文字を "U+000C" の形式で表す（エラーメッセージ用） */
const formatCodePoint = (ch: string): string =>
  `U+${(ch.codePointAt(0) ?? 0).toString(16).toUpperCase().padStart(4, '0')}`;

/**
 * 訳文の「文字」そのものの問題を列挙する（原文や表示種別に依らない検査。問題が無ければ空配列）。
 * merge・verify（checkTranslation）と build（訳文表の読み込み）の両方で使い、ゲーム本体の無い CI でも同じ規則で止める。
 * - 改行以外の C0 制御文字・DEL（error）
 * - 置換文字 U+FFFD（error）
 */
export const checkCharacters = (text: string): Issue[] => {
  const controls = Array.from(new Set(text.match(FORBIDDEN_CONTROL_CHARS) ?? [])).map(formatCodePoint);
  const controlIssues: Issue[] =
    controls.length > 0
      ? [
          {
            severity: 'error',
            message: `改行以外の制御用の文字（${controls.join(', ')}）が含まれています（JSON で「\\」を 1 つしか書かなかった可能性があります。制御文字は "\\\\C[2]" のように書きます）`,
          },
        ]
      : [];
  const replacementIssues: Issue[] = text.includes(REPLACEMENT_CHARACTER)
    ? [{ severity: 'error', message: '文字化けの痕跡（U+FFFD）が含まれています（訳文ファイルを UTF-8 以外で保存した可能性があります）' }]
    : [];
  return [...controlIssues, ...replacementIssues];
};

/**
 * term・db・quest・name のうち、改行を含めてよい（複数行で描かれる）項目の出現箇所（UnitRef.where）の形。
 * - アイテム等の説明（Items.json の [n].description）: ヘルプウィンドウに drawTextEx で 2 行まで描く
 * - アクターのプロフィール（Actors.json の [n].profile）: ステータス画面に drawTextEx で 2 行まで描く
 * - クエストの説明・手順（Quests.json の [n].desc・[n].steps[m][0]）: ミッションログで折り返して複数行で描く
 * - コモンイベントメニューの既定のヘルプ・説明文（plugins.js）: ヘルプ・説明文のウィンドウに drawTextEx で描く
 * それ以外（名前・二つ名・用語・メニュー名・マップ名・クエスト名/カテゴリ・戦闘メッセージ message1〜4 など）は、
 * 1 行の枠に描かれる（drawText では改行が空白になり、戦闘ログの drawTextEx では次の行と重なる）。
 */
const MULTILINE_WHERE_PATTERNS: readonly RegExp[] = [
  /\]\.description$/,
  /\]\.profile$/,
  /\]\.desc$/,
  /\]\.steps\[\d+\]\[0\]$/,
  /^YEP_CommonEventMenu\.Default (?:Help|Subtext)$/,
];

/**
 * イベントコマンドの出現箇所（"EV6 p0 #12"・"CE3 #4"・"T2 p0 #1"）。
 * name の名前空間では、名前（320）・二つ名（324）・プロフィール（325）の変更がこの形になる。
 * どのコマンドかは記録していないため、複数行のプロフィールの可能性を残して改行を許す（正しい訳を拒否しない側に倒す）。
 */
const EVENT_COMMAND_WHERE = /^(?:EV\d+ p\d+|CE\d+|T\d+ p\d+) #\d+$/;

/**
 * 訳文に改行を含めてよい（ゲーム内で複数行として描かれる）単位か。
 * 1 行で描かれる項目に改行があると、drawTextEx で描く画面（選択肢・データベース画面の項目名・戦闘ログ）では
 * 次の項目の行に文字が重なり、drawText で描く画面（項目名・マップ名など）では改行が空白になって崩れる。
 * 原文（work/source/units.json）で改行を含むのは msg と cem の説明文（subtext）だけで、それ以外はすべて 1 行。
 *
 * - msg（文章の表示・スクロール）: 複数行（改行で行を分ける）
 * - choice（選択肢）: 単一行（Window_ChoiceList.drawItem が 1 行の枠に drawTextEx で描く）
 * - map（マップ表示名）: 単一行（Window_MapName が drawText で描く）
 * - cem: 説明文（display=subtext）は複数行（Window_Help に折り返して描く）。
 *   項目名（display=menu）は単一行（YEP_CommonEventMenu の drawItem が 1 行の枠に drawTextEx で描く）
 * - term・db・quest・name: 出現箇所で判定する（MULTILINE_WHERE_PATTERNS・EVENT_COMMAND_WHERE）。
 *   同じ訳文はすべての出現箇所で使われるため、出現箇所がすべて複数行の項目のときだけ改行を許す
 *   （1 つでも 1 行の項目があれば、そこの表示が崩れる）。出現箇所（refs）は抽出時に最大 5 件までしか記録しないため、
 *   6 件目以降は判定に含まれない（現行の原文では、この 4 つの名前空間で出現が 5 件を超える単位は 0 件）。
 */
export const allowsLineBreaks = (unit: Pick<SourceUnit, 'ns' | 'display' | 'refs'>): boolean => {
  switch (unit.ns) {
    case 'msg':
      return true;
    case 'choice':
    case 'map':
      return false;
    case 'cem':
      return unit.display === 'subtext';
    case 'term':
    case 'db':
    case 'quest':
    case 'name':
      return (
        unit.refs.length > 0 &&
        unit.refs.every((ref) => EVENT_COMMAND_WHERE.test(ref.where) || MULTILINE_WHERE_PATTERNS.some((p) => p.test(ref.where)))
      );
  }
};

/** 連続する空行（空白だけの行を含む）の最大の行数 */
const maxBlankLineRun = (text: string): number =>
  text
    .split('\n')
    .reduce<{ readonly run: number; readonly max: number }>(
      (acc, line) => {
        const run = line.trim() === '' ? acc.run + 1 : 0;
        return { run, max: Math.max(acc.max, run) };
      },
      { run: 0, max: 0 },
    ).max;

/** 原文に無い連続した空行を警告する行数（この行数以上続き、かつ原文の最大より長い場合） */
const BLANK_LINE_RUN_WARNING = 3;

/**
 * 空行の問題（警告）を列挙する。吹き出しは空行の分だけ縦に伸び、通常ウィンドウは空のページで入力待ちになるため。
 * - 原文に無い先頭の空行（原文が先頭の空行で字下げしている見出しなどは対象外）
 * - 末尾の空行（merge では normalizeTranslation が取り除くため、手で編集した訳文ファイルで起こる）
 * - 原文に無い 3 行以上の連続した空行
 */
const checkBlankLines = (source: string, translation: string): Issue[] => {
  const issues: Issue[] = [];
  if (translation.startsWith('\n') && !source.startsWith('\n')) {
    issues.push({ severity: 'warning', message: '原文に無い空行が先頭にあります' });
  }
  if (translation.endsWith('\n')) {
    issues.push({ severity: 'warning', message: '末尾に空行があります' });
  }
  const run = maxBlankLineRun(translation);
  if (run >= BLANK_LINE_RUN_WARNING && run > maxBlankLineRun(source)) {
    issues.push({ severity: 'warning', message: `空行が ${run} 行続いています（原文に無い連続した空行）` });
  }
  return issues;
};

/**
 * 訳文の問題点を列挙する（問題が無ければ空配列）。
 */
export const checkTranslation = (unit: SourceUnit, translation: string): Issue[] => {
  const issues: Issue[] = [];
  const codeDiff = diffCodes(codeList(unit.source), translation);
  if (codeDiff.length > 0) {
    issues.push({ severity: 'error', message: `制御文字が一致しません: ${codeDiff.join(', ')}` });
  }
  if (/[｡-ﾟ]/.test(translation)) {
    issues.push({ severity: 'error', message: '半角カナが含まれています' });
  }
  issues.push(...checkCharacters(translation));
  if (translation.includes('\n') && !allowsLineBreaks(unit)) {
    issues.push({ severity: 'error', message: '1 行で表示される項目（選択肢・項目名・用語・名前など）に改行が含まれています' });
  }
  issues.push(...checkBlankLines(unit.source, translation));
  if ((unit.ns === 'msg' || unit.ns === 'choice') && !hasJapanese(translation) && /[a-z]{3,}/i.test(unit.source)) {
    issues.push({ severity: 'warning', message: '日本語が含まれていません（未翻訳の可能性）' });
  }
  if (unit.ns === 'msg' && unit.display === 'pop') {
    const lines = estimateLines(translation, POP_MAX_CHARS);
    if (lines > POP_MAX_LINES) {
      issues.push({ severity: 'warning', message: `吹き出しが長すぎます（推定 ${lines} 行）` });
    }
  }
  if (unit.ns === 'msg' && unit.display === 'window') {
    // 通常ウィンドウは 1 ページ 4 行。超えると改ページ（入力待ち）が入るので確認を促す
    const lines = estimateLines(translation, WINDOW_MAX_CHARS);
    if (lines > WINDOW_PAGE_LINES) {
      issues.push({ severity: 'warning', message: `ウィンドウの文章が ${WINDOW_PAGE_LINES} 行を超えます（推定 ${lines} 行。改ページされます）` });
    }
  }
  return issues;
};

/** 指摘の一覧に error が含まれるか */
export const hasError = (issues: readonly Issue[]): boolean => issues.some((i) => i.severity === 'error');
