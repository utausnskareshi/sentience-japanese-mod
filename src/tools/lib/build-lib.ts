/**
 * 配布ペイロードの生成（build.ts）で行う検査（純粋関数）。build.ts（CLI）から使い、単体テストで規則を確かめる。
 *
 * - 訳文表の検証（キーの形式・空の訳文・使えない文字）
 * - 訳文の制御文字と原文の制御文字一覧（translation/source-codes.json）の照合
 * - 出力したゲーム内プラグインが Chromium 59（NW.js 0.23.7）で動く書き方だけかの検査
 */
import { HASH_HEX_LENGTH } from '../../shared/hash.ts';
import { NAMESPACES, type Namespace } from '../../shared/text.ts';
import { SOURCE_CODES_PATH, checkCharacters, diffCodes } from './checks.ts';
import { asArray, asRecord, asString } from './json.ts';

/** 問題の一覧の表示上限（多すぎると読めないため） */
export const MAX_REPORTED_PROBLEMS = 20;

/** 名前空間ごとの訳文表（原文ハッシュ → 訳文） */
export type TranslationEntries = Readonly<Record<Namespace, Readonly<Record<string, string>>>>;

/** 問題の一覧を、表示上限までの行と「…ほか N 件」にまとめる（各行の先頭に字下げを付ける） */
export const formatProblemList = (problems: readonly string[], limit = MAX_REPORTED_PROBLEMS): string => {
  const shown = problems.slice(0, limit).map((p) => `  ${p}`);
  const rest = problems.length > limit ? [`  …ほか ${problems.length - limit} 件`] : [];
  return [...shown, ...rest].join('\n');
};

/** 訳文表のキー（原文ハッシュ。16 進 14 桁） */
const KEY_PATTERN = new RegExp(`^[0-9a-f]{${HASH_HEX_LENGTH}}$`);

/**
 * 訳文表（translation/ja/<名前空間>.json を読んだ unknown）を検証して返す。不正なら理由つきの例外を投げる。
 * - キーは原文ハッシュの形式、値は空でない文字列（従来の build.ts の検査）
 * - 文字の検査（checkCharacters: 改行以外の C0 制御文字・U+FFFD）で error が無いこと。
 *   merge・verify と同じ規則を、ゲーム本体（原文）の無い CI のビルドでも適用して、配布物に入る前に止める。
 *
 * @param path エラーメッセージに含めるファイルのパス
 */
export const parseTranslationTable = (raw: unknown, path: string): Record<string, string> => {
  const record = asRecord(raw, path);
  const table = Object.keys(record).reduce<Record<string, string>>((acc, key) => {
    if (!KEY_PATTERN.test(key)) {
      throw new Error(`${path}: キーの形式が不正です: ${key}`);
    }
    const text = asString(record[key], `${path}.${key}`);
    if (text.trim() === '') {
      throw new Error(`${path}: 訳文が空です: ${key}`);
    }
    acc[key] = text;
    return acc;
  }, {});
  const problems = Object.keys(table).flatMap((key) =>
    checkCharacters(table[key] ?? '')
      .filter((issue) => issue.severity === 'error')
      .map((issue) => `${key}: ${issue.message}`),
  );
  if (problems.length > 0) {
    throw new Error(`${path}: 使えない文字を含む訳文が ${problems.length} 件あります:\n${formatProblemList(problems)}`);
  }
  return table;
};

/** 訳文の総数 */
const countEntries = (entries: TranslationEntries): number =>
  NAMESPACES.reduce((acc, ns) => acc + Object.keys(entries[ns]).length, 0);

/**
 * 訳文の制御文字が原文と一致しないものを列挙する（"<名前空間>:<キー>: <差分>" の配列。一致なら空配列）。
 *
 * @param recorded 原文の制御文字の一覧（translation/source-codes.json の内容。一覧に無いキーは制御文字なし）
 */
