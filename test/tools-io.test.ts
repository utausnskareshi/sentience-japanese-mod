/**
 * 作業用CLIのファイル入出力の部品（JSON の読み込み・書き込み）のテスト。
 * テストは一時フォルダの中だけで行い、リポジトリのファイルには触れない。
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { NAMESPACES, type Namespace } from '../src/shared/text.ts';
import { readJson, renameWithRetry, writeJson, writeTextAtomic, type RenameOps } from '../src/tools/lib/json.ts';
import { acquireLock } from '../src/tools/lib/lock.ts';
import {
  TableConflictError,
  assertTablesUnchanged,
  findChangedTables,
  readTablesWithSnapshot,
  snapshotTables,
  tablePath,
  writeTable,
  type TranslationTables,
} from '../src/tools/lib/translations.ts';
import { changedNamespaces, describeConflict, describeWriteFailure } from '../src/tools/lib/merge-lib.ts';

/** テストごとの一時フォルダ */
let dir = '';
beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'sentience-jp-io-'));
});
afterEach(() => {
  rmSync(dir, { recursive: true, force: true });
});

/** errno 風の例外（code 付き）を作る */
const systemError = (code: string): Error => Object.assign(new Error(`${code}: テスト用の失敗`), { code });

describe('JSON の読み込み（TA-04: 厳密な UTF-8）', () => {
  const write = (name: string, bytes: Buffer): string => {
    const path = join(dir, name);
    writeFileSync(path, bytes);
    return path;
  };

  it('UTF-8（BOM なし・BOM 付き）の JSON を読み、BOM は取り除く', () => {
    const json = Buffer.from('{"msg:00000000000001":"こんにちは"}', 'utf8');
    expect(readJson(write('plain.json', json))).toEqual({ 'msg:00000000000001': 'こんにちは' });
    const withBom = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), json]);
    expect(readJson(write('bom.json', withBom))).toEqual({ 'msg:00000000000001': 'こんにちは' });
  });

  it('BOM は 1 つだけ取り除く（二重に取り除かない）', () => {
    // BOM の後にさらに U+FEFF があるファイルは、JSON として不正（JSON の空白に U+FEFF は含まれない）
    const doubled = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf]), Buffer.from('{}', 'utf8')]);
    expect(() => readJson(write('double-bom.json', doubled))).toThrow(/JSON の解析に失敗しました/);
  });

  it('Shift_JIS（CP932）や UTF-16 で保存したファイルは、ファイル名と対処を含めて拒否する', () => {
    // 「回復アイテム」を CP932 で表したバイト列（JSON の構造は ASCII なので、緩い復号では解析が通ってしまう）
    const cp932 = Buffer.concat([
      Buffer.from('{"db:0e127772c72cdc":"', 'ascii'),
      Buffer.from([0x89, 0xf1, 0x95, 0x9c, 0x83, 0x41, 0x83, 0x43, 0x83, 0x65, 0x83, 0x80]),
      Buffer.from('"}', 'ascii'),
    ]);
    const sjisPath = write('adv-sjis.json', cp932);
    expect(() => readJson(sjisPath)).toThrow(/adv-sjis\.json は UTF-8 として不正です。UTF-8（BOM なし可）で保存してください/);
    // UTF-16LE（BOM 付き）は先頭の FF FE が UTF-8 として不正
    const utf16 = Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('{}', 'utf16le')]);
    expect(() => readJson(write('utf16.json', utf16))).toThrow(/UTF-8 として不正です/);
  });
});

