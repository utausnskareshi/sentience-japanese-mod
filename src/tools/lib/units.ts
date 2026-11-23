/**
 * 翻訳単位（ユニット）の型と、抽出結果ファイルの読み書き。
 *
 * ユニットは「名前空間 + 原文ハッシュ」で一意に決まる。
 * 抽出結果（原文を含む）は work/ 配下にだけ置き、リポジトリや配布物には含めない。
 */
import type { Namespace } from '../../shared/text.ts';
import { NAMESPACES } from '../../shared/text.ts';
import { asArray, asNumber, asRecord, asString, optString, readJson, writeJson } from './json.ts';

/** ゲーム内での表示のされ方（翻訳者向けの文脈情報） */
export type DisplayKind =
  | 'pop' // 吹き出し（GALV \pop）
  | 'window' // 通常のメッセージウィンドウ
  | 'scroll' // スクロール文章
  | 'choice' // 選択肢
  | 'name' // アクター名など
  | 'item' // アイテム・スキル等の名前や説明
  | 'term' // システム用語・UI文言
  | 'map' // マップ表示名
  | 'quest' // クエスト関連
  | 'menu' // メニュー項目名
  | 'subtext'; // データベース画面の説明文

/** 出現箇所 */
export interface UnitRef {
  /** ファイル名（例: "Map004.json"） */
  readonly file: string;
  /** ファイル内の位置（例: "EV6 p0 #12"、"Items[5].description"） */
  readonly where: string;
}

/** 翻訳単位 */
export interface SourceUnit {
  readonly ns: Namespace;
  /** 原文ハッシュ */
  readonly key: string;
  /** 正規化済みの原文 */
  readonly source: string;
  readonly display: DisplayKind;
  /** 話者の推定（不明なら省略） */
  readonly speaker?: string;
  /** 初出の順番（物語順の目安） */
  readonly order: number;
  /** 出現回数 */
  readonly count: number;
  /** 出現箇所（最大 5 件） */
  readonly refs: readonly UnitRef[];
  /** 場面のまとまり（バッチ分割用。例: "Map004.json#EV6"） */
  readonly scene: string;
  /** 場面の見出し（翻訳者向け。例: "Map004 格納庫 / EV006"） */
  readonly sceneTitle: string;
}

/** 抽出結果ファイル */
export interface SourceFile {
  readonly generatedAt: string;
  readonly buildId: string | undefined;
  readonly units: readonly SourceUnit[];
}

/** ユニットの一意な識別子（"msg:0a1b..."） */
export const unitId = (unit: Pick<SourceUnit, 'ns' | 'key'>): string => `${unit.ns}:${unit.key}`;

/** 名前空間の文字列を検証する */
export const asNamespace = (value: unknown, where: string): Namespace => {
  const text = asString(value, where);
  const found = NAMESPACES.find((ns) => ns === text);
  if (found === undefined) {
    throw new Error(`${where}: 不明な名前空間です: ${text}`);
  }
  return found;
};

const DISPLAY_KINDS: readonly DisplayKind[] = [
  'pop',
  'window',
  'scroll',
  'choice',
  'name',
  'item',
  'term',
  'map',
  'quest',
  'menu',
  'subtext',
];

/** 表示種別の文字列を検証する */
const asDisplayKind = (value: unknown, where: string): DisplayKind => {
  const text = asString(value, where);
  const found = DISPLAY_KINDS.find((d) => d === text);
  if (found === undefined) {
    throw new Error(`${where}: 不明な表示種別です: ${text}`);
  }
  return found;
};

/** 抽出結果ファイルを読み込む */
export const readSourceFile = (path: string): SourceFile => {
  const root = asRecord(readJson(path), path);
  const units = asArray(root['units'], `${path}.units`).map((raw, i): SourceUnit => {
    const where = `${path}.units[${i}]`;
    const u = asRecord(raw, where);
    const speaker = optString(u['speaker']);
    const base = {
      ns: asNamespace(u['ns'], `${where}.ns`),
      key: asString(u['key'], `${where}.key`),
      source: asString(u['source'], `${where}.source`),
      display: asDisplayKind(u['display'], `${where}.display`),
      order: asNumber(u['order'], `${where}.order`),
      count: asNumber(u['count'], `${where}.count`),
      refs: asArray(u['refs'], `${where}.refs`).map((r, j) => {
        const ref = asRecord(r, `${where}.refs[${j}]`);
        return { file: asString(ref['file'], `${where}.refs[${j}].file`), where: asString(ref['where'], `${where}.refs[${j}].where`) };
      }),
      scene: asString(u['scene'], `${where}.scene`),
      sceneTitle: asString(u['sceneTitle'], `${where}.sceneTitle`),
    };
    return speaker === undefined ? base : { ...base, speaker };
  });
  return {
    generatedAt: asString(root['generatedAt'], `${path}.generatedAt`),
    buildId: optString(root['buildId']),
    units,
  };
};

/** 抽出結果ファイルを書き出す */
export const writeSourceFile = (path: string, file: SourceFile): void => writeJson(path, file);