export const findControlCodeMismatches = (entries: TranslationEntries, recorded: Readonly<Record<string, unknown>>): string[] => {
  /** 原文の制御文字（一覧に無いキーは制御文字なし） */
  const sourceCodesOf = (id: string): string[] => {
    const value = recorded[id];
    return value === undefined
      ? []
      : asArray(value, `${SOURCE_CODES_PATH}.${id}`).map((v, i) => asString(v, `${SOURCE_CODES_PATH}.${id}[${i}]`));
  };
  return NAMESPACES.flatMap((ns) =>
    Object.keys(entries[ns]).flatMap((key) => {
      const id = `${ns}:${key}`;
      const diff = diffCodes(sourceCodesOf(id), entries[ns][key] ?? '');
      return diff.length === 0 ? [] : [`${id}: ${diff.join(', ')}`];
    }),
  );
};

/**
 * 訳文の制御文字（文字色・変数表示など）が原文と一致することを、原文の制御文字一覧で確かめる（不一致なら例外）。
 * 一覧には原文の文章は含まれないため、ゲーム本体の無い CI でも検査できる。
 * 不一致の訳文を同梱すると、ゲーム内で文字色や名前の表示が崩れるため、ビルドを止める。
 *
 * @param recorded translation/source-codes.json の内容（ファイルが無ければ undefined）
 */
export const assertControlCodesMatch = (entries: TranslationEntries, recorded: unknown): void => {
  if (recorded === undefined) {
    if (countEntries(entries) === 0) {
      // 訳文がまだ無い開発初期は照合するものが無い
      return;
    }
    throw new Error(`${SOURCE_CODES_PATH} がありません。npm run extract で作成してから再実行してください`);
  }
  const mismatches = findControlCodeMismatches(entries, asRecord(recorded, SOURCE_CODES_PATH));
  if (mismatches.length > 0) {
    throw new Error(`制御文字が原文と一致しない訳文が ${mismatches.length} 件あります:\n${formatProblemList(mismatches)}`);
  }
};

// ---------------------------------------------------------------------------
// ゲーム内プラグインの出力の検査（Chromium 59 = NW.js 0.23.7 で動くか）
// ---------------------------------------------------------------------------

/**
 * Chromium 59 で使えない正規表現の構文。
 * esbuild は target: 'chrome59' でも、これらを含む正規表現リテラルをエラーにせず new RegExp("…") に変えて通すため
 * （ビルドも型チェックも成功したまま、ゲーム内で SyntaxError になる）、出力の文字列そのものを調べる。
 * new RegExp の引数（文字列リテラル）の中の \p{ は、出力では \\p{ と書かれるが、この検査はその中の \p{ にも一致する。
 */
