/**
 * 訳文データの検証（verify.ts）の判定部分（純粋関数）。終了コードを決める規則を単体テストで確かめる。
 */

/** 検証で見つかった問題の件数 */
export interface VerifyCounts {
  /** 検査の error（制御文字の不一致・半角カナ・使えない文字など） */
  readonly errors: number;
  /** 検査の warning（未翻訳の疑い・長すぎる文章・空行など） */
  readonly warnings: number;
  /** フォントに無い文字の種類数 */
  readonly missingGlyphKinds: number;
  /** 原文に存在しないキーの数 */
  readonly orphans: number;
  /** 未訳の件数 */
  readonly untranslated: number;
}

/** 検証の設定（コマンドライン引数） */
export interface VerifyOptions {
  /** --strict: 問題があれば終了コード 1（リリース前の確認用）。未訳も問題として数える */
  readonly strict: boolean;
  /** --allow-untranslated: --strict でも未訳は問題として数えない */
  readonly allowUntranslated: boolean;
}

/** 判定の結果 */
export interface VerifySummary {
  /** 問題の総数（表示用） */
  readonly problemCount: number;
  /** 問題として数えた未訳の件数（--strict かつ --allow-untranslated なしのときだけ未訳の件数、それ以外は 0） */
  readonly untranslatedProblem: number;
  /** プロセスの終了コード（--strict で問題が 1 件でもあれば 1、それ以外は 0） */
  readonly exitCode: 0 | 1;
}

/**
 * 問題の件数と設定から、問題の総数と終了コードを決める（純粋関数）。
 * --strict でないときは、問題があっても終了コード 0（翻訳作業の途中での確認用）。
 */
export const summarizeVerification = (counts: VerifyCounts, options: VerifyOptions): VerifySummary => {
  const untranslatedProblem = options.strict && !options.allowUntranslated ? counts.untranslated : 0;
  const problemCount = counts.errors + counts.warnings + counts.missingGlyphKinds + counts.orphans + untranslatedProblem;
  return { problemCount, untranslatedProblem, exitCode: options.strict && problemCount > 0 ? 1 : 0 };
};
