/**
 * 訳文の統合規則（純粋関数）。merge.ts（CLI）から使い、単体テストで規則を確かめる。
 *
 * 規則:
 * - 原文に存在しない識別子、文字列でない値、正規化後に空になる訳文は取り込まない（拒否）
 * - 改行コードを LF に統一し、行末の空白と末尾の空行を除去する。選択肢は表記統一のため文末の「。」を除く
 * - 検査（checks.ts）で error がある訳文は取り込まない（force のときだけ取り込む）
 * - 既存の訳文と異なる場合は、overwrite のときだけ上書きし、それ以外は保留する
 */
import { NAMESPACES, type Namespace } from '../../shared/text.ts';
import { checkTranslation, hasError, type Issue } from './checks.ts';
import type { SourceUnit } from './units.ts';
import { parseUnitId, type TranslationTables } from './translations.ts';

/** 統合の設定 */
export interface MergeOptions {
  /** 既存の訳文と異なる場合に上書きする */
  readonly overwrite: boolean;
  /** 検査で error があっても取り込む */
  readonly force: boolean;
}

/** 統合の結果 */
export interface MergeResult {
  /** 統合後の訳文表（入力の表は変更しない） */
  readonly tables: TranslationTables;
  readonly added: readonly string[];
  readonly changed: readonly string[];
  /** 既存と異なるため保留した識別子 */
  readonly held: readonly string[];
  /** 取り込まなかった識別子と理由 */
  readonly rejected: ReadonlyArray<{ readonly id: string; readonly reason: string }>;
  /** 取り込んだ（または保留した）訳文の指摘 */
  readonly issues: ReadonlyArray<{ readonly id: string; readonly issue: Issue }>;
}

/**
 * 訳文を正規化する（改行コード・行末空白・末尾の空行・選択肢の句点）。
 * 末尾の空行は、原文の正規化（src/shared/text.ts の normalizeBody）と同じ規則で取り除く。
 * 残すと吹き出しが空行の分だけ縦に伸び、通常ウィンドウでは空のページで入力待ちになるため。
 * 先頭の空行はレイアウト用途（見出しの字下げなど）があるため残す（原文に無い場合は検査で警告する）。
 */
export const normalizeTranslation = (ns: Namespace, value: string): string => {
  const normalized = value
    .replace(/\r\n?/g, '\n')
    .replace(/[ \t]+$/gm, '')
    .replace(/\n+$/, '');
  return ns === 'choice' ? normalized.replace(/。$/, '') : normalized;
};

/** 訳文表を複製する（入力を変更しないため） */
const cloneTables = (tables: TranslationTables): TranslationTables =>
  (Object.keys(tables) as Namespace[]).reduce((acc, ns) => {
    acc[ns] = { ...tables[ns] };
    return acc;
  }, {} as TranslationTables);

/**
 * 翻訳者の出力（"<名前空間>:<キー>" → 訳文）を訳文表に統合する（純粋関数）。
 *
 * @param tables 現在の訳文表
 * @param output 翻訳者の出力（JSON を unknown のまま渡す）
 * @param unitsById 原文の翻訳単位（識別子 → 単位）
 * @param options 統合の設定
 */
export const mergeTranslations = (
  tables: TranslationTables,
  output: Readonly<Record<string, unknown>>,
  unitsById: ReadonlyMap<string, SourceUnit>,
  options: MergeOptions,
): MergeResult => {
  const next = cloneTables(tables);
  const added: string[] = [];
  const changed: string[] = [];
  const held: string[] = [];
  const rejected: Array<{ id: string; reason: string }> = [];
  const issues: Array<{ id: string; issue: Issue }> = [];

  Object.keys(output).forEach((id) => {
    const value = output[id];
    const parsed = parseUnitId(id);
    const unit = unitsById.get(id);
    if (parsed === undefined || unit === undefined) {
      rejected.push({ id, reason: '原文に存在しない識別子です' });
      return;
    }
    if (typeof value !== 'string') {
      rejected.push({ id, reason: '訳文が文字列ではありません' });
      return;
    }
    const text = normalizeTranslation(parsed.ns, value);
    // 空かどうかは正規化の後で判定する（選択肢の「。」だけの訳などを空のまま保存しないため）
    if (text.trim() === '') {
      rejected.push({ id, reason: '訳文が空です' });
      return;
    }
    const found = checkTranslation(unit, text);
    found.forEach((issue) => issues.push({ id, issue }));
    if (hasError(found) && !options.force) {
      rejected.push({ id, reason: `検査でエラーがあります（${found.filter((i) => i.severity === 'error').map((i) => i.message).join(' / ')}）` });
      return;
    }
    const table = next[parsed.ns];
    const existing = table[parsed.key];
    if (existing === undefined) {
      table[parsed.key] = text;
      added.push(id);
    } else if (existing !== text) {
      if (options.overwrite) {
        table[parsed.key] = text;
        changed.push(id);
      } else {
        held.push(id);
      }
    }
  });

  return { tables: next, added, changed, held, rejected, issues };
};

/** 2 つの訳文表が同じ内容（キーと訳文の組）か */
const sameTable = (a: Readonly<Record<string, string>>, b: Readonly<Record<string, string>>): boolean => {
  const keys = Object.keys(a);
  return keys.length === Object.keys(b).length && keys.every((key) => Object.hasOwn(b, key) && a[key] === b[key]);
};

/**
 * 内容が変わった名前空間を列挙する（書き込みが必要なものだけ。純粋関数）。
 * 変更の無い名前空間を書かないことで、書き込み途中の失敗で影響を受けるファイルを減らし、
 * 他のプログラムが開いているだけの無関係なファイルで失敗しないようにする。
 */
export const changedNamespaces = (before: TranslationTables, after: TranslationTables): Namespace[] =>
  NAMESPACES.filter((ns) => !sameTable(before[ns], after[ns]));

/**
 * 訳文ファイルの書き込みに失敗したときの案内（純粋関数）。
 * 名前空間ごとにファイルを置き換えるため、失敗した時点で先に書いた名前空間は更新済みになる。
 * 再実行すれば残りが取り込まれる（統合は冪等）ことが分かるよう、更新済みの名前空間を示す。
 *
 * @param file 書き込みに失敗したファイル
 * @param updated 失敗の前に書き終えた名前空間
 * @param cause 失敗の原因（エラーコードなど）
 */
export const describeWriteFailure = (file: string, updated: readonly Namespace[], cause: string): string =>
  `${file} の書き込みに失敗しました（${cause}）。開いているプログラム（エディタ・ウイルス対策ソフトなど）を閉じてから再実行してください` +
  `（${updated.length > 0 ? `${updated.join('・')} は更新済み` : 'ほかの名前空間は更新していません'}）`;

/**
 * 読み込み後に訳文ファイルが他のプロセスに変えられていたため、書き込みを中止したときの案内（純粋関数）。
 *
 * @param files 変えられていたファイル
 * @param updated 中止の前に書き終えた名前空間（書き込みの前の確認で見つかった場合は空）
 */
export const describeConflict = (files: readonly string[], updated: readonly Namespace[]): string =>
  `${files.join(', ')} が読み込み後に変更されています（別の merge やエディタでの保存などと競合しました）。` +
  `上書きして消さないよう、書き込みを中止しました（${updated.length > 0 ? `${updated.join('・')} は更新済み` : 'translation/ja は変更していません'}）。再実行してください`;