const UNSUPPORTED_REGEXP_SYNTAX: ReadonlyArray<{ readonly pattern: RegExp; readonly label: string }> = [
  { pattern: /\(\?<=/g, label: '正規表現の後読み「(?<=」（Chromium 62 以降）' },
  { pattern: /\(\?<!/g, label: '正規表現の否定の後読み「(?<!」（Chromium 62 以降）' },
  { pattern: /\(\?<[A-Za-z_$]/g, label: '正規表現の名前付きグループ「(?<名前>」（Chromium 64 以降）' },
  { pattern: /\\[pP]\{/g, label: '正規表現の Unicode プロパティ「\\p{…}」「\\P{…}」（Chromium 64 以降）' },
];

/**
 * Chromium 59 に無い API の代表（[名前, 使えるようになった Chromium の版]）。
 * ゲーム内プラグインの型チェック（lib ES2017＋DOM）では検出できないもの（globalThis は lib に依らず宣言され、
 * queueMicrotask・structuredClone・AbortController・ResizeObserver は DOM の型定義に含まれる）と、
 * esbuild が BigInt("…") に変えて通す BigInt。
 */
const UNSUPPORTED_GLOBALS: ReadonlyArray<readonly [name: string, since: number]> = [
  ['globalThis', 71],
  ['queueMicrotask', 71],
  ['structuredClone', 98],
  ['AbortController', 66],
  ['ResizeObserver', 64],
  ['BigInt', 67],
];

/** 正規表現のフラグのうち Chromium 59 に無いもの（s: 62、d: 90、v: 112） */
const UNSUPPORTED_REGEXP_FLAGS = /[sdv]/;

/** 位置（文字の添字）から行番号（1 始まり）を求める */
const lineOf = (code: string, index: number): number => code.slice(0, index).split('\n').length;

/**
 * 文字列リテラル（" ' `）の終わりの添字を返す（エスケープを考慮。閉じていなければ -1）。
 * テンプレートリテラルの ${…} の入れ子は考慮しない（new RegExp の引数を区切る目的には十分）。
 */
const findStringEnd = (code: string, start: number): number => {
  const quote = code[start];
  for (let i = start + 1; i < code.length; i++) {
    const ch = code[i];
    if (ch === '\\') {
      i += 1;
    } else if (ch === quote) {
      return i;
    }
  }
  return -1;
};

/**
 * 関数呼び出しの引数を、括弧・文字列リテラルの入れ子を考慮して最上位のカンマで分けて返す。
 *
 * @param start 開き括弧の直後の添字
 * @returns 各引数のソース文字列（前後の空白は除く）。閉じ括弧が見つからなければ undefined
 */
const readCallArguments = (code: string, start: number): string[] | undefined => {
  const args: string[] = [];
  let depth = 0;
  let current = '';
  for (let i = start; i < code.length; i++) {
    const ch = code[i] ?? '';
    if (ch === '"' || ch === "'" || ch === '`') {
      const end = findStringEnd(code, i);
      if (end < 0) {
        return undefined;
      }
      current += code.slice(i, end + 1);
      i = end;
    } else if (ch === '(' || ch === '[' || ch === '{') {
      depth += 1;
      current += ch;
    } else if (ch === ')' || ch === ']' || ch === '}') {
      if (depth === 0) {
        return [...args, current.trim()];
      }
      depth -= 1;
      current += ch;
    } else if (ch === ',' && depth === 0) {
      args.push(current.trim());
      current = '';
    } else {
      current += ch;
    }
  }
  return undefined;
};

/** 引数が単純な文字列リテラル（" または '）なら中身（エスケープはそのまま）を返す */
const stringLiteralBody = (arg: string): string | undefined => {
  const match = /^(["'])([\s\S]*)\1$/.exec(arg);
  return match === null ? undefined : match[2];
};

/**
 * ゲーム内プラグインの出力（esbuild の結果）に、Chromium 59 で動かない書き方が無いかを調べる（純粋関数）。
 * 見つかったものを "<行> 行目: <説明>" の配列で返す（無ければ空配列）。
 * - 正規表現の後読み・名前付きグループ・Unicode プロパティ（正規表現リテラルと new RegExp の文字列の両方）
 * - new RegExp の第 2 引数（文字列リテラル）の s・d・v フラグ
 * - Chromium 59 に無い API の代表（globalThis・queueMicrotask・structuredClone など）
 * コメントや文字列の中の語にも一致するが、現行の出力には無い。誤検出が出たら、その語を避けるか検査を見直す。
 */
export const findChromium59Incompatibilities = (code: string): string[] => {
  const syntax = UNSUPPORTED_REGEXP_SYNTAX.flatMap(({ pattern, label }) =>
    Array.from(code.matchAll(pattern), (match) => ({ index: match.index, label })),
  );
  const flags = Array.from(code.matchAll(/\bnew\s+RegExp\s*\(/g)).flatMap((match) => {
    const args = readCallArguments(code, match.index + match[0].length);
    const flagText = args?.[1] === undefined ? undefined : stringLiteralBody(args[1]);
    return flagText !== undefined && UNSUPPORTED_REGEXP_FLAGS.test(flagText)
      ? [{ index: match.index, label: `正規表現のフラグ「${flagText}」（s は Chromium 62、d は 90、v は 112 以降）` }]
      : [];
  });
  const globals = UNSUPPORTED_GLOBALS.flatMap(([name, since]) =>
    Array.from(code.matchAll(new RegExp(`\\b${name}\\b`, 'g')), (match) => ({ index: match.index, label: `${name}（Chromium ${since} 以降）` })),
  );
  return [...syntax, ...flags, ...globals]
    .sort((a, b) => a.index - b.index)
    .map(({ index, label }) => `${lineOf(code, index)} 行目: ${label}`);
};
