/**
 * 配布用の訳文バンドル（translation.json）の型と検証。
 *
 * バンドルには原文を含めない。キーは「原文を正規化した文字列のハッシュ値」、値は訳文。
 * ゲーム内プラグインは起動時にこれを読み込み、表示直前に原文のハッシュで訳文を引く。
 */
import { NAMESPACES, type Namespace } from './text.ts';

/** バンドルの形式識別子（別ファイルの誤読み込み防止） */
export const BUNDLE_FORMAT = 'sentience-jp-translation';

/** 名前空間ごとの「キー → 訳文」表 */
export type EntryTable = Readonly<Record<string, string>>;

/**
 * 画像の上に重ねる日本語ラベル（画像内の英語を隠して日本語を表示する）。
 * 座標は対象画像の左上を原点とするピクセル単位。画像ファイル自体は読み書きしない。
 */
export interface OverlaySpec {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
  /** 背景色（英語の文字を隠す）。省略時は透明 */
  readonly fill?: string;
  /** 表示する日本語（\n で複数行） */
  readonly text: string;
  readonly fontSize: number;
  /** 文字色（既定: #ffffff） */
  readonly color?: string;
  /** 縁取りの色（既定: rgba(0, 0, 0, 0.6)） */
  readonly outlineColor?: string;
  /** 文字揃え（既定: center） */
  readonly align?: 'left' | 'center' | 'right';
}

/** 画像の種類ごとの重ね表示の定義（キーは画像ファイル名。拡張子なし） */
export interface OverlayTable {
  /** img/pictures（ピクチャの表示） */
  readonly pictures: Readonly<Record<string, readonly OverlaySpec[]>>;
  /** img/parallaxes（遠景） */
  readonly parallaxes: Readonly<Record<string, readonly OverlaySpec[]>>;
  /** img/titles1（タイトル画面の背景） */
  readonly titles1: Readonly<Record<string, readonly OverlaySpec[]>>;
}

/** 訳文バンドル */
export interface TranslationBundle {
  readonly format: typeof BUNDLE_FORMAT;
  /** 訳文データのバージョン（例: "2026.11.21"） */
  readonly version: string;
  /** 名前空間ごとの訳文表 */
  readonly entries: Readonly<Record<Namespace, EntryTable>>;
  /** 画像の上に重ねる日本語ラベル */
  readonly overlays: OverlayTable;
}

/** 空の重ね表示定義 */
export const EMPTY_OVERLAYS: OverlayTable = { pictures: {}, parallaxes: {}, titles1: {} };

/**
 * 画像名に対応する重ね表示の定義を引く（純粋関数。定義が無ければ undefined）。
 *
 * 表は普通のオブジェクトなので、`group[name]` のまま引くと、画像名が constructor・toString・
 * __proto__ などの組み込みプロパティ名と同じ場合に、Object.prototype の関数やオブジェクトが
 * 「定義」として返ってしまう（ゲーム内では毎フレームの例外になる。RA-02）。
 * そのため、表自身が持つプロパティで、かつ配列のものだけを定義として扱う。
 *
 * @param group 画像の種類ごとの表（OverlayTable の pictures など）
 * @param name 画像名（拡張子なし）
 */
export const overlaySpecsOf = (
  group: Readonly<Record<string, readonly OverlaySpec[]>>,
  name: string,
): readonly OverlaySpec[] | undefined => {
  if (!Object.prototype.hasOwnProperty.call(group, name)) {
    return undefined;
  }
  const specs: unknown = group[name];
  return Array.isArray(specs) ? (specs as readonly OverlaySpec[]) : undefined;
};

/** 数値であることを要求する */
const requireNumber = (value: unknown, where: string): number => {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new Error(`${where} が数値ではありません`);
  }
  return value;
};

/** 任意の文字列（無ければ undefined） */
const optionalString = (value: unknown, where: string): string | undefined => {
  if (value === undefined) {
    return undefined;
  }
  if (typeof value !== 'string') {
    throw new Error(`${where} が文字列ではありません`);
  }
  return value;
};

