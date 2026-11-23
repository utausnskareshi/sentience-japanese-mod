/**
 * 訳文バンドルの読み込みと、原文 → 訳文の検索を行うモジュール。
 *
 * 原文は正規化（src/shared/text.ts）してからハッシュ化し、名前空間ごとの表で訳文を引く。
 * 訳文が見つからない場合は undefined を返し、呼び出し側は原文のまま表示する。
 */
import { EMPTY_OVERLAYS, parseBundle, type OverlayTable, type TranslationBundle } from '../shared/bundle.ts';
import { textKey } from '../shared/hash.ts';
import {
  blockKey,
  joinAffixed,
  splitChoice,
  splitMessageLines,
  splitWhitespace,
  type MessageParts,
  type Namespace,
} from '../shared/text.ts';
import { isJapanese } from './state.ts';

/** 訳文バンドルの配置場所（www フォルダからの相対パス） */
export const BUNDLE_URL = 'sentience_jp/translation.json';

/** 読み込んだバンドル（未読み込みなら undefined） */
let bundle: TranslationBundle | undefined;

/** 翻訳の成否の統計（デバッグ表示用） */
const stats = { hit: 0, miss: 0 };

/** 訳が見つからなかったキー（デバッグ用。debug を有効にしたときだけ記録） */
const misses = new Set<string>();

/** デバッグ記録を有効にするか */
let debug = false;

/** デバッグ記録の有効・無効を切り替える */
export const setDebug = (value: boolean): void => {
  debug = value;
};

/** 統計を返す */
export const translationStats = (): { readonly hit: number; readonly miss: number; readonly misses: readonly string[] } => ({
  hit: stats.hit,
  miss: stats.miss,
  misses: Array.from(misses),
});

/**
 * 訳文バンドルを同期的に読み込む。
 * NW.js（ローカルファイル）でも開発用サーバ（HTTP）でも動くよう、相対URLの同期XHRを使う。
 * 失敗した場合は理由つきの例外を投げる。
 */
export const loadBundle = (): TranslationBundle => {
  const xhr = new XMLHttpRequest();
  xhr.open('GET', BUNDLE_URL, false);
  xhr.overrideMimeType('application/json');
  xhr.send();
  // ローカルファイルの読み込みでは成功時も status が 0 になる（ツクールMV本体と同じ判定）
  if (xhr.status >= 400 || xhr.responseText === '') {
    throw new Error(`訳文データ ${BUNDLE_URL} を読み込めませんでした（status=${xhr.status}）`);
  }
  bundle = parseBundle(JSON.parse(xhr.responseText) as unknown);
  return bundle;
};

/** 読み込み済みバンドルのバージョン */
export const bundleVersion = (): string => bundle?.version ?? '(未読み込み)';

/** 画像に重ねる日本語ラベルの定義（未読み込みなら空） */
export const bundleOverlays = (): OverlayTable => bundle?.overlays ?? EMPTY_OVERLAYS;

/**
 * 正規化済みの原文から訳文を引く（言語設定は見ない低水準の関数）。
 */
export const lookup = (ns: Namespace, normalizedSource: string): string | undefined => {
  if (bundle === undefined || normalizedSource === '') {
    return undefined;
  }
  const key = textKey(normalizedSource);
  const found = bundle.entries[ns][key];
  if (found === undefined) {
    stats.miss += 1;
    if (debug) {
      misses.add(`${ns}:${key} ${normalizedSource.slice(0, 60)}`);
    }
    return undefined;
  }
  stats.hit += 1;
  return found;
};

/** 日本語表示が有効なときだけ訳文を引く */
const lookupIfJapanese = (ns: Namespace, normalizedSource: string): string | undefined =>
  isJapanese() ? lookup(ns, normalizedSource) : undefined;

/**
 * 単一行の文字列（用語・名前など）を翻訳する。訳が無ければ原文を返す。
 */
export const translatePlain = (ns: Namespace, text: string): string => {
  const parts = splitWhitespace(text);
  const ja = lookupIfJapanese(ns, parts.body);
  return ja === undefined ? text : joinAffixed(parts, ja);
};

/** 単一行の文字列の訳文だけを返す（言語設定は見ない。データ書き換え用） */
export const plainTranslation = (ns: Namespace, text: string): string | undefined => {
  const parts = splitWhitespace(text);
  const ja = lookup(ns, parts.body);
  return ja === undefined ? undefined : joinAffixed(parts, ja);
};

/** 複数行ブロックの訳文を返す（言語設定は見ない。データ書き換え用） */
export const blockTranslation = (ns: Namespace, text: string): string | undefined => {
  if (bundle === undefined) {
    return undefined;
  }
  const found = bundle.entries[ns][blockKey(text)];
  if (found === undefined) {
    stats.miss += 1;
    return undefined;
  }
  stats.hit += 1;
  return found;
};

/** 選択肢を翻訳する。訳が無ければ原文を返す */
export const translateChoice = (text: string): string => {
  const parts = splitChoice(text);
  const ja = lookupIfJapanese('choice', parts.body);
  return ja === undefined ? text : joinAffixed(parts, ja);
};

/** メッセージブロックの翻訳結果 */
export interface MessageTranslation {
  readonly parts: MessageParts;
  /** 訳文（改行は明示的な段落区切りのみ。自動改行は呼び出し側で行う） */
  readonly text: string;
}

/**
 * メッセージブロック（401 の行配列）を翻訳する。訳が無ければ undefined。
 */
export const translateMessage = (lines: readonly string[]): MessageTranslation | undefined => {
  const parts = splitMessageLines(lines);
  const ja = lookupIfJapanese('msg', parts.body);
  return ja === undefined ? undefined : { parts, text: ja };
};
