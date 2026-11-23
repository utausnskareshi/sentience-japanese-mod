/**
 * 表示幅の計測と、メッセージの折り返し幅の決定を行うモジュール。
 *
 * 実際に描画されるフォント（GameFont＋日本語フォント）で Canvas の measureText を使って測る。
 */
import { wrapText, type WrapOptions } from '../shared/wrap.ts';
import { POP_MAX_CHARS, WINDOW_MAX_CHARS } from '../shared/layout.ts';
import { getGlobalPath } from './env.ts';

/** 計測用の Canvas（使い回す） */
let context: CanvasRenderingContext2D | null = null;

/** 計測結果のキャッシュ（フォント指定 → 文字列 → 幅） */
const cache = new Map<string, Map<string, number>>();

/** 計測用の 2D コンテキストを取得する */
const getContext = (): CanvasRenderingContext2D => {
  if (context === null) {
    const canvas = document.createElement('canvas');
    context = canvas.getContext('2d');
    if (context === null) {
      throw new Error('文字幅の計測用 Canvas を作成できませんでした');
    }
  }
  return context;
};

/**
 * 指定フォントでの文字列の幅を返す計測関数を作る。
 *
 * @param fontSize フォントサイズ（px）
 * @param fontFace フォント名（CSS の font-family 形式）
 */
export const createMeasure = (fontSize: number, fontFace: string): ((text: string) => number) => {
  const font = `${fontSize}px ${fontFace}`;
  const table = cache.get(font) ?? new Map<string, number>();
  cache.set(font, table);
  return (text: string): number => {
    const cached = table.get(text);
    if (cached !== undefined) {
      return cached;
    }
    const ctx = getContext();
    ctx.font = font;
    const width = ctx.measureText(text).width;
    table.set(text, width);
    return width;
  };
};

/** 数値を安全に取り出す（取れなければ既定値） */
const numberOr = (value: unknown, fallback: number): number =>
  typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : fallback;

/** 文字列を安全に取り出す（取れなければ既定値） */
const stringOr = (value: unknown, fallback: string): string => (typeof value === 'string' && value !== '' ? value : fallback);

/**
 * 制御文字が画面上で表示する文字列（幅計算用）を返す。
 * 名前や変数は実際の値に展開し、色変更などの書式コードは空文字とする。
 */
export const expandCodeForMeasure = (code: string): string => {
  const match = /^\\([A-Za-z]+)(?:\[(\d+)\])?/.exec(code);
  if (match === null) {
    return '';
  }
  const name = (match[1] ?? '').toUpperCase();
  const arg = Number(match[2] ?? '0');
  switch (name) {
    case 'N': // アクター名
      return $gameActors?.actor(arg)?.name() ?? '';
    case 'AN': // アクターの二つ名（YEP_MessageCore）
      return $gameActors?.actor(arg)?.nickname() ?? '';
    case 'P': // パーティメンバー名
      return $gameParty?.members()[arg - 1]?.name() ?? '';
    case 'V': // 変数
      return String($gameVariables?.value(arg) ?? '');
    case 'G': // 通貨単位
      return typeof TextManager === 'object' ? TextManager.currencyUnit : '';
    case 'I': // アイコン（幅 32px 程度。全角 2 文字ぶんで近似）
      return '　　';
    default:
      return '';
  }
};

/** メッセージウィンドウの文字サイズ（GALV_MessageStyles の設定を優先） */
export const messageFontSize = (): number => numberOr(getGlobalPath('Galv.Mstyle.fontSize'), numberOr(getGlobalPath('Yanfly.Param.MSGFontSize'), 28));

/** メッセージウィンドウのフォント名（YEP_MessageCore の設定を優先） */
export const messageFontFace = (): string => stringOr(getGlobalPath('Yanfly.Param.MSGFontName'), 'GameFont');

/**
 * メッセージ（文章の表示）の折り返し幅を求める。
 *
 * @param isPop 吹き出し表示か
 * @param hasFace 顔グラフィックを表示するか
 * @param fontSize 文字サイズ
 */
export const messageMaxWidth = (isPop: boolean, hasFace: boolean, fontSize: number): number => {
  if (isPop) {
    return fontSize * POP_MAX_CHARS;
  }
  // 通常ウィンドウ: 画面幅 − 左右の余白 − 顔グラフィックの字下げ（YEP_MessageCore の既定値）
  const faceWidth = numberOr(typeof Window_Base === 'function' ? Window_Base._faceWidth : undefined, 144);
  const available = Graphics.boxWidth - 18 * 2 - 8 - (hasFace ? faceWidth + 24 : 0);
  return Math.min(available, fontSize * WINDOW_MAX_CHARS);
};

/**
 * 訳文をメッセージ用に折り返して行配列にする。
 */
export const wrapMessage = (text: string, isPop: boolean, hasFace: boolean): string[] => {
  const fontSize = messageFontSize();
  const options: WrapOptions = {
    maxWidth: messageMaxWidth(isPop, hasFace, fontSize),
    measure: createMeasure(fontSize, messageFontFace()),
    expandCode: expandCodeForMeasure,
  };
  return wrapText(text, options);
};

/**
 * ウィンドウの現在のフォントで、指定幅に収まるよう折り返す（説明文などに使う）。
 * maxWidth が描画できる幅そのものなので、行頭禁則文字はぶら下げずに追い出す
 * （ぶら下げると窓の右端で文字が欠ける）。
 */
export const wrapForBitmap = (text: string, bitmap: Bitmap, maxWidth: number): string[] =>
  wrapText(text, {
    maxWidth,
    measure: (t) => bitmap.measureTextWidth(t),
    expandCode: expandCodeForMeasure,
    hanging: false,
  });