describe('ファイルの書き込み（TA-10: rename の再試行と一時ファイルの片付け）', () => {
  /** 指定した回数だけ code で失敗し、その後は成功する rename（呼び出しと待ち時間を記録する） */
  const flakyOps = (failures: number, code: string): RenameOps & { readonly calls: string[]; readonly sleeps: number[] } => {
    const calls: string[] = [];
    const sleeps: number[] = [];
    let remaining = failures;
    return {
      calls,
      sleeps,
      rename: (from, to) => {
        calls.push(`${from}->${to}`);
        if (remaining > 0) {
          remaining -= 1;
          throw systemError(code);
        }
      },
      sleep: (ms) => {
        sleeps.push(ms);
      },
    };
  };

  it('EPERM・EACCES・EBUSY は間隔をあけて再試行し、成功すればそのまま終わる', () => {
    ['EPERM', 'EACCES', 'EBUSY'].forEach((code) => {
      const ops = flakyOps(2, code);
      renameWithRetry('a.tmp', 'a.json', [10, 20, 30], ops);
      expect(ops.calls).toHaveLength(3);
      expect(ops.sleeps).toEqual([10, 20]);
    });
  });

  it('再試行を使い切ったら最後の例外を投げ、それ以外のエラーは再試行しない', () => {
    const always = flakyOps(100, 'EBUSY');
    expect(() => renameWithRetry('a.tmp', 'a.json', [1, 2], always)).toThrow(/EBUSY/);
    expect(always.calls).toHaveLength(3);
    expect(always.sleeps).toEqual([1, 2]);
    const missing = flakyOps(1, 'ENOENT');
    expect(() => renameWithRetry('a.tmp', 'a.json', [1, 2], missing)).toThrow(/ENOENT/);
    expect(missing.calls).toHaveLength(1);
    expect(missing.sleeps).toEqual([]);
  });

  it('最終的に失敗したら一時ファイルを削除し、対象のパスとエラーコードを含む例外を投げる', () => {
    const path = join(dir, 'quest.json');
    writeFileSync(path, '{"old":"内容"}\n', 'utf8');
    const ops = flakyOps(100, 'EPERM');
    expect(() => writeTextAtomic(path, '{"new":"内容"}\n', { retryDelaysMs: [1, 1], ops })).toThrow(/quest\.json を書き込めませんでした（EPERM）/);
    // 一時ファイルは残らず、元のファイルは変わらない
    expect(readdirSync(dir)).toEqual(['quest.json']);
    expect(readFileSync(path, 'utf8')).toBe('{"old":"内容"}\n');
  });

  it('実際のファイル操作で置き換えに失敗した場合（置き換え先がフォルダ）も一時ファイルを残さない', () => {
    const target = join(dir, 'msg.json');
    mkdirSync(target);
    expect(() => writeTextAtomic(target, '{}\n', { retryDelaysMs: [1] })).toThrow(/msg\.json を書き込めませんでした/);
    expect(readdirSync(dir)).toEqual(['msg.json']);
  });

  it('置き換え直前の確認（beforeReplace）が例外を投げたら、置き換えずに一時ファイルを消し、その例外をそのまま投げる', () => {
    const path = join(dir, 'msg.json');
    writeFileSync(path, '{"old":"内容"}\n', 'utf8');
    const stop = new Error('競合しました');
    expect(() =>
      writeTextAtomic(path, '{"new":"内容"}\n', {
        beforeReplace: () => {
          // 一時ファイルは書き終わっている
          expect(existsSync(`${path}.${process.pid}.tmp`)).toBe(true);
          throw stop;
        },
      }),
    ).toThrow(stop);
    expect(readdirSync(dir)).toEqual(['msg.json']);
    expect(readFileSync(path, 'utf8')).toBe('{"old":"内容"}\n');
  });

  it('writeJson は親フォルダを作り、整形した JSON（末尾改行・LF）を書く', () => {
    const path = join(dir, 'sub', 'a.json');
    writeJson(path, { b: 'い', a: ['x'] });
    expect(readFileSync(path, 'utf8')).toBe('{\n  "b": "い",\n  "a": [\n    "x"\n  ]\n}\n');
    expect(existsSync(`${path}.${process.pid}.tmp`)).toBe(false);
  });
});

describe('merge の排他（TA-09: ロックファイル）', () => {
  it('ロックは 1 つだけ取れ、2 つ目は場所と作成したプロセスの情報を含む案内で拒否し、解放すると再び取れる', () => {
    const path = join(dir, 'ja', '.merge.lock');
    const release = acquireLock(path, 'merge');
    expect(readFileSync(path, 'utf8')).toMatch(new RegExp(`^pid=${process.pid} started=\\d{4}-`));
    expect(() => acquireLock(path, 'merge')).toThrow(/別の merge が実行中です（異常終了で残った場合は .*\.merge\.lock を削除してください）。ロックの情報: pid=/);
    release();
    expect(existsSync(path)).toBe(false);
    const again = acquireLock(path, 'merge');
    again();
    expect(existsSync(path)).toBe(false);
  });

  it('異常終了で残ったロック（中身が空）でも、場所を案内して拒否する', () => {
    const path = join(dir, '.merge.lock');
    writeFileSync(path, '');
    expect(() => acquireLock(path, 'merge')).toThrow(/別の merge が実行中です（異常終了で残った場合は .*\.merge\.lock を削除してください）$/);
  });
});