/** 1 件の重ね表示定義を検証する */
const parseOverlaySpec = (value: unknown, where: string): OverlaySpec => {
  if (!isRecord(value)) {
    throw new Error(`${where} がオブジェクトではありません`);
  }
  const text = value['text'];
  if (typeof text !== 'string') {
    throw new Error(`${where}.text が文字列ではありません`);
  }
  const align = value['align'];
  if (align !== undefined && align !== 'left' && align !== 'center' && align !== 'right') {
    throw new Error(`${where}.align は left / center / right のいずれかです`);
  }
  const base: OverlaySpec = {
    x: requireNumber(value['x'], `${where}.x`),
    y: requireNumber(value['y'], `${where}.y`),
    width: requireNumber(value['width'], `${where}.width`),
    height: requireNumber(value['height'], `${where}.height`),
    text,
    fontSize: requireNumber(value['fontSize'], `${where}.fontSize`),
  };
  const fill = optionalString(value['fill'], `${where}.fill`);
  const color = optionalString(value['color'], `${where}.color`);
  const outlineColor = optionalString(value['outlineColor'], `${where}.outlineColor`);
  return {
    ...base,
    ...(fill === undefined ? {} : { fill }),
    ...(color === undefined ? {} : { color }),
    ...(outlineColor === undefined ? {} : { outlineColor }),
    ...(align === undefined ? {} : { align }),
  };
};

/** 画像名 → 重ね表示定義の表を検証する */
const parseOverlayGroup = (value: unknown, where: string): Readonly<Record<string, readonly OverlaySpec[]>> => {
  if (value === undefined) {
    return {};
  }
  if (!isRecord(value)) {
    throw new Error(`${where} がオブジェクトではありません`);
  }
  const result: Record<string, readonly OverlaySpec[]> = {};
  for (const name of Object.keys(value)) {
    const list = value[name];
    if (!Array.isArray(list)) {
      throw new Error(`${where}.${name} が配列ではありません`);
    }
    result[name] = list.map((spec: unknown, i: number) => parseOverlaySpec(spec, `${where}.${name}[${i}]`));
  }
  return result;
};

/** 重ね表示の定義全体を検証する（未定義なら空） */
export const parseOverlays = (value: unknown, where = 'overlays'): OverlayTable => {
  if (value === undefined) {
    return EMPTY_OVERLAYS;
  }
  if (!isRecord(value)) {
    throw new Error(`${where} がオブジェクトではありません`);
  }
  return {
    pictures: parseOverlayGroup(value['pictures'], `${where}.pictures`),
    parallaxes: parseOverlayGroup(value['parallaxes'], `${where}.parallaxes`),
    titles1: parseOverlayGroup(value['titles1'], `${where}.titles1`),
  };
};

/** オブジェクト（配列以外）かどうか */
const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

/** 1 つの名前空間の訳文表を検証する */
const parseTable = (ns: Namespace, value: unknown): EntryTable => {
  if (value === undefined) {
    return {};
  }
  if (!isRecord(value)) {
    throw new Error(`訳文バンドルの entries.${ns} がオブジェクトではありません`);
  }
  const table: Record<string, string> = {};
  for (const key of Object.keys(value)) {
    const text = value[key];
    if (typeof text !== 'string') {
      throw new Error(`訳文バンドルの entries.${ns}.${key} が文字列ではありません`);
    }
    table[key] = text;
  }
  return table;
};

/**
 * JSON.parse の結果を検証して訳文バンドルに変換する。
 * 形式が不正な場合は理由を含む例外を投げる（握りつぶさない）。
 */
export const parseBundle = (json: unknown): TranslationBundle => {
  if (!isRecord(json)) {
    throw new Error('訳文バンドルの最上位がオブジェクトではありません');
  }
  if (json['format'] !== BUNDLE_FORMAT) {
    throw new Error(`訳文バンドルの形式が不正です（format=${String(json['format'])}）`);
  }
  const version = json['version'];
  if (typeof version !== 'string') {
    throw new Error('訳文バンドルの version が文字列ではありません');
  }
  const rawEntries = json['entries'];
  if (!isRecord(rawEntries)) {
    throw new Error('訳文バンドルの entries がオブジェクトではありません');
  }
  const entries = NAMESPACES.reduce(
    (acc, ns) => {
      acc[ns] = parseTable(ns, rawEntries[ns]);
      return acc;
    },
    {} as Record<Namespace, EntryTable>,
  );
  return { format: BUNDLE_FORMAT, version, entries, overlays: parseOverlays(json['overlays'], '訳文バンドルの overlays') };
};
