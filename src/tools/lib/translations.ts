/**
 * translation/ja/<名前空間>.json（原文ハッシュ → 訳文）の読み書き。
 */
import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { NAMESPACES, type Namespace } from '../../shared/text.ts';
import { asRecord, asString, parseJsonBytes, writeJson, type AtomicWriteOptions } from './json.ts';

/** 訳文ファイルの置き場所 */
export const TRANSLATION_DIR = join('translation', 'ja');

/** 名前空間ごとの訳文表 */
export type TranslationTables = Record<Namespace, Record<string, string>>;

/**
 * 訳文ファイルの内容の指紋（名前空間 → 内容の SHA-256。ファイルが無ければ null）。
 * merge が読み込んでから書き込むまでの間に、他のプロセス（別の merge・エディタでの保存・git の操作など）が
 * ファイルを変えていないかを確かめるために使う。
 */
export type TableSnapshot = Readonly<Record<Namespace, string | null>>;

/**
 * 1 つの名前空間の訳文ファイルのパス
 *
 * @param dir 訳文ファイルのフォルダ（既定は translation/ja。テストでは一時フォルダを渡す）
 */
export const tablePath = (ns: Namespace, dir: string = TRANSLATION_DIR): string => join(dir, `${ns}.json`);

/** 訳文ファイルの内容（バイト列）を読む（無ければ undefined） */
const readTableBytes = (ns: Namespace, dir: string): Buffer | undefined => {
  const path = tablePath(ns, dir);
  return existsSync(path) ? readFileSync(path) : undefined;
};

/** 内容の指紋（SHA-256。内容が無ければ null） */
const fingerprint = (bytes: Buffer | undefined): string | null =>
  bytes === undefined ? null : createHash('sha256').update(bytes).digest('hex');

/** 訳文ファイルの内容を訳文表として解析する（値はすべて文字列であること） */
const parseTable = (bytes: Buffer, path: string): Record<string, string> => {
  const raw = asRecord(parseJsonBytes(bytes, path), path);
  return Object.keys(raw).reduce<Record<string, string>>((table, key) => {
    table[key] = asString(raw[key], `${path}.${key}`);
    return table;
  }, {});
};

/**
 * すべての訳文表を読み込み、読み込んだ内容の指紋も返す（ファイルが無い名前空間は空の表・指紋 null）。
 * 表と指紋は同じバイト列から作るため、「読み込んだ内容」と「指紋」が食い違うことはない。
 */
export const readTablesWithSnapshot = (dir: string = TRANSLATION_DIR): { readonly tables: TranslationTables; readonly snapshot: TableSnapshot } => {
  const loaded = NAMESPACES.map((ns) => ({ ns, bytes: readTableBytes(ns, dir) }));
  const tables = loaded.reduce((acc, { ns, bytes }) => {
    acc[ns] = bytes === undefined ? {} : parseTable(bytes, tablePath(ns, dir));
    return acc;
  }, {} as TranslationTables);
  const snapshot = loaded.reduce(
    (acc, { ns, bytes }) => {
      acc[ns] = fingerprint(bytes);
      return acc;
    },
    {} as Record<Namespace, string | null>,
  );
  return { tables, snapshot };
};

/** すべての訳文表を読み込む（ファイルが無い名前空間は空） */
export const readTables = (dir: string = TRANSLATION_DIR): TranslationTables => readTablesWithSnapshot(dir).tables;

/** いまの訳文ファイルの内容の指紋を取る */
export const snapshotTables = (dir: string = TRANSLATION_DIR): TableSnapshot =>
  NAMESPACES.reduce(
    (acc, ns) => {
      acc[ns] = fingerprint(readTableBytes(ns, dir));
      return acc;
    },
    {} as Record<Namespace, string | null>,
  );

/**
 * 2 つの指紋を比べ、内容が変わった名前空間を返す（純粋関数）。
 *
 * @param targets 比べる名前空間（既定はすべて）
 */
export const findChangedTables = (before: TableSnapshot, after: TableSnapshot, targets: readonly Namespace[] = NAMESPACES): Namespace[] =>
  targets.filter((ns) => before[ns] !== after[ns]);

/** 読み込み後に、訳文ファイルが他のプロセスに変えられていた（上書きすると、その変更が消える） */
export class TableConflictError extends Error {
  /** 変えられていた名前空間 */
  readonly namespaces: readonly Namespace[];

  constructor(namespaces: readonly Namespace[], dir: string) {
    super(`${namespaces.map((ns) => tablePath(ns, dir)).join(', ')} が読み込み後に変更されています`);
    this.name = 'TableConflictError';
    this.namespaces = namespaces;
  }
}

/**
 * 指定した名前空間の訳文ファイルが、指紋を取ったとき（読み込み時）から変わっていないことを確かめる。
 * 変わっていれば TableConflictError を投げる。
 */
export const assertTablesUnchanged = (snapshot: TableSnapshot, targets: readonly Namespace[], dir: string = TRANSLATION_DIR): void => {
  const changed = findChangedTables(snapshot, snapshotTables(dir), targets);
  if (changed.length > 0) {
    throw new TableConflictError(changed, dir);
  }
};

/**
 * 訳文表を書き出す（キーで並べ替えて差分を見やすくする）
 *
 * @param options 書き込みの設定（置き換え直前の確認など。lib/json.ts の writeTextAtomic）
 */
export const writeTable = (
  ns: Namespace,
  table: Readonly<Record<string, string>>,
  dir: string = TRANSLATION_DIR,
  options: AtomicWriteOptions = {},
): void => {
  const sorted = Object.keys(table)
    .sort()
    .reduce<Record<string, string>>((acc, key) => {
      const value = table[key];
      if (value !== undefined) {
        acc[key] = value;
      }
      return acc;
    }, {});
  writeJson(tablePath(ns, dir), sorted, 2, options);
};

/** "msg:0a1b..." 形式の識別子を名前空間とキーに分解する */
export const parseUnitId = (id: string): { readonly ns: Namespace; readonly key: string } | undefined => {
  const match = /^([a-z]+):([0-9a-f]+)$/.exec(id);
  if (match === null) {
    return undefined;
  }
  const ns = NAMESPACES.find((n) => n === match[1]);
  return ns === undefined ? undefined : { ns, key: match[2] ?? '' };
};