describe('訳文ファイルの変更の検出と書き込み（TA-09・TA-10）', () => {
  /** 空の訳文表 */
  const emptyTables = (): TranslationTables =>
    NAMESPACES.reduce((acc, ns) => {
      acc[ns] = {};
      return acc;
    }, {} as TranslationTables);

  it('読み込んだ表と内容の指紋を返し、読み込み後の他のプロセスの変更を名前空間ごとに検出する', () => {
    writeTable('msg', { b: 'い', a: 'あ' }, dir);
    const loaded = readTablesWithSnapshot(dir);
    expect(loaded.tables.msg).toEqual({ a: 'あ', b: 'い' });
    expect(loaded.tables.choice).toEqual({});
    expect(loaded.snapshot.choice).toBeNull();
    expect(findChangedTables(loaded.snapshot, snapshotTables(dir))).toEqual([]);
    // 別のプロセスが msg を書き換え、choice を新しく作った
    writeFileSync(tablePath('msg', dir), '{"a":"変更"}\n', 'utf8');
    writeTable('choice', { c: 'う' }, dir);
    expect(findChangedTables(loaded.snapshot, snapshotTables(dir))).toEqual(['msg', 'choice']);
    // 比べる名前空間（書き込む予定のもの）を限ると、その中だけを見る
    expect(findChangedTables(loaded.snapshot, snapshotTables(dir), ['choice', 'db'])).toEqual(['choice']);
    // 確認の関数は、変えられた名前空間を持つ TableConflictError を投げる
    expect(() => assertTablesUnchanged(loaded.snapshot, ['db', 'msg'], dir)).toThrow(TableConflictError);
    expect(() => assertTablesUnchanged(loaded.snapshot, ['db'], dir)).not.toThrow();
    try {
      assertTablesUnchanged(loaded.snapshot, ['msg', 'choice'], dir);
    } catch (error) {
      expect(error instanceof TableConflictError ? error.namespaces : []).toEqual(['msg', 'choice']);
    }
  });

  it('書き込みの途中で他のプロセスが置き換え先を変えたら、置き換え直前の確認で止まり、その変更を消さない', () => {
    writeTable('msg', { a: 'あ' }, dir);
    const loaded = readTablesWithSnapshot(dir);
    const external = '{"a":"あ","x":"外部の変更"}\n';
    expect(() =>
      writeTable('msg', { a: 'あ', b: 'い' }, dir, {
        beforeReplace: () => {
          // 一時ファイルを書いている間に、別のプロセスが msg.json を保存した
          writeFileSync(tablePath('msg', dir), external, 'utf8');
          assertTablesUnchanged(loaded.snapshot, ['msg'], dir);
        },
      }),
    ).toThrow(TableConflictError);
    expect(readFileSync(tablePath('msg', dir), 'utf8')).toBe(external);
    expect(readdirSync(dir)).toEqual(['msg.json']);
  });

  it('内容（キーと訳文の組）が変わった名前空間だけを書き込み対象にする', () => {
    const before = emptyTables();
    before.msg = { a: 'あ', b: 'い' };
    before.term = { t: '用語' };
    const after: TranslationTables = { ...before, msg: { b: 'い', a: 'あ' }, choice: { c: 'はい' }, term: { t: '用語2' } };
    expect(changedNamespaces(before, after)).toEqual<Namespace[]>(['choice', 'term']);
    expect(changedNamespaces(before, { ...before, db: { constructor: 'x' } })).toEqual<Namespace[]>(['db']);
    expect(changedNamespaces(before, before)).toEqual([]);
  });

  it('書き込みの失敗の案内に、失敗したファイル・原因・更新済みの名前空間を含める', () => {
    expect(describeWriteFailure('translation\\ja\\quest.json', ['msg', 'cem'], 'EPERM')).toBe(
      'translation\\ja\\quest.json の書き込みに失敗しました（EPERM）。開いているプログラム（エディタ・ウイルス対策ソフトなど）を閉じてから再実行してください（msg・cem は更新済み）',
    );
    expect(describeWriteFailure('translation\\ja\\msg.json', [], 'EBUSY')).toMatch(/（ほかの名前空間は更新していません）$/);
  });

  it('競合で中止したときの案内に、変えられていたファイルと更新済みの名前空間を含める', () => {
    expect(describeConflict(['translation\\ja\\msg.json'], [])).toBe(
      'translation\\ja\\msg.json が読み込み後に変更されています（別の merge やエディタでの保存などと競合しました）。上書きして消さないよう、書き込みを中止しました（translation/ja は変更していません）。再実行してください',
    );
    expect(describeConflict(['translation\\ja\\term.json'], ['msg'])).toMatch(/（msg は更新済み）。再実行してください$/);
  });
});
