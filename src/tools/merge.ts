/**
 * 翻訳者（エージェント）が出力した訳文を translation/ja に統合する CLI。
 *
 * 使い方: npm run merge -- [--overwrite] [--force] [--dry-run] [--batch <ID> | ファイル...]
 * 入力:   work/out/*.json（形式: { "msg:0a1b...": "訳文", ... }）。ファイル指定が無ければ全件
 * 出力:   translation/ja/<名前空間>.json
 *
 * 統合規則は lib/merge-lib.ts（単体テストあり）。検査で error がある訳文は取り込まない（--force で許可）。
 * 取り込めなかった訳文・訳し漏れ・error があれば終了コード 1 を返す（自己点検や CI で気付けるように）。
 *
 * 書き込みありの統合は同時に 1 つだけ実行できる（translation/ja/.merge.lock で排他する）。
 * 書き込みの直前に、読み込んだ後で訳文ファイルが他のプロセスに変えられていないかも確かめ、変わっていれば書かずに失敗にする。
 * 書き込むのは内容が変わった名前空間のファイルだけ。
 */
import { existsSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import type { Namespace } from '../shared/text.ts';
import { asArray, asRecord, asString, errorCode, readJson } from './lib/json.ts';
import { parseCliArgs } from './lib/cli.ts';
import { acquireLock } from './lib/lock.ts';
import { readSourceFile, unitId } from './lib/units.ts';
import {
  TRANSLATION_DIR,
  TableConflictError,
  assertTablesUnchanged,
  readTablesWithSnapshot,
  tablePath,
  writeTable,
  type TranslationTables,
} from './lib/translations.ts';
import { changedNamespaces, describeConflict, describeWriteFailure, mergeTranslations } from './lib/merge-lib.ts';

const OUT_DIR = join('work', 'out');

/** 一覧表示の上限（多すぎると読めないため） */
const MAX_LIST = 200;

/** 使い方（引数の誤りのときに表示する） */
const USAGE = 'npm run merge -- [--overwrite] [--force] [--dry-run] [--batch <ID> | ファイル...]';

/**
 * merge の排他用のロックファイル（Git の管理外。.gitignore に記載）。
 * 異常終了（Ctrl+C など）で残った場合は、メッセージの案内に従って削除する。
 */
const MERGE_LOCK_PATH = join(TRANSLATION_DIR, '.merge.lock');

/** 例外の原因を短く説明する（エラーコードがあればコード。writeJson の例外は cause に元の例外を持つ） */
const describeCause = (error: unknown): string =>
  errorCode(error) ??
  (error instanceof Error ? (errorCode(error.cause) ?? error.message) : String(error));

/**
 * 統合後の訳文表を書き出す（内容が変わった名前空間だけ）。
 * 読み込み後に他のプロセス（エディタでの保存・git の操作など）がファイルを変えていれば、その変更を古い内容で上書きして
 * 消さないよう、書き込みを中止して例外を投げる。確認は 2 回行う。
 * 1. 書き込みを始める前に、書き込むすべてのファイルを確かめる（見つかれば何も書かない）
 * 2. 各ファイルの置き換えの直前に、そのファイルをもう一度確かめる（1 から置き換えまでの間の変更も見逃さないため）
 * 途中で書き込みに失敗・中止したら、どのファイルか・どの名前空間が更新済みかを含む例外を投げる。
 *
 * @returns 書き込んだ名前空間
 */
const writeChangedTables = (loaded: ReturnType<typeof readTablesWithSnapshot>, tables: TranslationTables): Namespace[] => {
  const targets = changedNamespaces(loaded.tables, tables);
  const written: Namespace[] = [];
  /** 競合の例外を、利用者向けの案内（どのファイルか・更新済みの名前空間）に変える */
  const conflictError = (error: TableConflictError): Error =>
    new Error(describeConflict(error.namespaces.map((ns) => tablePath(ns)), written), { cause: error });
  try {
    assertTablesUnchanged(loaded.snapshot, targets);
  } catch (error) {
    throw error instanceof TableConflictError ? conflictError(error) : error;
  }
  for (const ns of targets) {
    try {
      writeTable(ns, tables[ns], TRANSLATION_DIR, { beforeReplace: () => assertTablesUnchanged(loaded.snapshot, [ns]) });
    } catch (error) {
      throw error instanceof TableConflictError
        ? conflictError(error)
        : new Error(describeWriteFailure(tablePath(ns), written, describeCause(error)), { cause: error });
    }
    written.push(ns);
  }
  return written;
};

/** 統合の設定（コマンドライン引数） */
interface MergeRunOptions {
  readonly overwrite: boolean;
  readonly force: boolean;
  readonly dryRun: boolean;
  readonly batchId: string | undefined;
}

/** 訳文ファイルを統合し、結果を表示する（dryRun でなければ translation/ja に書き込む） */
const mergeFiles = (files: readonly string[], { overwrite, force, dryRun, batchId }: MergeRunOptions): void => {
  const source = readSourceFile(join('work', 'source', 'units.json'));
  const unitsById = new Map(source.units.map((u) => [unitId(u), u]));
  // 読み込んだ内容の指紋も取っておき、書き込みの直前に他のプロセスの変更が無いかを確かめる
  const loaded = readTablesWithSnapshot();
  let tables = loaded.tables;
  const problems: string[] = [];
  let errorCount = 0;

  // バッチ指定時は、バッチに含まれる識別子がすべて訳されているか確認する
  if (batchId !== undefined) {
    const expected = asArray(readJson(join('work', 'batches', `${batchId}.json`)), `${batchId}.json`).map((v, i) => asString(v, `${batchId}.json[${i}]`));
    const produced = asRecord(readJson(join(OUT_DIR, `${batchId}.json`)), `${batchId}.json`);
    const missing = expected.filter((id) => produced[id] === undefined);
    if (missing.length > 0) {
      errorCount += missing.length;
      problems.push(`${batchId}: 訳し漏れ ${missing.length} 件: ${missing.slice(0, 30).join(', ')}`);
    }
  }

  const totals = { added: 0, changed: 0, held: [] as string[] };
  files.forEach((file) => {
    const result = mergeTranslations(tables, asRecord(readJson(file), file), unitsById, { overwrite, force });
    tables = result.tables;
    totals.added += result.added.length;
    totals.changed += result.changed.length;
    totals.held.push(...result.held);
    result.rejected.forEach(({ id, reason }) => {
      errorCount += 1;
      problems.push(`${file}: ${id}: 取り込みませんでした: ${reason}`);
    });
    result.issues.forEach(({ id, issue }) => {
      if (issue.severity === 'error') {
        errorCount += force ? 1 : 0;
      }
      problems.push(`${file}: ${id}: [${issue.severity === 'error' ? 'エラー' : '警告'}] ${issue.message}`);
    });
  });

  // 書き込むのは内容が変わった名前空間だけ（書き込みの直前に競合も確かめる）
  const written = dryRun ? [] : writeChangedTables(loaded, tables);
  const verb = dryRun ? '確認のみ（書き込みなし）' : '統合';
  console.log(
    `${verb}: 追加 ${totals.added} 件 / 上書き ${totals.changed} 件 / 既存と相違のため保留 ${totals.held.length} 件（${files.length} ファイル）`,
  );
  if (!dryRun) {
    console.log(written.length > 0 ? `更新したファイル: ${written.map((ns) => tablePath(ns)).join(', ')}` : '更新したファイルはありません（変更なし）');
  }
  if (totals.held.length > 0) {
    console.log(`保留した識別子（--overwrite で上書き）: ${totals.held.slice(0, MAX_LIST).join(', ')}`);
  }
  if (problems.length > 0) {
    console.log(`要確認 ${problems.length} 件:`);
    problems.slice(0, MAX_LIST).forEach((p) => console.log(`  ${p}`));
    if (problems.length > MAX_LIST) {
      console.log(`  …ほか ${problems.length - MAX_LIST} 件`);
    }
  }
  if (errorCount > 0) {
    process.exitCode = 1;
  }
};

const main = (): void => {
  // 未知のオプション（--dryrun などの打ち間違い）・値の無い --batch は使い方を表示して終了する（lib/cli.ts）
  const { values, positionals } = parseCliArgs(process.argv.slice(2), {
    usage: USAGE,
    options: { overwrite: 'boolean', force: 'boolean', 'dry-run': 'boolean', batch: 'string' },
    allowPositionals: true,
  });
  const overwrite = values.overwrite;
  const force = values.force;
  // --dry-run: チェックだけ行い、translation/ja には書き込まない（翻訳者の自己点検用）
  const dryRun = values['dry-run'];
  // --batch <ID>: そのバッチの出力（work/out/<ID>.json）だけを対象にし、訳し漏れも確認する
  const batchId = values.batch;
  if (batchId !== undefined && positionals.length > 0) {
    // 以前はファイルの指定を黙って無視していた
    throw new Error(`--batch とファイルの指定は同時に使えません（ファイル: ${positionals.join(', ')}）\n使い方: ${USAGE}`);
  }
  const files =
    batchId !== undefined
      ? [join(OUT_DIR, `${batchId}.json`)]
      : positionals.length > 0
        ? [...positionals]
        : existsSync(OUT_DIR)
          ? readdirSync(OUT_DIR)
              .filter((f) => f.endsWith('.json') && !f.endsWith('.terms.json'))
              .sort()
              .map((f) => join(OUT_DIR, f))
          : [];
  if (files.length === 0) {
    console.log('統合する訳文ファイルがありません（work/out/*.json）');
    return;
  }
  const missingFiles = files.filter((f) => !existsSync(f));
  if (missingFiles.length > 0) {
    throw new Error(`訳文ファイルが見つかりません: ${missingFiles.join(', ')}（翻訳の出力を先に作成してください）`);
  }

  // 書き込みありの統合だけを排他する。--dry-run は読むだけなので、翻訳担当が並行して自己点検できるようロックを取らない
  const releaseLock = dryRun ? undefined : acquireLock(MERGE_LOCK_PATH, 'merge');
  try {
    mergeFiles(files, { overwrite, force, dryRun, batchId });
  } finally {
    if (releaseLock !== undefined) {
      try {
        releaseLock();
      } catch (error) {
        // ロックを消せなくても、統合の結果（成功・失敗）は隠さない。残ったロックは次回の実行で場所を案内する
        console.error(`ロックファイル ${MERGE_LOCK_PATH} を削除できませんでした（${describeCause(error)}）。次の merge の前に削除してください`);
        process.exitCode = 1;
      }
    }
  }
};

try {
  main();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
}
